using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;

namespace JarviTools.Commands.MaintenanceReachability
{
    /// <summary>Options for one read-only Revit-to-ledger bridge snapshot.</summary>
    public sealed class MaintenanceLedgerSyncOptions
    {
        public string OutputDirectory { get; set; }
        public string FilePrefix { get; set; } = "maintenance-ledger";
        public string ExpectedModelFingerprint { get; set; }
        public bool DryRun { get; set; }
    }

    /// <summary>Machine-readable outcome returned to MCP/AI callers.</summary>
    public sealed class MaintenanceLedgerSyncResult
    {
        public string SchemaVersion { get; internal set; }
        public string ModelFingerprint { get; internal set; }
        public string GeneratedAtUtc { get; internal set; }
        public int OwnedDirectShapeCount { get; internal set; }
        public int UserLedgerRowCount { get; internal set; }
        public int PreservedManualRowCount { get; internal set; }
        public string SnapshotHashSha256 { get; internal set; }
        public string UserLedgerCsvPath { get; internal set; }
        public string CodexEvidenceCsvPath { get; internal set; }
        public string ManifestJsonPath { get; internal set; }
        public bool FilesWritten { get; internal set; }
        public IList<string> Warnings { get; internal set; } = new List<string>();
    }

    /// <summary>
    /// Exports a deterministic bridge snapshot from CODEX-owned maintenance DirectShapes.
    /// Revit's eight shared parameters are the source of truth.  The service deliberately
    /// does not edit XLSX: an AI/MCP layer can map these stable CSV rows into a project
    /// workbook while retaining its formulas, formatting and project-only columns.
    /// </summary>
    public static class MaintenanceLedgerSyncService
    {
        public const string SchemaVersion = "1.2";
        public const string DefaultFilePrefix = "maintenance-ledger";

        public const string UserRowKeyColumn = "行键";
        public const string LedgerManualConclusionColumn = "台账人工确认";
        public const string LedgerManualNoteColumn = "台账人工备注";

        private const double MillimetresPerFoot = 304.8;

        private static readonly string[] UserHeaders =
        {
            UserRowKeyColumn,
            "逻辑组",
            "入口组",
            "维修对象",
            "推荐入口",
            "梯型",
            "Revit维修结论",
            "Revit判断说明",
            "Revit专业备注",
            "AI复核人",
            "AI复核说明",
            "AI复核时间UTC",
            "分析证据指纹",
            LedgerManualConclusionColumn,
            LedgerManualNoteColumn,
            "来源DirectShape图元ID",
            "来源ApplicationDataId",
            "模型指纹",
            "同步时间UTC"
        };

        private static readonly string[] EvidenceHeaders =
        {
            "证据行键",
            "模型指纹",
            "DirectShape图元ID",
            "DirectShape唯一ID",
            "ApplicationId",
            "ApplicationDataId",
            MaintenanceParameterService.ParameterElementName,
            MaintenanceParameterService.ParameterCeilingGroup,
            MaintenanceParameterService.ParameterEntryGroup,
            MaintenanceParameterService.ParameterElementRole,
            MaintenanceParameterService.ParameterMaintenanceTarget,
            MaintenanceParameterService.ParameterMaintenanceConclusion,
            MaintenanceParameterService.ParameterDecisionNote,
            MaintenanceParameterService.ParameterProfessionalNote,
            "AI复核人",
            "AI复核说明",
            "AI复核时间UTC",
            "分析证据指纹",
            "包围框最小点mm",
            "包围框最大点mm"
        };

        private sealed class ShapeRecord
        {
            public long ElementId;
            public string UniqueId;
            public string ApplicationId;
            public string ApplicationDataId;
            public string EvidenceKey;
            public string ElementName;
            public string CeilingGroup;
            public string EntryGroup;
            public string ElementRole;
            public string MaintenanceTarget;
            public string MaintenanceConclusion;
            public string DecisionNote;
            public string ProfessionalNote;
            public string ReviewReviewer;
            public string ReviewNote;
            public string ReviewApprovedAtUtc;
            public string AnalysisEvidenceFingerprint;
            public string BoundingBoxMinMm;
            public string BoundingBoxMaxMm;
        }

        private sealed class ManualLedgerState
        {
            public string Conclusion;
            public string Note;
        }

        public static MaintenanceLedgerSyncResult Export(
            Document doc,
            MaintenanceLedgerSyncOptions options)
        {
            if (doc == null) throw new ArgumentNullException("doc");
            if (options == null) throw new ArgumentNullException("options");
            if (doc.IsFamilyDocument)
                throw new InvalidOperationException("维修可达台账只能从项目文档同步。");

            string outputDirectory = ValidateOutputDirectory(options.OutputDirectory);
            string filePrefix = ValidateFilePrefix(options.FilePrefix);
            string modelFingerprint = GetModelFingerprint(doc);
            if (!string.IsNullOrWhiteSpace(options.ExpectedModelFingerprint) &&
                !string.Equals(
                    options.ExpectedModelFingerprint.Trim(),
                    modelFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "当前 Revit 模型指纹与请求不一致，已拒绝把数据写入另一个项目的台账。");
            }

            string generatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            string userPath = Path.Combine(outputDirectory, filePrefix + ".user.csv");
            string evidencePath = Path.Combine(outputDirectory, filePrefix + ".codex.csv");
            string manifestPath = Path.Combine(outputDirectory, filePrefix + ".manifest.json");

            var warnings = new List<string>();
            Dictionary<string, ManualLedgerState> manualStates =
                ReadManualLedgerStates(userPath, warnings);
            List<ShapeRecord> records = ReadOwnedShapes(doc, warnings);
            List<IDictionary<string, string>> evidenceRows = BuildEvidenceRows(
                records, modelFingerprint);
            int preservedManualRows;
            List<IDictionary<string, string>> userRows = BuildUserRows(
                records,
                manualStates,
                modelFingerprint,
                generatedAtUtc,
                out preservedManualRows);
            List<string> orphanManualKeys = MaintenanceLedgerCsv.FindOrphanManualKeys(
                userRows.Select(x => x[UserRowKeyColumn]),
                manualStates.Select(x => new KeyValuePair<string, bool>(
                    x.Key,
                    !string.IsNullOrWhiteSpace(x.Value.Conclusion) ||
                    !string.IsNullOrWhiteSpace(x.Value.Note))));
            if (orphanManualKeys.Count > 0)
                throw new InvalidDataException(
                    "旧用户桥接 CSV 有 " + orphanManualKeys.Count +
                    " 行带人工确认/备注，但对应方案本次已不存在或行键已改变。" +
                    "为防止人工数据静默丢失，本次同步已在写文件前停止，旧文件保持不变。孤儿行键：" +
                    string.Join(";", orphanManualKeys.Take(8)));

            if (!options.DryRun)
            {
                try
                {
                    MaintenanceLedgerConfigurationService.Remember(
                        doc,
                        outputDirectory,
                        filePrefix);
                }
                catch (Exception exception)
                {
                    warnings.Add(
                        "台账文件已可继续生成，但无法记住自动 HandReach 台账目录：" +
                        exception.Message);
                }
            }

            string userCsv = MaintenanceLedgerCsv.Serialize(UserHeaders, userRows);
            string evidenceCsv = MaintenanceLedgerCsv.Serialize(EvidenceHeaders, evidenceRows);
            // CSV files are intentionally UTF-8 with BOM for Excel.  Manifest hashes
            // therefore cover the exact bytes written to disk, including the BOM.
            string userHash = MaintenanceLedgerCsv.Sha256HexUtf8BomFile(userCsv);
            string evidenceHash = MaintenanceLedgerCsv.Sha256HexUtf8BomFile(evidenceCsv);
            string snapshotHash = MaintenanceLedgerCsv.Sha256Hex(
                SchemaVersion + "|" + modelFingerprint + "|" + evidenceHash);

            string manifestJson = BuildManifestJson(
                doc,
                generatedAtUtc,
                modelFingerprint,
                snapshotHash,
                Path.GetFileName(userPath),
                userHash,
                userRows.Count,
                Path.GetFileName(evidencePath),
                evidenceHash,
                evidenceRows.Count,
                preservedManualRows,
                warnings);

            if (!options.DryRun)
            {
                // The manifest is the commit marker.  AI consumers must verify its two
                // file hashes; an interrupted multi-file write is therefore detectable.
                try
                {
                    MaintenanceLedgerCsv.WriteAllTextAtomic(evidencePath, evidenceCsv);
                    MaintenanceLedgerCsv.WriteAllTextAtomic(userPath, userCsv);
                    MaintenanceLedgerCsv.WriteAllTextAtomic(manifestPath, manifestJson);
                }
                catch (Exception exception)
                {
                    throw new IOException(
                        "维修台账多文件提交未完成；现有 manifest 可能仍对应旧 CSV。" +
                        "在逐文件核对 manifest 哈希前请勿使用本次快照。", exception);
                }
            }

            return new MaintenanceLedgerSyncResult
            {
                SchemaVersion = SchemaVersion,
                ModelFingerprint = modelFingerprint,
                GeneratedAtUtc = generatedAtUtc,
                OwnedDirectShapeCount = records.Count,
                UserLedgerRowCount = userRows.Count,
                PreservedManualRowCount = preservedManualRows,
                SnapshotHashSha256 = snapshotHash,
                UserLedgerCsvPath = userPath,
                CodexEvidenceCsvPath = evidencePath,
                ManifestJsonPath = manifestPath,
                FilesWritten = !options.DryRun,
                Warnings = warnings
            };
        }

        public static string GetModelFingerprint(Document doc)
        {
            if (doc == null) throw new ArgumentNullException("doc");

            string centralGuid = string.Empty;
            if (doc.IsWorkshared)
            {
                try { centralGuid = doc.WorksharingCentralGUID.ToString("D"); }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
            }

            string projectInformationId = doc.ProjectInformation == null
                ? string.Empty
                : Safe(doc.ProjectInformation.UniqueId);
            string normalizedPath = NormalizeSourcePath(doc.PathName);
            string sourcePathHash = MaintenanceLedgerCsv.Sha256Hex(normalizedPath);
            return MaintenanceLedgerCsv.Sha256Hex(string.Join("|", new[]
            {
                "JarviTools.MaintenanceLedger.Model.v1",
                centralGuid,
                projectInformationId,
                sourcePathHash,
                Safe(doc.Title)
            }));
        }

        private static string ValidateOutputDirectory(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("必须提供台账桥接文件的输出文件夹。", "OutputDirectory");
            string requestedPath = value.Trim();
            if (!Path.IsPathRooted(requestedPath))
                throw new ArgumentException("输出文件夹必须是绝对路径。", "OutputDirectory");
            string fullPath = Path.GetFullPath(requestedPath);
            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException("台账桥接输出文件夹不存在：" + fullPath);
            return fullPath;
        }

        private static string ValidateFilePrefix(string value)
        {
            string prefix = string.IsNullOrWhiteSpace(value)
                ? DefaultFilePrefix
                : value.Trim();
            if (prefix == "." || prefix == ".." ||
                !string.Equals(Path.GetFileName(prefix), prefix, StringComparison.Ordinal) ||
                prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("文件前缀只能是一个安全文件名，不能包含路径。", "FilePrefix");
            }
            return prefix;
        }

        private static List<ShapeRecord> ReadOwnedShapes(
            Document doc,
            IList<string> warnings)
        {
            var missingCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var records = new List<ShapeRecord>();
            foreach (DirectShape shape in new FilteredElementCollector(doc)
                .OfClass(typeof(DirectShape))
                .Cast<DirectShape>()
                .Where(item => string.Equals(
                        item.ApplicationId,
                        MaintenanceVisualizationService.OwnerApplicationId,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        item.ApplicationId,
                        MaintenanceHandReachVisualizationService.FormalApplicationId,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        item.ApplicationId,
                        MaintenanceWallAlternativeVisualizationService.OwnerApplicationId,
                        StringComparison.Ordinal))
                .OrderBy(item => Safe(item.ApplicationDataId), StringComparer.Ordinal)
                .ThenBy(item => item.Id.Value))
            {
                var record = new ShapeRecord
                {
                    ElementId = shape.Id.Value,
                    UniqueId = Safe(shape.UniqueId),
                    ApplicationId = Safe(shape.ApplicationId),
                    ApplicationDataId = Safe(shape.ApplicationDataId),
                    ElementName = ReadText(shape, MaintenanceParameterService.ElementNameGuid,
                        MaintenanceParameterService.ParameterElementName, missingCounts),
                    CeilingGroup = ReadText(shape, MaintenanceParameterService.CeilingGroupGuid,
                        MaintenanceParameterService.ParameterCeilingGroup, missingCounts),
                    EntryGroup = ReadText(shape, MaintenanceParameterService.EntryGroupGuid,
                        MaintenanceParameterService.ParameterEntryGroup, missingCounts),
                    ElementRole = ReadText(shape, MaintenanceParameterService.ElementRoleGuid,
                        MaintenanceParameterService.ParameterElementRole, missingCounts),
                    MaintenanceTarget = ReadText(shape, MaintenanceParameterService.MaintenanceTargetGuid,
                        MaintenanceParameterService.ParameterMaintenanceTarget, missingCounts),
                    MaintenanceConclusion = ReadText(shape, MaintenanceParameterService.MaintenanceConclusionGuid,
                        MaintenanceParameterService.ParameterMaintenanceConclusion, missingCounts),
                    DecisionNote = ReadText(shape, MaintenanceParameterService.DecisionNoteGuid,
                        MaintenanceParameterService.ParameterDecisionNote, missingCounts),
                    ProfessionalNote = ReadText(shape, MaintenanceParameterService.ProfessionalNoteGuid,
                        MaintenanceParameterService.ParameterProfessionalNote, missingCounts)
                };
                BoundingBoxXYZ box = shape.get_BoundingBox(null);
                MaintenanceReviewTrace reviewTrace =
                    MaintenanceVisualizationService.ReadReviewTrace(shape);
                record.ReviewReviewer = reviewTrace.Reviewer;
                record.ReviewNote = reviewTrace.ReviewNote;
                record.ReviewApprovedAtUtc = reviewTrace.ApprovedAtUtc;
                record.AnalysisEvidenceFingerprint = reviewTrace.EvidenceFingerprint;
                record.BoundingBoxMinMm = box == null ? string.Empty : FormatPointMm(box.Min);
                record.BoundingBoxMaxMm = box == null ? string.Empty : FormatPointMm(box.Max);
                record.EvidenceKey = string.IsNullOrWhiteSpace(record.ApplicationDataId)
                    ? "ELEMENT-" + record.ElementId.ToString(CultureInfo.InvariantCulture)
                    : record.ApplicationDataId;
                if (string.IsNullOrWhiteSpace(record.ApplicationDataId))
                    warnings.Add("DirectShape " + record.ElementId + " 缺少 ApplicationDataId，本次只能使用图元 ID 作为行键。");
                records.Add(record);
            }

            foreach (IGrouping<string, ShapeRecord> duplicate in records
                .GroupBy(item => item.EvidenceKey, StringComparer.Ordinal)
                .Where(group => group.Count() > 1))
            {
                warnings.Add("ApplicationDataId 重复：" + duplicate.Key +
                             "；证据行键已附加 DirectShape 图元 ID 防止丢行。");
                foreach (ShapeRecord record in duplicate)
                    record.EvidenceKey += "#" + record.ElementId.ToString(CultureInfo.InvariantCulture);
            }

            foreach (KeyValuePair<string, int> missing in missingCounts.OrderBy(x => x.Key))
                warnings.Add("共有 " + missing.Value + " 个 DirectShape 缺少共享参数“" + missing.Key + "”。");
            if (records.Count == 0)
                warnings.Add("当前模型没有 JarviTools 生成的维修可达 DirectShape。");
            return records;
        }

        private static string ReadText(
            Element element,
            Guid guid,
            string name,
            IDictionary<string, int> missingCounts)
        {
            Parameter parameter = element.get_Parameter(guid);
            if (parameter == null)
            {
                int count;
                missingCounts.TryGetValue(name, out count);
                missingCounts[name] = count + 1;
                return string.Empty;
            }
            return Safe(parameter.AsString());
        }

        private static List<IDictionary<string, string>> BuildEvidenceRows(
            IEnumerable<ShapeRecord> records,
            string modelFingerprint)
        {
            return records.Select(record => (IDictionary<string, string>)
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["证据行键"] = record.EvidenceKey,
                    ["模型指纹"] = modelFingerprint,
                    ["DirectShape图元ID"] = record.ElementId.ToString(CultureInfo.InvariantCulture),
                    ["DirectShape唯一ID"] = record.UniqueId,
                    ["ApplicationId"] = record.ApplicationId,
                    ["ApplicationDataId"] = record.ApplicationDataId,
                    [MaintenanceParameterService.ParameterElementName] = record.ElementName,
                    [MaintenanceParameterService.ParameterCeilingGroup] = record.CeilingGroup,
                    [MaintenanceParameterService.ParameterEntryGroup] = record.EntryGroup,
                    [MaintenanceParameterService.ParameterElementRole] = record.ElementRole,
                    [MaintenanceParameterService.ParameterMaintenanceTarget] = record.MaintenanceTarget,
                    [MaintenanceParameterService.ParameterMaintenanceConclusion] = record.MaintenanceConclusion,
                    [MaintenanceParameterService.ParameterDecisionNote] = record.DecisionNote,
                    [MaintenanceParameterService.ParameterProfessionalNote] = record.ProfessionalNote,
                    ["AI复核人"] = record.ReviewReviewer,
                    ["AI复核说明"] = record.ReviewNote,
                    ["AI复核时间UTC"] = record.ReviewApprovedAtUtc,
                    ["分析证据指纹"] = record.AnalysisEvidenceFingerprint,
                    ["包围框最小点mm"] = record.BoundingBoxMinMm,
                    ["包围框最大点mm"] = record.BoundingBoxMaxMm
                })
                .OrderBy(row => row["证据行键"], StringComparer.Ordinal)
                .ToList();
        }

        private static List<IDictionary<string, string>> BuildUserRows(
            IList<ShapeRecord> records,
            IDictionary<string, ManualLedgerState> manualStates,
            string modelFingerprint,
            string generatedAtUtc,
            out int preservedManualRows)
        {
            preservedManualRows = 0;
            var rows = new List<IDictionary<string, string>>();
            foreach (ShapeRecord decision in records
                .Where(item => IsDecisionRole(item.ElementRole))
                .OrderBy(item => item.CeilingGroup, StringComparer.Ordinal)
                .ThenBy(item => item.MaintenanceTarget, StringComparer.Ordinal)
                .ThenBy(item => item.EvidenceKey, StringComparer.Ordinal))
            {
                List<ShapeRecord> related = records.Where(item =>
                    string.Equals(item.CeilingGroup, decision.CeilingGroup, StringComparison.Ordinal) &&
                    string.Equals(item.EntryGroup, decision.EntryGroup, StringComparison.Ordinal) &&
                    (string.IsNullOrWhiteSpace(decision.MaintenanceTarget) ||
                     string.IsNullOrWhiteSpace(item.MaintenanceTarget) ||
                     TargetListContains(item.MaintenanceTarget, decision.MaintenanceTarget)))
                    .ToList();

                string entry = JoinRoles(
                    related,
                    new[] { "侧墙检修门", "侧墙检修口", "天花检修口" },
                    "无可实施入口");
                string ladder = JoinRoles(related, new[] { "人字梯", "一字梯" }, "不适用");
                ManualLedgerState manual;
                if (!manualStates.TryGetValue(decision.EvidenceKey, out manual))
                    manual = new ManualLedgerState();
                else if (!string.IsNullOrWhiteSpace(manual.Conclusion) ||
                         !string.IsNullOrWhiteSpace(manual.Note))
                    preservedManualRows++;

                rows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [UserRowKeyColumn] = decision.EvidenceKey,
                    ["逻辑组"] = decision.CeilingGroup,
                    ["入口组"] = decision.EntryGroup,
                    ["维修对象"] = decision.MaintenanceTarget,
                    ["推荐入口"] = entry,
                    ["梯型"] = ladder,
                    ["Revit维修结论"] = decision.MaintenanceConclusion,
                    ["Revit判断说明"] = decision.DecisionNote,
                    ["Revit专业备注"] = decision.ProfessionalNote,
                    ["AI复核人"] = decision.ReviewReviewer,
                    ["AI复核说明"] = decision.ReviewNote,
                    ["AI复核时间UTC"] = decision.ReviewApprovedAtUtc,
                    ["分析证据指纹"] = decision.AnalysisEvidenceFingerprint,
                    [LedgerManualConclusionColumn] = Safe(manual.Conclusion),
                    [LedgerManualNoteColumn] = Safe(manual.Note),
                    ["来源DirectShape图元ID"] = decision.ElementId.ToString(CultureInfo.InvariantCulture),
                    ["来源ApplicationDataId"] = decision.ApplicationDataId,
                    ["模型指纹"] = modelFingerprint,
                    ["同步时间UTC"] = generatedAtUtc
                });
            }
            return rows;
        }

        private static bool TargetListContains(string candidateList, string target)
        {
            if (string.IsNullOrWhiteSpace(candidateList) || string.IsNullOrWhiteSpace(target))
                return false;
            return candidateList.Split(new[] { '、' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(value => string.Equals(value.Trim(), target.Trim(), StringComparison.Ordinal));
        }

        private static bool IsDecisionRole(string role)
        {
            string safe = Safe(role);
            // Earlier model snapshots used “设备检修区” while the current
            // presentation service names the same semantic role “设备维修区”.
            // Both names are part of the bridge's backwards-compatible read contract.
            return safe.IndexOf("维修区", StringComparison.Ordinal) >= 0 ||
                   safe.IndexOf("检修区", StringComparison.Ordinal) >= 0;
        }

        private static string JoinRoles(
            IEnumerable<ShapeRecord> records,
            IEnumerable<string> acceptedRoles,
            string fallback)
        {
            HashSet<string> accepted = new HashSet<string>(acceptedRoles, StringComparer.Ordinal);
            List<string> values = records
                .Select(item => Safe(item.ElementRole))
                .Where(accepted.Contains)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            return values.Count == 0 ? fallback : string.Join(" / ", values);
        }

        private static Dictionary<string, ManualLedgerState> ReadManualLedgerStates(
            string userCsvPath,
            IList<string> warnings)
        {
            var result = new Dictionary<string, ManualLedgerState>(StringComparer.Ordinal);
            if (!File.Exists(userCsvPath)) return result;

            List<Dictionary<string, string>> rows;
            try
            {
                rows = MaintenanceLedgerCsv.Parse(
                    MaintenanceLedgerCsv.ReadAllTextShared(userCsvPath));
            }
            catch (Exception exception)
            {
                throw new InvalidDataException(
                    "旧用户桥接 CSV 无法解析，为防止人工结论/备注丢失，本次同步已停止。",
                    exception);
            }

            foreach (Dictionary<string, string> row in rows)
            {
                if (!row.ContainsKey(UserRowKeyColumn) ||
                    !row.ContainsKey(LedgerManualConclusionColumn) ||
                    !row.ContainsKey(LedgerManualNoteColumn))
                {
                    throw new InvalidDataException(
                        "旧用户桥接 CSV 缺少行键或人工列，为防止人工数据丢失，本次同步已停止。");
                }

                string key = Safe(row[UserRowKeyColumn]);
                if (string.IsNullOrWhiteSpace(key)) continue;
                var state = new ManualLedgerState
                {
                    Conclusion = Safe(row[LedgerManualConclusionColumn]),
                    Note = Safe(row[LedgerManualNoteColumn])
                };
                ManualLedgerState existing;
                if (!result.TryGetValue(key, out existing))
                {
                    result[key] = state;
                }
                else if (!string.Equals(existing.Conclusion, state.Conclusion, StringComparison.Ordinal) ||
                         !string.Equals(existing.Note, state.Note, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "旧用户桥接 CSV 中行键“" + key +
                        "”存在互相冲突的人工数据，已停止同步。");
                }
                else
                {
                    warnings.Add("旧用户桥接 CSV 存在重复行键“" + key +
                                 "”，人工数据相同，本次已合并为一行。");
                }
            }
            return result;
        }

        private static string BuildManifestJson(
            Document doc,
            string generatedAtUtc,
            string modelFingerprint,
            string snapshotHash,
            string userFileName,
            string userFileHash,
            int userRowCount,
            string evidenceFileName,
            string evidenceFileHash,
            int evidenceRowCount,
            int preservedManualRows,
            IList<string> warnings)
        {
            string sourcePathHash = MaintenanceLedgerCsv.Sha256Hex(
                NormalizeSourcePath(doc.PathName));
            var manifest = new
            {
                schemaVersion = SchemaVersion,
                generatedAtUtc,
                sourceOfTruth = "Revit DirectShape parameters",
                ownerApplicationIds = new[]
                {
                    MaintenanceVisualizationService.OwnerApplicationId,
                    MaintenanceHandReachVisualizationService.FormalApplicationId,
                    MaintenanceWallAlternativeVisualizationService.OwnerApplicationId
                },
                model = new
                {
                    title = Safe(doc.Title),
                    fingerprint = modelFingerprint,
                    sourcePathHashSha256 = sourcePathHash,
                    isWorkshared = doc.IsWorkshared,
                    hasUnsavedChanges = doc.IsModified
                },
                snapshotHashSha256 = snapshotHash,
                files = new[]
                {
                    new { role = "user-ledger-bridge", name = userFileName, sha256 = userFileHash, rowCount = userRowCount },
                    new { role = "codex-evidence", name = evidenceFileName, sha256 = evidenceFileHash, rowCount = evidenceRowCount }
                },
                parameterContract = new[]
                {
                    MaintenanceParameterService.ParameterElementName,
                    MaintenanceParameterService.ParameterCeilingGroup,
                    MaintenanceParameterService.ParameterEntryGroup,
                    MaintenanceParameterService.ParameterElementRole,
                    MaintenanceParameterService.ParameterMaintenanceTarget,
                    MaintenanceParameterService.ParameterMaintenanceConclusion,
                    MaintenanceParameterService.ParameterDecisionNote,
                    MaintenanceParameterService.ParameterProfessionalNote
                },
                manualDataPolicy = new
                {
                    revitEditableFields = new[]
                    {
                        MaintenanceParameterService.ParameterMaintenanceConclusion,
                        MaintenanceParameterService.ParameterProfessionalNote
                    },
                    preservedBridgeFields = new[]
                    {
                        LedgerManualConclusionColumn,
                        LedgerManualNoteColumn
                    },
                    preservedManualRowCount = preservedManualRows
                },
                idempotency = "Full snapshot replacement by stable row key; manifest hashes are the commit check.",
                privacy = "The manifest stores no absolute Revit model path; only a SHA-256 path hash.",
                warnings = warnings.ToArray()
            };
            return JsonConvert.SerializeObject(manifest, Formatting.Indented) + Environment.NewLine;
        }

        private static string FormatPointMm(XYZ point)
        {
            if (point == null) return string.Empty;
            return string.Join(";", new[]
            {
                (point.X * MillimetresPerFoot).ToString("0.0", CultureInfo.InvariantCulture),
                (point.Y * MillimetresPerFoot).ToString("0.0", CultureInfo.InvariantCulture),
                (point.Z * MillimetresPerFoot).ToString("0.0", CultureInfo.InvariantCulture)
            });
        }

        private static string NormalizeSourcePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string path = value.Trim();
            try
            {
                // Local and UNC paths get canonicalized.  Autodesk Docs/BIM 360 cloud
                // paths are not valid System.IO paths, but are still safe to normalize
                // as opaque identifiers because only their SHA-256 is exported.
                if (Path.IsPathRooted(path)) path = Path.GetFullPath(path);
            }
            catch (Exception exception)
            {
                if (!(exception is ArgumentException) &&
                    !(exception is NotSupportedException) &&
                    !(exception is PathTooLongException))
                    throw;
            }
            return path.Replace('/', '\\').ToUpperInvariant();
        }

        private static string Safe(string value)
        {
            return value ?? string.Empty;
        }
    }
}

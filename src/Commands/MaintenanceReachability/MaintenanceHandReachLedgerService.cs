using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace JarviTools.Commands.MaintenanceReachability
{
    internal sealed class MaintenanceHandReachLedgerExportResult
    {
        public string SummaryCsvPath;
        public string CandidateCsvPath;
        public string ManifestJsonPath;
        public string SnapshotHashSha256;
        public int SummaryRowCount;
        public int CandidateRowCount;
        public int PreservedManualRowCount;
        public int ResetStaleManualConclusionCount;
        public string ManualConclusionWarning;
        public string LegacyMigrationStatus;
        public string LegacyArchiveDirectory;
        public int LegacyMappedManualRowCount;
        public string LegacyMigrationWarning;
    }

    /// <summary>
    /// Persists the data-only HandReach result itself.  It does not depend on
    /// DirectShape visualization, so every analyzed device receives a ledger row,
    /// including rejected and pending-review targets.
    /// </summary>
    internal static class MaintenanceHandReachLedgerService
    {
        internal const string SchemaVersion = "OpenRevit.HandReachLedger.v3";
        private const string DefaultFilePrefix = "maintenance-ledger";
        private const string OwnerApplicationId = "JarviTools.MaintenanceHandReach.v1";
        private const string ManualConclusionColumn = "台账人工确认";
        private const string ManualNoteColumn = "台账人工备注";

        private static readonly string[] SummaryHeaders =
        {
            "行键", "逻辑组", "设备编号", "方案编号", "入口组", "维修对象", "目标键", "链接实例ID", "设备图元ID",
            "分析类型", "入口类型", "墙面键", "候选区域数", "推荐口中心mm", "最近口边缘mm", "伸手通道起点mm", "水平距离mm", "斜向实际距离mm",
            "垂直高差mm", "最大已验证通道直径mm", "梯具状态", "推荐梯向", "可行点数", "连通性一致",
            "豁免证据数", "真实障碍数", "关注级", "Revit维修结论", "Revit判断说明",
            ManualConclusionColumn, ManualNoteColumn,
            "分析ID", "模型指纹", "模型证据指纹", "结果指纹", "分析时间UTC", "同步时间UTC"
        };

        private static readonly string[] CandidateHeaders =
        {
            "候选行键", "逻辑组", "设备编号", "方案编号", "入口组", "维修对象", "目标键", "入口类型", "墙面键", "区域编号", "区域点数", "区域面积m2",
            "区域范围mm", "推荐口中心mm", "最近口边缘mm", "伸手通道起点mm", "检修面代理点mm", "水平距离mm", "斜向实际距离mm",
            "垂直高差mm", "距离等级", "垂直等级", "逐档通道结果", "最大已验证通道直径mm",
            "推荐梯向", "梯具操作区通过", "豁免相交数", "阻挡键", "关注级", "Revit维修结论",
            "分析ID", "模型证据指纹", "结果指纹"
        };

        internal static MaintenanceHandReachLedgerExportResult Export(
            HandReachAnalysisResult result,
            MaintenanceLedgerDestination destination)
        {
            if (result == null) throw new ArgumentNullException("result");
            if (destination == null) throw new ArgumentNullException("destination");
            MaintenanceLedgerDestination normalized;
            string destinationErrorCode;
            string destinationErrorMessage;
            if (!destination.TryNormalize(
                DefaultFilePrefix,
                out normalized,
                out destinationErrorCode,
                out destinationErrorMessage))
            {
                if (string.Equals(destinationErrorCode, "directory_missing", StringComparison.Ordinal))
                    throw new DirectoryNotFoundException(destinationErrorMessage);
                if (string.Equals(destinationErrorCode, "destination_not_configured", StringComparison.Ordinal))
                    throw new InvalidOperationException(destinationErrorMessage);
                throw new ArgumentException(destinationErrorMessage, "destination");
            }
            destination = normalized;
            string prefix = destination.FilePrefix;

            string summaryPath = Path.Combine(
                destination.OutputDirectory,
                prefix + ".handreach.summary.csv");
            string candidatePath = Path.Combine(
                destination.OutputDirectory,
                prefix + ".handreach.candidates.csv");
            string manifestPath = Path.Combine(
                destination.OutputDirectory,
                prefix + ".handreach.manifest.json");
            MaintenanceHandReachLegacyMigrationResult legacyMigration =
                ReadManualStates(
                    summaryPath,
                    candidatePath,
                    manifestPath,
                    result,
                    destination.LegacyArchiveRoot);
            Dictionary<string, MaintenanceHandReachManualState> manualStates =
                legacyMigration.ManualStates;
            int preservedManualRows;
            int resetStaleManualConclusions;
            List<IDictionary<string, string>> summaryRows = BuildSummaryRows(
                result,
                manualStates,
                out preservedManualRows,
                out resetStaleManualConclusions);
            string manualConclusionWarning = resetStaleManualConclusions > 0
                ? resetStaleManualConclusions +
                  " 行旧人工确认因模型证据、结果或判断理由已变化而未继承；人工备注已保留。"
                : string.Empty;
            var currentRowKeys = new HashSet<string>(
                summaryRows.Select(x => x["行键"]),
                StringComparer.Ordinal);
            List<string> orphanManualKeys = manualStates
                .Where(x => !currentRowKeys.Contains(x.Key) &&
                            (!string.IsNullOrWhiteSpace(x.Value.Conclusion) ||
                             !string.IsNullOrWhiteSpace(x.Value.Note)))
                .Select(x => x.Key)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            if (orphanManualKeys.Count > 0)
                throw new InvalidDataException(
                    "旧 HandReach 汇总中有 " + orphanManualKeys.Count +
                    " 行带人工确认/备注，但本次目标已不存在。为防止人工数据静默丢失，本次自动同步已停止，旧文件保持不变。孤儿行键：" +
                    string.Join(";", orphanManualKeys.Take(8)));
            List<IDictionary<string, string>> candidateRows = BuildCandidateRows(result);

            string summaryCsv = MaintenanceLedgerCsv.Serialize(SummaryHeaders, summaryRows);
            string candidateCsv = MaintenanceLedgerCsv.Serialize(CandidateHeaders, candidateRows);
            string summaryHash = MaintenanceLedgerCsv.Sha256HexUtf8BomFile(summaryCsv);
            string candidateHash = MaintenanceLedgerCsv.Sha256HexUtf8BomFile(candidateCsv);
            string sourceHash = MaintenanceLedgerCsv.Sha256Hex(string.Join("\n",
                result.EvidenceSources
                    .Where(x => x != null)
                    .Select(x => x.GetStableKey())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)));
            string snapshotHash = MaintenanceLedgerCsv.Sha256Hex(string.Join("|", new[]
            {
                SchemaVersion,
                result.AnalysisId ?? string.Empty,
                result.ModelFingerprint ?? string.Empty,
                result.EvidenceFingerprint ?? string.Empty,
                result.ResultFingerprint ?? string.Empty,
                summaryHash,
                candidateHash,
                sourceHash
            }));
            string generatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            MaintenanceLinkScopeSnapshot linkScope =
                result.LinkScope ?? new MaintenanceLinkScopeSnapshot();
            var manifest = new
            {
                schemaVersion = SchemaVersion,
                generatedAtUtc,
                sourceOfTruth = "data-only HandReach analysis snapshot",
                ownerApplicationId = OwnerApplicationId,
                analysis = new
                {
                    id = result.AnalysisId,
                    createdAtUtc = result.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
                    modelFingerprint = result.ModelFingerprint,
                    evidenceFingerprint = result.EvidenceFingerprint,
                    resultFingerprint = result.ResultFingerprint,
                    linkScope = new
                    {
                        contractVersion = MaintenanceLinkScopePolicy.ContractVersion,
                        explicitScope = linkScope.Explicit,
                        count = linkScope.RelevantLinks.Count +
                                linkScope.OutOfScopeLinks.Count,
                        relevantLinkCount = linkScope.RelevantLinks.Count,
                        outOfScopeLinkCount = linkScope.OutOfScopeLinks.Count,
                        relevantLinks = linkScope.RelevantLinks
                            .Where(x => x != null)
                            .OrderBy(x => x.GetStableKey(), StringComparer.Ordinal)
                            .Select(x => new
                            {
                                key = x.GetStableKey(),
                                linkInstanceId = x.LinkInstanceId,
                                linkInstanceUniqueId = x.LinkInstanceUniqueId,
                                instanceName = x.InstanceName,
                                typeName = x.TypeName,
                                loadedAtAnalysis = x.LoadedAtAnalysis
                            })
                            .ToArray(),
                        outOfScopeLinks = linkScope.OutOfScopeLinks
                            .Where(x => x != null)
                            .OrderBy(x => x.GetStableKey(), StringComparer.Ordinal)
                            .Select(x => new
                            {
                                key = x.GetStableKey(),
                                linkInstanceId = x.LinkInstanceId,
                                linkInstanceUniqueId = x.LinkInstanceUniqueId,
                                instanceName = x.InstanceName,
                                typeName = x.TypeName,
                                loadedAtAnalysis = x.LoadedAtAnalysis
                            })
                            .ToArray()
                    },
                    groupCount = result.TargetResults
                        .Where(x => x != null && x.Target != null)
                        .Select(x => SafeGroup(result, x.Target))
                        .Distinct(StringComparer.Ordinal)
                        .Count(),
                    targetCount = result.TargetResults.Count,
                    evidenceSourceCount = result.EvidenceSources
                        .Where(x => x != null)
                        .Select(x => x.GetStableKey())
                        .Distinct(StringComparer.Ordinal)
                        .Count(),
                    evidenceSourceKeysSha256 = sourceHash,
                    exemptPipeEvidence = result.ExemptPipeEvidence
                        .Where(x => x != null && x.Element != null)
                        .OrderBy(x => x.GroupKey, StringComparer.Ordinal)
                        .ThenBy(x => x.TargetKey, StringComparer.Ordinal)
                        .ThenBy(x => x.Element.GetStableKey(), StringComparer.Ordinal)
                        .Select(x => new
                        {
                            group = x.GroupKey,
                            targetKey = x.TargetKey,
                            key = x.Element.GetStableKey(),
                            systemKind = x.SystemKind,
                            systemTypeEvidence = x.SystemTypeEvidence,
                            systemEvidenceSource = x.SystemEvidenceSource,
                            reasonCode = x.ReasonCode,
                            reason = x.Reason
                        })
                        .ToArray(),
                    coverageComplete = result.CoverageComplete,
                    collectionFailures = result.CoverageFailures
                        .Where(x => x != null)
                        .OrderBy(x => x.Stage, StringComparer.Ordinal)
                        .ThenBy(x => x.SourceKey, StringComparer.Ordinal)
                        .Select(x => new
                        {
                            stage = x.Stage,
                            sourceKey = x.SourceKey,
                            linkInstanceId = x.LinkInstanceId,
                            linkInstanceUniqueId = x.LinkInstanceUniqueId,
                            elementId = x.ElementId,
                            category = x.Category,
                            mark = x.Mark,
                            reason = x.Reason
                        })
                        .ToArray(),
                    coverageLimitations = result.CoverageLimitations.ToArray()
                },
                snapshotHashSha256 = snapshotHash,
                files = new[]
                {
                    new { role = "handreach-summary", name = Path.GetFileName(summaryPath), sha256 = summaryHash, rowCount = summaryRows.Count },
                    new { role = "handreach-candidates", name = Path.GetFileName(candidatePath), sha256 = candidateHash, rowCount = candidateRows.Count }
                },
                manualDataPolicy = new
                {
                    preservedColumns = new[] { ManualConclusionColumn, ManualNoteColumn },
                    preservedManualRowCount = preservedManualRows,
                    resetStaleManualConclusionCount = resetStaleManualConclusions,
                    warning = manualConclusionWarning,
                    conclusionFreshness =
                        "same row key + evidence fingerprint + result fingerprint + decision reason"
                },
                legacyMigration = new
                {
                    status = legacyMigration.Status,
                    archiveDirectory = legacyMigration.ArchiveDirectory,
                    mappedManualRowCount = legacyMigration.MappedManualRowCount,
                    warning = legacyMigration.Warning,
                    ambiguousLegacyConclusionMigrated = false
                },
                candidateContract = new
                {
                    scope = "one representative per merged feasible region",
                    allPathsEnumerated = false,
                    windowLimitedSampling = result.WindowLimitedSampling
                },
                idempotency = "Full snapshot replacement by stable target/region row keys; manifest hashes are the commit check."
            };
            string manifestJson = JsonConvert.SerializeObject(manifest, Formatting.Indented) +
                                  Environment.NewLine;

            // Manifest is deliberately written last and acts as the commit marker.
            try
            {
                MaintenanceLedgerCsv.WriteAllTextAtomic(candidatePath, candidateCsv);
                MaintenanceLedgerCsv.WriteAllTextAtomic(summaryPath, summaryCsv);
                MaintenanceLedgerCsv.WriteAllTextAtomic(manifestPath, manifestJson);
            }
            catch (Exception exception)
            {
                throw new IOException(
                    "HandReach 台账多文件提交未完成；现有 manifest 可能仍对应旧 CSV。" +
                    "在逐文件核对 manifest 哈希前请勿使用本次快照。", exception);
            }

            return new MaintenanceHandReachLedgerExportResult
            {
                SummaryCsvPath = summaryPath,
                CandidateCsvPath = candidatePath,
                ManifestJsonPath = manifestPath,
                SnapshotHashSha256 = snapshotHash,
                SummaryRowCount = summaryRows.Count,
                CandidateRowCount = candidateRows.Count,
                PreservedManualRowCount = preservedManualRows,
                ResetStaleManualConclusionCount = resetStaleManualConclusions,
                ManualConclusionWarning = manualConclusionWarning,
                LegacyMigrationStatus = legacyMigration.Status,
                LegacyArchiveDirectory = legacyMigration.ArchiveDirectory,
                LegacyMappedManualRowCount = legacyMigration.MappedManualRowCount,
                LegacyMigrationWarning = legacyMigration.Warning
            };
        }

        private static List<IDictionary<string, string>> BuildSummaryRows(
            HandReachAnalysisResult result,
            IDictionary<string, MaintenanceHandReachManualState> manualStates,
            out int preservedManualRows,
            out int resetStaleManualConclusions)
        {
            preservedManualRows = 0;
            resetStaleManualConclusions = 0;
            string syncedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            double hatchSizeMm = result.Options == null
                ? MaintenanceHandReachOpeningPolicy.StandardOpeningSizeMm
                : result.Options.HatchSizeMm;
            string hatchSize = Math.Round(hatchSizeMm, 1)
                .ToString("0.#", CultureInfo.InvariantCulture);
            string openingLabel = hatchSize + "×" + hatchSize;
            var rows = new List<IDictionary<string, string>>();
            foreach (HandReachTargetResult target in result.TargetResults
                .Where(x => x != null && x.Target != null)
                .OrderBy(x => SafeGroup(result, x.Target), StringComparer.Ordinal)
                .ThenBy(x => x.Target.DeviceNo, StringComparer.Ordinal)
                .ThenBy(x => x.Target.TargetKey, StringComparer.Ordinal))
            {
                HandReachTargetInfo info = target.Target;
                HandReachRegion best = target.Regions.FirstOrDefault();
                HandReachSample sample = best == null ? null : best.Recommended;
                bool hasSelectedOpening = sample != null;
                string group = SafeGroup(result, info);
                HandReachOpeningPlaneKind openingPlane = !hasSelectedOpening
                    ? target.SelectedOpeningPlane
                    : sample.OpeningPlane;
                string rowKey = BuildSummaryRowKey(
                    group,
                    info.TargetKey,
                    openingPlane,
                    sample == null ? string.Empty : sample.SurfaceKey,
                    hasSelectedOpening);
                MaintenanceHandReachManualState manual;
                if (!manualStates.TryGetValue(rowKey, out manual))
                    manual = new MaintenanceHandReachManualState();
                bool hasManualConclusion = !string.IsNullOrWhiteSpace(manual.Conclusion);
                bool conclusionIsCurrent = hasManualConclusion &&
                    string.Equals(manual.EvidenceFingerprint, result.EvidenceFingerprint,
                        StringComparison.Ordinal) &&
                    string.Equals(manual.ResultFingerprint, result.ResultFingerprint,
                        StringComparison.Ordinal) &&
                    string.Equals(manual.DecisionReason, target.ConclusionReason,
                        StringComparison.Ordinal);
                string manualConclusion = conclusionIsCurrent
                    ? manual.Conclusion
                    : string.Empty;
                if (hasManualConclusion && !conclusionIsCurrent)
                    resetStaleManualConclusions++;
                if (!string.IsNullOrWhiteSpace(manualConclusion) ||
                    !string.IsNullOrWhiteSpace(manual.Note))
                    preservedManualRows++;

                rows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["行键"] = rowKey,
                    ["逻辑组"] = group,
                    ["设备编号"] = Safe(info.DeviceNo),
                    ["方案编号"] = SchemeLabel(info),
                    ["入口组"] = hasSelectedOpening
                        ? EntryGroup(info, openingPlane,
                            target.CeilingDirectReachApplied)
                        : "设备" + Safe(info.DeviceNo) + "-无可行检修入口",
                    ["维修对象"] = info.GetDisplayName(),
                    ["目标键"] = Safe(info.TargetKey),
                    ["链接实例ID"] = info.LinkInstanceId == 0 ? string.Empty : Number(info.LinkInstanceId),
                    ["设备图元ID"] = Number(info.ElementId),
                    ["分析类型"] = !hasSelectedOpening
                        ? "无可行" + openingLabel + "检修入口"
                        : (openingPlane == HandReachOpeningPlaneKind.SideWallVertical
                            ? openingLabel + "侧墙探身伸手检修"
                            : (target.CeilingDirectReachApplied
                                ? openingLabel + "天花直接伸手检修"
                                : openingLabel + "天花人员钻入检修")),
                    ["入口类型"] = hasSelectedOpening ? openingPlane.ToString() : string.Empty,
                    ["墙面键"] = sample == null ? string.Empty : Safe(sample.SurfaceKey),
                    ["候选区域数"] = Number(target.Regions.Count),
                    ["推荐口中心mm"] = sample == null ? string.Empty : Point(sample.CenterX, sample.CenterY, SampleCenterZ(sample, info)),
                    ["最近口边缘mm"] = sample == null ? string.Empty : Point(sample.EdgeX, sample.EdgeY, SampleEdgeZ(sample, info)),
                    ["伸手通道起点mm"] = sample == null ? string.Empty : Point(sample.ChannelStartX, sample.ChannelStartY, sample.ChannelStartZ),
                    ["水平距离mm"] = sample == null ? string.Empty : Decimal(sample.HorizontalMm),
                    ["斜向实际距离mm"] = sample == null ? string.Empty : Decimal(sample.ObliqueMm),
                    ["垂直高差mm"] = sample == null
                        ? Decimal(target.AnalysisServiceFaceProxyZ != 0.0
                            ? target.AnalysisVerticalDifferenceMm
                            : info.ServiceFaceProxyZ - info.CeilingTopMm)
                        : Decimal(sample.VerticalMm),
                    ["最大已验证通道直径mm"] = best == null ? string.Empty : Number(best.MaxTestedClearDiameterMm),
                    ["梯具状态"] = target.LadderStatus.ToString(),
                    ["推荐梯向"] = best == null ? string.Empty : Safe(best.RecommendedLadderDirection),
                    ["可行点数"] = Number(target.ClearCount),
                    ["连通性一致"] = target.ConnectivityAgreed ? "true" : "false",
                    ["豁免证据数"] = Number(target.ExemptEvidence.Count),
                    ["真实障碍数"] = Number(target.RealObstacles.Count),
                    ["关注级"] = target.AttentionLevel.ToString(),
                    ["Revit维修结论"] = Safe(target.Conclusion),
                    ["Revit判断说明"] = Safe(target.ConclusionReason),
                    [ManualConclusionColumn] = Safe(manualConclusion),
                    [ManualNoteColumn] = Safe(manual.Note),
                    ["分析ID"] = Safe(result.AnalysisId),
                    ["模型指纹"] = Safe(result.ModelFingerprint),
                    ["模型证据指纹"] = Safe(result.EvidenceFingerprint),
                    ["结果指纹"] = Safe(result.ResultFingerprint),
                    ["分析时间UTC"] = result.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
                    ["同步时间UTC"] = syncedAt
                });
            }
            return rows;
        }

        private static List<IDictionary<string, string>> BuildCandidateRows(
            HandReachAnalysisResult result)
        {
            var rows = new List<IDictionary<string, string>>();
            foreach (HandReachTargetResult target in result.TargetResults
                .Where(x => x != null && x.Target != null))
            {
                HandReachTargetInfo info = target.Target;
                string group = SafeGroup(result, info);
                foreach (HandReachRegion region in target.Regions
                    .Where(x => x != null && x.Recommended != null)
                    .OrderBy(x => x.RegionNo))
                {
                    HandReachSample sample = region.Recommended;
                    string corridor = string.Join(";", result.Options.CorridorTestDiametersMm
                        .Select((diameter, index) =>
                            Number((int)Math.Round(diameter)) + "=" +
                            (region.RecommendedCorridorClear != null &&
                             index < region.RecommendedCorridorClear.Length &&
                             region.RecommendedCorridorClear[index]
                                ? "true"
                                : "false")));
                    rows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                    ["候选行键"] = group + "|" + Safe(info.TargetKey) + "|" +
                        region.OpeningPlane + "|" + Safe(sample.SurfaceKey) +
                        "|R" + Number(region.RegionNo),
                        ["逻辑组"] = group,
                        ["设备编号"] = Safe(info.DeviceNo),
                        ["方案编号"] = SchemeLabel(info),
                        ["入口组"] = EntryGroup(info, region.OpeningPlane,
                            target.CeilingDirectReachApplied),
                        ["维修对象"] = info.GetDisplayName(),
                        ["目标键"] = Safe(info.TargetKey),
                        ["入口类型"] = region.OpeningPlane.ToString(),
                        ["墙面键"] = Safe(region.SurfaceKey),
                        ["区域编号"] = Number(region.RegionNo),
                        ["区域点数"] = Number(region.PointCount),
                        ["区域面积m2"] = region.AreaM2.ToString("0.000", CultureInfo.InvariantCulture),
                        ["区域范围mm"] = Decimal(region.MinX) + ";" + Decimal(region.MinY) + ";" + Decimal(region.MinZ) + " -> " + Decimal(region.MaxX) + ";" + Decimal(region.MaxY) + ";" + Decimal(region.MaxZ),
                        ["推荐口中心mm"] = Point(sample.CenterX, sample.CenterY, SampleCenterZ(sample, info)),
                        ["最近口边缘mm"] = Point(sample.EdgeX, sample.EdgeY, SampleEdgeZ(sample, info)),
                        ["伸手通道起点mm"] = Point(sample.ChannelStartX, sample.ChannelStartY, sample.ChannelStartZ),
                        ["检修面代理点mm"] = Point(
                            info.ServiceFaceProxyX,
                            info.ServiceFaceProxyY,
                            target.AnalysisServiceFaceProxyZ != 0.0
                                ? target.AnalysisServiceFaceProxyZ
                                : info.ServiceFaceProxyZ),
                        ["水平距离mm"] = Decimal(sample.HorizontalMm),
                        ["斜向实际距离mm"] = Decimal(sample.ObliqueMm),
                        ["垂直高差mm"] = Decimal(sample.VerticalMm),
                        ["距离等级"] = MaintenanceHandReachMath.GradeDistanceText(sample.DistanceGrade),
                        ["垂直等级"] = MaintenanceHandReachMath.GradeVerticalText(region.RecommendedVerticalGrade),
                        ["逐档通道结果"] = corridor,
                        ["最大已验证通道直径mm"] = Number(region.MaxTestedClearDiameterMm),
                        ["推荐梯向"] = Safe(region.RecommendedLadderDirection),
                        ["梯具操作区通过"] = region.RecommendedOperationZoneClear ? "true" : "false",
                        ["豁免相交数"] = Number(region.RecommendedExemptIntersectCount),
                        ["阻挡键"] = Safe(region.RecommendedBlockerKey),
                        ["关注级"] = target.AttentionLevel.ToString(),
                        ["Revit维修结论"] = Safe(target.Conclusion),
                        ["分析ID"] = Safe(result.AnalysisId),
                        ["模型证据指纹"] = Safe(result.EvidenceFingerprint),
                        ["结果指纹"] = Safe(result.ResultFingerprint)
                    });
                }
            }
            return rows
                .OrderBy(x => x["候选行键"], StringComparer.Ordinal)
                .ToList();
        }

        private static MaintenanceHandReachLegacyMigrationResult ReadManualStates(
            string summaryPath,
            string candidatePath,
            string manifestPath,
            HandReachAnalysisResult result,
            string legacyArchiveRoot)
        {
            var loaded = new MaintenanceHandReachLegacyMigrationResult();
            if (!File.Exists(summaryPath)) return loaded;

            string csv = MaintenanceLedgerCsv.ReadAllTextShared(summaryPath);
            List<string> headers;
            try { headers = MaintenanceLedgerCsv.ParseHeaders(csv); }
            catch (Exception)
            {
                if (LooksLikeModernManualLedger(csv))
                    throw new InvalidDataException(
                        "现有 HandReach 现代版汇总无法解析；可能含人工数据，已拒绝覆盖。请先修复或人工归档。"
                    );
                return MaintenanceHandReachLegacyMigrationService.ArchiveAndMigrate(
                    summaryPath,
                    candidatePath,
                    manifestPath,
                    result,
                    legacyArchiveRoot);
            }

            bool modern = headers.Contains("行键", StringComparer.Ordinal) &&
                          headers.Contains(ManualConclusionColumn, StringComparer.Ordinal) &&
                          headers.Contains(ManualNoteColumn, StringComparer.Ordinal);
            if (!modern)
                return MaintenanceHandReachLegacyMigrationService.ArchiveAndMigrate(
                    summaryPath,
                    candidatePath,
                    manifestPath,
                    result,
                    legacyArchiveRoot);

            List<Dictionary<string, string>> rows = MaintenanceLedgerCsv.Parse(csv);
            foreach (Dictionary<string, string> row in rows)
            {
                string key = Safe(row["行键"]);
                var value = new MaintenanceHandReachManualState
                {
                    Conclusion = Safe(row[ManualConclusionColumn]),
                    Note = Safe(row[ManualNoteColumn]),
                    EvidenceFingerprint = ReadRowValue(row, "模型证据指纹"),
                    ResultFingerprint = ReadRowValue(row, "结果指纹"),
                    DecisionReason = ReadRowValue(row, "Revit判断说明")
                };
                if (string.IsNullOrWhiteSpace(key))
                {
                    if (!string.IsNullOrWhiteSpace(value.Conclusion) ||
                        !string.IsNullOrWhiteSpace(value.Note))
                        throw new InvalidDataException(
                            "现有 HandReach 汇总含无行键的人工确认/备注；已拒绝覆盖。"
                        );
                    continue;
                }
                MaintenanceHandReachManualState existing;
                if (!loaded.ManualStates.TryGetValue(key, out existing))
                    loaded.ManualStates[key] = value;
                else if (!string.Equals(existing.Conclusion, value.Conclusion,
                             StringComparison.Ordinal) ||
                          !string.Equals(existing.Note, value.Note,
                             StringComparison.Ordinal) ||
                         !string.Equals(existing.EvidenceFingerprint,
                             value.EvidenceFingerprint, StringComparison.Ordinal) ||
                         !string.Equals(existing.ResultFingerprint,
                             value.ResultFingerprint, StringComparison.Ordinal) ||
                         !string.Equals(existing.DecisionReason,
                             value.DecisionReason, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "现有 HandReach 汇总存在重复且冲突的人工行键：" + key);
            }
            return loaded;
        }

        private static string ReadRowValue(
            IDictionary<string, string> row,
            string column)
        {
            if (row == null || string.IsNullOrWhiteSpace(column)) return string.Empty;
            string value;
            return row.TryGetValue(column, out value) ? Safe(value) : string.Empty;
        }

        private static bool LooksLikeModernManualLedger(string csv)
        {
            string firstLine = (csv ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;
            return firstLine.IndexOf("行键", StringComparison.Ordinal) >= 0 ||
                   firstLine.IndexOf(ManualConclusionColumn,
                       StringComparison.Ordinal) >= 0 ||
                   firstLine.IndexOf(ManualNoteColumn,
                       StringComparison.Ordinal) >= 0;
        }

        private static string SafeGroup(HandReachAnalysisResult result, HandReachTargetInfo target)
        {
            return !string.IsNullOrWhiteSpace(target.GroupKey)
                ? target.GroupKey.Trim()
                : Safe(result.GroupKey);
        }

        private static string Point(double x, double y, double z)
        {
            return Decimal(x) + ";" + Decimal(y) + ";" + Decimal(z);
        }

        private static string Decimal(double value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static string Number(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string SchemeLabel(HandReachTargetInfo target)
        {
            return target != null && target.SchemeNo > 0
                ? "方案" + target.SchemeNo.ToString("00", CultureInfo.InvariantCulture)
                : "未分配";
        }

        private static string EntryGroup(
            HandReachTargetInfo target,
            HandReachOpeningPlaneKind openingPlane,
            bool ceilingDirectReach)
        {
            if (target == null) return string.Empty;
            return "设备" + Safe(target.DeviceNo) + "-" + SchemeLabel(target) + "-" +
                   (openingPlane == HandReachOpeningPlaneKind.SideWallVertical
                       ? "侧墙伸手检修"
                       : (ceilingDirectReach
                           ? "天花直接伸手检修"
                           : "天花钻入检修"));
        }

        private static string BuildSummaryRowKey(
            string group,
            string targetKey,
            HandReachOpeningPlaneKind openingPlane,
            string surfaceKey,
            bool hasSelectedOpening)
        {
            string legacyStable = Safe(group) + "|" + Safe(targetKey) + "|HandReach";
            if (!hasSelectedOpening) return legacyStable + "|None";
            if (openingPlane == HandReachOpeningPlaneKind.CeilingHorizontal)
                return legacyStable;
            return legacyStable + "|SideWall|" +
                   MaintenanceLedgerCsv.Sha256Hex(Safe(surfaceKey)).Substring(0, 16);
        }

        private static double SampleCenterZ(
            HandReachSample sample,
            HandReachTargetInfo target)
        {
            if (sample != null && (sample.OpeningPlane == HandReachOpeningPlaneKind.SideWallVertical ||
                                   sample.CenterZ != 0.0)) return sample.CenterZ;
            return target == null ? 0.0 : target.CeilingTopMm;
        }

        private static double SampleEdgeZ(
            HandReachSample sample,
            HandReachTargetInfo target)
        {
            if (sample != null && (sample.OpeningPlane == HandReachOpeningPlaneKind.SideWallVertical ||
                                   sample.EdgeZ != 0.0)) return sample.EdgeZ;
            return target == null ? 0.0 : target.CeilingTopMm;
        }

        private static string Safe(string value)
        {
            return value ?? string.Empty;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JarviTools.Commands.MaintenanceReachability;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    // HandReach 快照存储：独立于旧 MaintenanceAnalysisStore，不干扰旧 route-candidate 契约。
    internal static class MaintenanceHandReachStore
    {
        private static readonly object Gate = new object();
        private static HandReachAnalysisResult _result;
        private static string _documentKey;
        private static string _approvedAnalysisId;
        private static string _approvalToken;
        private static string _reviewer;
        private static string _reviewNote;
        private static string _approvedAtUtc;
        private static bool _approvalConsumed;

        internal static void Set(Document document, HandReachAnalysisResult result)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (result == null) throw new ArgumentNullException("result");
            lock (Gate)
            {
                _documentKey = BuildDocumentKey(document);
                _result = result;
                _approvedAnalysisId = null;
                _approvalToken = null;
                _reviewer = null;
                _reviewNote = null;
                _approvedAtUtc = null;
                _approvalConsumed = false;
            }
        }

        internal static HandReachAnalysisResult Get(Document document)
        {
            lock (Gate)
                return document != null && string.Equals(_documentKey, BuildDocumentKey(document), StringComparison.Ordinal)
                    ? _result
                    : null;
        }

        internal static void Clear(Document document)
        {
            lock (Gate)
            {
                if (document != null && !string.Equals(_documentKey, BuildDocumentKey(document), StringComparison.Ordinal)) return;
                _result = null;
                _documentKey = null;
                _approvedAnalysisId = null;
                _approvalToken = null;
                _reviewer = null;
                _reviewNote = null;
                _approvedAtUtc = null;
                _approvalConsumed = false;
            }
        }

        internal static string Approve(
            Document document,
            HandReachAnalysisResult result,
            string reviewer,
            string reviewNote)
        {
            if (document == null || result == null) throw new ArgumentNullException("result");
            RequireFormalCoverage(result);
            lock (Gate)
            {
                if (_result != result ||
                    !string.Equals(_documentKey, BuildDocumentKey(document), StringComparison.Ordinal))
                    throw new InvalidOperationException("The HandReach candidate snapshot is no longer current.");
                ValidateEvidenceSnapshot(document, result);

                _approvedAnalysisId = result.AnalysisId;
                _approvalToken = Guid.NewGuid().ToString("N");
                _reviewer = reviewer;
                _reviewNote = reviewNote;
                _approvedAtUtc = DateTime.UtcNow.ToString("o");
                _approvalConsumed = false;
                return _approvalToken;
            }
        }

        internal static void RequireApproval(
            Document document,
            HandReachAnalysisResult result,
            string approvalToken)
        {
            RequireFormalCoverage(result);
            lock (Gate)
            {
                ValidateEvidenceSnapshot(document, result);
                if (_result != result ||
                    !string.Equals(_documentKey, BuildDocumentKey(document), StringComparison.Ordinal) ||
                    !string.Equals(_approvedAnalysisId, result.AnalysisId, StringComparison.Ordinal) ||
                    _approvalConsumed ||
                    string.IsNullOrWhiteSpace(approvalToken) ||
                    !string.Equals(_approvalToken, approvalToken, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "This HandReach snapshot has not been explicitly reviewed and approved. Call approve_maintenance_hand_reach first.");
            }
        }

        internal static void ConsumeApproval(
            Document document,
            HandReachAnalysisResult result)
        {
            lock (Gate)
            {
                if (_result != result ||
                    document == null ||
                    !string.Equals(_documentKey, BuildDocumentKey(document), StringComparison.Ordinal) ||
                    !string.Equals(_approvedAnalysisId, result.AnalysisId, StringComparison.Ordinal))
                    throw new InvalidOperationException("The HandReach approval snapshot is no longer current.");
                _approvalConsumed = true;
                _approvalToken = null;
            }
        }

        internal static JObject GetReviewStatus(Document document, HandReachAnalysisResult result)
        {
            lock (Gate)
            {
                bool approved = _result == result &&
                    string.Equals(_documentKey, BuildDocumentKey(document), StringComparison.Ordinal) &&
                    string.Equals(_approvedAnalysisId, result.AnalysisId, StringComparison.Ordinal) &&
                    (!_approvalConsumed && !string.IsNullOrWhiteSpace(_approvalToken));
                bool visualized = _result == result &&
                    string.Equals(_approvedAnalysisId, result.AnalysisId, StringComparison.Ordinal) &&
                    _approvalConsumed;
                bool evidenceCurrent = false;
                try
                {
                    evidenceCurrent = string.Equals(
                        result.EvidenceFingerprint,
                        MaintenanceEvidenceSnapshotService.Compute(
                            document,
                            result.EvidenceSources,
                            result.ExemptPipeEvidence,
                            result.LinkScope),
                        StringComparison.OrdinalIgnoreCase);
                }
                catch { }
                return new JObject
                {
                    ["status"] = !evidenceCurrent
                        ? "stale_model_evidence_reanalysis_required"
                        : (visualized
                            ? "visualized_from_single_use_approval"
                            : (approved ? "approved_candidate_snapshot" : "pending_ai_and_professional_review")),
                    ["reviewer"] = approved || visualized ? _reviewer : null,
                    ["reviewNote"] = approved || visualized ? _reviewNote : null,
                    ["evidenceCurrent"] = evidenceCurrent,
                    ["evidenceFingerprint"] = result.EvidenceFingerprint
                };
            }
        }

        private static void ValidateEvidenceSnapshot(
            Document document,
            HandReachAnalysisResult result)
        {
            if (document == null || result == null)
                throw new InvalidOperationException("The HandReach candidate snapshot is unavailable.");
            string currentModel = MaintenanceLedgerSyncService.GetModelFingerprint(document);
            string currentEvidence = MaintenanceEvidenceSnapshotService.Compute(
                document,
                result.EvidenceSources,
                result.ExemptPipeEvidence,
                result.LinkScope);
            if (!string.Equals(result.ModelFingerprint, currentModel, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(result.EvidenceFingerprint, currentEvidence, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Relevant Revit elements changed after analysis. Re-run analyze_maintenance_hand_reach_candidates before approval or visualization.");
            }
        }

        private static void RequireFormalCoverage(HandReachAnalysisResult result)
        {
            if (result == null || !MaintenanceHandReachMath.IsFormalSnapshotApprovable(
                result.CoverageComplete,
                result.TargetResults
                    .Where(x => x != null && x.Regions != null && x.Regions.Count > 0)
                    .Select(x => x.SelectedCandidateAuditComplete)))
                throw new InvalidOperationException(
                    "HandReach evidence collection or the selected candidate audit is incomplete. Resolve selected-scheme evidence failures before formal approval; unrelated rejected or unverified alternatives do not block an otherwise verified selected scheme.");
        }

        internal static string GetApprovalReviewer()
        {
            lock (Gate) { return _reviewer; }
        }

        internal static string GetApprovalNote()
        {
            lock (Gate) { return _reviewNote; }
        }

        internal static string GetApprovedAtUtc()
        {
            lock (Gate) { return _approvedAtUtc; }
        }

        private static string BuildDocumentKey(Document document)
        {
            string projectInfo = string.Empty;
            try { projectInfo = document.ProjectInformation == null ? string.Empty : document.ProjectInformation.UniqueId; }
            catch { }
            return (document.PathName ?? string.Empty) + "|" + (document.Title ?? string.Empty) + "|" + projectInfo;
        }
    }

    public sealed class AnalyzeMaintenanceHandReachCandidatesTool : IRevitTool
    {
        public string Name => "analyze_maintenance_hand_reach_candidates";

        public string Description =>
            "对天花分组执行检修口分析：默认沿天花真实顶面边界搜索450×450侧墙探身伸手口；显式SideWallOnly可用400×400作为现场缩小备选。两种侧墙口都验证方口、200mm伸手通道和梯具，不按人员穿门。侧墙伸手不成立后检查600×600爬入式侧墙检修门，最后以openingPreference=CeilingOnly搜索固定450×450天花口。默认只生成数据和台账，不写模型、不建视图。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["ceilingElementIds"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "integer" },
                    ["description"] = "宿主模型天花 ElementId；注释相同者按一个逻辑组。"
                },
                ["relevantLinkInstanceIds"] =
                    AnalyzeMaintenanceReachabilityTool.RelevantLinkScopeSchema(),
                ["deviceRefs"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["linkInstanceId"] = new JObject { ["type"] = "integer", ["description"] = "宿主模型中的链接实例 ElementId。" },
                            ["elementId"] = new JObject { ["type"] = "integer", ["description"] = "链接文档内的机械设备 ElementId。" }
                        },
                        ["required"] = new JArray { "linkInstanceId", "elementId" },
                        ["additionalProperties"] = false
                    },
                    ["description"] = "可选：显式指定设备。未填时自动发现分组范围内的机械设备。"
                },
                ["hatchSizeMm"] = new JObject { ["type"] = "number", ["enum"] = new JArray { 400, 450 }, ["default"] = 450, ["description"] = "默认450×450 mm。400×400只允许与openingPreference=SideWallOnly一起使用，作为明确指定的侧墙缩小备选；天花仍固定450×450。" },
                ["openingPreference"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray
                    {
                        "AutoPreferSideWall", "SideWallOnly", "CeilingOnly"
                    },
                    ["default"] = "SideWallOnly",
                    ["description"] = "本次方口搜索范围。默认沿天花边界生成虚拟侧墙；400口必须显式SideWallOnly。侧墙600×600爬入门检查完成后，显式传CeilingOnly才搜索天花450口。"
                },
                ["strictCeilingSelection"] = new JObject
                {
                    ["type"] = "boolean",
                    ["default"] = false,
                    ["description"] = "true 时只分析 ceilingElementIds 明确列出的天花，不自动并入其他同注释天花；结果仍保留原注释作为分组名。"
                },
                ["allowSideWallDistanceOver500Review"] = new JObject
                {
                    ["type"] = "boolean",
                    ["default"] = false,
                    ["description"] = "仅供显式人工复核：SideWallOnly时允许实际伸手距离500~600mm的候选继续完成当前400/450侧墙口、200通道和梯具检查；结果只能为橙色待复核，正式500mm上限不变。"
                },
                ["gridSpacingMm"] = new JObject { ["type"] = "number", ["minimum"] = 10, ["maximum"] = 200, ["default"] = 40, ["description"] = "检修口中心网格步长，默认40 mm（41×41窗口）。" },
                ["corridorDiametersMm"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "number", ["minimum"] = 200, ["maximum"] = 600 },
                    ["default"] = new JArray { 200, 250, 300, 350, 400 },
                    ["description"] = "逐档测试的伸手通道直径，默认200/250/300/350/400 mm。"
                },
                ["createViews"] = new JObject
                {
                    ["type"] = "boolean",
                    ["default"] = false,
                    ["description"] = "保留字段：HandReach 分析始终 data-only。视图须 approve 后调用 show_maintenance_hand_reach 显式生成。"
                }
            },
            ["required"] = new JArray { "ceilingElementIds" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            UIDocument uidoc = AnalyzeMaintenanceReachabilityTool.RequireUiDocument(uiapp);
            ICollection<ElementId> ids = AnalyzeMaintenanceReachabilityTool.ReadCeilingIds(input);
            if (ids.Count == 0) ids = uidoc.Selection.GetElementIds();

            if (input != null && ((bool?)input["createViews"]).GetValueOrDefault())
                throw new InvalidOperationException(
                    "HandReach 分析始终 data-only。请先审阅候选证据，调用 approve_maintenance_hand_reach，再调用 show_maintenance_hand_reach 按需生成视图。");

            var options = new HandReachOptions();
            options.StrictCeilingSelection = input != null &&
                ((bool?)input["strictCeilingSelection"]).GetValueOrDefault();
            options.AllowSideWallDistanceOver500Review = input != null &&
                ((bool?)input["allowSideWallDistanceOver500Review"]).GetValueOrDefault();
            if (input != null && input["openingPreference"] != null)
            {
                HandReachOpeningPreference preference;
                if (!Enum.TryParse(
                    (string)input["openingPreference"],
                    true,
                    out preference))
                    throw new ArgumentException(
                        "openingPreference must be AutoPreferSideWall, SideWallOnly, or CeilingOnly.");
                options.OpeningPreference = preference;
            }
            options.RelevantLinkInstanceIds =
                AnalyzeMaintenanceReachabilityTool.ReadRelevantLinkInstanceIds(input);
            double hatchSizeMm = input != null && input["hatchSizeMm"] != null
                ? (double)input["hatchSizeMm"]
                : MaintenanceHandReachOpeningPolicy.StandardOpeningSizeMm;
            bool standard450 = Math.Abs(hatchSizeMm -
                MaintenanceHandReachOpeningPolicy.StandardOpeningSizeMm) <= 1e-6;
            bool reduced400 = Math.Abs(hatchSizeMm -
                MaintenanceHandReachOpeningPolicy.ReducedSideWallOpeningSizeMm) <= 1e-6;
            if (!standard450 && !reduced400)
                throw new ArgumentException("hatchSizeMm只允许400或450。");
            options.HatchSizeMm = hatchSizeMm;
            if (input != null && input["gridSpacingMm"] != null) options.GridSpacingMm = (double)input["gridSpacingMm"];
            if (input != null && input["corridorDiametersMm"] != null)
            {
                var diameters = ((JArray)input["corridorDiametersMm"])
                    .Select(x => (double)x)
                    .OrderBy(x => x)
                    .ToArray();
                if (diameters.Length == 0)
                    throw new ArgumentException("corridorDiametersMm 不能为空。");
                if (!diameters.Any(x => Math.Abs(x - 200.0) <= 1e-6))
                    throw new ArgumentException("正式 HandReach 契约必须包含 200 mm 默认伸手通道验证。");
                options.CorridorTestDiametersMm = diameters;
                options.DefaultCorridorDiameterMm = 200.0;
            }

            List<HandReachDeviceInput> deviceRefs = null;
            if (input != null && input["deviceRefs"] != null)
            {
                deviceRefs = new List<HandReachDeviceInput>();
                foreach (JToken token in (JArray)input["deviceRefs"])
                {
                    deviceRefs.Add(new HandReachDeviceInput(
                        (long)token["linkInstanceId"],
                        (long)token["elementId"]));
                }
            }

            HandReachAnalysisResult result = MaintenanceHandReachAnalysisService.Analyze(
                uidoc.Document, ids, options, deviceRefs);
            MaintenanceHandReachVisualizationService.ResolveSchemeAssignments(
                uidoc.Document,
                result);
            result.ResultFingerprint =
                MaintenanceHandReachAnalysisService.ComputeFingerprintForReview(result);
            result.EvidenceFingerprint = MaintenanceEvidenceSnapshotService.Compute(
                uidoc.Document,
                result.EvidenceSources,
                result.ExemptPipeEvidence,
                result.LinkScope);

            JObject ledgerStatus;
            MaintenanceLedgerDestination destination;
            if (MaintenanceLedgerConfigurationService.TryResolve(
                uidoc.Document,
                out destination))
            {
                try
                {
                    MaintenanceHandReachLedgerExportResult ledger =
                        MaintenanceHandReachLedgerService.Export(result, destination);
                    if (!string.IsNullOrWhiteSpace(ledger.LegacyMigrationWarning))
                        result.Warnings.Add(ledger.LegacyMigrationWarning);
                    ledgerStatus = new JObject
                    {
                        ["status"] = "written",
                        ["summaryCsvPath"] = ledger.SummaryCsvPath,
                        ["candidateCsvPath"] = ledger.CandidateCsvPath,
                        ["manifestJsonPath"] = ledger.ManifestJsonPath,
                        ["snapshotHashSha256"] = ledger.SnapshotHashSha256,
                        ["summaryRowCount"] = ledger.SummaryRowCount,
                        ["candidateRowCount"] = ledger.CandidateRowCount,
                        ["preservedManualRowCount"] = ledger.PreservedManualRowCount,
                        ["resetStaleManualConclusionCount"] =
                            ledger.ResetStaleManualConclusionCount,
                        ["manualConclusionWarning"] = ledger.ManualConclusionWarning,
                        ["legacyMigrationStatus"] = ledger.LegacyMigrationStatus,
                        ["legacyArchiveDirectory"] =
                            string.IsNullOrWhiteSpace(ledger.LegacyArchiveDirectory)
                                ? JValue.CreateNull()
                                : (JToken)ledger.LegacyArchiveDirectory,
                        ["legacyMappedManualRowCount"] =
                            ledger.LegacyMappedManualRowCount,
                        ["legacyMigrationWarning"] = ledger.LegacyMigrationWarning
                    };
                }
                catch (Exception exception)
                {
                    result.Warnings.Add(
                        "HandReach 分析已完成，但自动台账提交失败：" +
                        exception.Message +
                        " 若写入已开始，manifest 与 CSV 哈希可能不一致；该快照不可使用。 ");
                    result.ResultFingerprint =
                        MaintenanceHandReachAnalysisService.ComputeFingerprintForReview(result);
                    ledgerStatus = new JObject
                    {
                        ["status"] = "write_failed_snapshot_unusable_verify_manifest_hashes",
                        ["filesKnownConsistent"] = false,
                        ["error"] = exception.Message,
                        ["requiredAction"] =
                            "重新导出并逐文件核对 manifest SHA-256；核对前不要使用当前台账快照。"
                    };
                }
            }
            else
            {
                ledgerStatus = new JObject
                {
                    ["status"] = "destination_not_configured",
                    ["nextTool"] = "sync_maintenance_ledger_bridge",
                    ["note"] = "首次调用同步桥接并指定目录后，后续 HandReach 分析会自动刷新带哈希的台账。"
                };
            }
            MaintenanceHandReachStore.Set(uidoc.Document, result);
            JObject summary = MaintenanceHandReachJson.BuildSummary(result);
            summary["visualized"] = false;
            summary["ledger"] = ledgerStatus;
            summary["review"] = MaintenanceHandReachStore.GetReviewStatus(uidoc.Document, result);
            return summary;
        }
    }

    public sealed class GetMaintenanceHandReachSummaryTool : IRevitTool
    {
        public string Name => "get_maintenance_hand_reach_summary";

        public string Description =>
            "返回最近一次 HandReach 分析的分组与设备汇总：方案数、区域数、推荐候选、实际距离、垂直高差、最大通道、梯具、豁免、关注级与结论。";

        public JObject InputSchema => GetMaintenanceReachabilitySummaryTool.AnalysisIdSchema();

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            Document doc = AnalyzeMaintenanceReachabilityTool.RequireUiDocument(uiapp).Document;
            HandReachAnalysisResult result = RequireResult(doc, input);
            JObject summary = MaintenanceHandReachJson.BuildSummary(result);
            summary["review"] = MaintenanceHandReachStore.GetReviewStatus(doc, result);
            return summary;
        }

        internal static HandReachAnalysisResult RequireResult(Document doc, JObject input)
        {
            HandReachAnalysisResult result = MaintenanceHandReachStore.Get(doc);
            if (result == null) throw new InvalidOperationException("当前文档没有内存中的 HandReach 快照。");
            string expected = input == null ? null : (string)input["analysisId"];
            if (!string.IsNullOrWhiteSpace(expected) && !string.Equals(expected, result.AnalysisId, StringComparison.Ordinal))
                throw new InvalidOperationException("analysisId 已变化，请重新读取摘要。");
            return result;
        }
    }

    public sealed class GetMaintenanceHandReachCandidatesTool : IRevitTool
    {
        public string Name => "get_maintenance_hand_reach_candidates";

        public string Description =>
            "分页读取400/450侧墙或450天花检修口分析的可选区域与代表候选（一个连续区域一行），包含实际距离、整体高差、逐档通道、梯具、豁免证据与关注级。机器层全量计数留在汇总；不把40mm网格点铺给用户。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["analysisId"] = new JObject { ["type"] = "string" },
                ["targetKey"] = new JObject { ["type"] = "string", ["description"] = "可选：按设备过滤。" },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 50, ["default"] = 20 },
                ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            Document doc = AnalyzeMaintenanceReachabilityTool.RequireUiDocument(uiapp).Document;
            HandReachAnalysisResult result = GetMaintenanceHandReachSummaryTool.RequireResult(doc, input);
            int limit = input == null || input["limit"] == null ? 20 : (int)input["limit"];
            int offset = input == null || input["offset"] == null ? 0 : (int)input["offset"];
            string targetKey = input == null ? null : (string)input["targetKey"];
            JObject page = MaintenanceHandReachJson.BuildPage(result, targetKey, limit, offset);
            page["review"] = MaintenanceHandReachStore.GetReviewStatus(doc, result);
            return page;
        }
    }

    public sealed class ApproveMaintenanceHandReachTool : IRevitTool
    {
        public string Name => "approve_maintenance_hand_reach";

        public string Description =>
            "Explicitly record that the current HandReach snapshot has been reviewed by the named AI/reviewer. This does not certify code compliance; it only unlocks visualization of the unchanged snapshot.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["analysisId"] = new JObject { ["type"] = "string" },
                ["reviewer"] = new JObject { ["type"] = "string", ["description"] = "Reviewer identity, for example DeepSeek v4 Pro (max)." },
                ["reviewNote"] = new JObject { ["type"] = "string", ["description"] = "Concise evidence-based review note; at least 20 characters." }
            },
            ["required"] = new JArray { "analysisId", "reviewer", "reviewNote" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            Document doc = AnalyzeMaintenanceReachabilityTool.RequireUiDocument(uiapp).Document;
            HandReachAnalysisResult result = GetMaintenanceHandReachSummaryTool.RequireResult(doc, input);
            string reviewer = input == null ? null : (string)input["reviewer"];
            string note = input == null ? null : (string)input["reviewNote"];
            if (string.IsNullOrWhiteSpace(reviewer))
                throw new ArgumentException("'reviewer' is required.");
            if (string.IsNullOrWhiteSpace(note) || note.Trim().Length < 20)
                throw new ArgumentException("'reviewNote' must contain at least 20 characters of review evidence.");

            string token = MaintenanceHandReachStore.Approve(doc, result, reviewer.Trim(), note.Trim());
            return new JObject
            {
                ["analysisId"] = result.AnalysisId,
                ["approvalToken"] = token,
                ["modelFingerprint"] = result.ModelFingerprint,
                ["evidenceFingerprint"] = result.EvidenceFingerprint,
                ["status"] = "approved_candidate_snapshot",
                ["disclaimer"] = "Approval unlocks visualization only; it is not a code-compliance or construction certification."
            };
        }
    }

    internal static class MaintenanceHandReachJson
    {
        internal static JObject BuildSummary(HandReachAnalysisResult result)
        {
            var targets = new JArray();
            foreach (HandReachTargetResult item in result.TargetResults)
            {
                HandReachTargetInfo info = item.Target;
                HandReachRegion best = item.Regions.FirstOrDefault();
                bool hasSelectedOpening = item.HasSelectedOpening && best != null;
                var summary = new JObject
                {
                    ["group"] = string.IsNullOrWhiteSpace(info.GroupKey) ? result.GroupKey : info.GroupKey,
                    ["deviceNo"] = info.DeviceNo,
                    ["schemeNo"] = info.SchemeNo,
                    ["legacySchemeNos"] = new JArray(info.LegacySchemeNos),
                    ["targetKey"] = info.TargetKey,
                    ["device"] = info.GetDisplayName(),
                    ["linkInstanceId"] = info.LinkInstanceId != 0 ? new JValue(info.LinkInstanceId) : JValue.CreateNull(),
                    ["elementId"] = info.ElementId,
                    ["operationPointStatus"] = info.OperationPointStatus.ToString(),
                    ["operationPointNote"] = info.OperationPointNote,
                    ["selectedOpeningPlane"] = hasSelectedOpening
                        ? (JToken)new JValue(item.SelectedOpeningPlane.ToString())
                        : JValue.CreateNull(),
                    ["serviceFaceProxyMm"] = new JObject
                    {
                        ["x"] = Math.Round(info.ServiceFaceProxyX, 1),
                        ["y"] = Math.Round(info.ServiceFaceProxyY, 1),
                        ["z"] = Math.Round(info.ServiceFaceProxyZ, 1)
                    },
                    ["analysisServiceFaceProxyMm"] = new JObject
                    {
                        ["x"] = Math.Round(info.ServiceFaceProxyX, 1),
                        ["y"] = Math.Round(info.ServiceFaceProxyY, 1),
                        ["z"] = Math.Round(item.AnalysisServiceFaceProxyZ, 1)
                    },
                    ["ceilingPersonnelEntry"] = new JObject
                    {
                        ["applied"] = item.CeilingPersonnelEntryApplied,
                        ["modelVerticalDifferenceMm"] =
                            Math.Round(item.ModelVerticalDifferenceMm, 1),
                        ["deviceMoved"] = false,
                        ["modelDeviceBoundsMm"] = new JObject
                        {
                            ["min"] = new JArray(
                                Math.Round(item.ModelDeviceMinX, 1),
                                Math.Round(item.ModelDeviceMinY, 1),
                                Math.Round(item.ModelDeviceMinZ, 1)),
                            ["max"] = new JArray(
                                Math.Round(item.ModelDeviceMaxX, 1),
                                Math.Round(item.ModelDeviceMaxY, 1),
                                Math.Round(item.ModelDeviceMaxZ, 1))
                        }
                    },
                    ["ceilingDirectReach"] = new JObject
                    {
                        ["applied"] = item.CeilingDirectReachApplied,
                        ["openingRoomSideZMm"] = item.CeilingDirectReachApplied
                            ? (JToken)Math.Round(
                                info.CeilingTopMm - result.Options.OpeningHeightMm, 1)
                            : JValue.CreateNull(),
                        ["modelOverlapMm"] = Math.Round(
                            Math.Max(0.0, -item.ModelVerticalDifferenceMm), 1)
                    },
                    ["supplyDirection"] = new JArray(
                        Math.Round(info.SupplyDirectionX, 4), Math.Round(info.SupplyDirectionY, 4)),
                    ["serviceDirection"] = new JArray(
                        Math.Round(info.ServiceDirectionX, 4), Math.Round(info.ServiceDirectionY, 4)),
                    ["regionCount"] = item.Regions.Count,
                    ["candidateAuditComplete"] = item.CandidateAuditComplete,
                    ["selectedCandidateAuditComplete"] =
                        item.SelectedCandidateAuditComplete,
                    ["connectivityAgreed"] = item.ConnectivityAgreed,
                    ["ladderStatus"] = item.LadderStatus.ToString(),
                    ["ladderFloorMm"] = item.LadderFloorMm > 0 ? (JToken)Math.Round(item.LadderFloorMm, 1) : JValue.CreateNull(),
                    ["ladderTopMm"] = item.LadderTopMm > 0 ? (JToken)Math.Round(item.LadderTopMm, 1) : JValue.CreateNull(),
                    ["counts"] = new JObject
                    {
                        ["rawSamples"] = item.RawSampleCount,
                        ["openingFullyContained"] = item.HatchInsideCount,
                        ["hatchInsideCeiling"] = hasSelectedOpening &&
                            item.SelectedOpeningPlane == HandReachOpeningPlaneKind.CeilingHorizontal
                                ? item.HatchInsideCount
                                : 0,
                        ["verticalRejectedOver500"] = item.VerticalFailCount,
                        ["distanceLe500"] = item.DistanceOkCount,
                        ["openingRejected"] = item.OpeningFailCount,
                        ["corridorRejected"] = item.CorridorFailCount,
                        ["ladderRejected"] = item.LadderFailCount,
                        ["clearFullChain"] = item.ClearCount
                    },
                    ["sideWallSearch"] = new JObject
                    {
                        ["attempted"] = item.SideWallAttempted,
                        ["rawSamples"] = item.SideWallRawSampleCount,
                        ["fullFaceFit"] = item.SideWallFaceFitCount,
                        ["distanceLe500"] = item.SideWallDistanceOkCount,
                        ["openingRejected"] = item.SideWallOpeningFailCount,
                        ["corridorRejected"] = item.SideWallCorridorFailCount,
                        ["ladderRejected"] = item.SideWallLadderFailCount,
                        ["clearFullChain"] = item.SideWallClearCount
                    },
                    ["obstacleSolidCount"] = item.ObstacleSolidCount,
                    ["exemptSolidCount"] = item.ExemptSolidCount,
                    ["exemptEvidence"] = new JArray(item.ExemptEvidence.Select(x => new JObject
                    {
                        ["key"] = x.Key, ["uniqueId"] = x.UniqueId, ["category"] = x.Category,
                        ["name"] = x.Name, ["systemType"] = x.SystemType, ["relation"] = x.Relation
                    })),
                    ["realObstacles"] = new JArray(item.RealObstacles.Select(x => new JObject
                    {
                        ["key"] = x.Key, ["uniqueId"] = x.UniqueId, ["category"] = x.Category,
                        ["name"] = x.Name, ["systemType"] = x.SystemType, ["relation"] = x.Relation
                    })),
                    ["attentionLevel"] = item.AttentionLevel.ToString(),
                    ["conclusion"] = item.Conclusion,
                    ["conclusionReason"] = item.ConclusionReason,
                    ["recommended"] = best == null ? null : BuildRegionJson(result, item, best, false)
                };
                targets.Add(summary);
            }
            var groups = result.TargetResults
                .Where(x => x != null && x.Target != null)
                .GroupBy(
                    x => string.IsNullOrWhiteSpace(x.Target.GroupKey)
                        ? result.GroupKey
                        : x.Target.GroupKey,
                    StringComparer.Ordinal)
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new JObject
                {
                    ["group"] = x.Key,
                    ["ceilingTopMm"] = Math.Round(x.First().Target.CeilingTopMm, 1),
                    ["targetCount"] = x.Count()
                })
                .ToList();
            return new JObject
            {
                ["analysisId"] = result.AnalysisId,
                ["createdAtUtc"] = result.CreatedAtUtc.ToString("o"),
                ["group"] = result.GroupKey,
                ["ceilingTopMm"] = groups.Count == 1
                    ? groups[0]["ceilingTopMm"]
                    : JValue.CreateNull(),
                ["groups"] = new JArray(groups),
                ["modelFingerprint"] = result.ModelFingerprint,
                ["evidenceFingerprint"] = result.EvidenceFingerprint,
                ["resultFingerprint"] = result.ResultFingerprint,
                ["ceilingSources"] = new JArray(result.CeilingSources.Select(x => x.GetStableKey())),
                ["evidenceSourceCount"] = result.EvidenceSources.Count,
                ["linkScope"] = MaintenanceJson.BuildLinkScope(result.LinkScope),
                ["outOfScopeLinkCount"] = result.LinkScope == null
                    ? 0
                    : result.LinkScope.OutOfScopeLinks.Count,
                ["exemptPipeEvidenceCount"] = result.ExemptPipeEvidence.Count,
                ["exemptPipeEvidence"] = new JArray(result.ExemptPipeEvidence
                    .Where(x => x != null && x.Element != null)
                    .OrderBy(x => x.GroupKey, StringComparer.Ordinal)
                    .ThenBy(x => x.TargetKey, StringComparer.Ordinal)
                    .ThenBy(x => x.Element.GetStableKey(), StringComparer.Ordinal)
                    .Select(x => new JObject
                    {
                        ["group"] = x.GroupKey,
                        ["targetKey"] = x.TargetKey,
                        ["key"] = x.Element.GetStableKey(),
                        ["systemKind"] = x.SystemKind,
                        ["systemTypeEvidence"] = x.SystemTypeEvidence,
                        ["systemEvidenceSource"] = x.SystemEvidenceSource,
                        ["reasonCode"] = x.ReasonCode,
                        ["reason"] = x.Reason
                    })),
                ["coverageComplete"] = result.CoverageComplete,
                ["collectionFailures"] = new JArray(result.CoverageFailures
                    .Where(x => x != null)
                    .Select(x => new JObject
                    {
                        ["stage"] = x.Stage,
                        ["sourceKey"] = x.SourceKey,
                        ["linkInstanceId"] = x.LinkInstanceId.HasValue
                            ? (JToken)new JValue(x.LinkInstanceId.Value)
                            : JValue.CreateNull(),
                        ["linkInstanceUniqueId"] = string.IsNullOrWhiteSpace(x.LinkInstanceUniqueId)
                            ? JValue.CreateNull()
                            : new JValue(x.LinkInstanceUniqueId),
                        ["elementId"] = x.ElementId,
                        ["category"] = x.Category,
                        ["mark"] = x.Mark,
                        ["reason"] = x.Reason
                    })),
                ["coverageLimitations"] = new JArray(result.CoverageLimitations),
                ["windowLimitedSampling"] = result.WindowLimitedSampling,
                ["samplingNote"] = "天花或边界虚拟侧墙局部采样窗口为检修面代理点投影周边" +
                    Math.Round((result.Options.GridPointsPerAxis - 1) * result.Options.GridSpacingMm, 1) +
                    "×" + Math.Round((result.Options.GridPointsPerAxis - 1) * result.Options.GridSpacingMm, 1) +
                    "mm（" + result.Options.GridPointsPerAxis + "×" + result.Options.GridPointsPerAxis +
                    "网格，" + Math.Round(result.Options.GridSpacingMm, 1) + "mm步长）；窗口外未采样。",
                ["options"] = new JObject
                {
                    ["hatchSizeMm"] = result.Options.HatchSizeMm,
                    ["openingPreference"] = result.Options.OpeningPreference.ToString(),
                    ["strictCeilingSelection"] =
                        result.Options.StrictCeilingSelection,
                    ["ceilingPersonnelEntryRiseMm"] =
                        result.Options.CeilingPersonnelEntryRiseMm,
                    ["ceilingPersonnelFinalReachGapMm"] =
                        result.Options.CeilingPersonnelFinalReachGapMm,
                    ["allowSideWallDistanceOver500Review"] =
                        result.Options.AllowSideWallDistanceOver500Review,
                    ["sideWallReviewMaxDistanceMm"] =
                        result.Options.SideWallReviewMaxDistanceMm,
                    ["gridSpacingMm"] = result.Options.GridSpacingMm,
                    ["corridorDiametersMm"] = new JArray(result.Options.CorridorTestDiametersMm.Select(x => new JValue(x))),
                    ["maxDistanceMm"] = result.Options.MaxDistanceMm,
                    ["channelInwardOffsetMm"] = result.Options.ChannelInwardOffsetMm,
                    ["channelSurfaceLiftMm"] = result.Options.ChannelCeilingLiftMm,
                    ["sideWallOperatorZoneDepthMm"] =
                        result.Options.SideWallOperatorZoneDepthMm,
                    ["sideWallOperatorZoneWidthMm"] =
                        result.Options.SideWallOperatorZoneWidthMm,
                    ["ceilingDirectOperatorZoneLengthMm"] =
                        result.Options.CeilingDirectOperatorZoneLengthMm,
                    ["ceilingDirectOperatorZoneWidthMm"] =
                        result.Options.CeilingDirectOperatorZoneWidthMm
                },
                ["reviewStatement"] = "几何候选证据，须由顶级 AI 结合项目语义审查并由专业/施工人员确认；不是规范合规证明。",
                ["targetCount"] = result.TargetResults.Count,
                ["warnings"] = new JArray(result.Warnings),
                ["targets"] = targets
            };
        }

        internal static JObject BuildPage(
            HandReachAnalysisResult result,
            string targetKey,
            int limit,
            int offset)
        {
            if (result == null) throw new ArgumentNullException("result");
            if (limit < 1 || limit > 50) throw new ArgumentOutOfRangeException("limit");
            if (offset < 0) throw new ArgumentOutOfRangeException("offset");

            var rows = new List<Tuple<HandReachTargetResult, HandReachRegion>>();
            foreach (HandReachTargetResult item in result.TargetResults)
            {
                if (!string.IsNullOrWhiteSpace(targetKey) &&
                    !string.Equals(item.Target.TargetKey, targetKey.Trim(), StringComparison.Ordinal)) continue;
                foreach (HandReachRegion region in item.Regions)
                    rows.Add(Tuple.Create(item, region));
            }
            List<Tuple<HandReachTargetResult, HandReachRegion>> all = rows
                .OrderBy(x => x.Item1.Target.TargetKey, StringComparer.Ordinal)
                .ThenBy(x => x.Item2.RegionNo)
                .ToList();
            List<Tuple<HandReachTargetResult, HandReachRegion>> page = all.Skip(offset).Take(limit).ToList();
            bool pageHasMore = offset + page.Count < all.Count;

            return new JObject
            {
                ["analysisId"] = result.AnalysisId,
                ["group"] = result.GroupKey,
                ["candidateAuditComplete"] = result.TargetResults.All(x => x.CandidateAuditComplete),
                ["selectedCandidatesApprovable"] =
                    result.CoverageComplete &&
                    result.TargetResults
                        .Where(x => x != null && x.Regions != null && x.Regions.Count > 0)
                        .Any() &&
                    result.TargetResults
                        .Where(x => x != null && x.Regions != null && x.Regions.Count > 0)
                        .All(x => x.SelectedCandidateAuditComplete),
                ["coverageComplete"] = result.CoverageComplete,
                ["candidateAuditScopeDefinition"] = "merged_region_representatives",
                ["candidateAuditScopeDescription"] =
                    "每个连续可行区域一个代表候选；侧墙使用天花边界虚拟墙面的局部UV网格，天花使用XY网格；全量计数留在机器汇总。",
                ["presentationHint"] = "坐标为机器证据，用于 AI 追溯，不应直接放进项目经理 PPT。",
                ["total"] = all.Count,
                ["returned"] = page.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["pageHasMore"] = pageHasMore,
                ["nextOffset"] = pageHasMore ? (JToken)(offset + page.Count) : JValue.CreateNull(),
                ["candidates"] = new JArray(page.Select(x => BuildRegionJson(result, x.Item1, x.Item2, true)))
            };
        }

        private static JObject BuildRegionJson(
            HandReachAnalysisResult result,
            HandReachTargetResult target,
            HandReachRegion region,
            bool includeDetail)
        {
            HandReachSample rec = region.Recommended;
            var corridor = new JObject();
            for (int d = 0; d < result.Options.CorridorTestDiametersMm.Length; d++)
                corridor["diameter" + ((int)Math.Round(result.Options.CorridorTestDiametersMm[d]))] =
                    region.RecommendedCorridorClear != null && d < region.RecommendedCorridorClear.Length
                        ? region.RecommendedCorridorClear[d]
                        : false;

            var json = new JObject
            {
                ["group"] = string.IsNullOrWhiteSpace(target.Target.GroupKey)
                    ? result.GroupKey
                    : target.Target.GroupKey,
                ["deviceNo"] = target.Target.DeviceNo,
                ["schemeNo"] = target.Target.SchemeNo,
                ["legacySchemeNos"] = new JArray(target.Target.LegacySchemeNos),
                ["targetKey"] = target.Target.TargetKey,
                ["device"] = target.Target.GetDisplayName(),
                ["regionNo"] = region.RegionNo,
                ["regionPointCount"] = region.PointCount,
                ["regionBoundsMm"] = new JObject
                {
                    ["minX"] = Math.Round(region.MinX, 1),
                    ["maxX"] = Math.Round(region.MaxX, 1),
                    ["minY"] = Math.Round(region.MinY, 1),
                    ["maxY"] = Math.Round(region.MaxY, 1),
                    ["minZ"] = Math.Round(region.MinZ, 1),
                    ["maxZ"] = Math.Round(region.MaxZ, 1)
                },
                ["regionAreaM2"] = Math.Round(region.AreaM2, 3),
                ["openingPlane"] = region.OpeningPlane.ToString(),
                ["surfaceKey"] = string.IsNullOrWhiteSpace(region.SurfaceKey)
                    ? JValue.CreateNull()
                    : new JValue(region.SurfaceKey),
                ["recommendedHatchCenterMm"] = new JArray(
                    Math.Round(rec.CenterX, 1), Math.Round(rec.CenterY, 1),
                    Math.Round(rec.OpeningPlane == HandReachOpeningPlaneKind.SideWallVertical ||
                        rec.CenterZ != 0.0 ? rec.CenterZ : target.Target.CeilingTopMm, 1)),
                ["nearestHatchEdgeMm"] = new JArray(
                    Math.Round(rec.EdgeX, 1), Math.Round(rec.EdgeY, 1),
                    Math.Round(rec.OpeningPlane == HandReachOpeningPlaneKind.SideWallVertical ||
                        rec.EdgeZ != 0.0 ? rec.EdgeZ : target.Target.CeilingTopMm, 1)),
                ["channelStartMm"] = new JArray(
                    Math.Round(rec.ChannelStartX, 1),
                    Math.Round(rec.ChannelStartY, 1),
                    Math.Round(rec.ChannelStartZ, 1)),
                ["openingTangent"] = new JArray(
                    Math.Round(rec.OpeningTangentX, 6), Math.Round(rec.OpeningTangentY, 6)),
                ["openingInward"] = new JArray(
                    Math.Round(rec.OpeningInwardX, 6), Math.Round(rec.OpeningInwardY, 6)),
                ["openingDepthMm"] = Math.Round(rec.OpeningDepthMm, 1),
                ["serviceFaceProxyMm"] = new JArray(
                    Math.Round(target.Target.ServiceFaceProxyX, 1),
                    Math.Round(target.Target.ServiceFaceProxyY, 1),
                    Math.Round(target.Target.ServiceFaceProxyZ, 1)),
                ["analysisServiceFaceProxyMm"] = new JArray(
                    Math.Round(target.Target.ServiceFaceProxyX, 1),
                    Math.Round(target.Target.ServiceFaceProxyY, 1),
                    Math.Round(target.AnalysisServiceFaceProxyZ, 1)),
                ["ceilingPersonnelEntryApplied"] =
                    target.CeilingPersonnelEntryApplied,
                ["ceilingDirectReachApplied"] =
                    target.CeilingDirectReachApplied,
                ["directReachStartZMm"] = target.CeilingDirectReachApplied
                    ? (JToken)Math.Round(rec.ChannelStartZ, 1)
                    : JValue.CreateNull(),
                ["personnelEntryTopMm"] =
                    rec.PersonnelEntryTopZ > 0.0
                        ? (JToken)Math.Round(rec.PersonnelEntryTopZ, 1)
                        : JValue.CreateNull(),
                ["horizontalMm"] = Math.Round(rec.HorizontalMm, 1),
                ["obliqueActualMm"] = Math.Round(rec.ObliqueMm, 1),
                ["verticalDifferenceMm"] = Math.Round(rec.VerticalMm, 1),
                ["distanceGrade"] = MaintenanceHandReachMath.GradeDistanceText(rec.DistanceGrade),
                ["distanceReviewStatus"] =
                    result.Options.AllowSideWallDistanceOver500Review &&
                    rec.OpeningPlane == HandReachOpeningPlaneKind.SideWallVertical &&
                    rec.ObliqueMm > result.Options.MaxDistanceMm + 1e-6 &&
                    rec.ObliqueMm <= result.Options.SideWallReviewMaxDistanceMm + 1e-6
                        ? "orange_review_500_to_600"
                        : "formal_distance_rule",
                ["verticalGrade"] = MaintenanceHandReachMath.GradeVerticalText(region.RecommendedVerticalGrade),
                ["corridorTests"] = corridor,
                ["maxTestedClearDiameterMm"] = region.MaxTestedClearDiameterMm,
                ["absoluteMaximumDiameterMm"] = JValue.CreateNull(),
                ["absoluteMaximumStatus"] = "missing_not_continuously_solved",
                ["ladderDirection"] = string.IsNullOrEmpty(region.RecommendedLadderDirection)
                    ? JValue.CreateNull()
                    : new JValue(region.RecommendedLadderDirection),
                ["ladderCenterMm"] = new JArray(
                    Math.Round(rec.LadderCenterX, 1), Math.Round(rec.LadderCenterY, 1)),
                ["ladderAlong"] = new JArray(
                    Math.Round(rec.LadderAlongX, 6), Math.Round(rec.LadderAlongY, 6)),
                ["operationZoneClear"] = region.RecommendedOperationZoneClear,
                ["exemptIntersectCount"] = region.RecommendedExemptIntersectCount,
                ["blockerKey"] = string.IsNullOrEmpty(region.RecommendedBlockerKey)
                    ? JValue.CreateNull()
                    : new JValue(region.RecommendedBlockerKey),
                ["operationPointStatus"] = target.Target.OperationPointStatus.ToString(),
                ["attentionLevel"] = target.AttentionLevel.ToString(),
                ["conclusion"] = target.Conclusion
            };
            if (includeDetail)
            {
                json["counts"] = new JObject
                {
                    ["rawSamples"] = target.RawSampleCount,
                    ["openingFullyContained"] = target.HatchInsideCount,
                    ["hatchInsideCeiling"] = region.OpeningPlane ==
                        HandReachOpeningPlaneKind.CeilingHorizontal
                            ? target.HatchInsideCount
                            : 0,
                    ["verticalRejectedOver500"] = target.VerticalFailCount,
                    ["distanceLe500"] = target.DistanceOkCount,
                    ["openingRejected"] = target.OpeningFailCount,
                    ["corridorRejected"] = target.CorridorFailCount,
                    ["ladderRejected"] = target.LadderFailCount,
                    ["clearFullChain"] = target.ClearCount,
                    ["regions4"] = target.Regions4Count,
                    ["regions8"] = target.Regions8Count,
                    ["connectivityAgreed"] = target.ConnectivityAgreed
                };
            }
            return json;
        }
    }
}

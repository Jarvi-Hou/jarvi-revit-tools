using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JarviTools.Commands.MaintenanceReachability;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    internal static class MaintenanceAnalysisStore
    {
        private static readonly object Gate = new object();
        private static MaintenanceAnalysisResult _result;
        private static string _documentKey;
        private static string _approvedAnalysisId;
        private static string _approvalToken;
        private static string _reviewer;
        private static string _reviewNote;
        private static bool _approvalConsumed;
        private static string _visualizedAtUtc;
        private static long _visualizedChangeSerial = -1L;
        private static long _approvalBaseVisualizedSerial = -1L;

        internal static void Set(Document document, MaintenanceAnalysisResult result)
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
                _approvalConsumed = false;
                _visualizedAtUtc = null;
                _visualizedChangeSerial = -1L;
                _approvalBaseVisualizedSerial = -1L;
            }
        }

        internal static MaintenanceAnalysisResult Get(Document document)
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
                _approvalConsumed = false;
                _visualizedAtUtc = null;
                _visualizedChangeSerial = -1L;
                _approvalBaseVisualizedSerial = -1L;
            }
        }

        internal static string Approve(
            Document document,
            MaintenanceAnalysisResult result,
            string reviewer,
            string reviewNote)
        {
            if (document == null || result == null) throw new ArgumentNullException("result");
            RequireEvidenceCollectionComplete(result);
            lock (Gate)
            {
                if (_result != result ||
                    !string.Equals(_documentKey, BuildDocumentKey(document), StringComparison.Ordinal))
                    throw new InvalidOperationException("The maintenance candidate snapshot is no longer current.");
                if (!result.EvidenceCollectionComplete)
                    throw new InvalidOperationException(
                        "Obstacle evidence collection is incomplete. Re-run analysis after resolving collection failures; approval is forbidden.");
                long currentSerial = MaintenanceDocumentChangeTracker.GetSerial(document);
                long selfVisualizationSerial = MaintenanceApprovalSerialPolicy
                    .ResolveReusableOwnVisualizationSerial(
                        _result == result,
                        _approvalConsumed,
                        currentSerial,
                        _visualizedChangeSerial);
                ValidateEvidenceSnapshot(document, result, selfVisualizationSerial);

                _approvedAnalysisId = result.AnalysisId;
                _approvalToken = Guid.NewGuid().ToString("N");
                _reviewer = reviewer;
                _reviewNote = reviewNote;
                _approvalConsumed = false;
                _visualizedAtUtc = null;
                _approvalBaseVisualizedSerial = selfVisualizationSerial;
                result.ApprovalReviewer = reviewer;
                result.ApprovalNote = reviewNote;
                result.ApprovedAtUtc = DateTime.UtcNow;
                return _approvalToken;
            }
        }

        internal static void RequireApproval(
            Document document,
            MaintenanceAnalysisResult result,
            string approvalToken)
        {
            RequireEvidenceCollectionComplete(result);
            lock (Gate)
            {
                if (result == null || !result.EvidenceCollectionComplete)
                    throw new InvalidOperationException(
                        "Obstacle evidence collection is incomplete. Approval and visualization are forbidden.");
                ValidateEvidenceSnapshot(document, result, _approvalBaseVisualizedSerial);
                if (_result != result ||
                    !string.Equals(_documentKey, BuildDocumentKey(document), StringComparison.Ordinal) ||
                    !string.Equals(_approvedAnalysisId, result.AnalysisId, StringComparison.Ordinal) ||
                    _approvalConsumed ||
                    string.IsNullOrWhiteSpace(approvalToken) ||
                    !string.Equals(_approvalToken, approvalToken, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "This candidate snapshot has not been explicitly reviewed and approved. Call approve_maintenance_reachability first.");
                string approvedAt = result.ApprovedAtUtc.HasValue
                    ? result.ApprovedAtUtc.Value.ToString("o")
                    : string.Empty;
                foreach (MaintenanceRenderItem item in result.RenderItems.Where(x => x != null))
                {
                    item.EvidenceFingerprint = result.EvidenceFingerprint;
                    item.ApprovalReviewer = _reviewer ?? string.Empty;
                    item.ApprovalNote = _reviewNote ?? string.Empty;
                    item.ApprovedAtUtc = approvedAt;
                }
                foreach (MaintenanceWallAlternativeResult alternative in
                    result.WallAlternatives.Where(x => x != null))
                foreach (MaintenanceRenderItem item in alternative.RenderItems.Where(x => x != null))
                {
                    item.EvidenceFingerprint = result.EvidenceFingerprint;
                    item.ApprovalReviewer = _reviewer ?? string.Empty;
                    item.ApprovalNote = _reviewNote ?? string.Empty;
                    item.ApprovedAtUtc = approvedAt;
                }
            }
        }

        internal static void ConsumeApproval(
            Document document,
            MaintenanceAnalysisResult result,
            string approvalToken)
        {
            lock (Gate)
            {
                if (document == null || result == null ||
                    _result != result ||
                    !string.Equals(_documentKey, BuildDocumentKey(document), StringComparison.Ordinal) ||
                    !string.Equals(_approvedAnalysisId, result.AnalysisId, StringComparison.Ordinal) ||
                    _approvalConsumed ||
                    string.IsNullOrWhiteSpace(approvalToken) ||
                    !string.Equals(_approvalToken, approvalToken, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "The maintenance approval snapshot is no longer current or has already been consumed.");

                // Do not re-run evidence validation here: a successful Show call has
                // intentionally written owned DirectShapes and advanced the document
                // change serial.  RequireApproval validated immediately before Show.
                _approvalConsumed = true;
                _approvalToken = null;
                _visualizedAtUtc = DateTime.UtcNow.ToString("o");
                _visualizedChangeSerial = MaintenanceDocumentChangeTracker.GetSerial(document);
                _approvalBaseVisualizedSerial = -1L;
            }
        }

        internal static JObject GetReviewStatus(Document document, MaintenanceAnalysisResult result)
        {
            lock (Gate)
            {
                bool sameSnapshot = _result == result &&
                    string.Equals(_documentKey, BuildDocumentKey(document), StringComparison.Ordinal) &&
                    string.Equals(_approvedAnalysisId, result.AnalysisId, StringComparison.Ordinal);
                bool approved = sameSnapshot &&
                    !_approvalConsumed &&
                    !string.IsNullOrWhiteSpace(_approvalToken);
                bool visualized = sameSnapshot && _approvalConsumed;
                bool evidenceCurrent = false;
                try
                {
                    evidenceCurrent = string.Equals(
                        result.EvidenceFingerprint,
                        MaintenanceAnalysisService.ComputeEvidenceFingerprint(document, result),
                        StringComparison.OrdinalIgnoreCase);
                }
                catch { }
                // The visualization transaction only adds/removes OpenRevit-owned
                // display artifacts, so the pre-Show evidence remains current until
                // the next document change.  This avoids reporting the successful
                // visualization itself as an external stale-model edit.
                if (visualized &&
                    MaintenanceDocumentChangeTracker.GetSerial(document) == _visualizedChangeSerial)
                    evidenceCurrent = true;
                if (approved && _approvalBaseVisualizedSerial >= 0L &&
                    MaintenanceDocumentChangeTracker.GetSerial(document) ==
                        _approvalBaseVisualizedSerial)
                    evidenceCurrent = true;
                return new JObject
                {
                    ["status"] = !evidenceCurrent
                        ? "stale_model_evidence_reanalysis_required"
                        : (visualized
                            ? "visualized_from_single_use_approval"
                            : (approved ? "approved_candidate_snapshot" : "pending_ai_and_professional_review")),
                    ["reviewer"] = approved || visualized ? _reviewer : null,
                    ["reviewNote"] = approved || visualized ? _reviewNote : null,
                    ["visualized"] = visualized,
                    ["approvalTokenConsumed"] = visualized,
                    ["visualizedAtUtc"] = visualized ? _visualizedAtUtc : null,
                    ["evidenceCurrent"] = evidenceCurrent,
                    ["evidenceFingerprint"] = result.EvidenceFingerprint,
                    ["doorWidthMm"] = result.DoorWidthMm,
                    ["doorHeightMm"] = result.DoorHeightMm
                };
            }
        }

        private static void ValidateEvidenceSnapshot(
            Document document,
            MaintenanceAnalysisResult result,
            long allowedOwnVisualizationSerial = -1L)
        {
            if (document == null || result == null)
                throw new InvalidOperationException("The maintenance candidate snapshot is unavailable.");
            if (MaintenanceApprovalSerialPolicy.IsAllowedOwnVisualizationSerial(
                MaintenanceDocumentChangeTracker.GetSerial(document),
                allowedOwnVisualizationSerial))
                return;
            string currentModel = MaintenanceLedgerSyncService.GetModelFingerprint(document);
            string currentEvidence = MaintenanceAnalysisService.ComputeEvidenceFingerprint(document, result);
            if (!string.Equals(result.ModelFingerprint, currentModel, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(result.EvidenceFingerprint, currentEvidence, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Relevant Revit elements changed after analysis. Re-run analyze_maintenance_reachability before approval or visualization.");
            }
        }

        private static void RequireEvidenceCollectionComplete(MaintenanceAnalysisResult result)
        {
            if (result == null || !result.EvidenceCollectionComplete)
                throw new InvalidOperationException(
                    "Maintenance evidence collection is incomplete. Resolve every collection failure and re-run analysis before formal approval or visualization.");
        }

        private static string BuildDocumentKey(Document document)
        {
            string projectInfo = string.Empty;
            try { projectInfo = document.ProjectInformation == null ? string.Empty : document.ProjectInformation.UniqueId; }
            catch { }
            return (document.PathName ?? string.Empty) + "|" + (document.Title ?? string.Empty) + "|" + projectInfo;
        }
    }

    public sealed class AnalyzeMaintenanceReachabilityTool : IRevitTool
    {
        public string Name => "analyze_maintenance_reachability";
        public string Description =>
            "计算维修可达候选方案：注释分组、宿主/链接几何、侧墙门/天花口、梯子、人体通行和设备检修侧。返回需顶级 AI 与专业人员复核的候选证据，不是规范合规证明。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["ceilingElementIds"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "integer" },
                    ["description"] = "宿主模型天花 ElementId。未填时使用 Revit 当前选中的天花；注释相同者按一个逻辑组。"
                },
                ["relevantLinkInstanceIds"] = RelevantLinkScopeSchema(),
                ["strictCeilingSelection"] = new JObject
                {
                    ["type"] = "boolean",
                    ["default"] = false,
                    ["description"] = "true 时只分析 ceilingElementIds 明确列出的天花，不自动并入其他同注释天花。"
                },
                ["doorWidthMm"] = DoorDimensionSchema(
                    "侧墙检修门净宽，参与候选采样、开口/门框碰撞、结果指纹和显示模型。",
                    MaintenanceAnalysisOptions.DefaultDoorWidthMm),
                ["doorHeightMm"] = DoorDimensionSchema(
                    "侧墙检修门净高，参与开口/门框碰撞、结果指纹和显示模型。",
                    MaintenanceAnalysisOptions.DefaultDoorHeightMm),
                ["show"] = new JObject
                {
                    ["type"] = "boolean",
                    ["default"] = false,
                    ["description"] = "是否立即在当前普通三维视图写入可追溯 DirectShape。建议 AI 先审查摘要。"
                }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            UIDocument uidoc = RequireUiDocument(uiapp);
            ICollection<ElementId> ids = ReadCeilingIds(input);
            if (ids.Count == 0) ids = uidoc.Selection.GetElementIds();

            var options = new MaintenanceAnalysisOptions
            {
                StrictCeilingSelection = input != null &&
                    ((bool?)input["strictCeilingSelection"]).GetValueOrDefault(),
                RelevantLinkInstanceIds = ReadRelevantLinkInstanceIds(input),
                DoorWidthMm = ReadDoorDimension(
                    input,
                    "doorWidthMm",
                    MaintenanceAnalysisOptions.DefaultDoorWidthMm),
                DoorHeightMm = ReadDoorDimension(
                    input,
                    "doorHeightMm",
                    MaintenanceAnalysisOptions.DefaultDoorHeightMm)
            };
            MaintenanceAnalysisResult result = MaintenanceAnalysisService.Analyze(
                uidoc.Document,
                ids,
                options);
            MaintenanceWallAlternativeVisualizationService.ResolveSchemeAssignments(
                uidoc.Document, result);
            MaintenanceAnalysisStore.Set(uidoc.Document, result);
            bool show = input != null && ((bool?)input["show"]).GetValueOrDefault();
            if (show)
                throw new InvalidOperationException(
                    "Direct visualization from analyze is disabled. Review the candidate evidence, call approve_maintenance_reachability, then call show_maintenance_reachability.");
            JObject summary = MaintenanceJson.BuildSummary(result);
            summary["visualized"] = false;
            summary["review"] = MaintenanceAnalysisStore.GetReviewStatus(uidoc.Document, result);
            return summary;
        }

        internal static ICollection<ElementId> ReadCeilingIds(JObject input)
        {
            var ids = new List<ElementId>();
            var array = input == null ? null : input["ceilingElementIds"] as JArray;
            if (array == null) return ids;
            foreach (JToken token in array)
            {
                long value;
                if (!long.TryParse(token.ToString(), out value) || value <= 0)
                    throw new ArgumentException("ceilingElementIds must contain positive integer ElementIds.");
                ids.Add(new ElementId(value));
            }
            return ids;
        }

        internal static long[] ReadRelevantLinkInstanceIds(JObject input)
        {
            JToken token = input == null ? null : input["relevantLinkInstanceIds"];
            if (token == null) return null;
            return ((JArray)token)
                .Select(x => (long)x)
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
        }

        internal static JObject RelevantLinkScopeSchema()
        {
            return new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = "integer", ["minimum"] = 1 },
                ["description"] =
                    "可选显式正向证据范围：仅这些 RevitLinkInstance 与宿主参与候选/失败审计；其余链接结构化标为 outOfScope。未提供时保持严格全链接门禁；空数组表示仅宿主。"
            };
        }

        internal static JObject DoorDimensionSchema(string description, double defaultValueMm)
        {
            return new JObject
            {
                ["type"] = "number",
                ["minimum"] = MaintenanceAnalysisOptions.MinimumDoorDimensionMm,
                ["maximum"] = MaintenanceAnalysisOptions.MaximumDoorDimensionMm,
                ["default"] = defaultValueMm,
                ["description"] = description
            };
        }

        internal static double ReadDoorDimension(
            JObject input,
            string propertyName,
            double defaultValueMm)
        {
            JToken token = input == null ? null : input[propertyName];
            if (token == null) return defaultValueMm;
            double valueMm = (double)token;
            if (double.IsNaN(valueMm) || double.IsInfinity(valueMm) ||
                valueMm < MaintenanceAnalysisOptions.MinimumDoorDimensionMm ||
                valueMm > MaintenanceAnalysisOptions.MaximumDoorDimensionMm)
                throw new ArgumentOutOfRangeException(propertyName);
            return valueMm;
        }

        internal static UIDocument RequireUiDocument(UIApplication uiapp)
        {
            if (uiapp == null || uiapp.ActiveUIDocument == null)
                throw new InvalidOperationException("Revit 没有活动文档。");
            return uiapp.ActiveUIDocument;
        }
    }

    public sealed class AnalyzeMaintenanceRouteCandidatesTool : IRevitTool
    {
        public string Name => "analyze_maintenance_route_candidates";
        public string Description =>
            "对一个或多个天花分组执行可审计维修路线分析：保留本规则下的代表性入口方案、失败阶段、主阻挡证据、入口×设备的确定性路线、备选排序和最终选择；可把明确选中的相邻天花合并为一个跨房间共用入口复核范围。结果供任意顶级 AI 审查与汇报，不直接写入模型，也不声称枚举所有数学路径。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["ceilingElementIds"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "integer" },
                    ["description"] = "宿主模型天花 ElementId；未填时使用 Revit 当前选择。注释相同者按一个逻辑组。"
                },
                ["relevantLinkInstanceIds"] = AnalyzeMaintenanceReachabilityTool.RelevantLinkScopeSchema(),
                ["strictCeilingSelection"] = new JObject
                {
                    ["type"] = "boolean",
                    ["default"] = false,
                    ["description"] = "true 时只分析 ceilingElementIds 明确列出的天花，不自动并入其他同注释天花。"
                },
                ["combineSelectedCeilingsForSharedEntry"] = new JObject
                {
                    ["type"] = "boolean",
                    ["default"] = false,
                    ["description"] = "true 时仅把明确选中的天花跨注释合成一个连续范围，按450×450天花人员入口复核：能否从同一入口跨房间走到至少两台设备。该结果是待复核备选，不自动替换正式方案。"
                },
                ["doorWidthMm"] = AnalyzeMaintenanceReachabilityTool.DoorDimensionSchema(
                    "侧墙爬入式检修门净宽，默认 600 mm。",
                    MaintenanceAnalysisOptions.DefaultDoorWidthMm),
                ["doorHeightMm"] = AnalyzeMaintenanceReachabilityTool.DoorDimensionSchema(
                    "侧墙爬入式检修门净高，默认 600 mm。",
                    MaintenanceAnalysisOptions.DefaultDoorHeightMm),
                ["maxHatchCandidatesPerTarget"] = new JObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["maximum"] = 200,
                    ["default"] = 32,
                    ["description"] = "每个设备/通行档位最多保留的天花口位置代表；代表点按400 mm空间桶去重，实际开口固定为450×450 mm。达到上限时 truncated=true、auditComplete=false。"
                }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            UIDocument uidoc = AnalyzeMaintenanceReachabilityTool.RequireUiDocument(uiapp);
            ICollection<ElementId> ids = AnalyzeMaintenanceReachabilityTool.ReadCeilingIds(input);
            if (ids.Count == 0) ids = uidoc.Selection.GetElementIds();
            int maxHatch = input == null || input["maxHatchCandidatesPerTarget"] == null
                ? 32
                : (int)input["maxHatchCandidatesPerTarget"];
            if (maxHatch < 1 || maxHatch > 200)
                throw new ArgumentOutOfRangeException("maxHatchCandidatesPerTarget");

            var options = new MaintenanceAnalysisOptions
            {
                PreserveCandidateAudit = true,
                StrictCeilingSelection = input != null &&
                    ((bool?)input["strictCeilingSelection"]).GetValueOrDefault(),
                CombineSelectedCeilingsForSharedEntry = input != null &&
                    ((bool?)input["combineSelectedCeilingsForSharedEntry"]).GetValueOrDefault(),
                MaxHatchCandidatesPerTarget = maxHatch,
                RelevantLinkInstanceIds =
                    AnalyzeMaintenanceReachabilityTool.ReadRelevantLinkInstanceIds(input),
                DoorWidthMm = AnalyzeMaintenanceReachabilityTool.ReadDoorDimension(
                    input,
                    "doorWidthMm",
                    MaintenanceAnalysisOptions.DefaultDoorWidthMm),
                DoorHeightMm = AnalyzeMaintenanceReachabilityTool.ReadDoorDimension(
                    input,
                    "doorHeightMm",
                    MaintenanceAnalysisOptions.DefaultDoorHeightMm)
            };
            MaintenanceAnalysisResult result = MaintenanceAnalysisService.Analyze(uidoc.Document, ids, options);
            MaintenanceWallAlternativeVisualizationService.ResolveSchemeAssignments(
                uidoc.Document, result);
            MaintenanceAnalysisStore.Set(uidoc.Document, result);
            JObject summary = MaintenanceJson.BuildSummary(result);
            summary["visualized"] = false;
            summary["candidateAudit"] = new JObject
            {
                ["enabled"] = true,
                ["complete"] = result.CandidateAuditComplete,
                ["strategy"] = result.CandidateAuditStrategy,
                ["scopeDefinition"] = result.CandidateAuditScopeDefinition,
                ["scopeDescription"] = result.CandidateAuditScopeDescription,
                ["allPathsEnumerated"] = result.CandidateAuditAllPathsEnumerated,
                ["routePolicy"] = result.CandidateAuditRoutePolicy,
                ["selectionPolicy"] = result.CandidateAuditSelectionPolicy,
                ["displayRankingPolicy"] = result.CandidateAuditDisplayRankingPolicy,
                ["candidateAuditFingerprint"] = result.CandidateAuditFingerprint,
                ["candidateCount"] = result.CandidateEvaluations.Count,
                ["selectedRouteCount"] = result.CandidateEvaluations.Count(x => x.IsSelected),
                ["invalidatedSelectedCount"] = result.CandidateEvaluations.Count(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Rejected),
                ["unverifiedSelectedCount"] = result.CandidateEvaluations.Count(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Unverified),
                ["requiresReselection"] = result.CandidateEvaluations.Any(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Rejected),
                ["requiresSelectedReview"] = result.CandidateEvaluations.Any(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Unverified),
                ["searchStats"] = MaintenanceCandidateJson.BuildSearchStats(result),
                ["nextTool"] = "get_maintenance_route_candidates"
            };
            summary["review"] = MaintenanceAnalysisStore.GetReviewStatus(uidoc.Document, result);
            return summary;
        }
    }

    public sealed class GetMaintenanceReachabilitySummaryTool : IRevitTool
    {
        public string Name => "get_maintenance_reachability_summary";
        public string Description => "返回当前文档最近一次维修可达候选分析的分组、入口、通行档位、判断理由与阻挡图元。";
        public JObject InputSchema => AnalysisIdSchema();

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            Document doc = AnalyzeMaintenanceReachabilityTool.RequireUiDocument(uiapp).Document;
            MaintenanceAnalysisResult result = RequireResult(doc, input);
            JObject summary = MaintenanceJson.BuildSummary(result);
            summary["review"] = MaintenanceAnalysisStore.GetReviewStatus(doc, result);
            return summary;
        }

        internal static JObject AnalysisIdSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["analysisId"] = new JObject { ["type"] = "string", ["description"] = "可选期望 analysisId，用于防止误读后续重算快照。" }
                },
                ["additionalProperties"] = false
            };
        }

        internal static MaintenanceAnalysisResult RequireResult(Document doc, JObject input)
        {
            MaintenanceAnalysisResult result = MaintenanceAnalysisStore.Get(doc);
            if (result == null) throw new InvalidOperationException("当前文档没有内存中的维修可达快照。");
            string expected = input == null ? null : (string)input["analysisId"];
            if (!string.IsNullOrWhiteSpace(expected) && !string.Equals(expected, result.AnalysisId, StringComparison.Ordinal))
                throw new InvalidOperationException("analysisId 已变化，请重新读取摘要。");
            return result;
        }
    }

    public sealed class GetMaintenanceRouteCandidatesTool : IRevitTool
    {
        public string Name => "get_maintenance_route_candidates";
        public string Description =>
            "分页读取最近一次可审计维修分析中保留的代表性入口与路线方案，包括选中方案、可行备选、失败阶段、原因、主阻挡证据和机器坐标。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["analysisId"] = new JObject { ["type"] = "string" },
                ["groupKey"] = new JObject { ["type"] = "string" },
                ["targetKey"] = new JObject { ["type"] = "string" },
                ["scope"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Entry", "Route" } },
                ["status"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Rejected", "Unverified", "Feasible" } },
                ["entryType"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "WallDoor", "CeilingHatch" } },
                ["selectedOnly"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["includeRoutePoints"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 50, ["default"] = 20 },
                ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            Document doc = AnalyzeMaintenanceReachabilityTool.RequireUiDocument(uiapp).Document;
            MaintenanceAnalysisResult result = GetMaintenanceReachabilitySummaryTool.RequireResult(doc, input);
            int limit = input == null || input["limit"] == null ? 20 : (int)input["limit"];
            int offset = input == null || input["offset"] == null ? 0 : (int)input["offset"];
            bool selectedOnly = input != null && ((bool?)input["selectedOnly"]).GetValueOrDefault();
            bool includeRoutePoints = input != null &&
                                      ((bool?)input["includeRoutePoints"]).GetValueOrDefault();
            JObject page = MaintenanceCandidateJson.BuildPage(
                result,
                input == null ? null : (string)input["groupKey"],
                input == null ? null : (string)input["targetKey"],
                input == null ? null : (string)input["scope"],
                input == null ? null : (string)input["status"],
                input == null ? null : (string)input["entryType"],
                selectedOnly,
                includeRoutePoints,
                limit,
                offset);
            page["searchStats"] = MaintenanceCandidateJson.BuildSearchStats(result);
            page["review"] = MaintenanceAnalysisStore.GetReviewStatus(doc, result);
            return page;
        }
    }

    public sealed class ApproveMaintenanceReachabilityTool : IRevitTool
    {
        public string Name => "approve_maintenance_reachability";
        public string Description =>
            "Explicitly record that the current candidate snapshot has been reviewed by the named AI/reviewer. This does not certify code compliance; it only unlocks visualization of the unchanged snapshot.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["analysisId"] = new JObject { ["type"] = "string" },
                ["reviewer"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Reviewer identity, for example Codex 5.6 Sol (xhigh)."
                },
                ["reviewNote"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Concise evidence-based review note; at least 20 characters."
                }
            },
            ["required"] = new JArray { "analysisId", "reviewer", "reviewNote" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            Document doc = AnalyzeMaintenanceReachabilityTool.RequireUiDocument(uiapp).Document;
            MaintenanceAnalysisResult result = GetMaintenanceReachabilitySummaryTool.RequireResult(doc, input);
            string reviewer = input == null ? null : (string)input["reviewer"];
            string note = input == null ? null : (string)input["reviewNote"];
            if (string.IsNullOrWhiteSpace(reviewer))
                throw new ArgumentException("'reviewer' is required.");
            if (string.IsNullOrWhiteSpace(note) || note.Trim().Length < 20)
                throw new ArgumentException("'reviewNote' must contain at least 20 characters of review evidence.");

            string token = MaintenanceAnalysisStore.Approve(doc, result, reviewer.Trim(), note.Trim());
            return new JObject
            {
                ["analysisId"] = result.AnalysisId,
                ["approvalToken"] = token,
                ["modelFingerprint"] = result.ModelFingerprint,
                ["evidenceFingerprint"] = result.EvidenceFingerprint,
                ["doorWidthMm"] = result.DoorWidthMm,
                ["doorHeightMm"] = result.DoorHeightMm,
                ["evidenceCollectionComplete"] = result.EvidenceCollectionComplete,
                ["collectionFailureCount"] = result.CollectionFailures.Count,
                ["collectionFailures"] = new JArray(result.CollectionFailures.Select(x =>
                    new JObject
                    {
                        ["group"] = x.GroupKey,
                        ["sourceKey"] = x.SourceKey,
                        ["linkInstanceId"] = x.LinkInstanceId.HasValue
                            ? (JToken)x.LinkInstanceId.Value
                            : JValue.CreateNull(),
                        ["elementId"] = x.ElementId,
                        ["category"] = x.Category,
                        ["reason"] = x.Reason
                    })),
                ["status"] = "approved_candidate_snapshot",
                ["disclaimer"] = "Approval unlocks visualization only; it is not a code-compliance or construction certification."
            };
        }
    }

    public sealed class ShowMaintenanceReachabilityTool : IRevitTool
    {
        public string Name => "show_maintenance_reachability";
        public string Description => "将当前文档内存中已经 AI 复核的维修可达快照写入当前普通三维视图。";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["analysisId"] = new JObject { ["type"] = "string" },
                ["approvalToken"] = new JObject { ["type"] = "string" }
            },
            ["required"] = new JArray { "analysisId", "approvalToken" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            Document doc = AnalyzeMaintenanceReachabilityTool.RequireUiDocument(uiapp).Document;
            MaintenanceAnalysisResult result = GetMaintenanceReachabilitySummaryTool.RequireResult(doc, input);
            string approvalToken = input == null ? null : (string)input["approvalToken"];
            MaintenanceAnalysisStore.RequireApproval(doc, result, approvalToken);
            MaintenanceVisualizationStats stats = MaintenanceVisualizationService.Show(uiapp, result);
            MaintenanceAnalysisStore.ConsumeApproval(doc, result, approvalToken);
            var warnings = new List<string>();
            warnings.AddRange(stats.Warnings);
            JObject ledger = MaintenanceLedgerAutoSync.TryWrite(
                doc, result.ModelFingerprint, warnings, "模型显示");
            return new JObject
            {
                ["analysisId"] = result.AnalysisId,
                ["createdElementCount"] = stats.CreatedElementCount,
                ["deletedPreviousElementCount"] = stats.DeletedPreviousElementCount,
                ["targetViewId"] = stats.TargetViewId,
                ["targetViewName"] = stats.TargetViewName,
                ["createdViewCount"] = stats.CreatedViewCount,
                ["contextViewNames"] = new JArray(stats.ContextViewNames.Distinct()),
                ["approvalTokenConsumed"] = true,
                ["doorWidthMm"] = result.DoorWidthMm,
                ["doorHeightMm"] = result.DoorHeightMm,
                ["warnings"] = new JArray(warnings),
                ["ledger"] = ledger
            };
        }
    }

    public sealed class ClearMaintenanceReachabilityTool : IRevitTool
    {
        public string Name => "clear_maintenance_reachability";
        public string Description => "仅删除 OpenRevit Tools 拥有的维修可达 DirectShape；可选同时清空当前文档的内存快照。";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["clearStoredAnalysis"] = new JObject { ["type"] = "boolean", ["default"] = true }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            Document doc = AnalyzeMaintenanceReachabilityTool.RequireUiDocument(uiapp).Document;
            int deleted = MaintenanceVisualizationService.Clear(uiapp);
            bool clearStored = input == null || input["clearStoredAnalysis"] == null || (bool)input["clearStoredAnalysis"];
            if (clearStored) MaintenanceAnalysisStore.Clear(doc);
            var warnings = new List<string>();
            JObject ledger = MaintenanceLedgerAutoSync.TryWrite(
                doc,
                MaintenanceLedgerSyncService.GetModelFingerprint(doc),
                warnings,
                "模型删除");
            return new JObject
            {
                ["deletedElementCount"] = deleted,
                ["storedAnalysisCleared"] = clearStored,
                ["warnings"] = new JArray(warnings),
                ["ledger"] = ledger
            };
        }
    }

    internal static class MaintenanceJson
    {
        internal static JObject BuildLinkScope(MaintenanceLinkScopeSnapshot scope)
        {
            scope = scope ?? new MaintenanceLinkScopeSnapshot();
            Func<MaintenanceLinkScopeEntry, JObject> build = x => new JObject
            {
                ["key"] = x.GetStableKey(),
                ["linkInstanceId"] = x.LinkInstanceId,
                ["linkInstanceUniqueId"] = string.IsNullOrWhiteSpace(x.LinkInstanceUniqueId)
                    ? JValue.CreateNull()
                    : (JToken)x.LinkInstanceUniqueId,
                ["instanceName"] = x.InstanceName,
                ["typeName"] = x.TypeName,
                ["loadedAtAnalysis"] = x.LoadedAtAnalysis
            };
            return new JObject
            {
                ["contract"] = MaintenanceLinkScopePolicy.ContractVersion,
                ["explicit"] = scope.Explicit,
                ["hostAlwaysIncluded"] = true,
                ["relevantLinkCount"] = scope.RelevantLinks.Count,
                ["relevantLinks"] = new JArray(scope.RelevantLinks
                    .Where(x => x != null)
                    .OrderBy(x => x.GetStableKey(), StringComparer.Ordinal)
                    .Select(build)),
                ["outOfScopeLinkCount"] = scope.OutOfScopeLinks.Count,
                ["outOfScopeLinks"] = new JArray(scope.OutOfScopeLinks
                    .Where(x => x != null)
                    .OrderBy(x => x.GetStableKey(), StringComparer.Ordinal)
                    .Select(build)),
                ["warning"] = scope.Explicit && scope.OutOfScopeLinks.Count > 0
                    ? (JToken)"outOfScope links were deliberately excluded and were not checked for devices, obstacles, walls, transforms, or collection failures."
                    : JValue.CreateNull()
            };
        }

        internal static JObject BuildSummary(MaintenanceAnalysisResult result)
        {
            var decisions = result.TargetResults.GroupBy(x => x.Decision.ToString())
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
            var targets = new JArray();
            foreach (MaintenanceTargetResult item in result.TargetResults)
            {
                targets.Add(new JObject
                {
                    ["group"] = item.GroupKey,
                    ["targetKey"] = item.Target == null ? null : item.Target.TargetKey,
                    ["deviceNo"] = item.Target == null ? null : item.Target.DeviceNo,
                    ["target"] = item.Target == null ? null : item.Target.GetDisplayName(),
                    ["decision"] = item.Decision.ToString(),
                    ["accessProfile"] = item.Profile.ToString(),
                    ["completeChainSucceeded"] = item.CompleteChainSucceeded,
                    ["reason"] = item.DecisionReason,
                    ["routeLengthMm"] = Math.Round(item.RouteLengthMm, 1),
                    ["entryType"] = item.SelectedEntry == null ? null : item.SelectedEntry.EntryType.ToString(),
                    ["ladderType"] = item.SelectedEntry == null ? null : item.SelectedEntry.LadderType.ToString(),
                    ["openingWidthMm"] = item.SelectedEntry == null
                        ? JValue.CreateNull()
                        : (JToken)Math.Round(item.SelectedEntry.OpeningWidthMm, 1),
                    ["openingHeightMm"] = item.SelectedEntry == null
                        ? JValue.CreateNull()
                        : (JToken)Math.Round(item.SelectedEntry.OpeningHeightMm, 1),
                    ["exemptPipeEvidence"] = MaintenanceCandidateJson.BuildExemptPipeEvidence(
                        result,
                        item.GroupKey,
                        item.Target == null ? null : item.Target.TargetKey),
                    ["blockers"] = new JArray(item.Blockers.Select(x => new JObject
                    {
                        ["key"] = x.GetStableKey(), ["uniqueId"] = x.UniqueId, ["category"] = x.Category, ["name"] = x.Name
                    }))
                });
            }
            var wallAlternatives = new JArray();
            foreach (MaintenanceWallAlternativeResult alternative in result.WallAlternatives
                .Where(x => x != null)
                .OrderBy(x => x.GroupKey, StringComparer.Ordinal)
                .ThenBy(x => x.DeviceNo, StringComparer.Ordinal)
                .ThenBy(x => x.TargetKey, StringComparer.Ordinal))
            {
                wallAlternatives.Add(new JObject
                {
                    ["alternativeKey"] = alternative.AlternativeKey,
                    ["group"] = alternative.GroupKey,
                    ["targetKey"] = alternative.TargetKey,
                    ["deviceNo"] = alternative.DeviceNo,
                    ["schemeNo"] = alternative.SchemeNo > 0
                        ? (JToken)alternative.SchemeNo
                        : JValue.CreateNull(),
                    ["entryGroup"] = string.IsNullOrWhiteSpace(alternative.EntryGroup)
                        ? JValue.CreateNull()
                        : (JToken)alternative.EntryGroup,
                    ["viewName"] = string.IsNullOrWhiteSpace(alternative.ViewName)
                        ? JValue.CreateNull()
                        : (JToken)alternative.ViewName,
                    ["status"] = alternative.Status.ToString(),
                    ["canVisualize"] = alternative.CanVisualize,
                    ["sameAsRouteFormal"] = alternative.SameAsRouteFormal,
                    ["reason"] = alternative.Reason,
                    ["accessProfile"] = alternative.Profile.ToString(),
                    ["entryType"] = alternative.EntryType.ToString(),
                    ["ladderType"] = alternative.LadderType.ToString(),
                    ["decision"] = alternative.Decision.ToString(),
                    ["decisionReason"] = alternative.DecisionReason,
                    ["routeLengthMm"] = Math.Round(alternative.RouteLengthMm, 1),
                    ["entryKey"] = alternative.SelectedEntry == null
                        ? null
                        : alternative.SelectedEntry.CandidateKey,
                    ["openingWidthMm"] = alternative.SelectedEntry == null
                        ? JValue.CreateNull()
                        : (JToken)Math.Round(alternative.SelectedEntry.OpeningWidthMm, 1),
                    ["openingHeightMm"] = alternative.SelectedEntry == null
                        ? JValue.CreateNull()
                        : (JToken)Math.Round(alternative.SelectedEntry.OpeningHeightMm, 1),
                    ["geometryFingerprint"] = string.IsNullOrWhiteSpace(
                        alternative.GeometryFingerprint)
                        ? JValue.CreateNull()
                        : (JToken)alternative.GeometryFingerprint,
                    ["renderItemCount"] = alternative.RenderItems.Count,
                    ["blockers"] = new JArray(alternative.Blockers.Select(x =>
                        new JObject
                        {
                            ["key"] = x.GetStableKey(),
                            ["uniqueId"] = x.UniqueId,
                            ["category"] = x.Category,
                            ["name"] = x.Name
                        }))
                });
            }
            var sharedCeilingEntryAlternatives = new JArray(
                result.SharedCeilingEntryAlternatives
                    .Where(x => x != null)
                    .Select(x => new JObject
                    {
                        ["candidateKey"] = x.CandidateKey,
                        ["group"] = x.GroupKey,
                        ["accessProfile"] = x.Profile.ToString(),
                        ["status"] = x.Status.ToString(),
                        ["allTargetsComplete"] = x.AllTargetsComplete,
                        ["coveredTargetCount"] = x.CoveredTargetCount,
                        ["targetKeys"] = new JArray(x.TargetKeys),
                        ["entryCenterMm"] = new JObject
                        {
                            ["x"] = Math.Round(x.EntryCenter.X, 1),
                            ["y"] = Math.Round(x.EntryCenter.Y, 1),
                            ["z"] = Math.Round(x.EntryCenter.Z, 1)
                        },
                        ["openingWidthMm"] = Math.Round(x.OpeningWidthMm, 1),
                        ["openingHeightMm"] = Math.Round(x.OpeningHeightMm, 1),
                        ["maxRouteLengthMm"] = Math.Round(x.MaxRouteLengthMm, 1)
                    }));
            return new JObject
            {
                ["analysisId"] = result.AnalysisId,
                ["createdAtUtc"] = result.CreatedAtUtc.ToString("o"),
                ["modelFingerprint"] = result.ModelFingerprint,
                ["evidenceFingerprint"] = result.EvidenceFingerprint,
                ["doorWidthMm"] = result.DoorWidthMm,
                ["doorHeightMm"] = result.DoorHeightMm,
                ["ceilingHatchSizeMm"] = result.CeilingHatchSizeMm,
                ["sharedCeilingEntryReview"] = result.SharedCeilingEntryReview,
                ["sharedCeilingEntryPolicy"] =
                    MaintenanceSharedCeilingEntryPolicy.PolicyVersion,
                ["sharedCeilingEntryAlternativeCount"] =
                    result.SharedCeilingEntryAlternatives.Count,
                ["sharedCeilingEntryAlternatives"] = sharedCeilingEntryAlternatives,
                ["evidenceSourceCount"] = result.EvidenceSources.Count,
                ["evidenceScopeDefinition"] = result.EvidenceScopeDefinition,
                ["linkScope"] = BuildLinkScope(result.LinkScope),
                ["outOfScopeLinkCount"] = result.LinkScope == null
                    ? 0
                    : result.LinkScope.OutOfScopeLinks.Count,
                ["evidenceCollectionComplete"] = result.EvidenceCollectionComplete,
                ["collectionFailureCount"] = result.CollectionFailures.Count,
                ["collectionFailures"] = new JArray(result.CollectionFailures
                    .Where(x => x != null)
                    .Select(x => new JObject
                    {
                        ["group"] = x.GroupKey,
                        ["sourceKey"] = x.SourceKey,
                        ["linkInstanceId"] = x.LinkInstanceId.HasValue
                            ? (JToken)new JValue(x.LinkInstanceId.Value)
                            : JValue.CreateNull(),
                        ["linkInstanceUniqueId"] = string.IsNullOrWhiteSpace(x.LinkInstanceUniqueId)
                            ? JValue.CreateNull()
                            : new JValue(x.LinkInstanceUniqueId),
                        ["elementId"] = x.ElementId,
                        ["category"] = x.Category,
                        ["reason"] = x.Reason
                    })),
                ["exemptPipeEvidenceCount"] = result.ExemptPipeEvidence.Count,
                ["exemptPipeEvidence"] = MaintenanceCandidateJson.BuildExemptPipeEvidence(
                    result,
                    null,
                    null),
                ["candidateOnly"] = true,
                ["reviewStatement"] = "几何候选证据，须由顶级 AI 结合项目语义审查并由专业/施工人员确认；不是规范合规证明。",
                ["groupCount"] = result.Groups.Count,
                ["targetCount"] = result.TargetResults.Count,
                ["renderItemCount"] = result.RenderItems.Count,
                ["wallAlternativeCount"] = result.WallAlternatives.Count,
                ["modelableWallAlternativeCount"] = result.WallAlternatives.Count(x =>
                    x != null && x.CanVisualize),
                ["wallAlternativeFingerprint"] = result.WallAlternativeFingerprint,
                ["wallAlternatives"] = wallAlternatives,
                ["candidateAuditEnabled"] = result.CandidateAuditEnabled,
                ["candidateAuditComplete"] = result.CandidateAuditComplete,
                ["candidateAuditScopeDefinition"] = result.CandidateAuditScopeDefinition,
                ["candidateAuditScopeDescription"] = result.CandidateAuditScopeDescription,
                ["candidateAuditAllPathsEnumerated"] = result.CandidateAuditAllPathsEnumerated,
                ["candidateAuditRoutePolicy"] = result.CandidateAuditRoutePolicy,
                ["candidateAuditSelectionPolicy"] = result.CandidateAuditSelectionPolicy,
                ["candidateAuditDisplayRankingPolicy"] = result.CandidateAuditDisplayRankingPolicy,
                ["candidateAuditFingerprint"] = result.CandidateAuditFingerprint,
                ["candidateEvaluationCount"] = result.CandidateEvaluations.Count,
                ["invalidatedSelectedCount"] = result.CandidateEvaluations.Count(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Rejected),
                ["unverifiedSelectedCount"] = result.CandidateEvaluations.Count(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Unverified),
                ["requiresReselection"] = result.CandidateEvaluations.Any(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Rejected),
                ["requiresSelectedReview"] = result.CandidateEvaluations.Any(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Unverified),
                ["decisionCounts"] = JObject.FromObject(decisions),
                ["coverageLimitations"] = new JArray(result.CoverageLimitations),
                ["warnings"] = new JArray(result.Warnings),
                ["targets"] = targets
            };
        }
    }
}

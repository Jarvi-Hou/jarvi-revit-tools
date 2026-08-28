using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JarviTools.Commands.MaintenanceReachability;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    public sealed class ShowMaintenanceHandReachTool : IRevitTool
    {
        public string Name => "show_maintenance_hand_reach";

        public string Description =>
            "将已审批的 HandReach 快照写入当前模型：正式 ApplicationId DirectShape、8 个共享参数全填。createViews=true 时生成正式方案视图、天花设备方案总览和天花 AI 内部分析视图；正式视图归入“三维-空间可达性分析”，AI视图归入“三维-AI内部分析”。同时把该楼层全部正式维修可达常规模型同步显示到已有的“楼层{层号}-整体可达”视图；若整层视图不存在则只警告、不擅自创建。普通{三维}不作为结果视图。默认只写图元不建视图。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["analysisId"] = new JObject { ["type"] = "string" },
                ["approvalToken"] = new JObject { ["type"] = "string" },
                ["createViews"] = new JObject { ["type"] = "boolean", ["default"] = false, ["description"] = "是否按需生成设备方案视图并归入“三维-空间可达性分析”。默认 false：只写图元、不建视图。" }
            },
            ["required"] = new JArray { "analysisId", "approvalToken" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            Document doc = AnalyzeMaintenanceReachabilityTool.RequireUiDocument(uiapp).Document;
            HandReachAnalysisResult result = GetMaintenanceHandReachSummaryTool.RequireResult(doc, input);
            MaintenanceHandReachStore.RequireApproval(doc, result, input == null ? null : (string)input["approvalToken"]);
            bool createViews = input != null && ((bool?)input["createViews"]).GetValueOrDefault();

            MaintenanceHandReachVisualizationService.ShowStats stats =
                MaintenanceHandReachVisualizationService.Show(
                    uiapp,
                    result,
                    createViews,
                    MaintenanceHandReachStore.GetApprovalReviewer(),
                    MaintenanceHandReachStore.GetApprovalNote(),
                    MaintenanceHandReachStore.GetApprovedAtUtc());
            MaintenanceHandReachStore.ConsumeApproval(doc, result);

            JObject ledgerSync = MaintenanceLedgerAutoSync.TryWrite(
                doc,
                MaintenanceLedgerSyncService.GetModelFingerprint(doc),
                stats.Warnings,
                "模型显示");

            return new JObject
            {
                ["analysisId"] = result.AnalysisId,
                ["createdElementCount"] = stats.CreatedElementCount,
                ["deletedPreviousElementCount"] = stats.DeletedPreviousElementCount,
                ["deletedLegacyViewCount"] = stats.DeletedLegacyViewCount,
                ["createdViewCount"] = stats.CreatedViewCount,
                ["viewNames"] = new JArray(stats.ViewNames),
                ["viewIds"] = new JArray(stats.ViewIds),
                ["warnings"] = new JArray(stats.Warnings),
                ["approvalTokenConsumed"] = true,
                ["ledger"] = ledgerSync
            };
        }
    }

    public sealed class ClearMaintenanceHandReachTool : IRevitTool
    {
        public string Name => "clear_maintenance_hand_reach";

        public string Description => "定向删除 OpenRevit Tools 拥有的 HandReach 方案模型/自有管理视图。提供 groupKey+targetKey+deviceNo+schemeNo 时只删该目标方案；全部省略时只清理当前内存快照对应方案。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["groupKey"] = new JObject { ["type"] = "string", ["description"] = "可选：天花逻辑组，例如8A。与deviceNo、schemeNo同时提供。" },
                ["targetKey"] = new JObject { ["type"] = "string", ["description"] = "定向清理必填：稳定设备 TargetKey。" },
                ["deviceNo"] = new JObject { ["type"] = "string", ["description"] = "可选：设备编号，例如01。" },
                ["schemeNo"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["description"] = "可选：方案编号。" },
                ["clearStoredAnalysis"] = new JObject { ["type"] = "boolean", ["default"] = true }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            Document doc = AnalyzeMaintenanceReachabilityTool.RequireUiDocument(uiapp).Document;
            string groupKey = input == null ? null : (string)input["groupKey"];
            string targetKey = input == null ? null : (string)input["targetKey"];
            string deviceNo = input == null ? null : (string)input["deviceNo"];
            int? schemeNo = input == null || input["schemeNo"] == null
                ? (int?)null
                : (int)input["schemeNo"];
            bool anyScope = !string.IsNullOrWhiteSpace(groupKey) ||
                            !string.IsNullOrWhiteSpace(targetKey) ||
                            !string.IsNullOrWhiteSpace(deviceNo) || schemeNo.HasValue;
            bool completeScope = !string.IsNullOrWhiteSpace(groupKey) &&
                                 !string.IsNullOrWhiteSpace(targetKey) &&
                                 !string.IsNullOrWhiteSpace(deviceNo) && schemeNo.HasValue;
            if (anyScope && !completeScope)
                throw new ArgumentException("定向清理必须同时提供 groupKey、targetKey、deviceNo、schemeNo。");
            MaintenanceHandReachVisualizationService.ClearStats targetedStats =
                completeScope
                    ? MaintenanceHandReachVisualizationService.ClearDetailed(
                        uiapp,
                        groupKey,
                        targetKey,
                        deviceNo,
                        schemeNo.Value)
                    : MaintenanceHandReachVisualizationService.ClearCurrentDetailed(uiapp);
            int deleted = targetedStats.TotalDeletedCount;
            bool clearStored = input == null || input["clearStoredAnalysis"] == null || (bool)input["clearStoredAnalysis"];
            if (clearStored) MaintenanceHandReachStore.Clear(doc);
            var warnings = targetedStats.Warnings;
            JObject ledger = MaintenanceLedgerAutoSync.TryWrite(
                doc,
                MaintenanceLedgerSyncService.GetModelFingerprint(doc),
                warnings,
                "模型删除");
            return new JObject
            {
                ["deletedElementCount"] = deleted,
                ["deletedShapeCount"] = targetedStats.DeletedShapeCount,
                ["deletedViewCount"] = targetedStats.DeletedViewCount,
                ["warnings"] = new JArray(warnings),
                ["targeted"] = completeScope,
                ["groupKey"] = completeScope ? groupKey : null,
                ["targetKey"] = completeScope ? targetKey : null,
                ["deviceNo"] = completeScope ? deviceNo : null,
                ["schemeNo"] = completeScope ? (JToken)schemeNo.Value : JValue.CreateNull(),
                ["storedAnalysisCleared"] = clearStored,
                ["ledger"] = ledger
            };
        }
    }
}

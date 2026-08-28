using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JarviTools.Commands.MaintenanceReachability;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    public sealed class ShowMaintenanceWallAlternativeTool : IRevitTool
    {
        public string Name => "show_maintenance_wall_alternative";
        public string Description =>
            "将已审批快照中的一个完整侧墙备选写入独立 Owner 和独立三维视图，并强制归入“三维-空间可达性分析”；自动同步天花设备方案总览、AI内部分析和既有整层整体可达视图；按 group+target/device+scheme 精确替换，普通{三维}不作为结果视图，同一正式路线几何不得重复生成。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["analysisId"] = new JObject { ["type"] = "string" },
                ["approvalToken"] = new JObject { ["type"] = "string" },
                ["groupKey"] = new JObject { ["type"] = "string" },
                ["targetKey"] = new JObject { ["type"] = "string" },
                ["deviceNo"] = new JObject { ["type"] = "string" },
                ["schemeNo"] = new JObject { ["type"] = "integer", ["minimum"] = 1 }
            },
            ["required"] = new JArray
            {
                "analysisId", "approvalToken", "groupKey", "targetKey",
                "deviceNo", "schemeNo"
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            Document doc = AnalyzeMaintenanceReachabilityTool.RequireUiDocument(uiapp).Document;
            MaintenanceAnalysisResult result =
                GetMaintenanceReachabilitySummaryTool.RequireResult(doc, input);
            string groupKey = input == null ? null : (string)input["groupKey"];
            string targetKey = input == null ? null : (string)input["targetKey"];
            string deviceNo = input == null ? null : (string)input["deviceNo"];
            int schemeNo = input == null || input["schemeNo"] == null
                ? 0
                : (int)input["schemeNo"];
            MaintenanceWallAlternativeResult alternative = result.WallAlternatives
                .SingleOrDefault(x => x != null &&
                    string.Equals(x.GroupKey, groupKey, StringComparison.Ordinal) &&
                    string.Equals(x.TargetKey, targetKey, StringComparison.Ordinal) &&
                    string.Equals(x.DeviceNo, deviceNo, StringComparison.Ordinal) &&
                    x.SchemeNo == schemeNo);
            if (alternative == null)
                throw new InvalidOperationException(
                    "未找到完全匹配 group+target+device+scheme 的侧墙备选，请重新读取摘要。");
            if (!alternative.CanVisualize)
                throw new InvalidOperationException(
                    "该侧墙备选不可建模：" + (alternative.Reason ?? string.Empty));

            string approvalToken = input == null ? null : (string)input["approvalToken"];
            MaintenanceAnalysisStore.RequireApproval(doc, result, approvalToken);
            MaintenanceWallAlternativeVisualizationService.ShowStats stats =
                MaintenanceWallAlternativeVisualizationService.Show(
                    uiapp, result, alternative);
            MaintenanceAnalysisStore.ConsumeApproval(doc, result, approvalToken);
            JObject ledger = MaintenanceLedgerAutoSync.TryWrite(
                doc, result.ModelFingerprint, stats.Warnings, "模型显示");
            return new JObject
            {
                ["analysisId"] = result.AnalysisId,
                ["groupKey"] = alternative.GroupKey,
                ["targetKey"] = alternative.TargetKey,
                ["deviceNo"] = alternative.DeviceNo,
                ["schemeNo"] = alternative.SchemeNo,
                ["alternativeKey"] = alternative.AlternativeKey,
                ["geometryFingerprint"] = alternative.GeometryFingerprint,
                ["createdElementCount"] = stats.CreatedElementCount,
                ["reusedFormalElementCount"] = stats.ReusedFormalElementCount,
                ["reusedRouteFormalGeometry"] = stats.ReusedFormalElementCount > 0,
                ["deletedPreviousElementCount"] = stats.DeletedPreviousElementCount,
                ["createdViewCount"] = stats.CreatedViewCount,
                ["viewId"] = stats.ViewId,
                ["viewName"] = stats.ViewName,
                ["overviewViewId"] = stats.OverviewViewId,
                ["overviewViewName"] = stats.OverviewViewName,
                ["warnings"] = new JArray(stats.Warnings),
                ["approvalTokenConsumed"] = true,
                ["ledger"] = ledger
            };
        }
    }

    public sealed class ClearMaintenanceWallAlternativeTool : IRevitTool
    {
        public string Name => "clear_maintenance_wall_alternative";
        public string Description =>
            "按 group+target+device+scheme 精确删除侧墙备选模型及其自有独立视图；不删除正式最佳方案、HandReach 或用户同名视图。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["groupKey"] = new JObject { ["type"] = "string" },
                ["targetKey"] = new JObject { ["type"] = "string" },
                ["deviceNo"] = new JObject { ["type"] = "string" },
                ["schemeNo"] = new JObject { ["type"] = "integer", ["minimum"] = 1 },
                ["clearStoredAnalysis"] = new JObject
                {
                    ["type"] = "boolean",
                    ["default"] = false
                }
            },
            ["required"] = new JArray
            {
                "groupKey", "targetKey", "deviceNo", "schemeNo"
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            Document doc = AnalyzeMaintenanceReachabilityTool.RequireUiDocument(uiapp).Document;
            MaintenanceWallAlternativeVisualizationService.ClearStats stats =
                MaintenanceWallAlternativeVisualizationService.Clear(
                    uiapp,
                    (string)input["groupKey"],
                    (string)input["targetKey"],
                    (string)input["deviceNo"],
                    (int)input["schemeNo"]);
            bool clearStored = input["clearStoredAnalysis"] != null &&
                               (bool)input["clearStoredAnalysis"];
            if (clearStored) MaintenanceAnalysisStore.Clear(doc);
            JObject ledger = MaintenanceLedgerAutoSync.TryWrite(
                doc,
                MaintenanceLedgerSyncService.GetModelFingerprint(doc),
                stats.Warnings,
                "模型删除");
            return new JObject
            {
                ["deletedShapeCount"] = stats.DeletedShapeCount,
                ["deletedViewCount"] = stats.DeletedViewCount,
                ["totalDeletedCount"] = stats.TotalDeletedCount,
                ["warnings"] = new JArray(stats.Warnings),
                ["storedAnalysisCleared"] = clearStored,
                ["ledger"] = ledger
            };
        }
    }

    internal static class MaintenanceLedgerAutoSync
    {
        internal static JObject TryWrite(
            Document doc,
            string expectedModelFingerprint,
            IList<string> warnings,
            string completedAction)
        {
            MaintenanceLedgerDestination destination;
            if (!MaintenanceLedgerConfigurationService.TryResolve(doc, out destination))
                return new JObject { ["status"] = "destination_not_configured" };
            try
            {
                MaintenanceLedgerSyncResult ledger = MaintenanceLedgerSyncService.Export(
                    doc,
                    new MaintenanceLedgerSyncOptions
                    {
                        OutputDirectory = destination.OutputDirectory,
                        FilePrefix = destination.FilePrefix,
                        ExpectedModelFingerprint = expectedModelFingerprint,
                        DryRun = false
                    });
                return new JObject
                {
                    ["status"] = "written",
                    ["manifestJsonPath"] = ledger.ManifestJsonPath,
                    ["snapshotHashSha256"] = ledger.SnapshotHashSha256,
                    ["userLedgerRowCount"] = ledger.UserLedgerRowCount
                };
            }
            catch (Exception exception)
            {
                if (warnings != null)
                    warnings.Add((completedAction ?? "模型操作") +
                                 "已成功，但 DirectShape 台账快照写入中断；必须重新校验 manifest 与全部文件哈希，不匹配时整组文件均不得使用：" +
                                 exception.Message);
                return new JObject
                {
                    ["status"] = exception is InvalidDataException
                        ? "sync_blocked_by_manual_orphan_old_snapshot_not_current"
                        : "snapshot_write_failed_manifest_must_be_revalidated",
                    ["error"] = exception.Message,
                    ["safetyNote"] = "现有 CSV/manifest 无论内部哈希是否相互匹配，都只是本次模型操作前的旧快照，不能当作当前模型状态；须处理人工孤儿行或写入错误后重新同步并重新校验。"
                };
            }
        }
    }
}

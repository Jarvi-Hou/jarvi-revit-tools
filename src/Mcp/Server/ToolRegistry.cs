using System.Collections.Generic;
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using JarviTools.Mcp.Tools;

namespace JarviTools.Mcp.Server
{
    public class ToolRegistry
    {
        private readonly Dictionary<string, IRevitTool> _tools = new Dictionary<string, IRevitTool>();

        public void RegisterAll()
        {
            // Phase 1 — read-only
            Register(new GetModelInfoTool());
            Register(new ListElementsTool());
            Register(new GetElementParametersTool());
            Register(new ListSheetsAndViewsTool());
            Register(new GetWarningsSummaryTool());

            // Phase 2 — writes (Transaction-wrapped)
            Register(new SetElementParameterTool());
            Register(new SetParametersBatchTool());

            // Phase 2.5 — element creation
            Register(new CreateLevelTool());
            Register(new CreateWallTool());

            // Phase 3 — compliance checks
            Register(new CheckDoorClearWidthsTool());

            // JarviTools 业务命令的 MCP 包装
            Register(new MatchQuantityParametersTool());
            Register(new FilterUnmatchedElementsTool());
            Register(new ExportVisibleElementsTool());
            Register(new ExportAllSchedulesTool());
            Register(new ParameterManagerStatsTool());

            // Phase 4 — 元素深度查询（只读）
            Register(new CountElementsByCategoryTool());
            Register(new FindElementsByParamValueTool());
            Register(new GetFamilyListTool());
            Register(new FindUnusedFamiliesTool());
            Register(new GetRoomFinishesTool());

            // Phase 5 — 选择 / 隔离（UI 操作）
            Register(new SelectElementsByIdsTool());
            Register(new SelectByCategoryTool());
            Register(new IsolateInViewTool());
            Register(new HideInViewTool());
            Register(new UnhideAllTool());

            // Phase 6 — Sheet / View 分析（只读）
            Register(new GetViewsOnSheetsTool());
            Register(new GetUnplacedViewsTool());
            Register(new GetViewFiltersTool());
            Register(new GetViewTemplateInfoTool());
            Register(new GetSheetRevisionsTool());

            // Phase 7 — 模型健康 / Audit（只读）
            Register(new GetModelHealthTool());
            Register(new FindUnusedTypesTool());
            Register(new CountGroupsAndInstancesTool());
            Register(new FindUnhostedElementsTool());
            Register(new GetLinkStatusTool());

            // Phase 8 — 写入扩展（Transaction 包裹）
            Register(new CreateFloorTool());
            Register(new CreateColumnTool());
            Register(new DeleteElementTool());
            Register(new MoveElementTool());

            // Phase 9 — 元素深度查询 & UI 扩展
            Register(new GetFamilySizesMbTool());
            Register(new GetFamilySizesTopNTool());
            Register(new FindDuplicateTypesTool());
            Register(new GetRoomBoundariesTool());
            Register(new GetElementGeometryTool());
            Register(new GetPhaseInfoTool());
            Register(new ZoomToElementTool());

            // Phase 10 — 视图/图纸操作（写入）
            Register(new DuplicateViewTool());
            Register(new ApplyViewTemplateTool());
            Register(new CreateSheetTool());
            Register(new PlaceViewOnSheetTool());
            Register(new AddTextNoteTool());

            // Phase 11 — 视图设置（写入）
            Register(new SetViewScaleTool());
            Register(new SetViewDetailLevelTool());
            Register(new SetViewPhaseTool());
            Register(new CropViewToElementsTool());

            // Phase 12 — 视口操作（写入）
            Register(new RemoveViewFromSheetTool());
            Register(new MoveViewportTool());
            Register(new AlignViewportsTool());

            // Phase 13 — 高级视图创建（写入）
            Register(new Create3DViewTool());
            Register(new CreatePlanViewTool());
            Register(new CreateSectionTool());

            // Phase 14 — 分级聚合统计（只读）
            Register(new GetWallsSummaryTool());
            Register(new GetDoorsSummaryTool());
            Register(new GetWindowsSummaryTool());
            Register(new GetFloorsSummaryTool());
            Register(new GetRoomsSummaryTool());

            // Phase 15 — CSV 导出
            Register(new ExportQuantitiesToCsvTool());
            Register(new ExportRoomsToCsvTool());
            Register(new ExportElementsWithParamsToCsvTool());

            // Phase 16 — 缺漏分析（只读）
            Register(new FindElementsMissingParamTool());
            Register(new FindUntaggedRoomsTool());
            Register(new FindUntaggedDoorsTool());
            Register(new FindRoomsWithMissingFinishesTool());

            // Phase 17 — MEP 查询（只读）
            Register(new ListMepElementsTool());
            Register(new GetDuctParametersTool());
            Register(new GetPipeParametersTool());
            Register(new GetElementConnectivityTool());
            Register(new GetMepSystemInfoTool());

            // Phase 18 — 碰撞检测（只读）
            Register(new RunClashDetectionTool());
            Register(new GetClashReportTool());
            Register(new HighlightClashTool());

            // Phase 19 — Revit built-in command bridge.
            if (IsInteractiveCommandBridgeEnabled())
                Register(new RunCommandTool());

            // Arbitrary C# is intentionally opt-in. It executes with the same full trust as Revit,
            // including file/process/network access, and is meant only for supervised AI/developer sessions.
            if (IsFullTrustCSharpEnabled())
                Register(new ExecuteCSharpTool());

            // Phase 20 — 装饰吊顶负空间场
            Register(new AnalyzePlenumSpaceTool());
            Register(new GetPlenumAnalysisSummaryTool());
            Register(new QueryPlenumRegionsTool());
            Register(new ShowPlenumAnalysisTool());
            Register(new ClearPlenumAnalysisTool());

            // Phase 21 — Revit 参数事实源 → AI 台账同步桥接
            Register(new MaintenanceLedgerSyncTool());
            Register(new AnalyzeMaintenanceReachabilityTool());
            Register(new AnalyzeMaintenanceRouteCandidatesTool());
            Register(new GetMaintenanceReachabilitySummaryTool());
            Register(new GetMaintenanceRouteCandidatesTool());
            Register(new ApproveMaintenanceReachabilityTool());
            Register(new ShowMaintenanceReachabilityTool());
            Register(new ClearMaintenanceReachabilityTool());
            Register(new ShowMaintenanceWallAlternativeTool());
            Register(new ClearMaintenanceWallAlternativeTool());

            // Phase 22 — 450×450侧墙默认、400×400显式缩小备选 / 天花450（data-only 默认）
            Register(new AnalyzeMaintenanceHandReachCandidatesTool());
            Register(new GetMaintenanceHandReachSummaryTool());
            Register(new GetMaintenanceHandReachCandidatesTool());
            Register(new ApproveMaintenanceHandReachTool());
            Register(new ShowMaintenanceHandReachTool());
            Register(new ClearMaintenanceHandReachTool());

            Logger.Info("Registered " + _tools.Count + " tools: " + string.Join(", ", _tools.Keys));
        }

        private static bool IsFullTrustCSharpEnabled()
        {
            var value = Environment.GetEnvironmentVariable("OPENREVIT_ENABLE_FULL_TRUST_CSHARP");
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInteractiveCommandBridgeEnabled()
        {
            var value = Environment.GetEnvironmentVariable("OPENREVIT_ENABLE_INTERACTIVE_COMMANDS");
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        public void Register(IRevitTool tool) => _tools[tool.Name] = tool;

        public IRevitTool Get(string name) =>
            _tools.TryGetValue(name, out var t) ? t : null;

        public IEnumerable<IRevitTool> All() => _tools.Values;

        /// <summary>Returns the tools/list payload for MCP.</summary>
        public JArray Describe()
        {
            var arr = new JArray();
            foreach (var t in _tools.Values.OrderBy(t => t.Name))
            {
                arr.Add(new JObject
                {
                    ["name"]        = t.Name,
                    ["description"] = t.Description,
                    ["inputSchema"] = t.InputSchema
                });
            }
            return arr;
        }
    }
}

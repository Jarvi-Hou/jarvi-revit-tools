using System;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JarviTools.Commands.Plenum;
using JarviTools.Mcp;
using JarviTools.Mcp.Server;

namespace JarviTools.Commands.MaintenanceReachability
{
    /// <summary>
    /// 维修可达是 AI 协同工作流，不是单机按钮可独立完成的确定性计算。
    /// 本命令只负责检查准备状态、启动 MCP 和交付给顶级 AI；
    /// 真正分析由 AI + MCP + 负空间分析协同执行。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class MaintenanceReachabilityCommand : IExternalCommand
    {
        private const string DialogTitle = "AI维修可达入口";

        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            try
            {
                UIApplication uiapp = commandData == null
                    ? null
                    : commandData.Application;
                UIDocument uidoc = uiapp == null ? null : uiapp.ActiveUIDocument;

                TaskDialog dialog = BuildEntryDialog(uidoc);
                TaskDialogResult result = dialog.Show();

                if (result == TaskDialogResult.CommandLink1)
                {
                    ShowMcpStatusAndStartIfNeeded();
                }
                else if (result == TaskDialogResult.CommandLink2)
                {
                    CopyAiHandoffPrompt(uidoc);
                }
                else if (result == TaskDialogResult.CommandLink3)
                {
                    ShowPreparationGuide(uidoc);
                }

                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                Logger.Error("MaintenanceReachabilityCommand failed", exception);
                message = exception.Message;
                TaskDialog.Show(DialogTitle + "｜错误", exception.Message);
                return Result.Failed;
            }
        }

        private static TaskDialog BuildEntryDialog(UIDocument uidoc)
        {
            TaskDialog dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = "这是 AI 协同入口，不是按钮单独自动分析。",
                MainContent =
                    "正式维修可达需要：\n" +
                    "顶级 AI + Revit MCP + 负空间分析。\n" +
                    "入口顺序：侧墙450×450探身伸手优先；明确现场复核时可缩为400×400；其后是侧墙600×600爬入式检修门，最后才是天花450×450检修。\n" +
                    "默认只生成数据和台账，不建 Revit 视图；视图仅按需生成。\n\n" +
                    BuildReadinessSummary(uidoc) + "\n\n" +
                    "请选择下一步：",
                CommonButtons = TaskDialogCommonButtons.Close,
                FooterText = "按钮本身不生成或修改维修可达结果。"
            };

            dialog.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink1,
                "检查并启动 Revit MCP");
            dialog.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink2,
                "复制正式分析提示（侧墙450/400 / 600门 / 天花450）");
            dialog.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink3,
                "查看负空间与模型准备步骤");
            return dialog;
        }

        private static string BuildReadinessSummary(UIDocument uidoc)
        {
            string mcp = McpHost.Server != null && McpHost.Server.IsRunning
                ? "已运行（127.0.0.1:" + McpHost.Server.Port + "）"
                : "未启动";

            if (uidoc == null || uidoc.Document == null)
            {
                return "当前准备状态：\n" +
                       "• MCP：" + mcp + "\n" +
                       "• Revit 项目：未打开";
            }

            Document doc = uidoc.Document;
            bool isReadyView = doc.ActiveView is View3D && !doc.ActiveView.IsTemplate;
            int selectedCeilingCount = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .Count(IsCeiling);

            PlenumAnalysisResult stored = PlenumAnalysisStore.Get(doc);
            int plenumShapeCount = CountPlenumShapes(doc);
            string plenum;
            if (stored != null)
            {
                plenum = "已有当前会话结果（" +
                         stored.CeilingName + "，" + stored.Cells.Count + " 个单元）";
            }
            else if (plenumShapeCount > 0)
            {
                plenum = "模型中发现 " + plenumShapeCount +
                         " 个结果图元，需 AI 复核／必要时重算";
            }
            else
            {
                plenum = "未发现，需先做负空间分析";
            }

            return "当前准备状态：\n" +
                   "• MCP：" + mcp + "\n" +
                   "• 视图：" +
                   (isReadyView ? "普通三维视图（就绪）" : "请切换到普通三维视图") + "\n" +
                   "• 已选天花：" + selectedCeilingCount + " 块\n" +
                   "• 负空间：" + plenum;
        }

        private static int CountPlenumShapes(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(DirectShape))
                .Cast<DirectShape>()
                .Count(x => string.Equals(
                    x.ApplicationId,
                    PlenumVisualizationService.OwnerApplicationId,
                    StringComparison.Ordinal));
        }

        private static void ShowMcpStatusAndStartIfNeeded()
        {
            if (McpHost.Server != null && McpHost.Server.IsRunning)
            {
                TaskDialog.Show(
                    DialogTitle + "｜MCP就绪",
                    "Revit MCP 已运行：\nhttp://127.0.0.1:" +
                    McpHost.Server.Port + "/\n\n现在可复制分析提示并交给 Codex。");
                return;
            }

            if (!McpHost.IsInitialized)
                McpHost.Initialize();

            McpHost.Server = new HttpServer(7800);
            McpHost.Server.Start();
            TaskDialog.Show(
                DialogTitle + "｜MCP已启动",
                "Revit MCP 已启动：\nhttp://127.0.0.1:" +
                McpHost.Server.Port + "/\n\n下一步：再次点击入口，复制正式分析提示。");
        }

        private static void CopyAiHandoffPrompt(UIDocument uidoc)
        {
            string prompt = BuildAiHandoffPrompt(uidoc);
            Clipboard.SetText(prompt);
            TaskDialog.Show(
                DialogTitle + "｜已复制",
                "正式分析提示已复制到剪贴板。\n\n" +
                "请粘贴给支持 Revit MCP 的顶级 AI（Codex 或 DeepSeek 均可）。");
        }

        private static string BuildAiHandoffPrompt(UIDocument uidoc)
        {
            StringBuilder prompt = new StringBuilder();
            prompt.AppendLine("请通过当前 Revit MCP 执行正式维修可达分析（适用任意支持 MCP 的顶级 AI）。");
            prompt.AppendLine();
            prompt.AppendLine("这不是按钮单独计算：必须由顶级 AI + MCP + 负空间分析协同完成。");
            prompt.AppendLine("请先检查 MCP 连接、当前模型/视图、已选天花与负空间结果；信息不足时明确告诉我，不要猜测。");
            prompt.AppendLine("分析须结合宿主与链接模型中的结构、机电、风管附件及真实设备几何。");
            prompt.AppendLine("入口规则：");
            prompt.AppendLine("- 第一优先：按天花真实顶面边界生成100mm厚虚拟侧墙，再搜索450×450侧墙检修口；项目无需预建实体墙。人员可向洞内探身伸手，不要求完整穿门或900×900转身区，但必须完整验证方口、200mm最终伸手通道以及墙外梯具/操作位置。");
            prompt.AppendLine("- 第二优先（明确现场缩小备选）：只有用户明确要求，或450口确实放不下且需复核时，才以 hatchSizeMm=400、openingPreference=SideWallOnly 搜索400×400侧墙口；它只按探身伸手判断，不能作为人员入口，结果保持橙色待复核。");
            prompt.AppendLine("- 第三优先：侧墙爬入式检修门；默认净开口600×600mm，可通过 doorWidthMm、doorHeightMm 配置，并完整检查梯具、入口转身、连续路线和设备检修区。");
            prompt.AppendLine("- 第四优先：天花450×450检修；设备保持模型原高度。检修面处于天花厚度附近时，直接从设备正下方洞口伸手，不建立人员钻入包络；设备较高时，人通过人字梯从方口勉强钻入吊顶，再验证到设备检修面的最后200mm操作伸手段。模型与天花轻微交叠的直接伸手方案必须标橙色目视复核。");
            prompt.AppendLine("- 用户明确指定侧墙或门位时，以该现场意向为候选约束，不擅自换到另一面墙；用户若要求旧方案只替换门尺寸，则保留原门位、铰链、路线和其他模型，只替换门洞/门框/门扇并重新记录尺寸。");
            prompt.AppendLine("- 默认只生成数据和台账（data-only），不自动建Revit视图；视图仅按需生成。");
            prompt.AppendLine("- 视图治理：所有正式方案视图必须使用三维视图类型“三维-空间可达性分析”；楼层{层号}-整体可达看该层全部正式方案，天花{分组}-设备方案总览看本天花全部方案，天花{分组}-设备{编号}-方案{编号}-{类型}看单方案。正式显示时自动建立“天花{分组}-维修可达”并归入“三维-AI内部分析”，同时同步既有整层总览；普通{三维}不得作为维修可达结果视图。");
            prompt.AppendLine("执行顺序必须是：先调用 analyze_maintenance_hand_reach_candidates（默认 SideWallOnly、hatchSizeMm=450）检查侧墙450口；只有用户明确同意缩小备选时才用 hatchSizeMm=400 再跑 SideWallOnly；仍不成立时检查默认600×600侧墙爬入门；再不成立才以 hatchSizeMm=450、openingPreference=CeilingOnly检查天花口。不要使用AutoPreferSideWall跳过600门，也不要虚拟降低设备。以Revit实例参数为事实源，完成后通过MCP自动同步维修可达台账。");
            prompt.AppendLine("- 用户要求复核“多个检修口能否合并”时，必须把待比较天花明确选中，并调用 analyze_maintenance_route_candidates：strictCeilingSelection=true、combineSelectedCeilingsForSharedEntry=true。算法以一个450×450天花人员入口为起点，跨注释合并所选相邻天花轮廓，验证能否继续走到至少两台设备；不得只凭两个伸手候选区重叠就判定可以合并。返回的是代表性路线待复核备选，不声称穷举所有数学路径，也不自动替换正式方案。");

            if (uidoc != null && uidoc.Document != null)
            {
                Document doc = uidoc.Document;
                string ceilingIds = string.Join(
                    ", ",
                    uidoc.Selection.GetElementIds()
                        .Where(id => IsCeiling(doc.GetElement(id)))
                        .Select(id => id.Value.ToString())
                        .ToArray());
                prompt.AppendLine();
                prompt.AppendLine("当前 Revit 项目：" + doc.Title);
                prompt.AppendLine("当前视图：" + doc.ActiveView.Name);
                prompt.AppendLine("当前已选天花 ID：" +
                                  (string.IsNullOrWhiteSpace(ceilingIds) ? "无（请先在 Revit 选择）" : ceilingIds));
            }

            return prompt.ToString();
        }

        private static void ShowPreparationGuide(UIDocument uidoc)
        {
            TaskDialog dialog = new TaskDialog(DialogTitle + "｜准备步骤")
            {
                MainInstruction = "准备好模型证据，再交给 AI 做判断。",
                MainContent =
                    "1. 打开用于检查的普通三维视图。\n" +
                    "2. 选中目标天花；“注释”相同的天花按一个逻辑分组理解。\n" +
                    "3. 运行“负空间分析”，并保留当前结果。\n" +
                    "4. 从本入口检查／启动 MCP。\n" +
                    "5. 复制正式提示，交给支持 Revit MCP 的顶级 AI。\n\n" +
                    BuildReadinessSummary(uidoc),
                CommonButtons = TaskDialogCommonButtons.Close
            };
            dialog.Show();
        }

        private static bool IsCeiling(Element element)
        {
            return element != null &&
                   element.Category != null &&
                   element.Category.Id.Value == (long)BuiltInCategory.OST_Ceilings;
        }
    }
}

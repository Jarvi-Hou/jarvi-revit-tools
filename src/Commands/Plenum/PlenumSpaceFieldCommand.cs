using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JarviTools.Mcp.Server;

namespace JarviTools.Commands.Plenum
{
    [Transaction(TransactionMode.Manual)]
    public class PlenumSpaceFieldCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp == null ? null : uiapp.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("装饰负空间", "请先打开 Revit 项目。");
                return Result.Cancelled;
            }
            if (!(uidoc.Document.ActiveView is View3D))
            {
                TaskDialog.Show("装饰负空间", "V1 请在目标三维视图中运行。");
                return Result.Cancelled;
            }

            try
            {
                Element ceiling = PlenumAnalysisService.ResolveCeiling(uidoc, null);
                var config = new PlenumAnalysisConfig();
                PlenumAnalysisResult result = PlenumAnalysisService.Analyze(uiapp, ceiling, config);
                PlenumAnalysisStore.Set(uidoc.Document, result);
                if (config.ShowVisualization)
                    PlenumVisualizationService.Show(uiapp, result);

                var known = result.Cells.Where(c => !c.IsUnknown).ToList();
                string range = known.Count == 0
                    ? "无已知单元"
                    : Math.Round(known.Min(c => c.ConnectedFreeHeightMm), 0) + " – " +
                      Math.Round(known.Max(c => c.ConnectedFreeHeightMm), 0) + " mm";
                TaskDialog.Show("装饰负空间 — 完成",
                    "吊顶：" + result.CeilingName + " [" + result.CeilingId + "]\n" +
                    "分析单元：" + result.Cells.Count +
                    "（Unknown " + result.Cells.Count(c => c.IsUnknown) + "）\n" +
                    "吊顶连通净空：" + range + "\n" +
                    "候选构件：" + result.CandidateCount +
                    "（机电 " + result.MepCandidateCount +
                    " / 结构 " + result.StructureCandidateCount + "）\n" +
                    "三维色块：" + result.DirectShapeCount + "\n" +
                    "耗时：" + result.ElapsedMs + " ms\n\n" +
                    "红 <400，橙 400–699，黄 700–999，绿 ≥1000 mm；" +
                    "紫色为 MixedAtLeaf，灰色为 Unknown。\n" +
                    "色块已写入宿主模型，可撤销，也可用 MCP 清除。");
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                Logger.Error("PlenumSpaceFieldCommand failed", ex);
                message = ex.Message;
                TaskDialog.Show("装饰负空间 — 错误", ex.Message);
                return Result.Failed;
            }
        }
    }
}

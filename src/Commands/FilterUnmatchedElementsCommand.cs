using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JarviTools.Core;
using JarviTools.Mcp.Server;

namespace JarviTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class FilterUnmatchedElementsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show(Constants.PLUGIN_NAME, "请先打开一个Revit项目文件。");
                return Result.Cancelled;
            }

            try
            {
                Document doc = uidoc.Document;
                Autodesk.Revit.DB.View activeView = doc.ActiveView;

                if (activeView == null)
                {
                    TaskDialog.Show("提示", "没有活动视图，请先打开一个 3D 视图。");
                    return Result.Cancelled;
                }
                if (!(activeView is View3D))
                {
                    TaskDialog.Show("提示", "请在 3D 视图中运行此命令！\n\n当前视图类型：" + activeView.ViewType.ToString());
                    return Result.Cancelled;
                }

                IList<Element> visibleElements;
                using (FilteredElementCollector collector = new FilteredElementCollector(doc, activeView.Id))
                {
                    visibleElements = collector.WhereElementIsNotElementType().ToElements();
                }

                if (visibleElements.Count == 0)
                {
                    TaskDialog.Show("提示", "当前视图中没有可见构件。");
                    return Result.Cancelled;
                }

                bool hasParameters = false;
                foreach (Element elem in visibleElements)
                {
                    if (elem.get_Parameter(Constants.GUID_MAJOR) != null)
                    {
                        hasParameters = true;
                        break;
                    }
                }

                if (!hasParameters)
                {
                    TaskDialog.Show("提示", "未找到工程量统计参数！\n\n请先运行“一键匹配计量参数”命令。");
                    return Result.Cancelled;
                }

                List<ElementId> unmatchedIds = new List<ElementId>();
                int totalCount = 0;

                foreach (Element elem in visibleElements)
                {
                    if (elem.Category == null) continue;
                    totalCount++;

                    if (ElementDataHelper.IsUnmatched(elem))
                        unmatchedIds.Add(elem.Id);
                }

                if (unmatchedIds.Count == 0)
                {
                    TaskDialog.Show("提示", string.Format("所有构件都已匹配成功！\n\n共检查 {0} 个构件。", totalCount));
                    return Result.Succeeded;
                }

                using (Transaction trans = new Transaction(doc, "临时隔离未匹配构件"))
                {
                    trans.Start();
                    activeView.IsolateElementsTemporary(unmatchedIds);
                    TransactionSafety.Commit(trans, "Filter unmatched elements");
                }

                TaskDialog.Show("完成", string.Format(
                    "已临时隔离未匹配成功的构件！\n\n共检查：{0} 个\n未匹配成功：{1} 个\n\n请手动修改这些构件的参数值。\n完成后点击视图右上角“重设临时隐藏/隔离”即可恢复。",
                    totalCount, unmatchedIds.Count));

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Logger.Error("FilterUnmatchedElementsCommand failed", ex);
                TaskDialog.Show("错误", "执行失败：\n" + ex.Message);
                return Result.Failed;
            }
        }
    }
}

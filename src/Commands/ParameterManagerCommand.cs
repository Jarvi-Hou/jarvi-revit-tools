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
    /// <summary>
    /// 命令：参数管理器
    /// 统计项目中工程量参数的使用情况，仅做只读查看，无需事务
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ParameterManagerCommand : IExternalCommand
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

                int totalCount    = 0;
                int matchedCount  = 0;
                int unmatchedCount = 0;
                int noParamCount  = 0;

                // 性能：先用 doc.Settings.Categories 收集模型类别 Id，再以 ElementMulticategoryFilter
                // 限定扫描范围，避免遍历注解/视图/材质等无关图元。
                ICollection<ElementId> modelCategoryIds = doc.Settings.Categories
                    .Cast<Category>()
                    .Where(c => c != null && c.CategoryType == CategoryType.Model && c.AllowsBoundParameters)
                    .Select(c => c.Id)
                    .ToList();

                if (modelCategoryIds.Count == 0)
                {
                    TaskDialog.Show("参数管理器", "项目中没有可用的模型类别。");
                    return Result.Succeeded;
                }

                using (FilteredElementCollector collector = new FilteredElementCollector(doc))
                {
                    var modelElements = collector
                        .WherePasses(new ElementMulticategoryFilter(modelCategoryIds))
                        .WhereElementIsNotElementType()
                        .ToElements();

                    foreach (Element elem in modelElements)
                    {
                        if (elem.Category == null) continue;
                        totalCount++;

                        Parameter paramMajor = elem.get_Parameter(Constants.GUID_MAJOR);
                        if (paramMajor == null)
                        {
                            noParamCount++;
                            continue;
                        }

                        // 与 FilterUnmatchedElementsCommand 走同一套"未匹配"判定
                        if (ElementDataHelper.IsUnmatched(elem))
                            unmatchedCount++;
                        else
                            matchedCount++;
                    }
                }

                TaskDialog.Show("参数管理器",
                    string.Format(Constants.PARAM_MANAGER_REPORT_TEMPLATE,
                        totalCount,
                        totalCount - noParamCount,
                        matchedCount,
                        unmatchedCount,
                        noParamCount));

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Logger.Error("ParameterManagerCommand failed", ex);
                TaskDialog.Show("错误", "执行失败：\n" + ex.Message);
                return Result.Failed;
            }
        }
    }
}

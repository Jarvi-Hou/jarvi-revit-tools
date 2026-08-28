using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JarviTools.Core;
using JarviTools.Mcp.Server;

namespace JarviTools.Commands
{
    /// <summary>
    /// 命令：导出所有类别明细表
    /// 收集项目中所有模型类别的图元，导出为Excel多工作表文件
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ExportAllSchedulesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // ActiveUIDocument 在没有打开文档时为null，必须先检查
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show(Constants.PLUGIN_NAME, "请先打开一个Revit项目文件。");
                return Result.Cancelled;
            }
            Document doc = uidoc.Document;

            try
            {
                // 1. 弹出保存文件对话框（using 块确保对话框句柄被释放）
                string filePath;
                using (SaveFileDialog saveDialog = new SaveFileDialog
                {
                    Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
                    Title = "保存明细表导出文件",
                    FileName = string.Format("{0}_{1}_{2}{3}",
                        doc.Title,
                        Constants.FILE_PREFIX_SCHEDULE,
                        DateTime.Now.ToString(Constants.TIMESTAMP_FORMAT),
                        Constants.FILE_EXTENSION),
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                })
                {
                    if (saveDialog.ShowDialog() != DialogResult.OK)
                        return Result.Cancelled;

                    filePath = saveDialog.FileName;
                }

                // 2. 获取所有有效的模型类别
                List<Category> validCategories = new List<Category>();
                foreach (Category cat in doc.Settings.Categories)
                {
                    if (cat == null || string.IsNullOrEmpty(cat.Name)) continue;
                    if (cat.CategoryType != CategoryType.Model) continue;
                    validCategories.Add(cat);
                }

                if (validCategories.Count == 0)
                {
                    TaskDialog.Show(Constants.PLUGIN_NAME, Constants.MSG_NO_CATEGORIES);
                    return Result.Failed;
                }

                // 3. 一次性收集所有有效类别的图元，再按 Category.Id 分组。
                // 之前对每个类别都 new 一个 FilteredElementCollector 做全量扫描，
                // 几十个类别 = 几十次 O(N) 遍历，大项目卡死。
                ICollection<ElementId> validCategoryIds = validCategories.Select(c => c.Id).ToList();
                IDictionary<ElementId, List<Element>> byCategoryId;
                using (FilteredElementCollector collector = new FilteredElementCollector(doc))
                {
                    byCategoryId = collector
                        .WherePasses(new ElementMulticategoryFilter(validCategoryIds))
                        .WhereElementIsNotElementType()
                        .ToElements()
                        .Where(e => e != null && e.Category != null)
                        .GroupBy(e => e.Category.Id)
                        .ToDictionary(g => g.Key, g => g.ToList());
                }

                Dictionary<string, List<ElementData>> categoryData = new Dictionary<string, List<ElementData>>();
                Dictionary<string, int> categoryCounts = new Dictionary<string, int>();
                int totalElements = 0;

                foreach (Category cat in validCategories)
                {
                    List<Element> elementsOfCategory;
                    if (!byCategoryId.TryGetValue(cat.Id, out elementsOfCategory)) continue;
                    if (elementsOfCategory.Count == 0) continue;

                    List<ElementData> dataList = new List<ElementData>();
                    foreach (Element elem in elementsOfCategory)
                        dataList.Add(ElementDataHelper.ExtractElementData(elem));

                    // 工作表名去重
                    string safeName = GetSafeSheetName(cat.Name);
                    string uniqueName = safeName;
                    int suffix = 2;
                    while (categoryData.ContainsKey(uniqueName))
                    {
                        string suffixStr = "_" + suffix;
                        uniqueName = safeName.Length + suffixStr.Length > Constants.MAX_SHEET_NAME_LENGTH
                            ? safeName.Substring(0, Constants.MAX_SHEET_NAME_LENGTH - suffixStr.Length) + suffixStr
                            : safeName + suffixStr;
                        suffix++;
                    }

                    categoryData[uniqueName] = dataList;
                    categoryCounts[cat.Name] = elementsOfCategory.Count;
                    totalElements += elementsOfCategory.Count;
                }

                if (categoryData.Count == 0)
                {
                    TaskDialog.Show(Constants.PLUGIN_NAME, "项目中没有找到任何图元。");
                    return Result.Failed;
                }

                // 4. 生成并保存 .xlsx
                try
                {
                    ExcelHelper.Write(filePath, categoryCounts, categoryData);
                }
                catch (IOException ioEx)
                {
                    Logger.Warn("ExportAllSchedulesCommand IO failure: " + ioEx.Message);
                    TaskDialog.Show("错误", Constants.ERROR_FILE_OPEN);
                    return Result.Failed;
                }
                catch (UnauthorizedAccessException ex)
                {
                    Logger.Warn("ExportAllSchedulesCommand permission failure: " + ex.Message);
                    TaskDialog.Show("权限错误", Constants.ERROR_PERMISSION + "\n\n" + ex.Message);
                    return Result.Failed;
                }

                TaskDialog.Show(Constants.MSG_EXPORT_SUCCESS,
                    string.Format("导出完成！\n\n共处理类别数：{0}\n共导出图元数：{1}\n文件已保存至：{2}",
                        categoryData.Count, totalElements, filePath));

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Logger.Error("ExportAllSchedulesCommand failed", ex);
                TaskDialog.Show(Constants.PLUGIN_NAME, Constants.MSG_EXPORT_FAILED + ex.Message);
                return Result.Failed;
            }
        }

        private string GetSafeSheetName(string name)
        {
            string safe = name
                .Replace(":", "_").Replace("\\", "_").Replace("/", "_")
                .Replace("?", "_").Replace("*", "_")
                .Replace("[", "_").Replace("]", "_");
            return safe.Length > Constants.MAX_SHEET_NAME_LENGTH
                ? safe.Substring(0, Constants.MAX_SHEET_NAME_LENGTH) : safe;
        }

    }
}

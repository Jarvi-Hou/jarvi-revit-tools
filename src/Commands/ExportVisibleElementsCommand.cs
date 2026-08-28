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
    [Transaction(TransactionMode.Manual)]
    public class ExportVisibleElementsCommand : IExternalCommand
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
                // 用全限定名避免与 System.Windows.Forms.View 冲突
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

                string defaultFileName = string.Format("{0}_{1}_{2}{3}",
                    doc.Title, Constants.FILE_PREFIX_ELEMENTS,
                    DateTime.Now.ToString(Constants.TIMESTAMP_FORMAT),
                    Constants.FILE_EXTENSION);

                string filePath;
                using (SaveFileDialog saveDialog = new SaveFileDialog())
                {
                    saveDialog.Filter = "Excel 工作簿 (*.xlsx)|*.xlsx";
                    saveDialog.Title = "保存可见构件导出文件";
                    saveDialog.FileName = defaultFileName;
                    saveDialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                    if (saveDialog.ShowDialog() != DialogResult.OK)
                        return Result.Cancelled;

                    filePath = saveDialog.FileName;
                }

                IList<Element> visibleElements;
                using (FilteredElementCollector collector = new FilteredElementCollector(doc, activeView.Id))
                {
                    visibleElements = collector.WhereElementIsNotElementType().ToElements();
                }

                List<ElementData> dataList = new List<ElementData>();
                int exportedCount = 0;
                int excludedCount = 0;

                foreach (Element elem in visibleElements)
                {
                    if (elem.Category == null) continue;
                    ElementData data = ElementDataHelper.ExtractElementData(elem);
                    dataList.Add(data);
                    if (data.ShouldExport == Constants.VALUE_YES) exportedCount++;
                    else excludedCount++;
                }

                if (dataList.Count == 0)
                {
                    TaskDialog.Show("提示", "当前视图中没有可导出的构件。");
                    return Result.Cancelled;
                }

                Dictionary<string, List<ElementData>> sheets = OrganizeSheets(dataList);
                Dictionary<string, int> summaryCounts = new Dictionary<string, int>();
                foreach (var kvp in sheets)
                    if (kvp.Key != Constants.SHEET_EXCLUDED)
                        summaryCounts[kvp.Key] = kvp.Value.Count;
                List<string> customHeaders = new List<string>
                    { Constants.PARAM_MAJOR_NAME, Constants.PARAM_SUBCONTRACTOR, Constants.PARAM_SHOULD_EXPORT };

                try
                {
                    ExcelHelper.Write(filePath, summaryCounts, sheets, customHeaders);
                }
                catch (IOException ioEx)
                {
                    Logger.Warn("ExportVisibleElementsCommand IO failure: " + ioEx.Message);
                    TaskDialog.Show("错误", Constants.ERROR_FILE_OPEN);
                    return Result.Failed;
                }
                catch (UnauthorizedAccessException ex)
                {
                    Logger.Warn("ExportVisibleElementsCommand permission failure: " + ex.Message);
                    TaskDialog.Show("权限错误", Constants.ERROR_PERMISSION + "\n\n" + ex.Message);
                    return Result.Failed;
                }

                TaskDialog.Show("导出成功", string.Format(
                    "文件已导出！\n\n路径：{0}\n导出图元数：{1}\n已排除图元数：{2}",
                    filePath, exportedCount, excludedCount));

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Logger.Error("ExportVisibleElementsCommand failed", ex);
                TaskDialog.Show("错误", "导出失败：\n" + ex.Message);
                return Result.Failed;
            }
        }

        private Dictionary<string, List<ElementData>> OrganizeSheets(List<ElementData> allData)
        {
            Dictionary<string, List<ElementData>> sheets = new Dictionary<string, List<ElementData>>();

            var sorted = allData
                .Where(x => x.ShouldExport == Constants.VALUE_YES)
                .GroupBy(x => new { x.MajorName, x.Subcontractor })
                .OrderBy(g =>
                {
                    string code = Constants.MajorCodeMapping.ContainsKey(g.Key.MajorName)
                        ? Constants.MajorCodeMapping[g.Key.MajorName] : Constants.CODE_UNMATCHED;
                    return Constants.MajorPriority.ContainsKey(code) ? Constants.MajorPriority[code] : 99;
                })
                .ThenByDescending(g => g.Count())
                .ToList();

            Dictionary<string, int> majorCounters = new Dictionary<string, int>();
            foreach (var group in sorted)
            {
                string code = Constants.MajorCodeMapping.ContainsKey(group.Key.MajorName)
                    ? Constants.MajorCodeMapping[group.Key.MajorName] : Constants.CODE_UNMATCHED;
                if (!majorCounters.ContainsKey(code)) majorCounters[code] = 1;
                int seq = majorCounters[code]++;
                string baseName = string.Format("{0}{1:D2}_{2}", code, seq, group.Key.Subcontractor);
                if (baseName.Length > Constants.MAX_SHEET_NAME_LENGTH)
                    baseName = baseName.Substring(0, Constants.MAX_SHEET_NAME_LENGTH);
                // 截断后可能跟已有 sheet 撞名 (例如长分包名前缀相同),
                // 加 _2/_3... 后缀去重,保证 Excel 打开不报错。
                string sheetName = baseName;
                int dupSuffix = 2;
                while (sheets.ContainsKey(sheetName))
                {
                    string suffixStr = "_" + dupSuffix;
                    sheetName = baseName.Length + suffixStr.Length > Constants.MAX_SHEET_NAME_LENGTH
                        ? baseName.Substring(0, Constants.MAX_SHEET_NAME_LENGTH - suffixStr.Length) + suffixStr
                        : baseName + suffixStr;
                    dupSuffix++;
                }
                sheets[sheetName] = group.ToList();
            }

            var excluded = allData.Where(x => x.ShouldExport != Constants.VALUE_YES).ToList();
            if (excluded.Count > 0)
                sheets[Constants.SHEET_EXCLUDED] = excluded;

            return sheets;
        }

    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using JarviTools.Core;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// MCP wrapper around ExportVisibleElementsCommand.
    /// Exports elements visible in the current 3D view to a real .xlsx (OOXML) file at outputPath.
    /// Sheets are organized by (major, subcontractor); excluded elements (ShouldExport != VALUE_YES)
    /// go to a separate 已排除构件 sheet.
    /// </summary>
    public class ExportVisibleElementsTool : IRevitTool
    {
        public string Name => "export_visible_elements";

        public string Description =>
            "将活动 3D 视图中的可见元素导出为 xlsx 文件，按专业和分包类型分组。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["outputPath"] = new JObject
                {
                    ["type"]        = "string",
                    ["description"] = "Absolute file path to write the workbook. Required extension: .xlsx."
                }
            },
            ["required"] = new JArray { "outputPath" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc   = uidoc.Document       ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            string outputPath = (string)input["outputPath"];
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("'outputPath' is required and must be a non-empty string.");
            if (!Path.IsPathRooted(outputPath))
                throw new ArgumentException("'outputPath' must be an absolute path: " + outputPath);

            string parentDir = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir))
                throw new DirectoryNotFoundException("Output directory does not exist: " + parentDir);

            var activeView = doc.ActiveView ?? throw new InvalidOperationException("No active view.");
            if (!(activeView is View3D))
                throw new InvalidOperationException("请在 3D 视图中使用 (current view type: " + activeView.ViewType + ").");

            // Collect visible elements.
            IList<Element> visibleElements;
            using (var collector = new FilteredElementCollector(doc, activeView.Id))
            {
                visibleElements = collector.WhereElementIsNotElementType().ToElements();
            }

            var dataList = new List<ElementData>();
            int skippedSheetExcluded = 0;

            foreach (var elem in visibleElements)
            {
                if (elem == null || elem.Category == null) continue;
                var data = ElementDataHelper.ExtractElementData(elem);
                dataList.Add(data);
                if (data.ShouldExport != Constants.VALUE_YES) skippedSheetExcluded++;
            }

            if (dataList.Count == 0)
                throw new InvalidOperationException("当前视图中没有可导出的构件。");

            var sheets = OrganizeSheets(dataList);
            var summaryCounts = new Dictionary<string, int>();
            foreach (var kvp in sheets)
                if (kvp.Key != Constants.SHEET_EXCLUDED)
                    summaryCounts[kvp.Key] = kvp.Value.Count;
            var customHeaders = new List<string>
                { Constants.PARAM_MAJOR_NAME, Constants.PARAM_SUBCONTRACTOR, Constants.PARAM_SHOULD_EXPORT };

            ExcelHelper.Write(outputPath, summaryCounts, sheets, customHeaders);

            int sheetsWritten = sheets.Count + 1; // +1 for the summary worksheet

            return new JObject
            {
                ["outputPath"]              = outputPath,
                ["sheets_written"]          = sheetsWritten,
                ["total_elements"]          = dataList.Count,
                ["skipped_sheet_excluded"]  = skippedSheetExcluded
            };
        }

        // ---- mirrors ExportVisibleElementsCommand.OrganizeSheets ----------

        private static Dictionary<string, List<ElementData>> OrganizeSheets(List<ElementData> allData)
        {
            var sheets = new Dictionary<string, List<ElementData>>();

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

            var majorCounters = new Dictionary<string, int>();
            foreach (var group in sorted)
            {
                string code = Constants.MajorCodeMapping.ContainsKey(group.Key.MajorName)
                    ? Constants.MajorCodeMapping[group.Key.MajorName] : Constants.CODE_UNMATCHED;
                if (!majorCounters.ContainsKey(code)) majorCounters[code] = 1;
                int seq = majorCounters[code]++;

                string baseName = string.Format("{0}{1:D2}_{2}", code, seq, group.Key.Subcontractor);
                if (baseName.Length > Constants.MAX_SHEET_NAME_LENGTH)
                    baseName = baseName.Substring(0, Constants.MAX_SHEET_NAME_LENGTH);

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

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
    /// MCP wrapper around ExportAllSchedulesCommand.
    /// Groups all model-category elements by their Category and writes one real .xlsx workbook (OOXML),
    /// one worksheet per category, with a leading summary sheet.
    /// </summary>
    public class ExportAllSchedulesTool : IRevitTool
    {
        public string Name => "export_all_schedules";

        public string Description =>
            "将每个模型类别的元素导出为 xlsx 文件，每个类别一个工作表。";

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

            // Gather model categories.
            var validCategories = new List<Category>();
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat == null || string.IsNullOrEmpty(cat.Name)) continue;
                if (cat.CategoryType != CategoryType.Model) continue;
                validCategories.Add(cat);
            }
            if (validCategories.Count == 0)
                throw new InvalidOperationException(Constants.MSG_NO_CATEGORIES);

            // Single collector + multi-category filter to avoid N full scans.
            var validCategoryIds = validCategories.Select(c => c.Id).ToList();
            IDictionary<ElementId, List<Element>> byCategoryId;
            using (var collector = new FilteredElementCollector(doc))
            {
                byCategoryId = collector
                    .WherePasses(new ElementMulticategoryFilter(validCategoryIds))
                    .WhereElementIsNotElementType()
                    .ToElements()
                    .Where(e => e != null && e.Category != null)
                    .GroupBy(e => e.Category.Id)
                    .ToDictionary(g => g.Key, g => g.ToList());
            }

            var categoryData   = new Dictionary<string, List<ElementData>>();
            var categoryCounts = new Dictionary<string, int>();
            int totalElements = 0;

            foreach (var cat in validCategories)
            {
                if (!byCategoryId.TryGetValue(cat.Id, out var elementsOfCategory)) continue;
                if (elementsOfCategory.Count == 0) continue;

                var dataList = new List<ElementData>();
                foreach (var elem in elementsOfCategory)
                    dataList.Add(ElementDataHelper.ExtractElementData(elem));

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
                throw new InvalidOperationException("项目中没有找到任何图元。");

            ExcelHelper.Write(outputPath, categoryCounts, categoryData);

            return new JObject
            {
                ["outputPath"]          = outputPath,
                ["categories_exported"] = categoryData.Count,
                ["total_elements"]      = totalElements
            };
        }

        private static string GetSafeSheetName(string name)
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

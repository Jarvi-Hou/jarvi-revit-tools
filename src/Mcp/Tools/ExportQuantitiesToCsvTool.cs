using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 按类别导出元素数量及物理量汇总到 CSV 文件。
    /// 可选 categories 过滤指定类别。输出 UTF-8 BOM 兼容 Excel 中文。
    /// </summary>
    public class ExportQuantitiesToCsvTool : IRevitTool
    {
        public string Name => "export_quantities_to_csv";
        public string Description =>
            "按类别导出元素数量及物理量(长度/面积/体积)到 CSV 文件。可选 categories 过滤。输出 UTF-8 BOM。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["outputPath"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Absolute Windows path for the output CSV file (e.g. 'C:\\temp\\quantities.csv')."
                },
                ["categories"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string" },
                    ["description"] = "Optional list of BuiltInCategory names to filter (e.g. OST_Walls, OST_Doors)."
                },
                ["overwrite"] = new JObject { ["type"] = "boolean", ["default"] = false }
            },
            ["required"] = new JArray { "outputPath" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            bool overwrite = input != null && ((bool?)input["overwrite"]).GetValueOrDefault();
            string outputPath = CsvExportSafety.PreparePath((string)input["outputPath"], overwrite);

            // 检查输出目录
            var parentDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                throw new DirectoryNotFoundException($"Output directory does not exist: {parentDir}");

            // 可选 categories 过滤
            HashSet<BuiltInCategory> filterBics = null;
            var categoriesToken = input["categories"];
            if (categoriesToken != null && categoriesToken.Type == JTokenType.Array && categoriesToken.HasValues)
            {
                filterBics = new HashSet<BuiltInCategory>();
                foreach (var cat in categoriesToken)
                {
                    if (Enum.TryParse((string)cat, ignoreCase: true, out BuiltInCategory bic))
                        filterBics.Add(bic);
                }
            }

            var allElems = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .ToElements();

            // 按类别分组
            var groups = allElems
                .Where(e => e.Category != null)
                .GroupBy(e => e.Category.Name)
                .OrderBy(g => g.Key)
                .ToList();

            var sb = new StringBuilder();
            // UTF-8 BOM
            sb.Append("类别,数量,总长度_米,总面积_平方米,总体积_立方米");
            sb.AppendLine();

            int totalCategories = 0;
            int skippedCategories = 0;
            int totalRows = 0;

            Func<Element, double> GetLength = (e) =>
            {
                var p = e.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                return (p != null) ? p.AsDouble() * 0.3048 : 0;
            };

            Func<Element, double> GetArea = (e) =>
            {
                var p = e.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                return (p != null) ? p.AsDouble() * 0.092903 : 0;
            };

            Func<Element, double> GetVolume = (e) =>
            {
                var p = e.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
                return (p != null) ? p.AsDouble() * 0.02832 : 0;
            };

            foreach (var g in groups)
            {
                var first = g.First();
                var cat = first.Category;

                // 如果传了 filterBics，跳过不在过滤列表中的
                if (filterBics != null && !filterBics.Contains((BuiltInCategory)cat.Id.Value))
                {
                    skippedCategories++;
                    continue;
                }

                // 跳过未分类的
                if (cat.Name == "<Unrecognized>" || cat.Name == "Model Text")
                    continue;

                int count = g.Count();
                double totalLength = 0;
                double totalArea = 0;
                double totalVolume = 0;

                foreach (var e in g)
                {
                    totalLength += GetLength(e);
                    totalArea += GetArea(e);
                    totalVolume += GetVolume(e);
                }

                // CSV escape
                string catName = EscapeCsvField(cat.Name);
                sb.Append($"{catName},{count},{Math.Round(totalLength, 3)},{Math.Round(totalArea, 3)},{Math.Round(totalVolume, 3)}");
                sb.AppendLine();

                totalCategories++;
                totalRows++;
            }

            // 写入文件（UTF-8 BOM）
            CsvExportSafety.WriteAllTextAtomic(outputPath, sb.ToString());

            return new JObject
            {
                ["filePath"] = outputPath,
                ["rowCount"] = totalRows,
                ["totalCategories"] = totalCategories,
                ["skippedCategories"] = skippedCategories
            };
        }

        private static string EscapeCsvField(string field)
        {
            return CsvExportSafety.EscapeField(field);
        }
    }
}

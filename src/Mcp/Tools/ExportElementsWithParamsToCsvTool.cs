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
    /// 按指定类别和参数名列表导出元素参数值到 CSV。
    /// 输出 UTF-8 BOM。返回 missingParams 告知哪些参数不存在。
    /// </summary>
    public class ExportElementsWithParamsToCsvTool : IRevitTool
    {
        public string Name => "export_elements_with_params_to_csv";
        public string Description =>
            "按指定类别和参数名列表导出元素参数值到 CSV 文件。返回 missingParams 告知哪些参数不存在。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["outputPath"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Absolute Windows path for the output CSV file."
                },
                ["category"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "BuiltInCategory name, e.g. 'OST_Walls'."
                },
                ["paramNames"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string" },
                    ["description"] = "Parameter names to export as columns."
                },
                ["overwrite"] = new JObject { ["type"] = "boolean", ["default"] = false }
            },
            ["required"] = new JArray { "outputPath", "category", "paramNames" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            bool overwrite = input != null && ((bool?)input["overwrite"]).GetValueOrDefault();
            string outputPath = CsvExportSafety.PreparePath((string)input["outputPath"], overwrite);

            var parentDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                throw new DirectoryNotFoundException($"Output directory does not exist: {parentDir}");

            string categoryStr = (string)input["category"];
            if (string.IsNullOrWhiteSpace(categoryStr))
                throw new ArgumentException("category is required.");

            var paramNames = input["paramNames"]?.ToObject<string[]>();
            if (paramNames == null || paramNames.Length == 0)
                throw new ArgumentException("paramNames is required with at least one parameter name.");

            // 解析 BuiltInCategory
            if (!Enum.TryParse(categoryStr, ignoreCase: true, out BuiltInCategory bic))
                throw new ArgumentException($"Invalid BuiltInCategory: {categoryStr}");

            var elements = new FilteredElementCollector(doc)
                .OfCategory(bic)
                .WhereElementIsNotElementType()
                .ToElements();

            var missingParamSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            // Header: 元素ID,名称,类别,param1,param2,...
            sb.Append("元素ID,名称,类别");
            foreach (var pn in paramNames)
                sb.Append($",{EscapeCsvField(pn)}");
            sb.AppendLine();

            int rowCount = 0;
            foreach (var e in elements)
            {
                string elemId = e.Id.Value.ToString();
                string elemName = "";
                try { elemName = e.Name ?? ""; } catch { }
                string catName = e.Category?.Name ?? "";

                sb.Append($"{elemId},{EscapeCsvField(elemName)},{EscapeCsvField(catName)}");

                foreach (var pn in paramNames)
                {
                    var p = e.LookupParameter(pn);
                    string val = "";
                    if (p == null)
                    {
                        missingParamSet.Add(pn);
                    }
                    else
                    {
                        try
                        {
                            switch (p.StorageType)
                            {
                                case StorageType.String:
                                    val = p.AsString() ?? "";
                                    break;
                                case StorageType.Integer:
                                    val = p.AsInteger().ToString();
                                    break;
                                case StorageType.Double:
                                    val = Math.Round(p.AsDouble(), 4).ToString();
                                    break;
                                case StorageType.ElementId:
                                    var idVal = p.AsElementId();
                                    val = idVal != null ? idVal.Value.ToString() : "";
                                    break;
                                default:
                                    val = p.AsValueString() ?? "";
                                    break;
                            }
                        }
                        catch
                        {
                            val = "";
                        }
                    }
                    sb.Append($",{EscapeCsvField(val)}");
                }
                sb.AppendLine();
                rowCount++;
            }

            CsvExportSafety.WriteAllTextAtomic(outputPath, sb.ToString());

            return new JObject
            {
                ["filePath"] = outputPath,
                ["rowCount"] = rowCount,
                ["missingParams"] = new JArray(missingParamSet.OrderBy(n => n))
            };
        }

        private static string EscapeCsvField(string field)
        {
            return CsvExportSafety.EscapeField(field);
        }
    }
}

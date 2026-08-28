using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 按指定类别+参数名+参数值搜索元素。
    /// 支持 equals（完全匹配）和 contains（模糊匹配）两种模式。
    /// </summary>
    public class FindElementsByParamValueTool : IRevitTool
    {
        public string Name => "find_elements_by_param_value";
        public string Description =>
            "在指定类别中按参数名称和值查找元素。支持精确匹配和模糊匹配。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["category"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "BuiltInCategory name, e.g. \"OST_Walls\", \"OST_Doors\"."
                },
                ["paramName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Parameter display name to search, e.g. \"Width\", \"Comments\"."
                },
                ["value"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Value to match against, as string."
                },
                ["matchMode"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Match mode: 'equals' (default) or 'contains'.",
                    ["default"] = "equals"
                }
            },
            ["required"] = new JArray { "category", "paramName", "value" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            string categoryName = (string)input["category"];
            if (string.IsNullOrWhiteSpace(categoryName))
                throw new ArgumentException("'category' is required.");

            BuiltInCategory bic;
            try { bic = (BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), categoryName, ignoreCase: true); }
            catch (Exception ex)
            {
                throw new ArgumentException("Unknown BuiltInCategory: '" + categoryName + "'.", ex);
            }

            string paramName = (string)input["paramName"];
            if (string.IsNullOrWhiteSpace(paramName))
                throw new ArgumentException("'paramName' is required.");

            string targetValue = (string)input["value"];
            if (string.IsNullOrWhiteSpace(targetValue))
                throw new ArgumentException("'value' is required.");

            string matchMode = "equals";
            var modeToken = input["matchMode"];
            if (modeToken != null && modeToken.Type != JTokenType.Null)
                matchMode = (string)modeToken;
            bool isContains = matchMode.Equals("contains", StringComparison.OrdinalIgnoreCase);

            var elements = new FilteredElementCollector(doc)
                .OfCategory(bic)
                .WhereElementIsNotElementType()
                .ToElements();

            var matches = new JArray();
            foreach (var e in elements)
            {
                var p = e.LookupParameter(paramName);
                if (p == null) continue;

                string displayValue = null;
                try
                {
                    displayValue = p.AsValueString();
                    if (string.IsNullOrEmpty(displayValue))
                    {
                        // Fallback to string representation
                        switch (p.StorageType)
                        {
                            case StorageType.String:
                                displayValue = p.AsString();
                                break;
                            case StorageType.Integer:
                                displayValue = p.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture);
                                break;
                            case StorageType.Double:
                                displayValue = p.AsDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
                                break;
                            case StorageType.ElementId:
                                displayValue = p.AsElementId().Value.ToString();
                                break;
                        }
                    }
                }
                catch { continue; }

                if (displayValue == null) continue;

                bool matched = isContains
                    ? displayValue.IndexOf(targetValue, StringComparison.OrdinalIgnoreCase) >= 0
                    : string.Equals(displayValue, targetValue, StringComparison.OrdinalIgnoreCase);

                if (!matched) continue;

                // Build summary
                string elemName = null;
                try { elemName = e.Name; } catch { }
                string catDisplay = null;
                try { catDisplay = e.Category?.Name; } catch { }

                matches.Add(new JObject
                {
                    ["id"] = e.Id.Value,
                    ["name"] = elemName ?? (JToken)JValue.CreateNull(),
                    ["category"] = catDisplay ?? (JToken)JValue.CreateNull(),
                    ["paramValue"] = displayValue
                });
            }

            return new JObject
            {
                ["elements"] = matches,
                ["totalFound"] = matches.Count
            };
        }
    }
}

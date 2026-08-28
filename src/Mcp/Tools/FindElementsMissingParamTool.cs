using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 查找指定类别中缺少某参数或参数值为空的元素。
    /// 区分"无此参数"和"值为空"两种情况。
    /// </summary>
    public class FindElementsMissingParamTool : IRevitTool
    {
        public string Name => "find_elements_missing_param";
        public string Description =>
            "查找指定类别中缺少某参数或参数值为空的元素。区分\"无此参数\"和\"值为空\"两种情况。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["category"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "BuiltInCategory name, e.g. 'OST_Walls'."
                },
                ["paramName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Parameter display name to check, e.g. '防火等级'."
                }
            },
            ["required"] = new JArray { "category", "paramName" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            string categoryStr = (string)input["category"];
            string paramName = (string)input["paramName"];

            if (string.IsNullOrWhiteSpace(categoryStr))
                throw new ArgumentException("category is required.");
            if (string.IsNullOrWhiteSpace(paramName))
                throw new ArgumentException("paramName is required.");

            if (!Enum.TryParse(categoryStr, ignoreCase: true, out BuiltInCategory bic))
                throw new ArgumentException($"Invalid BuiltInCategory: {categoryStr}");

            var elements = new FilteredElementCollector(doc)
                .OfCategory(bic)
                .WhereElementIsNotElementType()
                .ToElements();

            var missingArr = new JArray();
            int noParamCount = 0;
            int emptyValueCount = 0;

            foreach (var e in elements)
            {
                var p = e.LookupParameter(paramName);
                string reason = null;

                if (p == null)
                {
                    reason = "no_param";
                    noParamCount++;
                }
                else
                {
                    bool isEmpty = false;
                    switch (p.StorageType)
                    {
                        case StorageType.String:
                            isEmpty = string.IsNullOrWhiteSpace(p.AsString());
                            break;
                        case StorageType.Integer:
                            // 0 算有效
                            isEmpty = false;
                            break;
                        case StorageType.Double:
                            // 0.0 算有效
                            isEmpty = false;
                            break;
                        case StorageType.ElementId:
                            var idVal = p.AsElementId();
                            isEmpty = idVal == null || idVal.Value <= 0;
                            break;
                        default:
                            var vs = p.AsValueString();
                            isEmpty = string.IsNullOrWhiteSpace(vs);
                            break;
                    }

                    if (isEmpty)
                    {
                        reason = "empty_value";
                        emptyValueCount++;
                    }
                }

                if (reason != null)
                {
                    string elemName = "";
                    try { elemName = e.Name ?? ""; } catch { }
                    missingArr.Add(new JObject
                    {
                        ["id"] = e.Id.Value,
                        ["name"] = elemName,
                        ["reason"] = reason
                    });
                }
            }

            return new JObject
            {
                ["missingElements"] = missingArr,
                ["noParamCount"] = noParamCount,
                ["emptyValueCount"] = emptyValueCount,
                ["total"] = noParamCount + emptyValueCount
            };
        }
    }
}

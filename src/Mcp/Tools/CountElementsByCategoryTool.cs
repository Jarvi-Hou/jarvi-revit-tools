using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 按类别（Category）统计模型中所有非类型元素的个数。
    /// 可选 viewId 参数限定到某个视图。
    /// </summary>
    public class CountElementsByCategoryTool : IRevitTool
    {
        public string Name => "count_elements_by_category";
        public string Description =>
            "按类别统计所有模型元素数量。可选 viewId 限制到特定视图。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["viewId"] = new JObject
                {
                    ["type"] = new JArray { "integer", "null" },
                    ["description"] = "Optional view ElementId. If provided, only count elements visible in that view."
                }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            FilteredElementCollector collector;
            var viewIdToken = input?["viewId"];
            if (viewIdToken != null && viewIdToken.Type != JTokenType.Null)
            {
                long viewIdLong = (long)viewIdToken;
                var viewElem = doc.GetElement(new ElementId(viewIdLong)) as View;
                if (viewElem == null)
                    throw new ArgumentException("viewId " + viewIdLong + " does not refer to a View element.");
                collector = new FilteredElementCollector(doc, new ElementId(viewIdLong));
            }
            else
            {
                collector = new FilteredElementCollector(doc);
            }

            var elements = collector.WhereElementIsNotElementType().ToElements();
            var groups = new Dictionary<string, int>();

            foreach (var e in elements)
            {
                string catName = null;
                try { catName = e.Category?.Name; } catch { catName = null; }
                if (string.IsNullOrEmpty(catName)) catName = "__NoCategory__";
                groups[catName] = groups.TryGetValue(catName, out var c) ? c + 1 : 1;
            }

            var sorted = groups.OrderByDescending(kv => kv.Value).ToList();
            var categoriesArr = new JArray();
            foreach (var kv in sorted)
            {
                categoriesArr.Add(new JObject
                {
                    ["category"] = kv.Key,
                    ["count"] = kv.Value
                });
            }

            return new JObject
            {
                ["categories"] = categoriesArr,
                ["total"] = elements.Count
            };
        }
    }
}

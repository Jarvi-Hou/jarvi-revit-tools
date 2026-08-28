using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 获取项目中所有族（Family）的列表，包括类型数量和实例数量。
    /// 可选 categoryName 只过滤指定类别的族。
    /// </summary>
    public class GetFamilyListTool : IRevitTool
    {
        public string Name => "get_family_list";
        public string Description =>
            "列出项目中所有族及其类型数和实例数。可选 categoryName 过滤。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["categoryName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional category name filter, e.g. 'Doors', 'Walls', 'Generic Models'."
                }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            string filterCategory = null;
            if (input != null)
            {
                var token = input["categoryName"];
                if (token != null && token.Type != JTokenType.Null)
                    filterCategory = (string)token;
            }

            // 获取所有 Family 定义
            var families = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .ToList();

            // 获取所有已放置的 FamilyInstance TypeId 集合（用于后续统计）
            var allInstances = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .WhereElementIsNotElementType()
                .ToElements()
                .Cast<FamilyInstance>();

            // 按 family Symbol.TypeId 分组 count
            var instanceCountByType = new Dictionary<ElementId, int>();
            foreach (var fi in allInstances)
            {
                var typeId = fi.GetTypeId();
                instanceCountByType[typeId] = instanceCountByType.TryGetValue(typeId, out var c) ? c + 1 : 1;
            }

            var familiesArr = new JArray();
            foreach (var f in families.OrderBy(f => f.Name))
            {
                string catName = null;
                try { catName = f.Category?.Name; } catch { }

                // 如果传了类别过滤，跳过不匹配
                if (!string.IsNullOrEmpty(filterCategory) &&
                    !string.Equals(catName, filterCategory, StringComparison.OrdinalIgnoreCase))
                    continue;

                var symbolIds = f.GetFamilySymbolIds();
                int typeCount = symbolIds.Count;

                // 统计该族所有类型的实例总数
                int instanceCount = 0;
                foreach (var symId in symbolIds)
                {
                    instanceCount += instanceCountByType.TryGetValue(symId, out var ic) ? ic : 0;
                }

                familiesArr.Add(new JObject
                {
                    ["name"] = f.Name,
                    ["category"] = catName ?? (JToken)JValue.CreateNull(),
                    ["typeCount"] = typeCount,
                    ["instanceCount"] = instanceCount
                });
            }

            return new JObject
            {
                ["families"] = familiesArr,
                ["total"] = familiesArr.Count
            };
        }
    }
}

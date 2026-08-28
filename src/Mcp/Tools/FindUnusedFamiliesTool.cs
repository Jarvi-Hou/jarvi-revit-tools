using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 查找项目中已加载但未在模型中使用的族（零实例）。
    /// 按类别分组输出，便于清理冗余族。
    /// </summary>
    public class FindUnusedFamiliesTool : IRevitTool
    {
        public string Name => "find_unused_families";
        public string Description =>
            "查找已载入但未放置任何实例的族，帮助识别可清理的冗余族。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            // 所有 Family
            var families = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .ToList();

            // 所有已放置实例的 TypeId
            var placedTypeIds = new HashSet<ElementId>(
                new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .WhereElementIsNotElementType()
                    .ToElements()
                    .Cast<FamilyInstance>()
                    .Select(fi => fi.GetTypeId())
            );

            var unusedArr = new JArray();
            foreach (var f in families.OrderBy(f => f.Name))
            {
                var symbolIds = f.GetFamilySymbolIds();
                bool hasAnyPlaced = symbolIds.Any(sid => placedTypeIds.Contains(sid));
                if (hasAnyPlaced) continue;

                string catName = null;
                try { catName = f.Category?.Name; } catch { }

                unusedArr.Add(new JObject
                {
                    ["name"] = f.Name,
                    ["category"] = catName ?? (JToken)JValue.CreateNull(),
                    ["typeCount"] = symbolIds.Count
                });
            }

            return new JObject
            {
                ["unusedFamilies"] = unusedArr,
                ["totalUnused"] = unusedArr.Count
            };
        }
    }
}

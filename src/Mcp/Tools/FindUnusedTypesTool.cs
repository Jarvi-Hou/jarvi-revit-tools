using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 查找模型中已加载但未使用的 ElementType（类型）。
    /// 排除系统级抽象类型（如 ViewFamilyType、PrintSetting 等无 Category 的类型）。
    /// </summary>
    public class FindUnusedTypesTool : IRevitTool
    {
        public string Name => "find_unused_types";
        public string Description =>
            "查找已载入但未被任何实例使用的元素类型，帮助识别可清理的冗余类型。";

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

            // 收集所有 ElementType（有 Category 的）
            var allTypes = new FilteredElementCollector(doc)
                .WhereElementIsElementType()
                .Cast<ElementType>()
                .Where(et => et.Category != null)
                .ToList();

            // 收集所有非类型实例的 TypeId
            var placedTypeIds = new HashSet<ElementId>(
                new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .ToElements()
                    .Select(e => e.GetTypeId())
                    .Where(tid => tid != null && tid != ElementId.InvalidElementId)
            );

            var unusedArr = new JArray();
            foreach (var et in allTypes.OrderBy(et => et.Name))
            {
                if (placedTypeIds.Contains(et.Id)) continue;

                string catName = null;
                try { catName = et.Category?.Name; } catch { }

                unusedArr.Add(new JObject
                {
                    ["typeId"] = et.Id.Value,
                    ["typeName"] = et.Name ?? (JToken)JValue.CreateNull(),
                    ["categoryName"] = catName ?? (JToken)JValue.CreateNull(),
                    ["familyName"] = et.FamilyName ?? (JToken)JValue.CreateNull()
                });
            }

            return new JObject
            {
                ["unusedTypes"] = unusedArr,
                ["total"] = unusedArr.Count
            };
        }
    }
}

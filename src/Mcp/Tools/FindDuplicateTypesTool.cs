using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 查找同族下名称相似的重复类型。
    /// 去掉名称末尾数字后分组，count>1 的视为重复。
    /// </summary>
    public class FindDuplicateTypesTool : IRevitTool
    {
        // 去掉名字末尾数字（如 "类型 01" → "类型 "，"尺寸1200" → "尺寸"）
        private static readonly Regex TrailingDigits = new Regex(@"\d+\s*$", RegexOptions.Compiled);

        public string Name => "find_duplicate_types";
        public string Description =>
            "在同族中查找名称只有数字后缀差异的重复类型。可选 category 过滤。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["category"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional BuiltInCategory filter, e.g. 'OST_Walls'."
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
            BuiltInCategory? filterBuiltInCategory = null;
            if (input != null)
            {
                var token = input["category"];
                if (token != null && token.Type != JTokenType.Null)
                    filterCategory = (string)token;
            }
            if (!string.IsNullOrWhiteSpace(filterCategory))
            {
                BuiltInCategory parsed;
                if (!Enum.TryParse(filterCategory, true, out parsed))
                    throw new ArgumentException("Invalid BuiltInCategory: " + filterCategory);
                filterBuiltInCategory = parsed;
            }

            var allTypes = new FilteredElementCollector(doc)
                .WhereElementIsElementType()
                .Cast<ElementType>()
                .Where(et => et.Category != null)
                .ToList();

            // 按 (FamilyName, normalizedName) 分组
            var groups = new Dictionary<string, List<ElementType>>();
            foreach (var et in allTypes)
            {
                string familyName = null;
                try { familyName = et.FamilyName; } catch { familyName = ""; }
                if (string.IsNullOrEmpty(familyName)) continue;

                string catName = null;
                try { catName = et.Category?.Name; } catch { }

                // 如果传了类别过滤
                if (filterBuiltInCategory.HasValue &&
                    et.Category.BuiltInCategory != filterBuiltInCategory.Value)
                    continue;

                string typeName = null;
                try { typeName = et.Name; } catch { typeName = ""; }

                string normalized = TrailingDigits.Replace(typeName ?? "", "").Trim();
                if (string.IsNullOrEmpty(normalized)) continue;

                string key = familyName + "||" + normalized;
                if (!groups.ContainsKey(key))
                    groups[key] = new List<ElementType>();
                groups[key].Add(et);
            }

            var groupsArr = new JArray();
            foreach (var kv in groups.OrderBy(g => g.Key))
            {
                if (kv.Value.Count <= 1) continue; // 不算重复

                var typesArr = new JArray();
                foreach (var et in kv.Value)
                {
                    typesArr.Add(new JObject
                    {
                        ["id"] = et.Id.Value,
                        ["name"] = (et.Name ?? (JToken)JValue.CreateNull())
                    });
                }

                string[] parts = kv.Key.Split(new[] { "||" }, StringSplitOptions.None);
                string familyName = parts[0];
                string catName = null;
                try { catName = kv.Value[0].Category?.Name; } catch { }

                groupsArr.Add(new JObject
                {
                    ["familyName"] = familyName ?? (JToken)JValue.CreateNull(),
                    ["categoryName"] = catName ?? (JToken)JValue.CreateNull(),
                    ["types"] = typesArr,
                    ["count"] = kv.Value.Count
                });
            }

            return new JObject
            {
                ["duplicateGroups"] = groupsArr,
                ["totalGroups"] = groupsArr.Count
            };
        }
    }
}

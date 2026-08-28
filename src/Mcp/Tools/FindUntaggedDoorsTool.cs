using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 查找所有未放置门标签（Door Tag / IndependentTag）的门。
    /// 通过差集计算：所有 Door - 有 IndependentTag 关联的 Door。
    /// 可选 levelName 过滤。
    /// </summary>
    public class FindUntaggedDoorsTool : IRevitTool
    {
        public string Name => "find_untagged_doors";
        public string Description =>
            "查找所有未放置门标签的门。可选 levelName 过滤。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["levelName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional level name to filter doors (e.g. '标高 1')."
                }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            string filterLevel = null;
            if (input != null)
            {
                var lvl = input["levelName"];
                if (lvl != null && lvl.Type != JTokenType.Null)
                    filterLevel = (string)lvl;
            }

            // 所有 Door FamilyInstance
            var allDoors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .ToList();

            // 所有 IndependentTag（包括门标签）
            var allTags = new FilteredElementCollector(doc)
                .OfClass(typeof(IndependentTag))
                .Cast<IndependentTag>()
                .ToList();

            // 收集已标记的 Door ID
            var taggedDoorIds = new HashSet<ElementId>();
            foreach (var tag in allTags)
            {
                try
                {
                    // Revit 2024 API: GetTaggedLocalElementIds()
                    var taggedIds = tag.GetTaggedLocalElementIds();
                    if (taggedIds != null)
                    {
                        foreach (var tid in taggedIds)
                            taggedDoorIds.Add(tid);
                    }
                }
                catch { }
            }

            // 差集：未标记的门
            var untagged = allDoors.Where(d => !taggedDoorIds.Contains(d.Id)).ToList();

            // 可选 levelName 过滤
            if (!string.IsNullOrEmpty(filterLevel))
            {
                untagged = untagged.Where(d =>
                {
                    var hostLevelId = d.Host?.LevelId ?? d.LevelId;
                    var level = hostLevelId != null ? doc.GetElement(hostLevelId) : null;
                    return level != null && string.Equals(level.Name, filterLevel, StringComparison.Ordinal);
                }).ToList();
            }

            var doorsArr = new JArray();
            foreach (var d in untagged)
            {
                string doorName = "";
                try { doorName = d.Name ?? ""; } catch { }
                var hostLevelId = d.Host?.LevelId ?? d.LevelId;
                string levelName = hostLevelId != null
                    ? doc.GetElement(hostLevelId)?.Name ?? "(unknown)"
                    : "(unknown)";
                string hostWallType = d.Host?.Name ?? "";

                doorsArr.Add(new JObject
                {
                    ["id"] = d.Id.Value,
                    ["name"] = doorName,
                    ["level"] = levelName,
                    ["hostWallType"] = hostWallType
                });
            }

            return new JObject
            {
                ["untaggedDoors"] = doorsArr,
                ["total"] = untagged.Count
            };
        }
    }
}

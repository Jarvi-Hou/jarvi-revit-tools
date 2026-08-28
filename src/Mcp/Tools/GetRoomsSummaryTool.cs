using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 汇总所有房间（Room）按（楼层,部门）分组：房间数量、总面积、平均面积、缺失装修面层数。
    /// 可选 levelName / department 过滤。
    /// </summary>
    public class GetRoomsSummaryTool : IRevitTool
    {
        public string Name => "get_rooms_summary";
        public string Description =>
            "汇总所有房间按(楼层,部门)分组：数量、总面积(平方米)、平均面积、缺失面层数。可选 levelName/department 过滤。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["levelName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional level name to filter rooms (e.g. '标高 1')."
                },
                ["department"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional department name to filter rooms."
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
            string filterDept = null;
            if (input != null)
            {
                var lvl = input["levelName"];
                if (lvl != null && lvl.Type != JTokenType.Null)
                    filterLevel = (string)lvl;
                var dept = input["department"];
                if (dept != null && dept.Type != JTokenType.Null)
                    filterDept = (string)dept;
            }

            var allRooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .ToList();

            // 区分已放置和未放置
            var unplaced = allRooms.Where(r => r.Location == null || r.Area < 1e-6).ToList();
            var placed = allRooms.Where(r => r.Location != null && r.Area >= 1e-6).ToList();

            // 过滤
            var filtered = placed.AsEnumerable();
            if (!string.IsNullOrEmpty(filterLevel))
            {
                filtered = filtered.Where(r =>
                {
                    var level = doc.GetElement(r.LevelId);
                    return level != null && string.Equals(level.Name, filterLevel, StringComparison.Ordinal);
                });
            }
            if (!string.IsNullOrEmpty(filterDept))
            {
                filtered = filtered.Where(r =>
                {
                    var dept = r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString();
                    return string.Equals(dept, filterDept, StringComparison.OrdinalIgnoreCase);
                });
            }

            var roomsList = filtered.ToList();

            // 按 (levelName, department) 分组
            var groups = roomsList.GroupBy(r =>
            {
                var levelName = doc.GetElement(r.LevelId)?.Name ?? "(unknown)";
                var dept = r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString() ?? "未指定";
                return (levelName, dept);
            }).ToList();

            var summaryArr = new JArray();
            double totalAreaAll = 0;

            foreach (var g in groups)
            {
                var (levelName, dept) = g.Key;
                double groupArea = 0;
                int missingFinishCount = 0;

                foreach (var r in g)
                {
                    double area = r.Area * 0.092903; // sqft → m2
                    groupArea += area;

                    // 检查 4 个面层参数
                    bool anyMissing = false;
                    var finishParams = new[]
                    {
                        BuiltInParameter.ROOM_FINISH_FLOOR,
                        BuiltInParameter.ROOM_FINISH_CEILING,
                        BuiltInParameter.ROOM_FINISH_WALL,
                        BuiltInParameter.ROOM_FINISH_BASE
                    };
                    foreach (var bip in finishParams)
                    {
                        var p = r.get_Parameter(bip);
                        if (p == null || string.IsNullOrWhiteSpace(p.AsString()))
                        {
                            anyMissing = true;
                            break;
                        }
                    }
                    if (anyMissing)
                        missingFinishCount++;
                }

                totalAreaAll += groupArea;
                double avgArea = groupArea / g.Count();

                summaryArr.Add(new JObject
                {
                    ["level"] = levelName,
                    ["department"] = dept,
                    ["count"] = g.Count(),
                    ["totalArea_m2"] = Math.Round(groupArea, 3),
                    ["averageArea_m2"] = Math.Round(avgArea, 3),
                    ["missingFinishCount"] = missingFinishCount
                });
            }

            return new JObject
            {
                ["summary"] = summaryArr,
                ["totalRooms"] = placed.Count,
                ["totalArea_m2"] = Math.Round(totalAreaAll, 3),
                ["unplacedRoomCount"] = unplaced.Count
            };
        }
    }
}

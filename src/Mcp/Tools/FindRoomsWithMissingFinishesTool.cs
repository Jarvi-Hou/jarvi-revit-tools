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
    /// 查找缺少面层参数（地面、天花、墙面、踢脚）的房间。
    /// 每个房间报告具体缺失哪些字段。
    /// </summary>
    public class FindRoomsWithMissingFinishesTool : IRevitTool
    {
        public string Name => "find_rooms_with_missing_finishes";
        public string Description =>
            "查找缺少面层参数(地面/天花/墙面/踢脚)的房间。每个房间报告具体缺失哪些字段。可选 levelName 过滤。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["levelName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional level name to filter rooms (e.g. '标高 1')."
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

            // 所有已放置 Room
            var allRooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.Location != null && r.Area >= 1e-6)
                .ToList();

            if (!string.IsNullOrEmpty(filterLevel))
            {
                allRooms = allRooms.Where(r =>
                {
                    var level = doc.GetElement(r.LevelId);
                    return level != null && string.Equals(level.Name, filterLevel, StringComparison.Ordinal);
                }).ToList();
            }

            // 4 个面层参数映射
            var finishFields = new (string name, BuiltInParameter bip)[]
            {
                ("floor",   BuiltInParameter.ROOM_FINISH_FLOOR),
                ("ceiling", BuiltInParameter.ROOM_FINISH_CEILING),
                ("wall",    BuiltInParameter.ROOM_FINISH_WALL),
                ("base",    BuiltInParameter.ROOM_FINISH_BASE)
            };

            var roomsWithIssues = new JArray();
            foreach (var r in allRooms)
            {
                var missingFields = new List<string>();
                foreach (var (name, bip) in finishFields)
                {
                    var p = r.get_Parameter(bip);
                    if (p == null || string.IsNullOrWhiteSpace(p.AsString()))
                        missingFields.Add(name);
                }

                if (missingFields.Count > 0)
                {
                    string roomName = "";
                    try { roomName = r.Name ?? ""; } catch { }
                    string roomNumber = "";
                    try { roomNumber = r.Number ?? ""; } catch { }
                    string levelName = doc.GetElement(r.LevelId)?.Name ?? "(unknown)";

                    roomsWithIssues.Add(new JObject
                    {
                        ["id"] = r.Id.Value,
                        ["number"] = roomNumber,
                        ["name"] = roomName,
                        ["level"] = levelName,
                        ["missingFields"] = new JArray(missingFields)
                    });
                }
            }

            return new JObject
            {
                ["rooms"] = roomsWithIssues,
                ["total"] = roomsWithIssues.Count
            };
        }
    }
}

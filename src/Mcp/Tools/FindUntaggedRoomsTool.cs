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
    /// 查找所有未放置房间标签（Room Tag）的房间。
    /// 通过差集计算：所有已放置 Room - 有 Tag 关联的 Room = 未标记房间。
    /// 可选 levelName 过滤。
    /// </summary>
    public class FindUntaggedRoomsTool : IRevitTool
    {
        public string Name => "find_untagged_rooms";
        public string Description =>
            "Find placed rooms without room tags, ordered by ElementId. Supports levelName filtering and limit/offset pagination.";

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
                ["limit"] = new JObject
                {
                    ["type"] = "integer",
                    ["description"] = "Maximum rooms returned in this page. Default 100, maximum 1000.",
                    ["minimum"] = 1,
                    ["maximum"] = PaginationOptions.MaxLimit,
                    ["default"] = PaginationOptions.DefaultLimit
                },
                ["offset"] = new JObject
                {
                    ["type"] = "integer",
                    ["description"] = "Zero-based offset into the stable filtered result. Default 0.",
                    ["minimum"] = 0,
                    ["default"] = 0
                }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            var paging = PaginationOptions.Parse(input);

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

            // 所有 Room Tags（作为 IndependentTag，按类别 OST_RoomTags 过滤）
            var tags = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_RoomTags)
                .WhereElementIsNotElementType()
                .OfType<RoomTag>()
                .ToList();

            // 获取已标记的 Room ID 集合
            var taggedRoomIds = new HashSet<ElementId>();
            foreach (var tag in tags)
            {
                try
                {
                    var room = tag.Room;
                    if (room != null) taggedRoomIds.Add(room.Id);
                }
                catch { }
            }

            // 差集：未标记的房间
            IEnumerable<Room> untaggedQuery = allRooms.Where(r => !taggedRoomIds.Contains(r.Id));

            // 可选 levelName 过滤
            if (!string.IsNullOrEmpty(filterLevel))
            {
                untaggedQuery = untaggedQuery.Where(r =>
                {
                    var level = doc.GetElement(r.LevelId);
                    return level != null && string.Equals(level.Name, filterLevel, StringComparison.Ordinal);
                });
            }

            var untagged = untaggedQuery
                .OrderBy(r => r.Id.Value)
                .ToList();
            var pageRooms = untagged
                .Skip(paging.Offset)
                .Take(paging.Limit)
                .ToList();

            var roomsArr = new JArray();
            foreach (var r in pageRooms)
            {
                string roomName = "";
                try { roomName = r.Name ?? ""; } catch { }
                string roomNumber = "";
                try { roomNumber = r.Number ?? ""; } catch { }
                string levelName = doc.GetElement(r.LevelId)?.Name ?? "(unknown)";

                roomsArr.Add(new JObject
                {
                    ["id"] = r.Id.Value,
                    ["number"] = roomNumber,
                    ["name"] = roomName,
                    ["level"] = levelName
                });
            }

            var result = new JObject
            {
                ["untaggedRooms"] = roomsArr
            };
            result.Merge(paging.CreateMetadata(untagged.Count, roomsArr.Count));
            return result;
        }
    }
}

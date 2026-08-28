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
    /// 获取房间边界轮廓线。返回房间的外圈和内圈（米单位）。
    /// 可选 roomId 或 levelName 过滤。
    /// </summary>
    public class GetRoomBoundariesTool : IRevitTool
    {
        public string Name => "get_room_boundaries";
        public string Description =>
            "Get room boundary loops in metres, ordered by ElementId. Supports roomId/levelName filters and limit/offset pagination.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["roomId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Optional room ElementId. If provided, only that room is returned."
                },
                ["levelName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional level name filter."
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

            long? filterRoomId = null;
            string filterLevel = null;

            if (input != null)
            {
                var idToken = input["roomId"];
                if (idToken != null && idToken.Type != JTokenType.Null)
                    filterRoomId = (long)idToken;
                var lvToken = input["levelName"];
                if (lvToken != null && lvToken.Type != JTokenType.Null)
                    filterLevel = (string)lvToken;
            }

            IEnumerable<Room> filteredRooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>();

            if (filterRoomId.HasValue)
                filteredRooms = filteredRooms.Where(room => room.Id.Value == filterRoomId.Value);
            if (!string.IsNullOrEmpty(filterLevel))
            {
                filteredRooms = filteredRooms.Where(room =>
                {
                    var level = doc.GetElement(room.LevelId);
                    return level != null && string.Equals(level.Name, filterLevel, StringComparison.Ordinal);
                });
            }

            var rooms = filteredRooms
                .OrderBy(room => room.Id.Value)
                .ToList();
            var pageRooms = rooms
                .Skip(paging.Offset)
                .Take(paging.Limit)
                .ToList();

            var opts = new SpatialElementBoundaryOptions();

            var roomsArr = new JArray();
            foreach (var room in pageRooms)
            {
                string roomName = null;
                try { roomName = room.Name; } catch { }
                string roomNumber = null;
                try { roomNumber = room.Number; } catch { }

                var boundaries = new JArray();
                IList<IList<BoundarySegment>> loops = null;
                try { loops = room.GetBoundarySegments(opts); } catch { }

                if (loops != null)
                {
                    foreach (var loop in loops)
                    {
                        var ptsArr = new JArray();
                        foreach (var seg in loop)
                        {
                            var curve = seg.GetCurve();
                            var pt = curve.GetEndPoint(0);
                            ptsArr.Add(new JObject
                            {
                                ["x"] = Math.Round(pt.X * 0.3048, 3),
                                ["y"] = Math.Round(pt.Y * 0.3048, 3)
                            });
                        }
                        // 不重复添加结束点（首尾相接）
                        boundaries.Add(ptsArr);
                    }
                }

                roomsArr.Add(new JObject
                {
                    ["id"] = room.Id.Value,
                    ["name"] = roomName ?? (JToken)JValue.CreateNull(),
                    ["number"] = roomNumber ?? (JToken)JValue.CreateNull(),
                    ["boundaries"] = boundaries
                });
            }

            var result = new JObject
            {
                ["rooms"] = roomsArr
            };
            result.Merge(paging.CreateMetadata(rooms.Count, roomsArr.Count));
            return result;
        }
    }
}

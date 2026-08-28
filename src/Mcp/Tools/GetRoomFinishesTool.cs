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
    /// 读取房间（Room）的装修面层参数：地面、天花、墙面、踢脚线。
    /// 可选 levelName 过滤到指定标高。
    /// </summary>
    public class GetRoomFinishesTool : IRevitTool
    {
        public string Name => "get_room_finishes";
        public string Description =>
            "Read finish surface parameters from all Rooms (floor finish, ceiling finish, wall finish, base finish). " +
            "Optional 'levelName' filter restricts to rooms on a specific level.";

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
                var token = input["levelName"];
                if (token != null && token.Type != JTokenType.Null)
                    filterLevel = (string)token;
            }

            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>();

            var roomsArr = new JArray();
            foreach (var room in rooms)
            {
                // 如果传了 levelName 过滤
                if (!string.IsNullOrEmpty(filterLevel))
                {
                    var level = doc.GetElement(room.LevelId);
                    if (level == null || !string.Equals(level.Name, filterLevel, StringComparison.Ordinal))
                        continue;
                }

                string floorFinish  = GetBuiltInParamAsString(room, BuiltInParameter.ROOM_FINISH_FLOOR);
                string ceilingFinish = GetBuiltInParamAsString(room, BuiltInParameter.ROOM_FINISH_CEILING);
                string wallFinish   = GetBuiltInParamAsString(room, BuiltInParameter.ROOM_FINISH_WALL);
                string baseFinish   = GetBuiltInParamAsString(room, BuiltInParameter.ROOM_FINISH_BASE);

                string roomName = null;
                try { roomName = room.Name; } catch { }
                string roomNumber = null;
                try { roomNumber = room.Number; } catch { }

                roomsArr.Add(new JObject
                {
                    ["id"] = room.Id.Value,
                    ["name"] = roomName ?? (JToken)JValue.CreateNull(),
                    ["number"] = roomNumber ?? (JToken)JValue.CreateNull(),
                    ["floorFinish"] = floorFinish ?? (JToken)JValue.CreateNull(),
                    ["ceilingFinish"] = ceilingFinish ?? (JToken)JValue.CreateNull(),
                    ["wallFinish"] = wallFinish ?? (JToken)JValue.CreateNull(),
                    ["baseFinish"] = baseFinish ?? (JToken)JValue.CreateNull()
                });
            }

            return new JObject
            {
                ["rooms"] = roomsArr,
                ["total"] = roomsArr.Count
            };
        }

        private static string GetBuiltInParamAsString(Element e, BuiltInParameter bip)
        {
            try
            {
                var p = e.get_Parameter(bip);
                if (p == null) return null;
                var val = p.AsValueString();
                if (!string.IsNullOrEmpty(val)) return val;
                return p.AsString();
            }
            catch { return null; }
        }
    }
}

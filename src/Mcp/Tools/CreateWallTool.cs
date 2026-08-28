using System;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// Create a Wall in the active document between two points.
    /// Points are in millimeters, converted to Revit internal feet (1 ft = 304.8 mm).
    /// Entire operation wrapped in a Transaction.
    /// </summary>
    public class CreateWallTool : IRevitTool
    {
        public string Name => "create_wall";

        public string Description =>
            "在两点之间创建一段墙。标高必须已存在。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["startXMm"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Start point X coordinate in millimeters."
                },
                ["startYMm"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Start point Y coordinate in millimeters."
                },
                ["endXMm"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "End point X coordinate in millimeters."
                },
                ["endYMm"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "End point Y coordinate in millimeters."
                },
                ["levelName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Name of an existing Level to host the wall (e.g. '标高 1')."
                },
                ["heightMm"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Wall height in millimeters (default 3000)."
                },
                ["wallTypeName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Wall type name (default: first Basic wall type in the document)."
                }
            },
            ["required"] = new JArray { "startXMm", "startYMm", "endXMm", "endYMm", "levelName" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc   = uidoc.Document       ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            // --- 解析坐标（毫米 → 英尺）---
            double startXMm = (double)input["startXMm"];
            double startYMm = (double)input["startYMm"];
            double endXMm   = (double)input["endXMm"];
            double endYMm   = (double)input["endYMm"];

            double startXFt = startXMm / 304.8;
            double startYFt = startYMm / 304.8;
            double endXFt   = endXMm   / 304.8;
            double endYFt   = endYMm   / 304.8;

            // --- 解析高度（默认 3000 mm）---
            double heightMm = 3000;
            var heightToken = input["heightMm"];
            if (heightToken != null && heightToken.Type != JTokenType.Null)
                heightMm = (double)heightToken;
            double heightFt = heightMm / 304.8;

            // --- 找 Level ---
            string levelName = (string)input["levelName"];
            if (string.IsNullOrEmpty(levelName))
                throw new ArgumentException("'levelName' is required and must be non-empty.");

            Level level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => l.Name == levelName);
            if (level == null)
                throw new ArgumentException("Level '" + levelName + "' not found in the active document.");

            // --- 找 WallType ---
            WallType wallType = null;
            string wallTypeName = (string)input["wallTypeName"];
            if (!string.IsNullOrEmpty(wallTypeName))
            {
                wallType = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .FirstOrDefault(wt => wt.Name == wallTypeName);
                if (wallType == null)
                    throw new ArgumentException("WallType '" + wallTypeName + "' not found in the active document.");
            }
            else
            {
                // 默认取第一个 Basic 类型的墙
                wallType = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .FirstOrDefault(wt => wt.Kind == WallKind.Basic);
                if (wallType == null)
                    throw new InvalidOperationException("No Basic wall type found in the active document.");
            }

            // --- 创建 Line ---
            XYZ startPt = new XYZ(startXFt, startYFt, 0);
            XYZ endPt   = new XYZ(endXFt,   endYFt,   0);
            Line line = Line.CreateBound(startPt, endPt);

            // --- 计算长度（毫米）用于返回 ---
            double lengthMm = Math.Sqrt(
                (endXMm - startXMm) * (endXMm - startXMm) +
                (endYMm - startYMm) * (endYMm - startYMm));

            string txName = "Create wall: " + lengthMm.ToString("F1", CultureInfo.InvariantCulture) + "mm";
            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                try
                {
                    // 参照 SDK CreateWallsUnderBeams 的 8 参数 Wall.Create 模板
                    Wall wall = Wall.Create(doc, line, wallType.Id, level.Id, heightFt, 0, false, false);
                    if (wall == null)
                        throw new InvalidOperationException("Wall.Create returned null.");

                    JarviTools.Core.TransactionSafety.Commit(tx, "Create wall");

                    return new JObject
                    {
                        ["wallId"]    = wall.Id.Value,
                        ["level"]     = level.Name,
                        ["lengthMm"]  = Math.Round(lengthMm, 1),
                        ["heightMm"]  = Math.Round(heightMm, 1)
                    };
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }
        }
    }
}

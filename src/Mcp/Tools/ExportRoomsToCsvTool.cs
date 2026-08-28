using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 导出房间明细到 CSV：编号、名称、楼层、部门、面积、4种饰面。
    /// 可选 levelName 过滤到指定标高。跳过未放置房间。
    /// </summary>
    public class ExportRoomsToCsvTool : IRevitTool
    {
        public string Name => "export_rooms_to_csv";
        public string Description =>
            "导出房间明细(编号/名称/楼层/部门/面积/4种饰面)到 CSV 文件。可选 levelName 过滤。跳过未放置房间。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["outputPath"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Absolute Windows path for the output CSV file."
                },
                ["levelName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional level name to filter rooms."
                },
                ["overwrite"] = new JObject { ["type"] = "boolean", ["default"] = false }
            },
            ["required"] = new JArray { "outputPath" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            bool overwrite = input != null && ((bool?)input["overwrite"]).GetValueOrDefault();
            string outputPath = CsvExportSafety.PreparePath((string)input["outputPath"], overwrite);

            var parentDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                throw new DirectoryNotFoundException($"Output directory does not exist: {parentDir}");

            string filterLevel = null;
            if (input != null)
            {
                var lvl = input["levelName"];
                if (lvl != null && lvl.Type != JTokenType.Null)
                    filterLevel = (string)lvl;
            }

            var allRooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .ToList();

            // 过滤未放置
            var placed = allRooms.Where(r => r.Location != null && r.Area >= 1e-6).ToList();
            var unplacedCount = allRooms.Count - placed.Count;

            var filtered = placed.AsEnumerable();
            if (!string.IsNullOrEmpty(filterLevel))
            {
                filtered = filtered.Where(r =>
                {
                    var level = doc.GetElement(r.LevelId);
                    return level != null && string.Equals(level.Name, filterLevel, StringComparison.Ordinal);
                });
            }

            var roomsList = filtered.ToList();

            var sb = new StringBuilder();
            sb.AppendLine("编号,名称,楼层,部门,面积_平方米,楼板饰面,天花饰面,墙面饰面,踢脚饰面");

            int rowCount = 0;
            foreach (var r in roomsList)
            {
                string number = r.Number ?? "";
                string name = r.Name ?? "";
                string levelName = doc.GetElement(r.LevelId)?.Name ?? "";
                string department = r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString() ?? "";
                double area = r.Area * 0.092903;

                string floorFinish = r.get_Parameter(BuiltInParameter.ROOM_FINISH_FLOOR)?.AsString() ?? "";
                string ceilingFinish = r.get_Parameter(BuiltInParameter.ROOM_FINISH_CEILING)?.AsString() ?? "";
                string wallFinish = r.get_Parameter(BuiltInParameter.ROOM_FINISH_WALL)?.AsString() ?? "";
                string baseFinish = r.get_Parameter(BuiltInParameter.ROOM_FINISH_BASE)?.AsString() ?? "";

                sb.AppendLine(
                    $"{EscapeCsvField(number)},{EscapeCsvField(name)},{EscapeCsvField(levelName)}," +
                    $"{EscapeCsvField(department)},{Math.Round(area, 3)}," +
                    $"{EscapeCsvField(floorFinish)},{EscapeCsvField(ceilingFinish)}," +
                    $"{EscapeCsvField(wallFinish)},{EscapeCsvField(baseFinish)}"
                );
                rowCount++;
            }

            CsvExportSafety.WriteAllTextAtomic(outputPath, sb.ToString());

            return new JObject
            {
                ["filePath"] = outputPath,
                ["rowCount"] = rowCount,
                ["skippedUnplaced"] = unplacedCount
            };
        }

        private static string EscapeCsvField(string field)
        {
            return CsvExportSafety.EscapeField(field);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 汇总所有墙体（Wall）按类型分组：数量、总长度、总面积、平均厚度、楼层分布。
    /// 可选 levelName 过滤到指定标高。
    /// </summary>
    public class GetWallsSummaryTool : IRevitTool
    {
        public string Name => "get_walls_summary";
        public string Description =>
            "汇总所有墙体按类型分组：数量、总长度(米)、总面积(平方米)、平均厚度(毫米)、楼层分布。可选 levelName 过滤。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["levelName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional level name to filter walls (e.g. '标高 1')."
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

            var walls = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .Cast<Wall>()
                .ToList();

            // 可选 levelName 过滤
            if (!string.IsNullOrEmpty(filterLevel))
            {
                walls = walls.Where(w =>
                {
                    var level = doc.GetElement(w.LevelId);
                    return level != null && string.Equals(level.Name, filterLevel, StringComparison.Ordinal);
                }).ToList();
            }

            // 按墙类型分组
            var groups = walls.GroupBy(w => w.GetTypeId()).ToList();

            var wallsArr = new JArray();
            double totalLengthAll = 0;
            double totalAreaAll = 0;

            foreach (var g in groups)
            {
                var wallType = doc.GetElement(g.Key) as WallType;
                string typeName = wallType?.Name ?? "(unknown)";

                // 厚度 (mm)
                double thicknessMm = 0;
                if (wallType != null)
                    thicknessMm = wallType.Width * 0.3048 * 1000.0;

                double groupLength = 0;
                double groupArea = 0;
                var levelDist = new Dictionary<string, int>();

                foreach (var w in g)
                {
                    // 长度 (m)
                    var lenParam = w.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                    double len = (lenParam != null) ? lenParam.AsDouble() * 0.3048 : 0;
                    groupLength += len;

                    // 面积 (m2)
                    var areaParam = w.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                    double area = (areaParam != null) ? areaParam.AsDouble() * 0.092903 : 0;
                    groupArea += area;

                    // 楼层分布
                    var levelName = doc.GetElement(w.LevelId)?.Name ?? "(unknown)";
                    levelDist.TryGetValue(levelName, out int cnt);
                    levelDist[levelName] = cnt + 1;
                }

                totalLengthAll += groupLength;
                totalAreaAll += groupArea;

                wallsArr.Add(new JObject
                {
                    ["typeName"] = typeName,
                    ["count"] = g.Count(),
                    ["totalLength_m"] = Math.Round(groupLength, 3),
                    ["totalArea_m2"] = Math.Round(groupArea, 3),
                    ["averageThickness_mm"] = Math.Round(thicknessMm, 1),
                    ["levelDistribution"] = new JArray(
                        levelDist.Select(kv => new JObject
                        {
                            ["level"] = kv.Key,
                            ["count"] = kv.Value
                        })
                    )
                });
            }

            return new JObject
            {
                ["walls"] = wallsArr,
                ["totalWalls"] = walls.Count,
                ["totalLength_m"] = Math.Round(totalLengthAll, 3),
                ["totalArea_m2"] = Math.Round(totalAreaAll, 3)
            };
        }
    }
}

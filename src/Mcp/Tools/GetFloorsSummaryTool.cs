using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 汇总所有楼板（Floor）按类型分组：数量、总面积、总体积、平均厚度、楼层分布。
    /// 可选 levelName 过滤到指定标高。
    /// </summary>
    public class GetFloorsSummaryTool : IRevitTool
    {
        public string Name => "get_floors_summary";
        public string Description =>
            "汇总所有楼板按类型分组：数量、总面积(平方米)、总体积(立方米)、平均厚度(毫米)、楼层分布。可选 levelName 过滤。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["levelName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional level name to filter floors (e.g. '标高 1')."
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

            var floors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Floors)
                .WhereElementIsNotElementType()
                .Cast<Floor>()
                .ToList();

            // 可选 levelName 过滤
            if (!string.IsNullOrEmpty(filterLevel))
            {
                floors = floors.Where(f =>
                {
                    var level = doc.GetElement(f.LevelId);
                    return level != null && string.Equals(level.Name, filterLevel, StringComparison.Ordinal);
                }).ToList();
            }

            // 按类型分组
            var groups = floors.GroupBy(f => f.GetTypeId()).ToList();

            var floorsArr = new JArray();
            double totalAreaAll = 0;
            double totalVolumeAll = 0;

            foreach (var g in groups)
            {
                var floorType = doc.GetElement(g.Key) as FloorType;
                string typeName = floorType?.Name ?? "(unknown)";

                double groupArea = 0;
                double groupVolume = 0;
                double sumThickness = 0;
                var levelDist = new Dictionary<string, int>();

                foreach (var f in g)
                {
                    // 面积 (m2)
                    var areaParam = f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                    double area = (areaParam != null) ? areaParam.AsDouble() * 0.092903 : 0;
                    groupArea += area;

                    // 体积 (m3)
                    var volParam = f.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
                    double vol = (volParam != null) ? volParam.AsDouble() * 0.02832 : 0;
                    groupVolume += vol;

                    // 厚度 (mm)
                    double thicknessMm = 0;
                    var thickParam = f.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM);
                    if (thickParam != null)
                    {
                        thicknessMm = thickParam.AsDouble() * 304.8; // 英尺转毫米
                    }
                    else if (floorType != null)
                    {
                        var compStruct = floorType.GetCompoundStructure();
                        if (compStruct != null)
                            thicknessMm = compStruct.GetWidth() * 0.3048 * 1000.0;
                    }
                    sumThickness += thicknessMm;

                    // 楼层分布
                    var levelName = doc.GetElement(f.LevelId)?.Name ?? "(unknown)";
                    levelDist.TryGetValue(levelName, out int cnt);
                    levelDist[levelName] = cnt + 1;
                }

                totalAreaAll += groupArea;
                totalVolumeAll += groupVolume;

                floorsArr.Add(new JObject
                {
                    ["typeName"] = typeName,
                    ["count"] = g.Count(),
                    ["totalArea_m2"] = Math.Round(groupArea, 3),
                    ["totalVolume_m3"] = Math.Round(groupVolume, 3),
                    ["averageThickness_mm"] = Math.Round(sumThickness / g.Count(), 1),
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
                ["floors"] = floorsArr,
                ["totalFloors"] = floors.Count,
                ["totalArea_m2"] = Math.Round(totalAreaAll, 3),
                ["totalVolume_m3"] = Math.Round(totalVolumeAll, 3)
            };
        }
    }
}

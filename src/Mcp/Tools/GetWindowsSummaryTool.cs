using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 汇总所有窗（Window）按类型分组：数量、尺寸（mm）、楼层分布、所在墙类型。
    /// 可选 levelName 过滤到指定标高。
    /// </summary>
    public class GetWindowsSummaryTool : IRevitTool
    {
        public string Name => "get_windows_summary";
        public string Description =>
            "汇总所有窗按类型分组：数量、宽度/高度(毫米)、楼层分布、所在墙类型。可选 levelName 过滤。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["levelName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional level name to filter windows (e.g. '标高 1')."
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

            var windows = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Windows)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .ToList();

            // 可选 levelName 过滤
            if (!string.IsNullOrEmpty(filterLevel))
            {
                windows = windows.Where(w =>
                {
                    var hostLevelId = w.Host?.LevelId ?? w.LevelId;
                    var level = hostLevelId != null ? doc.GetElement(hostLevelId) : null;
                    return level != null && string.Equals(level.Name, filterLevel, StringComparison.Ordinal);
                }).ToList();
            }

            // 按类型分组
            var groups = windows.GroupBy(w => w.GetTypeId()).ToList();

            var windowsArr = new JArray();
            foreach (var g in groups)
            {
                var type = doc.GetElement(g.Key) as FamilySymbol;
                string typeName = type?.Name ?? "(unknown)";

                // 从 Type 取宽度/高度
                double widthMm = 0;
                double heightMm = 0;

                var wParam = type?.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM);
                if (wParam != null)
                    widthMm = wParam.AsDouble() * 304.8;

                var hParam = type?.get_Parameter(BuiltInParameter.FAMILY_HEIGHT_PARAM);
                if (hParam != null)
                    heightMm = hParam.AsDouble() * 304.8;

                // 楼层分布 & hostWallTypes
                var levelDist = new Dictionary<string, int>();
                var hostWallTypeNames = new HashSet<string>();

                foreach (var w in g)
                {
                    var hostLevelId = w.Host?.LevelId ?? w.LevelId;
                    var levelName = hostLevelId != null
                        ? doc.GetElement(hostLevelId)?.Name ?? "(unknown)"
                        : "(unknown)";
                    levelDist.TryGetValue(levelName, out int cnt);
                    levelDist[levelName] = cnt + 1;

                    if (w.Host is Wall hostWall)
                    {
                        var wallTypeName = hostWall.Name;
                        if (!string.IsNullOrEmpty(wallTypeName))
                            hostWallTypeNames.Add(wallTypeName);
                    }
                }

                windowsArr.Add(new JObject
                {
                    ["typeName"] = typeName,
                    ["count"] = g.Count(),
                    ["width_mm"] = Math.Round(widthMm, 1),
                    ["height_mm"] = Math.Round(heightMm, 1),
                    ["levelDistribution"] = new JArray(
                        levelDist.Select(kv => new JObject
                        {
                            ["level"] = kv.Key,
                            ["count"] = kv.Value
                        })
                    ),
                    ["hostWallTypes"] = new JArray(hostWallTypeNames.OrderBy(n => n))
                });
            }

            return new JObject
            {
                ["windows"] = windowsArr,
                ["totalWindows"] = windows.Count
            };
        }
    }
}

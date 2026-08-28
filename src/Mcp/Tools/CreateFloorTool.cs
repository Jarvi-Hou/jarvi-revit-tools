using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 在项目中创建楼板。输入多边形点列（单位米），
    /// 可选 floorTypeId 和 levelId。Transaction 包裹。
    /// </summary>
    public class CreateFloorTool : IRevitTool
    {
        public string Name => "create_floor";
        public string Description =>
            "根据闭合多边形创建楼板。可选 floorTypeId 和 levelId。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["points"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject
                    {
                        ["type"] = "array",
                        ["items"] = new JObject { ["type"] = "number" },
                        ["minItems"] = 2,
                        ["maxItems"] = 2,
                        ["description"] = "[x, y] coordinate pair in meters"
                    },
                    ["description"] = "Closed polygon points in meters, e.g. [[0,0],[5,0],[5,3],[0,3]]"
                },
                ["floorTypeId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Optional FloorType ElementId. Defaults to first FloorType in project."
                },
                ["levelId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Optional Level ElementId. Defaults to active view's associated level."
                }
            },
            ["required"] = new JArray { "points" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            // --- 解析点列（米 → 英尺）---
            var pointsToken = (JArray)input["points"];
            if (pointsToken == null || pointsToken.Count < 3)
                throw new ArgumentException("'points' must be a polygon with at least 3 points.");

            var profile = new List<Curve>();
            int n = pointsToken.Count;
            for (int i = 0; i < n; i++)
            {
                var pt = (JArray)pointsToken[i];
                double xM = (double)pt[0];
                double yM = (double)pt[1];
                var nextPt = (JArray)pointsToken[(i + 1) % n];
                double xNextM = (double)nextPt[0];
                double yNextM = (double)nextPt[1];

                var a = new XYZ(xM / 0.3048, yM / 0.3048, 0);
                var b = new XYZ(xNextM / 0.3048, yNextM / 0.3048, 0);
                profile.Add(Line.CreateBound(a, b));
            }

            // --- 找 Level ---
            ElementId levelId = null;
            var levelToken = input["levelId"];
            if (levelToken != null && levelToken.Type != JTokenType.Null)
            {
                levelId = new ElementId((long)levelToken);
            }
            else
            {
                // 默认用当前视图的标高
                var activeView = doc.ActiveView;
                if (activeView != null && activeView.GenLevel != null)
                    levelId = activeView.GenLevel.Id;
                else
                    throw new InvalidOperationException("Cannot determine default level. Set 'levelId' explicitly.");
            }

            // --- 找 FloorType ---
            ElementId floorTypeId = null;
            var ftToken = input["floorTypeId"];
            if (ftToken != null && ftToken.Type != JTokenType.Null)
            {
                floorTypeId = new ElementId((long)ftToken);
            }
            else
            {
                var firstFt = new FilteredElementCollector(doc)
                    .OfClass(typeof(FloorType))
                    .Cast<FloorType>()
                    .FirstOrDefault(ft => ft.Category != null);
                if (firstFt == null)
                    throw new InvalidOperationException("No FloorType found in project.");
                floorTypeId = firstFt.Id;
            }

            // --- 创建 CurveLoop ---
            var curveLoop = CurveLoop.Create(profile);
            var loops = new List<CurveLoop> { curveLoop };

            Floor floor = null;
            string levelName = null;
            string ftName = null;

            using (var tx = new Transaction(doc, "Create floor"))
            {
                tx.Start();
                try
                {
                    floor = Floor.Create(doc, loops, floorTypeId, levelId);
                    if (floor == null)
                        throw new InvalidOperationException("Floor.Create returned null.");

                    var level = doc.GetElement(levelId) as Level;
                    levelName = level?.Name;
                    var ft = doc.GetElement(floorTypeId) as FloorType;
                    ftName = ft?.Name;

                    JarviTools.Core.TransactionSafety.Commit(tx, "Create floor");
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }

            // 计算面积（平方英尺 → 平方米）
            double areaM2 = 0;
            try
            {
                var areaParam = floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                if (areaParam != null)
                    areaM2 = areaParam.AsDouble() * 0.092903; // sq ft → m²
            }
            catch { }

            return new JObject
            {
                ["floorId"] = floor.Id.Value,
                ["levelName"] = levelName ?? (JToken)JValue.CreateNull(),
                ["floorTypeName"] = ftName ?? (JToken)JValue.CreateNull(),
                ["area_m2"] = Math.Round(areaM2, 2)
            };
        }
    }
}

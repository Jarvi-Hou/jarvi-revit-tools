using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 获取单根风管（Duct）的详细参数：尺寸、长度、标高、坡度、连接元素等。
    /// </summary>
    public class GetDuctParametersTool : IRevitTool
    {
        public string Name => "get_duct_parameters";
        public string Description =>
            "获取单根风管的详细参数：尺寸(圆/矩)、长度、标高、坡度、系统信息、连接元素等。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["ductId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "ElementId of the Duct."
                }
            },
            ["required"] = new JArray { "ductId" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            long ductIdLong = (long)input["ductId"];
            var ductId = new ElementId(ductIdLong);
            var duct = doc.GetElement(ductId) as Duct;
            if (duct == null)
                throw new ArgumentException($"Element {ductIdLong} is not a Duct or does not exist.");

            // 系统信息
            string systemName = duct.MEPSystem?.Name;
            var mechanicalSystem = duct.MEPSystem as MechanicalSystem;
            string systemType = mechanicalSystem == null ? null : mechanicalSystem.SystemType.ToString();

            // 类型名称
            string typeName = duct.DuctType?.Name ?? "(unknown)";

            // 尺寸：圆形 vs 矩形
            double? diameterMm = null;
            double? widthMm = null;
            double? heightMm = null;
            bool isRound = (duct.DuctType?.Shape == ConnectorProfileType.Round);

            if (isRound)
            {
                var dParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                if (dParam != null)
                    diameterMm = dParam.AsDouble() * 304.8;
            }
            else
            {
                var wParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                if (wParam != null)
                    widthMm = wParam.AsDouble() * 304.8;

                var hParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                if (hParam != null)
                    heightMm = hParam.AsDouble() * 304.8;
            }

            // 长度 (m)
            double lengthM = 0;
            var lenParam = duct.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
            if (lenParam != null)
                lengthM = lenParam.AsDouble() * 0.3048;

            // 标高 (m)
            double? bottomElevationM = null;
            double? topElevationM = null;
            var bottomParam = duct.get_Parameter(BuiltInParameter.RBS_DUCT_BOTTOM_ELEVATION);
            if (bottomParam != null)
                bottomElevationM = bottomParam.AsDouble() * 0.3048;

            var topParam = duct.get_Parameter(BuiltInParameter.RBS_DUCT_TOP_ELEVATION);
            if (topParam != null)
                topElevationM = topParam.AsDouble() * 0.3048;

            // 坡度
            double? slopePercent = null;
            var slopeParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_SLOPE);
            if (slopeParam != null)
                slopePercent = Math.Round(slopeParam.AsDouble() * 100.0, 3);

            // 连接的元素
            var connectedArr = new JArray();
            try
            {
                var connectors = duct.ConnectorManager?.Connectors;
                if (connectors != null)
                {
                    var seenIds = new HashSet<long>();
                    foreach (Connector c in connectors)
                    {
                        foreach (Connector r in c.AllRefs)
                        {
                            if (r.Owner != null && r.Owner.Id.Value != duct.Id.Value)
                            {
                                long otherId = r.Owner.Id.Value;
                                if (seenIds.Add(otherId))
                                {
                                    string otherName = "";
                                    try { otherName = r.Owner.Name ?? ""; } catch { }
                                    connectedArr.Add(new JObject
                                    {
                                        ["id"] = otherId,
                                        ["name"] = otherName
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            var result = new JObject
            {
                ["id"] = duct.Id.Value,
                ["name"] = GetSafeName(duct),
                ["typeName"] = typeName,
                ["systemName"] = systemName ?? (JToken)JValue.CreateNull(),
                ["systemType"] = systemType ?? (JToken)JValue.CreateNull(),
                ["isRound"] = isRound,
                ["diameter_mm"] = diameterMm.HasValue ? Math.Round(diameterMm.Value, 1) : (JToken)JValue.CreateNull(),
                ["width_mm"] = widthMm.HasValue ? Math.Round(widthMm.Value, 1) : (JToken)JValue.CreateNull(),
                ["height_mm"] = heightMm.HasValue ? Math.Round(heightMm.Value, 1) : (JToken)JValue.CreateNull(),
                ["length_m"] = Math.Round(lengthM, 3),
                ["bottomElevation_m"] = bottomElevationM.HasValue ? Math.Round(bottomElevationM.Value, 3) : (JToken)JValue.CreateNull(),
                ["topElevation_m"] = topElevationM.HasValue ? Math.Round(topElevationM.Value, 3) : (JToken)JValue.CreateNull(),
                ["slope_percent"] = slopePercent.HasValue ? slopePercent.Value : (JToken)JValue.CreateNull(),
                ["connectedElements"] = connectedArr
            };

            return result;
        }

        private static string GetSafeName(Element e)
        {
            try { return e.Name ?? ""; }
            catch { return ""; }
        }
    }
}

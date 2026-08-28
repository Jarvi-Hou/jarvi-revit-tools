using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 获取元素的几何信息：包围框、体积、面积、长度。
    /// 所有数值以米/立方米/平方米为单位。
    /// </summary>
    public class GetElementGeometryTool : IRevitTool
    {
        public string Name => "get_element_geometry";
        public string Description =>
            "获取元素的几何信息：包围盒、体积、面积、长度。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["elementId"] = new JObject
                {
                    ["type"] = "integer",
                    ["description"] = "ElementId of the element to inspect."
                }
            },
            ["required"] = new JArray { "elementId" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            long idLong = (long)input["elementId"];
            var elemId = new ElementId(idLong);
            var elem = doc.GetElement(elemId);
            if (elem == null)
                throw new InvalidOperationException("Element with id " + idLong + " not found.");

            string elemName = null;
            try { elemName = elem.Name; } catch { }
            string catName = null;
            try { catName = elem.Category?.Name; } catch { }

            // 包围框
            JObject bbObj = null;
            try
            {
                var bb = elem.get_BoundingBox(null);
                if (bb != null)
                {
                    bbObj = new JObject
                    {
                        ["min"] = new JObject
                        {
                            ["x"] = Math.Round(bb.Min.X * 0.3048, 3),
                            ["y"] = Math.Round(bb.Min.Y * 0.3048, 3),
                            ["z"] = Math.Round(bb.Min.Z * 0.3048, 3)
                        },
                        ["max"] = new JObject
                        {
                            ["x"] = Math.Round(bb.Max.X * 0.3048, 3),
                            ["y"] = Math.Round(bb.Max.Y * 0.3048, 3),
                            ["z"] = Math.Round(bb.Max.Z * 0.3048, 3)
                        }
                    };
                }
            }
            catch { }

            // 体积（立方英尺 → 立方米）
            double? volumeM3 = null;
            try
            {
                var p = elem.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
                if (p != null && p.HasValue)
                    volumeM3 = p.AsDouble() * 0.0283168;
            }
            catch { }

            // 面积（平方英尺 → 平方米）
            double? areaM2 = null;
            try
            {
                var p = elem.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                if (p != null && p.HasValue)
                    areaM2 = p.AsDouble() * 0.092903;
            }
            catch { }

            // 长度（英尺 → 米）
            double? lengthM = null;
            try
            {
                var p = elem.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                if (p != null && p.HasValue)
                    lengthM = p.AsDouble() * 0.3048;
            }
            catch { }

            return new JObject
            {
                ["id"] = idLong,
                ["name"] = elemName ?? (JToken)JValue.CreateNull(),
                ["category"] = catName ?? (JToken)JValue.CreateNull(),
                ["boundingBox"] = bbObj ?? (JToken)JValue.CreateNull(),
                ["volume_m3"] = volumeM3.HasValue ? (JToken)Math.Round(volumeM3.Value, 4) : JValue.CreateNull(),
                ["area_m2"] = areaM2.HasValue ? (JToken)Math.Round(areaM2.Value, 3) : JValue.CreateNull(),
                ["length_m"] = lengthM.HasValue ? (JToken)Math.Round(lengthM.Value, 3) : JValue.CreateNull()
            };
        }
    }
}

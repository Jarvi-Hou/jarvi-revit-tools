using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 获取单根管道（Pipe）的详细参数：直径、长度、标高、坡度、材质、连接元素等。
    /// </summary>
    public class GetPipeParametersTool : IRevitTool
    {
        public string Name => "get_pipe_parameters";
        public string Description =>
            "获取单根管道的详细参数：直径、长度、标高、坡度、材质、系统信息、连接元素等。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["pipeId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "ElementId of the Pipe."
                }
            },
            ["required"] = new JArray { "pipeId" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            long pipeIdLong = (long)input["pipeId"];
            var pipeId = new ElementId(pipeIdLong);
            var pipe = doc.GetElement(pipeId) as Pipe;
            if (pipe == null)
                throw new ArgumentException($"Element {pipeIdLong} is not a Pipe or does not exist.");

            // 系统信息
            string systemName = pipe.MEPSystem?.Name;
            var pipingSystem = pipe.MEPSystem as PipingSystem;
            string systemType = pipingSystem == null ? null : pipingSystem.SystemType.ToString();

            // 类型/材质。ELEM_TYPE_PARAM 是“类型引用”，不是材质；此前会把
            // 管道类型名误报为材质。管道材质应从管道类型的专用参数读取。
            string typeName = pipe.PipeType?.Name ?? "(unknown)";
            string material = "";
            try
            {
                var matParam = pipe.PipeType?.get_Parameter(BuiltInParameter.RBS_PIPE_MATERIAL_PARAM);
                if (matParam != null)
                {
                    ElementId materialId = matParam.AsElementId();
                    Material materialElement = materialId == null || materialId == ElementId.InvalidElementId
                        ? null
                        : doc.GetElement(materialId) as Material;
                    material = materialElement == null
                        ? (matParam.AsValueString() ?? matParam.AsString() ?? "")
                        : materialElement.Name;
                }
            }
            catch { }

            // 直径 (mm)
            double? diameterMm = null;
            var dParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (dParam != null)
                diameterMm = dParam.AsDouble() * 304.8;

            // 长度 (m)
            double lengthM = 0;
            var lenParam = pipe.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
            if (lenParam != null)
                lengthM = lenParam.AsDouble() * 0.3048;

            // 标高 (m)
            double? bottomElevationM = null;
            double? topElevationM = null;
            var bottomParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_BOTTOM_ELEVATION);
            if (bottomParam != null)
                bottomElevationM = bottomParam.AsDouble() * 0.3048;

            var topParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_TOP_ELEVATION);
            if (topParam != null)
                topElevationM = topParam.AsDouble() * 0.3048;

            // 坡度
            double? slopePercent = null;
            var slopeParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_SLOPE);
            if (slopeParam != null)
                slopePercent = Math.Round(slopeParam.AsDouble() * 100.0, 3);

            // 连接的元素
            var connectedArr = new JArray();
            try
            {
                var connectors = pipe.ConnectorManager?.Connectors;
                if (connectors != null)
                {
                    var seenIds = new HashSet<long>();
                    foreach (Connector c in connectors)
                    {
                        foreach (Connector r in c.AllRefs)
                        {
                            if (r.Owner != null && r.Owner.Id.Value != pipe.Id.Value)
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
                ["id"] = pipe.Id.Value,
                ["name"] = GetSafeName(pipe),
                ["typeName"] = typeName,
                ["material"] = material,
                ["systemName"] = systemName ?? (JToken)JValue.CreateNull(),
                ["systemType"] = systemType ?? (JToken)JValue.CreateNull(),
                ["diameter_mm"] = diameterMm.HasValue ? Math.Round(diameterMm.Value, 1) : (JToken)JValue.CreateNull(),
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

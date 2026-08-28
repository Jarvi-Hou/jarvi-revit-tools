using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 获取指定 MEP 元素的连接器（Connector）详细拓扑信息：
    /// 每个连接器的位置、方向、已连接元素和空闲连接数。
    /// 同时支持 MEPCurve（管线）和 FamilyInstance（设备/附件）。
    /// </summary>
    public class GetElementConnectivityTool : IRevitTool
    {
        public string Name => "get_element_connectivity";
        public string Description =>
            "获取指定 MEP 元素的连接器拓扑信息：每个连接器的位置、方向、已连接元素和空闲连接数。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["elementId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "ElementId of the MEP element."
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

            long elemIdLong = (long)input["elementId"];
            var elemId = new ElementId(elemIdLong);
            var elem = doc.GetElement(elemId);
            if (elem == null)
                throw new ArgumentException($"Element {elemIdLong} does not exist.");

            // 获取 ConnectorManager
            ConnectorManager cm = null;
            if (elem is MEPCurve mc)
                cm = mc.ConnectorManager;
            else if (elem is FamilyInstance fi && fi.MEPModel != null)
                cm = fi.MEPModel.ConnectorManager;

            if (cm == null)
                throw new InvalidOperationException($"Element {elemIdLong} has no MEP connectors.");

            var connectors = cm.Connectors;
            if (connectors == null || connectors.IsEmpty)
            {
                return new JObject
                {
                    ["elementId"] = elemIdLong,
                    ["elementName"] = GetSafeName(elem),
                    ["category"] = elem.Category?.Name ?? "",
                    ["connectors"] = new JArray(),
                    ["totalConnectors"] = 0,
                    ["connectedCount"] = 0,
                    ["openCount"] = 0
                };
            }

            var connArr = new JArray();
            int totalConnectors = 0;
            int connectedCount = 0;
            int openCount = 0;

            foreach (Connector c in connectors)
            {
                totalConnectors++;

                // 位置（英尺→米）
                var origin = c.Origin;
                var pos = origin != null
                    ? new JObject
                    {
                        ["x"] = Math.Round(origin.X * 0.3048, 3),
                        ["y"] = Math.Round(origin.Y * 0.3048, 3),
                        ["z"] = Math.Round(origin.Z * 0.3048, 3)
                    }
                    : (JToken)JValue.CreateNull();

                // 连接方向
                string direction = c.Direction.ToString();

                // 已连接的元素
                var connectedArr = new JArray();
                int localConnected = 0;
                try
                {
                    foreach (Connector r in c.AllRefs)
                    {
                        if (r.Owner != null && r.Owner.Id.Value != elemIdLong)
                        {
                            long otherId = r.Owner.Id.Value;
                            string otherName = "";
                            try { otherName = r.Owner.Name ?? ""; } catch { }
                            string otherCat = r.Owner.Category?.Name ?? "";

                            connectedArr.Add(new JObject
                            {
                                ["id"] = otherId,
                                ["name"] = otherName,
                                ["category"] = otherCat
                            });
                            localConnected++;
                        }
                    }
                }
                catch { }

                connectedCount += localConnected;
                if (localConnected == 0)
                    openCount++;

                connArr.Add(new JObject
                {
                    ["connectorIndex"] = totalConnectors - 1,
                    ["direction"] = direction,
                    ["position"] = pos,
                    ["connectedTo"] = connectedArr
                });
            }

            return new JObject
            {
                ["elementId"] = elemIdLong,
                ["elementName"] = GetSafeName(elem),
                ["category"] = elem.Category?.Name ?? "",
                ["connectors"] = connArr,
                ["totalConnectors"] = totalConnectors,
                ["connectedCount"] = connectedCount,
                ["openCount"] = openCount
            };
        }

        private static string GetSafeName(Element e)
        {
            try { return e.Name ?? ""; }
            catch { return ""; }
        }
    }
}

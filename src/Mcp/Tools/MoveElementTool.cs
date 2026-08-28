using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 移动元素。输入 elementId 和位移向量（dx, dy, dz，单位米）。
    /// Transaction 包裹，返回新旧位置。
    /// </summary>
    public class MoveElementTool : IRevitTool
    {
        public string Name => "move_element";
        public string Description =>
            "按平移向量移动元素。适用于 LocationPoint 或 LocationCurve 的元素。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["elementId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "ElementId of the element to move."
                },
                ["dx"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Translation X in meters."
                },
                ["dy"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Translation Y in meters."
                },
                ["dz"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Translation Z in meters."
                }
            },
            ["required"] = new JArray { "elementId", "dx", "dy", "dz" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            long elemIdLong = (long)input["elementId"];
            var elemId = new ElementId(elemIdLong);
            var elem = doc.GetElement(elemId);
            if (elem == null)
                throw new InvalidOperationException("Element with id " + elemIdLong + " not found.");

            double dxM = (double)input["dx"];
            double dyM = (double)input["dy"];
            double dzM = (double)input["dz"];

            // 获取旧位置
            JObject oldLoc = null;
            try
            {
                var loc = elem.Location;
                if (loc is LocationPoint lp)
                {
                    oldLoc = new JObject
                    {
                        ["x"] = Math.Round(lp.Point.X * 0.3048, 3),
                        ["y"] = Math.Round(lp.Point.Y * 0.3048, 3),
                        ["z"] = Math.Round(lp.Point.Z * 0.3048, 3)
                    };
                }
                else if (loc is LocationCurve lc)
                {
                    var sp = lc.Curve.GetEndPoint(0);
                    oldLoc = new JObject
                    {
                        ["x"] = Math.Round(sp.X * 0.3048, 3),
                        ["y"] = Math.Round(sp.Y * 0.3048, 3),
                        ["z"] = Math.Round(sp.Z * 0.3048, 3)
                    };
                }
            }
            catch { }

            // 移动（米 → 英尺）
            var translation = new XYZ(dxM / 0.3048, dyM / 0.3048, dzM / 0.3048);

            using (var tx = new Transaction(doc, "Move element"))
            {
                tx.Start();
                try
                {
                    ElementTransformUtils.MoveElement(doc, elemId, translation);
                    JarviTools.Core.TransactionSafety.Commit(tx, "Move element");
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }

            // 获取新位置
            JObject newLoc = null;
            try
            {
                var loc = elem.Location;
                if (loc is LocationPoint lp)
                {
                    newLoc = new JObject
                    {
                        ["x"] = Math.Round(lp.Point.X * 0.3048, 3),
                        ["y"] = Math.Round(lp.Point.Y * 0.3048, 3),
                        ["z"] = Math.Round(lp.Point.Z * 0.3048, 3)
                    };
                }
                else if (loc is LocationCurve lc)
                {
                    var sp = lc.Curve.GetEndPoint(0);
                    newLoc = new JObject
                    {
                        ["x"] = Math.Round(sp.X * 0.3048, 3),
                        ["y"] = Math.Round(sp.Y * 0.3048, 3),
                        ["z"] = Math.Round(sp.Z * 0.3048, 3)
                    };
                }
            }
            catch { }

            return new JObject
            {
                ["elementId"] = elemIdLong,
                ["oldLocation"] = oldLoc ?? (JToken)JValue.CreateNull(),
                ["newLocation"] = newLoc ?? (JToken)JValue.CreateNull()
            };
        }
    }
}

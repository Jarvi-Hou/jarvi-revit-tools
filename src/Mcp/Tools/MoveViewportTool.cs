using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 在图纸上移动视口（相对偏移）。
    /// Transaction 包裹。
    /// </summary>
    public class MoveViewportTool : IRevitTool
    {
        public string Name => "move_viewport";
        public string Description =>
            "移动图纸上的视口位置。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["viewportId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "ElementId of the viewport to move."
                },
                ["dx"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Offset X in meters (positive = right)."
                },
                ["dy"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Offset Y in meters (positive = up)."
                }
            },
            ["required"] = new JArray { "viewportId", "dx", "dy" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            var vpId = new ElementId((long)input["viewportId"]);
            double dxM = (double)input["dx"];
            double dyM = (double)input["dy"];

            var vp = doc.GetElement(vpId) as Viewport;
            if (vp == null)
                throw new ArgumentException("viewportId does not refer to a Viewport element.");

            var oldCenter = vp.GetBoxCenter(); // 英尺
            var translation = new XYZ(dxM / 0.3048, dyM / 0.3048, 0);

            using (var tx = new Transaction(doc, "Move viewport"))
            {
                tx.Start();
                try
                {
                    ElementTransformUtils.MoveElement(doc, vpId, translation);
                    JarviTools.Core.TransactionSafety.Commit(tx, "Move viewport");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw new InvalidOperationException(
                        "Failed to move viewport: " + ex.Message, ex);
                }
            }

            var newCenter = vp.GetBoxCenter();

            return new JObject
            {
                ["viewportId"] = (long)input["viewportId"],
                ["dx_m"] = dxM,
                ["dy_m"] = dyM,
                ["oldCenter"] = new JObject
                {
                    ["x"] = Math.Round(oldCenter.X * 0.3048, 4),
                    ["y"] = Math.Round(oldCenter.Y * 0.3048, 4)
                },
                ["newCenter"] = new JObject
                {
                    ["x"] = Math.Round(newCenter.X * 0.3048, 4),
                    ["y"] = Math.Round(newCenter.Y * 0.3048, 4)
                }
            };
        }
    }
}

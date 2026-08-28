using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 将视图放置到图纸上的指定位置。
    /// 先校验 CanAddViewToSheet，再创建 Viewport。Transaction 包裹。
    /// </summary>
    public class PlaceViewOnSheetTool : IRevitTool
    {
        public string Name => "place_view_on_sheet";
        public string Description =>
            "将视图放置到图纸的指定坐标（米）上。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["sheetId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "ElementId of the target sheet."
                },
                ["viewId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "ElementId of the view to place."
                },
                ["x"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "X coordinate on sheet in meters."
                },
                ["y"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Y coordinate on sheet in meters."
                }
            },
            ["required"] = new JArray { "sheetId", "viewId", "x", "y" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            var sheetElemId = new ElementId((long)input["sheetId"]);
            var viewElemId = new ElementId((long)input["viewId"]);
            double xM = (double)input["x"];
            double yM = (double)input["y"];

            var sheet = doc.GetElement(sheetElemId) as ViewSheet;
            if (sheet == null)
                throw new ArgumentException("sheetId does not refer to a ViewSheet.");

            var view = doc.GetElement(viewElemId) as View;
            if (view == null)
                throw new ArgumentException("viewId does not refer to a View.");

            // 校验是否可放置
            if (!Viewport.CanAddViewToSheet(doc, sheetElemId, viewElemId))
                throw new InvalidOperationException("View cannot be placed on this sheet (already placed elsewhere or invalid).");

            var pt = new XYZ(xM / 0.3048, yM / 0.3048, 0);
            Viewport vp = null;

            using (var tx = new Transaction(doc, "Place view on sheet"))
            {
                tx.Start();
                try
                {
                    vp = Viewport.Create(doc, sheetElemId, viewElemId, pt);
                    JarviTools.Core.TransactionSafety.Commit(tx, "Place view on sheet");
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }

            return new JObject
            {
                ["viewportId"] = vp.Id.Value,
                ["sheetName"] = sheet.Name ?? (JToken)JValue.CreateNull(),
                ["viewName"] = view.Name ?? (JToken)JValue.CreateNull(),
                ["location"] = new JObject
                {
                    ["x"] = xM,
                    ["y"] = yM
                }
            };
        }
    }
}

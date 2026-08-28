using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 从图纸上移除指定的视口（不删除底层视图）。
    /// Transaction 包裹。
    /// </summary>
    public class RemoveViewFromSheetTool : IRevitTool
    {
        public string Name => "remove_view_from_sheet";
        public string Description =>
            "从图纸中移除指定视口。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["viewportId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "ElementId of the viewport to remove."
                }
            },
            ["required"] = new JArray { "viewportId" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            var vpId = new ElementId((long)input["viewportId"]);
            var vp = doc.GetElement(vpId) as Viewport;
            if (vp == null)
                throw new ArgumentException("viewportId does not refer to a Viewport element.");

            // 记录删除前的信息
            var view = doc.GetElement(vp.ViewId) as View;
            var sheet = doc.GetElement(vp.SheetId) as ViewSheet;
            string removedViewName = view?.Name ?? "unknown";
            string sheetName = (sheet != null)
                ? sheet.SheetNumber + " - " + sheet.Name
                : "unknown";

            using (var tx = new Transaction(doc, "Remove viewport from sheet"))
            {
                tx.Start();
                try
                {
                    doc.Delete(vpId);
                    JarviTools.Core.TransactionSafety.Commit(tx, "Remove view from sheet");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw new InvalidOperationException(
                        "Failed to remove viewport: " + ex.Message, ex);
                }
            }

            return new JObject
            {
                ["viewportId"] = (long)input["viewportId"],
                ["removedViewName"] = removedViewName,
                ["sheetName"] = sheetName
            };
        }
    }
}

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 设置视图比例（比例分母）。
    /// Transaction 包裹。视图被 view template 控制时给友好错误。
    /// </summary>
    public class SetViewScaleTool : IRevitTool
    {
        public string Name => "set_view_scale";
        public string Description =>
            "设置活动视图的比例。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["viewId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "ElementId of the target view."
                },
                ["scale"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Scale denominator, e.g. 50, 100, 200."
                }
            },
            ["required"] = new JArray { "viewId", "scale" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            var viewId = new ElementId((long)input["viewId"]);
            int newScale = (int)input["scale"];
            if (newScale < 0)
                throw new ArgumentException("Scale denominator must be positive.");

            var view = doc.GetElement(viewId) as View;
            if (view == null)
                throw new ArgumentException("viewId does not refer to a View element.");

            // 检查是否被 view template 控制
            if (view.ViewTemplateId != ElementId.InvalidElementId)
            {
                var template = doc.GetElement(view.ViewTemplateId);
                throw new InvalidOperationException(
                    "View '" + view.Name + "' is controlled by template '" +
                    (template?.Name ?? "unknown") +
                    "'. Scale cannot be modified directly on template-controlled views.");
            }

            int oldScale = view.Scale;
            string viewName = view.Name;

            using (var tx = new Transaction(doc, "Set view scale"))
            {
                tx.Start();
                try
                {
                    view.Scale = newScale;
                    JarviTools.Core.TransactionSafety.Commit(tx, "Set view scale");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw new InvalidOperationException(
                        "Failed to set scale on view '" + viewName + "': " + ex.Message, ex);
                }
            }

            return new JObject
            {
                ["viewName"] = viewName,
                ["oldScale"] = oldScale,
                ["newScale"] = newScale
            };
        }
    }
}

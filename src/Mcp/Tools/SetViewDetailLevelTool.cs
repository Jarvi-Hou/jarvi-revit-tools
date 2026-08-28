using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 设置视图详细程度：Coarse / Medium / Fine。
    /// Transaction 包裹。
    /// </summary>
    public class SetViewDetailLevelTool : IRevitTool
    {
        public string Name => "set_view_detail_level";
        public string Description =>
            "设置活动视图的详细程度：Coarse/Medium/Fine。";

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
                ["level"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Detail level: 'Coarse', 'Medium', or 'Fine'.",
                    ["enum"] = new JArray { "Coarse", "Medium", "Fine" }
                }
            },
            ["required"] = new JArray { "viewId", "level" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            var viewId = new ElementId((long)input["viewId"]);
            string levelStr = (string)input["level"];

            ViewDetailLevel target;
            switch (levelStr)
            {
                case "Coarse": target = ViewDetailLevel.Coarse; break;
                case "Medium": target = ViewDetailLevel.Medium; break;
                case "Fine":   target = ViewDetailLevel.Fine;   break;
                default:
                    throw new ArgumentException("level must be one of: Coarse, Medium, Fine. Got: '" + levelStr + "'.");
            }

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
                    "'. Detail level cannot be modified directly on template-controlled views.");
            }

            string oldLevel = view.DetailLevel.ToString();
            string viewName = view.Name;

            using (var tx = new Transaction(doc, "Set view detail level"))
            {
                tx.Start();
                try
                {
                    view.DetailLevel = target;
                    JarviTools.Core.TransactionSafety.Commit(tx, "Set view detail level");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw new InvalidOperationException(
                        "Failed to set detail level on view '" + viewName + "': " + ex.Message, ex);
                }
            }

            return new JObject
            {
                ["viewName"] = viewName,
                ["oldLevel"] = oldLevel,
                ["newLevel"] = levelStr
            };
        }
    }
}

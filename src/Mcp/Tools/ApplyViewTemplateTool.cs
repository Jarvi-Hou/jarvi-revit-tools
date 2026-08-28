using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 给指定视图套用视图模板。Transaction 包裹。
    /// </summary>
    public class ApplyViewTemplateTool : IRevitTool
    {
        public string Name => "apply_view_template";
        public string Description =>
            "对视图应用视图模板。viewId 和 templateId 必须存在。";

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
                ["templateId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "ElementId of the view template to apply."
                }
            },
            ["required"] = new JArray { "viewId", "templateId" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            long viewIdLong = (long)input["viewId"];
            var viewId = new ElementId(viewIdLong);
            var view = doc.GetElement(viewId) as View;
            if (view == null)
                throw new ArgumentException("viewId " + viewIdLong + " does not refer to a View element.");

            long templateIdLong = (long)input["templateId"];
            var templateId = new ElementId(templateIdLong);
            var template = doc.GetElement(templateId) as View;
            if (template == null)
                throw new ArgumentException("templateId " + templateIdLong + " does not refer to a View element.");
            if (!template.IsTemplate)
                throw new ArgumentException("templateId " + templateIdLong + " is not a view template.");

            using (var tx = new Transaction(doc, "Apply view template"))
            {
                tx.Start();
                try
                {
                    view.ViewTemplateId = template.Id;
                    JarviTools.Core.TransactionSafety.Commit(tx, "Apply view template");
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }

            return new JObject
            {
                ["viewName"] = view.Name ?? (JToken)JValue.CreateNull(),
                ["templateName"] = template.Name ?? (JToken)JValue.CreateNull()
            };
        }
    }
}

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 复制视图。支持三种模式：Duplicate、WithDetailing、AsDependent。
    /// 可选 newName 重命名。Transaction 包裹。
    /// </summary>
    public class DuplicateViewTool : IRevitTool
    {
        public string Name => "duplicate_view";
        public string Description =>
            "复制视图。支持 Duplicate/WithDetailing/AsDependent 三种模式。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["viewId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "ElementId of the view to duplicate."
                },
                ["mode"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Duplicate mode: 'Duplicate' (default), 'WithDetailing', or 'AsDependent'.",
                    ["default"] = "Duplicate"
                },
                ["newName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional new name for the duplicated view."
                }
            },
            ["required"] = new JArray { "viewId" },
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

            string mode = "Duplicate";
            var modeToken = input["mode"];
            if (modeToken != null && modeToken.Type != JTokenType.Null)
                mode = (string)modeToken;

            ViewDuplicateOption opt;
            switch (mode)
            {
                case "WithDetailing": opt = ViewDuplicateOption.WithDetailing; break;
                case "AsDependent":   opt = ViewDuplicateOption.AsDependent;   break;
                default:              opt = ViewDuplicateOption.Duplicate;     break;
            }

            string newName = null;
            var nameToken = input["newName"];
            if (nameToken != null && nameToken.Type != JTokenType.Null)
                newName = (string)nameToken;

            ElementId newId;
            string newViewName = null;

            using (var tx = new Transaction(doc, "Duplicate view"))
            {
                tx.Start();
                try
                {
                    newId = view.Duplicate(opt);
                    var newView = doc.GetElement(newId) as View;
                    if (newView != null)
                    {
                        if (!string.IsNullOrEmpty(newName))
                            newView.Name = newName;
                        newViewName = newView.Name;
                    }
                    JarviTools.Core.TransactionSafety.Commit(tx, "Duplicate view");
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }

            return new JObject
            {
                ["newViewId"] = newId.Value,
                ["newViewName"] = newViewName ?? (JToken)JValue.CreateNull(),
                ["sourceViewName"] = view.Name ?? (JToken)JValue.CreateNull(),
                ["mode"] = mode
            };
        }
    }
}

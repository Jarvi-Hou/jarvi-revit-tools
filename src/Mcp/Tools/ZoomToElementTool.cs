using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 在视图中缩放至指定元素。UI 操作，不修改文档，不需要 Transaction。
    /// </summary>
    public class ZoomToElementTool : IRevitTool
    {
        public string Name => "zoom_to_element";
        public string Description =>
            "缩放并选中活动视图中的指定元素。UI-only，不修改文档。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["elementId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "ElementId of the element to zoom to."
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
            if (input == null) throw new ArgumentException("Input is required.");

            long idLong = (long)input["elementId"];
            var elemId = new ElementId(idLong);
            var elem = doc.GetElement(elemId);
            if (elem == null)
                throw new InvalidOperationException("Element with id " + idLong + " not found.");

            // ShowElements 会切到合适视图并自动缩放
            var ids = new List<ElementId> { elemId };
            uidoc.ShowElements(ids);

            string viewName = null;
            try { viewName = doc.ActiveView?.Name; } catch { }

            return new JObject
            {
                ["elementId"] = idLong,
                ["viewName"] = viewName ?? (JToken)JValue.CreateNull(),
                ["success"] = true
            };
        }
    }
}

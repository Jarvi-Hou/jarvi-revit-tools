using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 在 Revit 视图中高亮选中碰撞的两个元素（UI-only，无 Transaction）。
    /// 支持 zoom 到元素。
    /// </summary>
    public class HighlightClashTool : IRevitTool
    {
        public string Name => "highlight_clash";
        public string Description =>
            "在 Revit 视图中高亮选中碰撞的两个元素。支持 zoom 到元素。UI-only，无 Transaction。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["elementAId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "ElementId of the first clash element."
                },
                ["elementBId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "ElementId of the second clash element."
                },
                ["zoom"] = new JObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Whether to zoom to the selected elements (default true).",
                    ["default"] = true
                }
            },
            ["required"] = new JArray { "elementAId", "elementBId" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            long aIdLong = (long)input["elementAId"];
            long bIdLong = (long)input["elementBId"];
            bool zoom = (bool)(input["zoom"] ?? true);

            var ids = new List<ElementId>();
            var idA = new ElementId(aIdLong);
            var idB = new ElementId(bIdLong);

            if (doc.GetElement(idA) != null)
                ids.Add(idA);
            if (doc.GetElement(idB) != null)
                ids.Add(idB);

            if (ids.Count == 0)
                throw new ArgumentException("Both element IDs are invalid.");

            // UI-only: 选择和缩放
            uidoc.Selection.SetElementIds(ids);

            bool zoomed = false;
            if (zoom)
            {
                try
                {
                    uidoc.ShowElements(ids);
                    zoomed = true;
                }
                catch { }
            }

            string viewName = "";
            try { viewName = doc.ActiveView?.Name ?? ""; } catch { }

            return new JObject
            {
                ["selectedCount"] = ids.Count,
                ["viewName"] = viewName,
                ["zoomed"] = zoomed
            };
        }
    }
}

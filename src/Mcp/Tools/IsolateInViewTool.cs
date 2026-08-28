using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 在当前视图中临时隔离指定的元素（临时隐藏其他所有元素）。
    /// 使用 TemporaryHideIsolate 模式，Transaction 包裹。
    /// </summary>
    public class IsolateInViewTool : IRevitTool
    {
        public string Name => "isolate_in_view";
        public string Description =>
            "临时隔离指定元素并隐藏其余元素。用 unhide_all 恢复显示。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["elementIds"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "number" },
                    ["description"] = "Array of ElementId integers to isolate."
                }
            },
            ["required"] = new JArray { "elementIds" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            var idsToken = input["elementIds"];
            if (idsToken == null || idsToken.Type != JTokenType.Array)
                throw new ArgumentException("'elementIds' must be an array of numbers.");

            var idArray = (JArray)idsToken;
            var elementIds = new List<ElementId>();
            foreach (var token in idArray)
            {
                elementIds.Add(new ElementId((long)token));
            }

            string viewName = null;
            try { viewName = doc.ActiveView?.Name; } catch { viewName = "Unknown"; }

            using (var tx = new Transaction(doc, "Isolate elements in view"))
            {
                tx.Start();
                try
                {
                    doc.ActiveView.IsolateElementsTemporary(elementIds);
                    JarviTools.Core.TransactionSafety.Commit(tx, "Isolate elements in view");
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }

            return new JObject
            {
                ["isolatedCount"] = elementIds.Count,
                ["viewName"] = viewName ?? (JToken)JValue.CreateNull()
            };
        }
    }
}

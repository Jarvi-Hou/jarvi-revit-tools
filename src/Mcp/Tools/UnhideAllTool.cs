using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 清除当前视图中所有临时隐藏/隔离状态，恢复完整显示。
    /// Transaction 包裹。
    /// </summary>
    public class UnhideAllTool : IRevitTool
    {
        public string Name => "unhide_all";
        public string Description =>
            "清除当前视图中所有临时隐藏/隔离状态，恢复完整显示。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            string viewName = null;
            try { viewName = doc.ActiveView?.Name; } catch { viewName = "Unknown"; }

            using (var tx = new Transaction(doc, "Unhide all in view"))
            {
                tx.Start();
                try
                {
                    doc.ActiveView.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                    JarviTools.Core.TransactionSafety.Commit(tx, "Unhide elements in view");
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }

            return new JObject
            {
                ["viewName"] = viewName ?? (JToken)JValue.CreateNull(),
                ["restored"] = true
            };
        }
    }
}

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 设置视图的阶段和阶段过滤器。
    /// Transaction 包裹。
    /// </summary>
    public class SetViewPhaseTool : IRevitTool
    {
        public string Name => "set_view_phase";
        public string Description =>
            "设置活动视图的阶段过滤。";

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
                ["phaseId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "ElementId of the phase to set."
                },
                ["phaseFilterId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Optional ElementId of the phase filter to set."
                }
            },
            ["required"] = new JArray { "viewId", "phaseId" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            var viewId = new ElementId((long)input["viewId"]);
            var newPhaseId = new ElementId((long)input["phaseId"]);

            var view = doc.GetElement(viewId) as View;
            if (view == null)
                throw new ArgumentException("viewId does not refer to a View element.");

            // 检查 view template 控制
            if (view.ViewTemplateId != ElementId.InvalidElementId)
            {
                var template = doc.GetElement(view.ViewTemplateId);
                throw new InvalidOperationException(
                    "View '" + view.Name + "' is controlled by template '" +
                    (template?.Name ?? "unknown") +
                    "'. Phase cannot be modified directly on template-controlled views.");
            }

            // 读取旧阶段
            var phaseParam = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
            if (phaseParam == null)
                throw new InvalidOperationException("View does not have a phase parameter.");
            var oldPhaseId = phaseParam.AsElementId();
            string oldPhaseName = (doc.GetElement(oldPhaseId) as Phase)?.Name ?? "unknown";
            string viewName = view.Name;

            // 验证新阶段存在
            var newPhase = doc.GetElement(newPhaseId) as Phase;
            if (newPhase == null)
                throw new ArgumentException("phaseId does not refer to a Phase element.");

            // 可选的阶段过滤器
            bool hasPhaseFilter = false;
            string phaseFilterName = null;
            JToken phaseFilterToken = input["phaseFilterId"];
            if (phaseFilterToken != null && phaseFilterToken.Type != JTokenType.Null)
            {
                hasPhaseFilter = true;
                var pfId = new ElementId((long)phaseFilterToken);
                var pf = doc.GetElement(pfId) as PhaseFilter;
                if (pf == null)
                    throw new ArgumentException("phaseFilterId does not refer to a PhaseFilter element.");
                phaseFilterName = pf.Name;
            }

            using (var tx = new Transaction(doc, "Set view phase"))
            {
                tx.Start();
                try
                {
                    phaseParam.Set(newPhaseId);
                    if (hasPhaseFilter)
                    {
                        var pfParam = view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER);
                        if (pfParam != null)
                            pfParam.Set(new ElementId((long)input["phaseFilterId"]));
                    }
                    JarviTools.Core.TransactionSafety.Commit(tx, "Set view phase");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw new InvalidOperationException(
                        "Failed to set phase on view '" + viewName + "': " + ex.Message, ex);
                }
            }

            return new JObject
            {
                ["viewName"] = viewName,
                ["oldPhase"] = oldPhaseName,
                ["newPhase"] = newPhase.Name,
                ["phaseFilter"] = phaseFilterName ?? (JToken)JValue.CreateNull()
            };
        }
    }
}

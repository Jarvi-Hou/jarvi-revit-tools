using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// Lists phases and counts only elements that explicitly reference each phase.
    /// Elements without phase parameters are reported separately and never guessed.
    /// </summary>
    public class GetPhaseInfoTool : IRevitTool
    {
        public string Name => "get_phase_info";
        public string Description =>
            "List project phases and exact created/demolished counts. Elements without phase parameters are reported separately rather than assigned to a guessed default phase.";

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

            var phases = doc.Phases.Cast<Phase>().ToList();
            var elements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .ToElements();

            int withoutCreatedPhaseParameter = 0;
            int withoutDemolishedPhaseParameter = 0;
            foreach (var element in elements)
            {
                var created = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
                if (created == null || !created.HasValue) withoutCreatedPhaseParameter++;
                var demolished = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                if (demolished == null || !demolished.HasValue) withoutDemolishedPhaseParameter++;
            }

            var phaseArray = new JArray();
            foreach (var phase in phases)
            {
                int createdCount = 0;
                int demolishedCount = 0;
                long phaseId = phase.Id.Value;

                foreach (var element in elements)
                {
                    try
                    {
                        var created = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
                        if (created != null && created.HasValue && created.AsElementId().Value == phaseId)
                            createdCount++;
                    }
                    catch { }

                    try
                    {
                        var demolished = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                        if (demolished != null && demolished.HasValue && demolished.AsElementId().Value == phaseId)
                            demolishedCount++;
                    }
                    catch { }
                }

                phaseArray.Add(new JObject
                {
                    ["id"] = phaseId,
                    ["name"] = phase.Name ?? (JToken)JValue.CreateNull(),
                    ["elementCreatedCount"] = createdCount,
                    ["elementDemolishedCount"] = demolishedCount
                });
            }

            return new JObject
            {
                ["phases"] = phaseArray,
                ["total"] = phases.Count,
                ["elementsWithoutCreatedPhaseParameter"] = withoutCreatedPhaseParameter,
                ["elementsWithoutDemolishedPhaseParameter"] = withoutDemolishedPhaseParameter,
                ["countingRule"] = "exact_parameter_reference_only"
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// Aggregate Document.GetWarnings() by description.
    /// Inputs: none.
    /// </summary>
    public class GetWarningsSummaryTool : IRevitTool
    {
        private const int MaxExamplesPerGroup = 5;

        public string Name => "get_warnings_summary";

        public string Description =>
            "Return a summary of all warnings in the active document, grouped by description. " +
            "Each group lists the count and up to 5 example failing-element ids.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null)
                throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument;
            if (uidoc == null)
                throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document;
            if (doc == null)
                throw new InvalidOperationException("Active UIDocument has no Document.");

            var warnings = doc.GetWarnings();
            int totalCount = warnings != null ? warnings.Count : 0;

            // Preserve first-seen order so output is deterministic.
            var orderedKeys = new List<string>();
            var groups = new Dictionary<string, GroupAgg>(StringComparer.Ordinal);

            if (warnings != null)
            {
                foreach (var w in warnings)
                {
                    if (w == null) continue;

                    string description;
                    try { description = w.GetDescriptionText() ?? "(no description)"; }
                    catch { description = "(no description)"; }

                    GroupAgg agg;
                    if (!groups.TryGetValue(description, out agg))
                    {
                        agg = new GroupAgg();
                        groups[description] = agg;
                        orderedKeys.Add(description);
                    }
                    agg.Count++;

                    try
                    {
                        var failing = w.GetFailingElements();
                        if (failing != null)
                        {
                            foreach (var eid in failing)
                            {
                                if (eid == null) continue;
                                if (agg.ExampleIds.Count >= MaxExamplesPerGroup) break;
                                long longVal = eid.Value;
                                if (!agg.SeenIds.Add(longVal)) continue;
                                agg.ExampleIds.Add(longVal);
                            }
                        }
                    }
                    catch
                    {
                        // ignore — example collection is best-effort
                    }
                }
            }

            // Sort groups by count desc, then by description for stability.
            var sortedKeys = orderedKeys
                .OrderByDescending(k => groups[k].Count)
                .ThenBy(k => k, StringComparer.Ordinal)
                .ToList();

            var byType = new JArray();
            foreach (var key in sortedKeys)
            {
                var agg = groups[key];
                var exampleArr = new JArray();
                foreach (var id in agg.ExampleIds)
                    exampleArr.Add(id);

                byType.Add(new JObject
                {
                    ["description"] = key,
                    ["count"] = agg.Count,
                    ["exampleElementIds"] = exampleArr
                });
            }

            return new JObject
            {
                ["totalCount"] = totalCount,
                ["byType"] = byType
            };
        }

        private sealed class GroupAgg
        {
            public int Count;
            public readonly List<long> ExampleIds = new List<long>(MaxExamplesPerGroup);
            public readonly HashSet<long> SeenIds = new HashSet<long>();
        }
    }
}

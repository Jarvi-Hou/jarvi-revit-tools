using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using JarviTools.Core;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// MCP wrapper around ParameterManagerCommand.
    /// Reports how many model-category elements have the 专业名称 shared parameter
    /// (matched / unmatched / no_param), plus a per-category breakdown for the largest categories.
    /// Read-only — no Transaction.
    /// </summary>
    public class ParameterManagerStatsTool : IRevitTool
    {
        // Cap the by_category list to avoid blowing up the response for projects with
        // hundreds of model categories.
        private const int MaxCategoriesInBreakdown = 30;

        public string Name => "get_parameter_manager_stats";

        public string Description =>
            "报告工程量统计参数在所有模型元素中的覆盖率：匹配/未匹配/无参数统计。";

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
            var doc   = uidoc.Document       ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            int totalCount     = 0;
            int matchedCount   = 0;
            int unmatchedCount = 0;
            int noParamCount   = 0;

            // category name -> [total, matched, unmatched]
            var byCategory = new Dictionary<string, int[]>(StringComparer.Ordinal);

            using (var collector = new FilteredElementCollector(doc))
            {
                var modelElements = collector.WhereElementIsNotElementType().ToElements();
                foreach (var elem in modelElements)
                {
                    if (elem == null || elem.Category == null) continue;
                    if (elem.Category.CategoryType != CategoryType.Model) continue;

                    totalCount++;
                    string catName = elem.Category.Name ?? Constants.DEFAULT_VALUE;
                    if (!byCategory.TryGetValue(catName, out var stat))
                    {
                        stat = new int[3];
                        byCategory[catName] = stat;
                    }
                    stat[0]++; // total

                    var paramMajor = elem.get_Parameter(Constants.GUID_MAJOR);
                    if (paramMajor == null)
                    {
                        noParamCount++;
                        continue;
                    }

                    string majorValue = paramMajor.AsString();
                    if (string.IsNullOrEmpty(majorValue) || majorValue == Constants.VALUE_UNMATCHED)
                    {
                        unmatchedCount++;
                        stat[2]++; // unmatched
                    }
                    else
                    {
                        matchedCount++;
                        stat[1]++; // matched
                    }
                }
            }

            double matchedPct = totalCount > 0
                ? Math.Round(100.0 * matchedCount / totalCount, 2)
                : 0.0;

            var topCategories = byCategory
                .OrderByDescending(kvp => kvp.Value[0])
                .Take(MaxCategoriesInBreakdown)
                .ToList();

            var byCategoryArr = new JArray();
            foreach (var kvp in topCategories)
            {
                byCategoryArr.Add(new JObject
                {
                    ["categoryName"] = kvp.Key,
                    ["total"]        = kvp.Value[0],
                    ["matched"]      = kvp.Value[1],
                    ["unmatched"]    = kvp.Value[2]
                });
            }

            return new JObject
            {
                ["total_model_elements"] = totalCount,
                ["matched"]              = matchedCount,
                ["unmatched"]            = unmatchedCount,
                ["no_param"]             = noParamCount,
                ["matched_pct"]          = matchedPct,
                ["by_category"]          = byCategoryArr
            };
        }
    }
}

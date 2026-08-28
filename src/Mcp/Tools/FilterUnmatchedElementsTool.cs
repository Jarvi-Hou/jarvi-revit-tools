using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using JarviTools.Core;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// MCP wrapper around FilterUnmatchedElementsCommand.
    /// Scans all model elements and lists those whose 专业名称 / 分包类型 are missing or VALUE_UNMATCHED.
    /// By default, returns only data (no view changes). Pass isolate_in_view=true to also temporarily
    /// isolate the unmatched elements in the active view (Transaction-wrapped).
    /// </summary>
    public class FilterUnmatchedElementsTool : IRevitTool
    {
        public string Name => "filter_unmatched_elements";

        public string Description =>
            "扫描所有模型元素，列出缺少或为空的主要名称/分包商参数的元素。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["isolate_in_view"] = new JObject
                {
                    ["type"]        = "boolean",
                    ["description"] = "If true, temporarily isolate the unmatched elements in the active view (Transaction-wrapped). Default false (read-only).",
                    ["default"]     = false
                }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc   = uidoc.Document       ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            bool isolate = false;
            if (input != null)
            {
                var t = input["isolate_in_view"];
                if (t != null && t.Type != JTokenType.Null)
                {
                    try { isolate = (bool)t; }
                    catch (Exception ex) { throw new ArgumentException("'isolate_in_view' must be a boolean.", ex); }
                }
            }

            // Scan: model-category, non-type elements across the whole document.
            int totalScanned = 0;
            var unmatched = new List<UnmatchedRow>();

            using (var collector = new FilteredElementCollector(doc))
            {
                var modelElements = collector.WhereElementIsNotElementType().ToElements();
                foreach (var elem in modelElements)
                {
                    if (elem == null || elem.Category == null) continue;
                    if (elem.Category.CategoryType != CategoryType.Model) continue;
                    totalScanned++;

                    var paramMajor = elem.get_Parameter(Constants.GUID_MAJOR);
                    var paramSub   = elem.get_Parameter(Constants.GUID_SUBCONTRACTOR);

                    if (paramMajor == null && paramSub == null)
                    {
                        unmatched.Add(new UnmatchedRow(elem, "无参数"));
                        continue;
                    }

                    string majorValue = paramMajor != null ? paramMajor.AsString() : null;
                    string subValue   = paramSub   != null ? paramSub.AsString()   : null;

                    bool majorEmpty = string.IsNullOrEmpty(majorValue);
                    bool subEmpty   = string.IsNullOrEmpty(subValue);
                    bool majorUnmatched = majorValue == Constants.VALUE_UNMATCHED;
                    bool subUnmatched   = subValue   == Constants.VALUE_UNMATCHED;

                    if (majorEmpty || subEmpty)
                    {
                        unmatched.Add(new UnmatchedRow(elem, "字段空"));
                    }
                    else if (majorUnmatched || subUnmatched)
                    {
                        unmatched.Add(new UnmatchedRow(elem, "未匹配"));
                    }
                }
            }

            bool isolatedInView = false;
            if (isolate && unmatched.Count > 0)
            {
                var activeView = doc.ActiveView ?? throw new InvalidOperationException("No active view.");
                var ids = new List<ElementId>(unmatched.Count);
                foreach (var u in unmatched) ids.Add(u.Element.Id);

                using (var tx = new Transaction(doc, "filter_unmatched_elements: isolate"))
                {
                    tx.Start();
                    try
                    {
                        activeView.IsolateElementsTemporary(ids);
                        JarviTools.Core.TransactionSafety.Commit(tx, "Filter unmatched elements");
                        isolatedInView = true;
                    }
                    catch
                    {
                        if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        throw;
                    }
                }
            }

            var arr = new JArray();
            foreach (var u in unmatched)
            {
                arr.Add(new JObject
                {
                    ["id"]           = u.Element.Id.Value,
                    ["familyName"]   = ElementDataHelper.GetFamilyName(u.Element),
                    ["categoryName"] = u.Element.Category != null ? u.Element.Category.Name : Constants.DEFAULT_VALUE,
                    ["reason"]       = u.Reason
                });
            }

            return new JObject
            {
                ["total_scanned"]      = totalScanned,
                ["unmatched_count"]    = unmatched.Count,
                ["isolated_in_view"]   = isolatedInView,
                ["unmatched_elements"] = arr
            };
        }

        private class UnmatchedRow
        {
            public Element Element { get; }
            public string Reason   { get; }
            public UnmatchedRow(Element elem, string reason) { Element = elem; Reason = reason; }
        }
    }
}

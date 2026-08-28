using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// List sheets and/or views in the active document.
    /// Inputs: { kind: "all" | "sheets" | "views" } (default "all")
    /// </summary>
    public class ListSheetsAndViewsTool : IRevitTool
    {
        private const string KindAll = "all";
        private const string KindSheets = "sheets";
        private const string KindViews = "views";

        public string Name => "list_sheets_and_views";

        public string Description =>
            "列出图纸和/或视图的清单。kind 参数控制返回内容（all/sheets/views）。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["kind"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray { KindAll, KindSheets, KindViews },
                    ["description"] = "What to return: \"all\", \"sheets\", or \"views\". Default is \"all\".",
                    ["default"] = KindAll
                }
            },
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

            string kind = KindAll;
            if (input != null)
            {
                var k = input["kind"];
                if (k != null && k.Type != JTokenType.Null)
                {
                    var raw = (string)k;
                    if (!string.IsNullOrEmpty(raw))
                        kind = raw.ToLowerInvariant();
                }
            }

            if (kind != KindAll && kind != KindSheets && kind != KindViews)
                throw new ArgumentException("'kind' must be one of: \"all\", \"sheets\", \"views\".");

            var sheetsArr = new JArray();
            if (kind == KindAll || kind == KindSheets)
            {
                var sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .OrderBy(s => s.SheetNumber, StringComparer.Ordinal);

                foreach (var s in sheets)
                {
                    sheetsArr.Add(new JObject
                    {
                        ["id"] = s.Id.Value,
                        ["number"] = s.SheetNumber ?? string.Empty,
                        ["name"] = s.Name ?? string.Empty
                    });
                }
            }

            var viewsArr = new JArray();
            if (kind == KindAll || kind == KindViews)
            {
                var views = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => v != null && !v.IsTemplate && !(v is ViewSheet))
                    .OrderBy(v => v.Name, StringComparer.Ordinal);

                foreach (var v in views)
                {
                    string viewType;
                    try { viewType = v.ViewType.ToString(); }
                    catch { viewType = "Unknown"; }

                    viewsArr.Add(new JObject
                    {
                        ["id"] = v.Id.Value,
                        ["name"] = v.Name ?? string.Empty,
                        ["viewType"] = viewType,
                        ["isTemplate"] = v.IsTemplate
                    });
                }
            }

            return new JObject
            {
                ["sheets"] = sheetsArr,
                ["views"] = viewsArr
            };
        }
    }
}

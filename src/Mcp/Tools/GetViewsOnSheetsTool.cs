using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 列出所有图纸（Sheet）及其包含的视口（Viewport）。
    /// 返回每张图纸的编号、名称以及上面放置的视图列表。
    /// </summary>
    public class GetViewsOnSheetsTool : IRevitTool
    {
        public string Name => "get_views_on_sheets";
        public string Description =>
            "列出所有图纸及其视口：图纸编号、名称、视口 ID/名称/类型。";

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

            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .OrderBy(s => s.SheetNumber)
                .ToList();

            var sheetsArr = new JArray();
            int totalViewports = 0;

            foreach (var sheet in sheets)
            {
                var viewportsArr = new JArray();
                var vpIds = sheet.GetAllViewports();
                foreach (var vpId in vpIds)
                {
                    var vp = doc.GetElement(vpId) as Viewport;
                    if (vp == null) continue;
                    var viewId = vp.ViewId;
                    var view = doc.GetElement(viewId) as View;
                    if (view == null) continue;

                    viewportsArr.Add(new JObject
                    {
                        ["viewportId"] = vpId.Value,
                        ["viewId"] = viewId.Value,
                        ["viewName"] = view.Name ?? (JToken)JValue.CreateNull(),
                        ["viewType"] = view.ViewType.ToString()
                    });
                    totalViewports++;
                }

                sheetsArr.Add(new JObject
                {
                    ["sheetId"] = sheet.Id.Value,
                    ["sheetNumber"] = sheet.SheetNumber ?? (JToken)JValue.CreateNull(),
                    ["sheetName"] = sheet.Name ?? (JToken)JValue.CreateNull(),
                    ["views"] = viewportsArr
                });
            }

            return new JObject
            {
                ["sheets"] = sheetsArr,
                ["totalSheets"] = sheets.Count,
                ["totalViewports"] = totalViewports
            };
        }
    }
}

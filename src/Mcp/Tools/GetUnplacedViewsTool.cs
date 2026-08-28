using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 查找已创建但未放置在图纸上的视图。
    /// 排除模板视图和系统浏览器。
    /// </summary>
    public class GetUnplacedViewsTool : IRevitTool
    {
        public string Name => "get_unplaced_views";
        public string Description =>
            "查找已创建但未放置在图纸上的视图。排除模板和浏览器默认视图。";

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

            // 收集所有非模板视图
            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && !(v is ViewSheet))
                .ToList();

            // 收集所有已放置到图纸上的 ViewId
            var placedViewIds = new HashSet<ElementId>();
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>();

            foreach (var sheet in sheets)
            {
                var vpIds = sheet.GetAllViewports();
                foreach (var vpId in vpIds)
                {
                    var vp = doc.GetElement(vpId) as Viewport;
                    if (vp != null)
                        placedViewIds.Add(vp.ViewId);
                }
            }

            foreach (ScheduleSheetInstance scheduleInstance in new FilteredElementCollector(doc)
                .OfClass(typeof(ScheduleSheetInstance)))
            {
                if (scheduleInstance.ScheduleId != ElementId.InvalidElementId)
                    placedViewIds.Add(scheduleInstance.ScheduleId);
            }

            // 差集 = 未放图纸视图
            var unplacedArr = new JArray();
            foreach (var view in allViews.OrderBy(v => v.Name))
            {
                if (placedViewIds.Contains(view.Id)) continue;
                // 跳过不适合放图纸的视图类型
                if (view.ViewType == ViewType.SystemBrowser) continue;
                if (view.ViewType == ViewType.Internal) continue;

                unplacedArr.Add(new JObject
                {
                    ["id"] = view.Id.Value,
                    ["name"] = view.Name ?? (JToken)JValue.CreateNull(),
                    ["viewType"] = view.ViewType.ToString()
                });
            }

            return new JObject
            {
                ["unplacedViews"] = unplacedArr,
                ["total"] = unplacedArr.Count
            };
        }
    }
}

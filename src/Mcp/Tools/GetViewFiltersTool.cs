using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 读取指定视图上应用的过滤器列表。
    /// 返回每个过滤器的名称、可见性（前景/背景）和启用状态。
    /// </summary>
    public class GetViewFiltersTool : IRevitTool
    {
        public string Name => "get_view_filters";
        public string Description =>
            "读取指定视图的过滤器列表：ID、名称、可见性（前景/背景）、启用状态。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["viewId"] = new JObject
                {
                    ["type"] = "integer",
                    ["description"] = "ElementId of the view to inspect."
                }
            },
            ["required"] = new JArray { "viewId" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            long viewIdLong = (long)input["viewId"];
            var viewElemId = new ElementId(viewIdLong);
            var view = doc.GetElement(viewElemId) as View;
            if (view == null)
                throw new ArgumentException("viewId " + viewIdLong + " does not refer to a View element.");

            var filterIds = view.GetFilters();
            var filtersArr = new JArray();

            foreach (var fid in filterIds)
            {
                var filterElem = doc.GetElement(fid) as ParameterFilterElement;
                if (filterElem == null) continue;

                string filterName = null;
                try { filterName = filterElem.Name; } catch { }

                // GetFilterVisibility: filter override 是否显示，
                // 不是 filter 是否启用。isEnabled 才是 filter 启用状态。
                bool isVisible = false;
                try { isVisible = view.GetFilterVisibility(fid); } catch { }

                bool isEnabled = false;
                try { isEnabled = view.GetIsFilterEnabled(fid); } catch { }

                filtersArr.Add(new JObject
                {
                    ["id"] = fid.Value,
                    ["name"] = filterName ?? (JToken)JValue.CreateNull(),
                    ["isVisible"] = isVisible,
                    ["isEnabled"] = isEnabled
                });
            }

            return new JObject
            {
                ["viewName"] = view.Name ?? (JToken)JValue.CreateNull(),
                ["filters"] = filtersArr
            };
        }
    }
}

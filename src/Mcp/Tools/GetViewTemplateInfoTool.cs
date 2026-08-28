using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 列出所有视图模板，以及每个模板被哪些视图引用。
    /// 帮助理解视图模板的覆盖关系。
    /// </summary>
    public class GetViewTemplateInfoTool : IRevitTool
    {
        public string Name => "get_view_template_info";
        public string Description =>
            "列出所有视图模板及使用该模板的视图。";

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

            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .ToList();

            var templates = allViews.Where(v => v.IsTemplate).ToList();
            var nonTemplates = allViews.Where(v => !v.IsTemplate).ToList();

            // 按 ViewTemplateId 分组
            var templateUsers = new Dictionary<ElementId, List<JObject>>();
            foreach (var nt in nonTemplates)
            {
                if (nt.ViewTemplateId == null || nt.ViewTemplateId == ElementId.InvalidElementId)
                    continue;
                if (!templateUsers.ContainsKey(nt.ViewTemplateId))
                    templateUsers[nt.ViewTemplateId] = new List<JObject>();
                templateUsers[nt.ViewTemplateId].Add(new JObject
                {
                    ["id"] = nt.Id.Value,
                    ["name"] = nt.Name ?? (JToken)JValue.CreateNull()
                });
            }

            var templatesArr = new JArray();
            foreach (var t in templates.OrderBy(t => t.Name))
            {
                var users = templateUsers.TryGetValue(t.Id, out var list) ? list : new List<JObject>();
                templatesArr.Add(new JObject
                {
                    ["id"] = t.Id.Value,
                    ["name"] = t.Name ?? (JToken)JValue.CreateNull(),
                    ["viewType"] = t.ViewType.ToString(),
                    ["appliedToViews"] = new JArray(users)
                });
            }

            return new JObject
            {
                ["templates"] = templatesArr,
                ["total"] = templates.Count
            };
        }
    }
}

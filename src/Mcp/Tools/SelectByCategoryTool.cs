using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 按 BuiltInCategory 选中模型中指定类别的所有非类型元素。
    /// 可选 viewId 限制到视图。
    /// UI 操作，不修改文档。
    /// </summary>
    public class SelectByCategoryTool : IRevitTool
    {
        public string Name => "select_by_category";
        public string Description =>
            "在活动视图中选择指定类别的所有元素。UI-only，不修改文档。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["category"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "BuiltInCategory name, e.g. \"OST_Walls\", \"OST_Doors\"."
                },
                ["viewId"] = new JObject
                {
                    ["type"] = new JArray { "integer", "null" },
                    ["description"] = "Optional view ElementId. If provided, only select elements visible in that view."
                }
            },
            ["required"] = new JArray { "category" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            string categoryName = (string)input["category"];
            if (string.IsNullOrWhiteSpace(categoryName))
                throw new ArgumentException("'category' is required.");

            BuiltInCategory bic;
            try { bic = (BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), categoryName, ignoreCase: true); }
            catch (Exception ex)
            {
                throw new ArgumentException("Unknown BuiltInCategory: '" + categoryName + "'.", ex);
            }

            FilteredElementCollector collector;
            var viewIdToken = input["viewId"];
            if (viewIdToken != null && viewIdToken.Type != JTokenType.Null)
            {
                long viewIdLong = (long)viewIdToken;
                collector = new FilteredElementCollector(doc, new ElementId(viewIdLong));
            }
            else
            {
                collector = new FilteredElementCollector(doc);
            }

            var elementIds = collector
                .OfCategory(bic)
                .WhereElementIsNotElementType()
                .ToElementIds()
                .ToList();

            uidoc.Selection.SetElementIds(elementIds);

            return new JObject
            {
                ["selectedCount"] = elementIds.Count,
                ["category"] = categoryName
            };
        }
    }
}

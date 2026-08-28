using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// List elements of a given BuiltInCategory in the active document.
    /// Inputs: { category: string (required, e.g. "OST_Doors"),
    ///           limit?: int (default 200, clamped to [1, 1000]),
    ///           viewId?: int (optional; if provided, only elements visible in that view) }
    /// </summary>
    public class ListElementsTool : IRevitTool
    {
        private const int DefaultLimit = 200;
        private const int MaxLimit = 1000;
        private const int MinLimit = 1;

        public string Name => "list_elements";

        public string Description =>
            "列出指定类别的所有元素。可选 viewId 限制到特定视图。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["category"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "BuiltInCategory name, e.g. \"OST_Doors\", \"OST_Walls\", \"OST_Windows\"."
                },
                ["limit"] = new JObject
                {
                    ["type"] = "integer",
                    ["description"] = "Maximum number of elements to return. Default 200, max 1000.",
                    ["minimum"] = MinLimit,
                    ["maximum"] = MaxLimit
                },
                ["viewId"] = new JObject
                {
                    ["type"] = new JArray { "integer", "null" },
                    ["description"] = "Optional view ElementId. If provided, only elements visible in that view are returned."
                }
            },
            ["required"] = new JArray { "category" },
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

            if (input == null)
                throw new ArgumentException("Input is required.");

            string categoryName = (string)input["category"];
            if (string.IsNullOrWhiteSpace(categoryName))
                throw new ArgumentException("'category' is required and must be a non-empty string (e.g. \"OST_Doors\").");

            BuiltInCategory bic;
            try
            {
                bic = (BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), categoryName, ignoreCase: false);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Unknown BuiltInCategory: '" + categoryName + "'. Use a name like 'OST_Doors' or 'OST_Walls'.", ex);
            }

            // Resolve limit with clamping.
            int limit = DefaultLimit;
            var limitToken = input["limit"];
            if (limitToken != null && limitToken.Type != JTokenType.Null)
            {
                try { limit = (int)limitToken; }
                catch (Exception ex) { throw new ArgumentException("'limit' must be an integer.", ex); }
            }
            if (limit < MinLimit) limit = MinLimit;
            if (limit > MaxLimit) limit = MaxLimit;

            // Optional viewId.
            FilteredElementCollector collector;
            var viewIdToken = input["viewId"];
            if (viewIdToken != null && viewIdToken.Type != JTokenType.Null)
            {
                long viewIdLong;
                try { viewIdLong = (long)viewIdToken; }
                catch (Exception ex) { throw new ArgumentException("'viewId' must be an integer.", ex); }

                var viewElementId = new ElementId(viewIdLong);
                var viewElem = doc.GetElement(viewElementId) as View;
                if (viewElem == null)
                    throw new ArgumentException("viewId " + viewIdLong + " does not refer to a View element.");

                collector = new FilteredElementCollector(doc, viewElementId);
            }
            else
            {
                collector = new FilteredElementCollector(doc);
            }

            var elements = collector
                .OfCategory(bic)
                .WhereElementIsNotElementType()
                .ToElements();

            int totalCount = elements.Count;

            var arr = new JArray();
            foreach (var elem in elements.Take(limit))
            {
                arr.Add(BuildElementSummary(doc, elem));
            }

            return new JObject
            {
                ["category"] = categoryName,
                ["count"] = totalCount,
                ["returned"] = arr.Count,
                ["limit"] = limit,
                ["elements"] = arr
            };
        }

        private static JObject BuildElementSummary(Document doc, Element elem)
        {
            string name = null;
            try { name = elem.Name; } catch { name = null; }

            string familyName = null;
            var fi = elem as FamilyInstance;
            if (fi != null)
            {
                try { familyName = fi.Symbol != null ? fi.Symbol.FamilyName : null; }
                catch { familyName = null; }
            }

            string levelName = null;
            try
            {
                var levelId = elem.LevelId;
                if (levelId != null && levelId != ElementId.InvalidElementId)
                {
                    var levelElem = doc.GetElement(levelId);
                    if (levelElem != null)
                        levelName = levelElem.Name;
                }
            }
            catch
            {
                levelName = null;
            }

            return new JObject
            {
                ["id"] = elem.Id.Value,
                ["name"] = name == null ? (JToken)JValue.CreateNull() : name,
                ["familyName"] = familyName == null ? (JToken)JValue.CreateNull() : familyName,
                ["levelName"] = levelName == null ? (JToken)JValue.CreateNull() : levelName
            };
        }
    }
}

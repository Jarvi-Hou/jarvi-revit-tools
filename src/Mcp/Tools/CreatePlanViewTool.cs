using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 在指定标高上创建平面视图（如楼层平面）。
    /// Transaction 包裹。
    /// </summary>
    public class CreatePlanViewTool : IRevitTool
    {
        public string Name => "create_plan_view";
        public string Description =>
            "创建新的平面视图。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["levelId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "ElementId of the level to create the plan view on."
                },
                ["name"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional name for the new plan view."
                },
                ["viewFamilyTypeId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Optional ViewFamilyType ElementId. Defaults to first FloorPlan type."
                }
            },
            ["required"] = new JArray { "levelId" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            var levelId = new ElementId((long)input["levelId"]);
            var level = doc.GetElement(levelId) as Level;
            if (level == null)
                throw new ArgumentException("levelId does not refer to a Level element.");

            // 确定 ViewFamilyType
            ElementId vftId;
            var vftToken = input["viewFamilyTypeId"];
            if (vftToken != null && vftToken.Type != JTokenType.Null)
            {
                vftId = new ElementId((long)vftToken);
                var vftCheck = doc.GetElement(vftId) as ViewFamilyType;
                if (vftCheck == null)
                    throw new ArgumentException("viewFamilyTypeId does not refer to a ViewFamilyType.");
            }
            else
            {
                var vft = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan);
                if (vft == null)
                    throw new InvalidOperationException("No FloorPlan ViewFamilyType found in the project.");
                vftId = vft.Id;
            }

            // 可选名称
            string desiredName = null;
            var nameToken = input["name"];
            if (nameToken != null && nameToken.Type != JTokenType.Null)
                desiredName = (string)nameToken;

            ViewPlan plan;
            using (var tx = new Transaction(doc, "Create plan view"))
            {
                tx.Start();
                try
                {
                    plan = ViewPlan.Create(doc, vftId, levelId);

                    if (!string.IsNullOrEmpty(desiredName))
                    {
                        string finalName = desiredName;
                        int suffix = 2;
                        while (ViewNameExists(doc, finalName))
                            finalName = desiredName + " (" + (suffix++) + ")";
                        plan.Name = finalName;
                    }

                    JarviTools.Core.TransactionSafety.Commit(tx, "Create plan view");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw new InvalidOperationException(
                        "Failed to create plan view on level '" + level.Name + "': " + ex.Message, ex);
                }
            }

            return new JObject
            {
                ["viewId"] = plan.Id.Value,
                ["viewName"] = plan.Name,
                ["levelName"] = level.Name,
                ["viewType"] = "FloorPlan"
            };
        }

        private static bool ViewNameExists(Document doc, string name)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Any(v => string.Equals(v.Name, name, StringComparison.Ordinal));
        }
    }
}

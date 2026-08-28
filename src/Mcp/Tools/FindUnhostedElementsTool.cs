using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 查找需要宿主（Host）但未附着在宿主上的 FamilyInstance。
    /// 检查基于标高的和基于工作面的族。
    /// </summary>
    public class FindUnhostedElementsTool : IRevitTool
    {
        public string Name => "find_unhosted_elements";
        public string Description =>
            "查找需要主体但缺少主体的族实例。";

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

            var unhostedArr = new JArray();

            var instances = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>();

            foreach (var fi in instances)
            {
                FamilyPlacementType placement;
                try { placement = fi.Symbol.Family.FamilyPlacementType; }
                catch { continue; }

                // 只检查需要宿主的放置类型
                if (placement != FamilyPlacementType.OneLevelBasedHosted &&
                    placement != FamilyPlacementType.WorkPlaneBased)
                    continue;

                // 如果 Host 为 null 说明未附着
                if (fi.Host != null) continue;

                string elemName = null;
                try { elemName = fi.Name; } catch { }
                string catName = null;
                try { catName = fi.Category?.Name; } catch { }

                unhostedArr.Add(new JObject
                {
                    ["id"] = fi.Id.Value,
                    ["name"] = elemName ?? (JToken)JValue.CreateNull(),
                    ["category"] = catName ?? (JToken)JValue.CreateNull(),
                    ["expectedHost"] = placement.ToString()
                });
            }

            return new JObject
            {
                ["unhostedElements"] = unhostedArr,
                ["total"] = unhostedArr.Count
            };
        }
    }
}

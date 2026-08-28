using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 统计模型组（Model Group）和详图组（Detail Group）的类型及实例数。
    /// 区分 OST_IOSModelGroups 和 OST_IOSDetailGroups 两类。
    /// </summary>
    public class CountGroupsAndInstancesTool : IRevitTool
    {
        public string Name => "count_groups_and_instances";
        public string Description =>
            "统计模型组和详图组的数量。返回类型名称、实例数和成员数。";

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

            var groupTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(GroupType))
                .Cast<GroupType>()
                .ToList();

            var modelArr = new JArray();
            var detailArr = new JArray();
            int totalModelInstances = 0;
            int totalDetailInstances = 0;

            foreach (var gt in groupTypes.OrderBy(g => g.Name))
            {
                bool isModel = false;
                try
                {
                    isModel = gt.Category != null &&
                        gt.Category.Id.Value == (long)BuiltInCategory.OST_IOSModelGroups;
                }
                catch { continue; }

                int instanceCount = 0;
                int memberCount = 0;
                try { instanceCount = gt.Groups.Size; } catch { }

                // memberCount: 取第一个实例的成员数
                try
                {
                    var firstGroup = gt.Groups.Cast<Group>().FirstOrDefault();
                    if (firstGroup != null)
                        memberCount = firstGroup.GetMemberIds().Count;
                }
                catch { }

                var entry = new JObject
                {
                    ["name"] = gt.Name ?? (JToken)JValue.CreateNull(),
                    ["instanceCount"] = instanceCount,
                    ["memberCount"] = memberCount
                };

                if (isModel)
                {
                    modelArr.Add(entry);
                    totalModelInstances += instanceCount;
                }
                else
                {
                    detailArr.Add(entry);
                    totalDetailInstances += instanceCount;
                }
            }

            return new JObject
            {
                ["modelGroups"] = modelArr,
                ["detailGroups"] = detailArr,
                ["totalModelGroupInstances"] = totalModelInstances,
                ["totalDetailGroupInstances"] = totalDetailInstances
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 获取项目中所有 MEP 系统（Mechanical/Piping/Electrical）的信息：
    /// 名称、类型、元素数量、总长度、拓扑完整性。
    /// 可选 systemType 过滤。
    /// </summary>
    public class GetMepSystemInfoTool : IRevitTool
    {
        public string Name => "get_mep_system_info";
        public string Description =>
            "获取项目中所有 MEP 系统的信息：名称、类型、元素数量、总长度、拓扑完整性。可选 systemType 过滤。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["systemType"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional filter: 'Mechanical' | 'Piping' | 'Electrical'"
                }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            string systemTypeFilter = null;
            if (input != null)
            {
                var token = input["systemType"];
                if (token != null && token.Type != JTokenType.Null)
                    systemTypeFilter = (string)token;
            }

            var allSystems = new FilteredElementCollector(doc)
                .OfClass(typeof(MEPSystem))
                .Cast<MEPSystem>()
                .ToList();

            // 可选的 systemType 过滤
            if (!string.IsNullOrEmpty(systemTypeFilter))
            {
                allSystems = allSystems.Where(s => MatchesSystemType(s, systemTypeFilter)).ToList();
            }

            var systemsArr = new JArray();
            foreach (var sys in allSystems.OrderBy(s => s.Name))
            {
                int elemCount = sys.Elements?.Size ?? 0;

                // 总长度（仅 MEPCurve 子元素）
                double totalLengthM = 0;
                if (sys.Elements != null)
                {
                    foreach (Element e in sys.Elements)
                    {
                        if (e is MEPCurve)
                        {
                            var lenParam = e.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                            if (lenParam != null)
                                totalLengthM += lenParam.AsDouble() * 0.3048;
                        }
                    }
                }

                // 系统类别字符串
                string systemCategory = GetSystemCategory(sys);

                // 拓扑完整性：BaseEquipment != null
                bool hasBaseEquipment = (sys.BaseEquipment != null);

                systemsArr.Add(new JObject
                {
                    ["id"] = sys.Id.Value,
                    ["name"] = sys.Name ?? "",
                    ["systemTypeName"] = sys.GetType().Name,
                    ["systemCategory"] = systemCategory,
                    ["elementCount"] = elemCount,
                    ["totalLength_m"] = Math.Round(totalLengthM, 3),
                    ["hasBaseEquipment"] = hasBaseEquipment,
                    ["connectorTopologyStatus"] = "not_evaluated"
                });
            }

            return new JObject
            {
                ["systems"] = systemsArr,
                ["total"] = systemsArr.Count
            };
        }

        /// <summary>
        /// 判断系统是否匹配过滤条件
        /// </summary>
        private static bool MatchesSystemType(MEPSystem sys, string filter)
        {
            string cat = GetSystemCategory(sys);
            return cat.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 获取 MEP 系统的分类名称
        /// </summary>
        private static string GetSystemCategory(MEPSystem sys)
        {
            if (sys is MechanicalSystem) return "Mechanical (HVAC)";
            if (sys is PipingSystem) return "Piping";
            if (sys is ElectricalSystem) return "Electrical";
            return "Other";
        }
    }
}

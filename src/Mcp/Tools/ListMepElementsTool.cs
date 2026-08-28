using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 列出项目中所有 MEP 元素（风管、管道、桥架、线管等），按系统分组和按类别分组。
    /// 可选 systemFilter 按系统名称过滤。
    /// </summary>
    public class ListMepElementsTool : IRevitTool
    {
        public string Name => "list_mep_elements";
        public string Description =>
            "List MEP elements in stable ElementId order. Supports systemFilter plus limit/offset pagination; category counts describe the full filtered result.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["systemFilter"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional system name to filter elements."
                },
                ["limit"] = new JObject
                {
                    ["type"] = "integer",
                    ["description"] = "Maximum elements returned in this page. Default 100, maximum 1000.",
                    ["minimum"] = 1,
                    ["maximum"] = PaginationOptions.MaxLimit,
                    ["default"] = PaginationOptions.DefaultLimit
                },
                ["offset"] = new JObject
                {
                    ["type"] = "integer",
                    ["description"] = "Zero-based offset into the stable filtered result. Default 0.",
                    ["minimum"] = 0,
                    ["default"] = 0
                }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            var paging = PaginationOptions.Parse(input);

            string systemFilter = null;
            if (input != null)
            {
                var token = input["systemFilter"];
                if (token != null && token.Type != JTokenType.Null)
                    systemFilter = (string)token;
            }

            // MEP 类别集合
            var mepCats = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_DuctCurves,
                BuiltInCategory.OST_DuctFitting,
                BuiltInCategory.OST_DuctAccessory,
                BuiltInCategory.OST_PipeCurves,
                BuiltInCategory.OST_PipeFitting,
                BuiltInCategory.OST_PipeAccessory,
                BuiltInCategory.OST_CableTray,
                BuiltInCategory.OST_CableTrayFitting,
                BuiltInCategory.OST_Conduit,
                BuiltInCategory.OST_FlexDuctCurves,
                BuiltInCategory.OST_FlexPipeCurves,
            };

            IEnumerable<Element> filteredElements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(new ElementMulticategoryFilter(mepCats))
                .ToElements();

            // 系统名过滤
            if (!string.IsNullOrEmpty(systemFilter))
            {
                filteredElements = filteredElements.Where(e =>
                {
                    var sysName = GetSystemName(e);
                    return sysName != null && sysName.IndexOf(systemFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                });
            }

            var allElems = filteredElements
                .OrderBy(e => e.Id.Value)
                .ToList();
            var pageElems = allElems
                .Skip(paging.Offset)
                .Take(paging.Limit)
                .ToList();

            // 按系统分组
            var bySystem = pageElems
                .GroupBy(e => GetSystemName(e) ?? "(无系统)")
                .OrderBy(g => g.Key)
                .ToList();

            var bySystemArr = new JArray();
            foreach (var g in bySystem)
            {
                var first = g.FirstOrDefault();
                string sysType = "";
                if (first != null)
                {
                    var mep = first as MEPCurve;
                    if (mep?.MEPSystem != null)
                        sysType = mep.MEPSystem.GetType().Name;
                }

                bySystemArr.Add(new JObject
                {
                    ["systemName"] = g.Key,
                    ["systemType"] = sysType,
                    ["elements"] = new JArray(
                        g.Select(e => new JObject
                        {
                            ["id"] = e.Id.Value,
                            ["category"] = e.Category?.Name ?? "",
                            ["name"] = GetElementName(e)
                        })
                    )
                });
            }

            // 按类别统计
            var byCategoryArr = new JArray(
                allElems.GroupBy(e => e.Category?.Name ?? "(未知)")
                    .OrderBy(g => g.Key)
                    .Select(g => new JObject
                    {
                        ["categoryName"] = g.Key,
                        ["count"] = g.Count()
                    })
            );

            var result = new JObject
            {
                ["bySystem"] = bySystemArr,
                ["byCategory"] = byCategoryArr
            };
            result.Merge(paging.CreateMetadata(allElems.Count, pageElems.Count));
            return result;
        }

        /// <summary>
        /// 获取 MEP 元素的系统名称
        /// </summary>
        private static string GetSystemName(Element e)
        {
            try
            {
                if (e is MEPCurve mc)
                    return mc.MEPSystem?.Name;

                if (e is FamilyInstance fi && fi.MEPModel != null)
                {
                    var conns = fi.MEPModel.ConnectorManager?.Connectors;
                    if (conns != null)
                    {
                        foreach (Connector c in conns)
                        {
                            if (c.MEPSystem != null)
                                return c.MEPSystem.Name;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 安全获取元素名称
        /// </summary>
        private static string GetElementName(Element e)
        {
            try { return e.Name ?? ""; }
            catch { return ""; }
        }
    }
}

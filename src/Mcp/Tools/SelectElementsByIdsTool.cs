using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 按 ElementId 选中模型中的元素（UI 操作，不修改文档）。
    /// 返回选中数量和缺失的 ID 列表。
    /// </summary>
    public class SelectElementsByIdsTool : IRevitTool
    {
        public string Name => "select_elements_by_ids";
        public string Description =>
            "在活动视图中按 ElementId 选择元素。UI-only，不修改文档。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["elementIds"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "number" },
                    ["description"] = "Array of ElementId integers to select."
                }
            },
            ["required"] = new JArray { "elementIds" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            var idsToken = input["elementIds"];
            if (idsToken == null || idsToken.Type != JTokenType.Array)
                throw new ArgumentException("'elementIds' must be an array of numbers.");

            var idArray = (JArray)idsToken;
            var elementIds = new List<ElementId>();
            var missingIds = new List<long>();

            foreach (var token in idArray)
            {
                long idLong = (long)token;
                var elemId = new ElementId(idLong);
                var elem = doc.GetElement(elemId);
                if (elem != null)
                    elementIds.Add(elemId);
                else
                    missingIds.Add(idLong);
            }

            // 设置选中（UI-only，不需要 Transaction）
            uidoc.Selection.SetElementIds(elementIds);

            return new JObject
            {
                ["selectedCount"] = elementIds.Count,
                ["missingIds"] = new JArray(missingIds)
            };
        }
    }
}

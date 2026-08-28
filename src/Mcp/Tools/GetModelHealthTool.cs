using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 综合模型健康检查：元素数、视图/图纸数、警告数、组、族、链接等。
    /// 一站式概览模型状态的 Audit 工具。
    /// </summary>
    public class GetModelHealthTool : IRevitTool
    {
        public string Name => "get_model_health";
        public string Description =>
            "一站式模型健康概览：元素/视图/图纸/警告/组/族/链接数量。";

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

            // 元素总数
            int elementCount = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .GetElementCount();

            // 视图总数（非模板）
            int viewCount = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Count(v => !v.IsTemplate);

            // 图纸数
            int sheetCount = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .GetElementCount();

            // 警告
            var warnings = doc.GetWarnings();
            int warningCount = warnings.Count();

            // 组类型 & 实例
            int groupTypeCount = new FilteredElementCollector(doc)
                .OfClass(typeof(GroupType))
                .GetElementCount();
            int groupInstanceCount = new FilteredElementCollector(doc)
                .OfClass(typeof(Group))
                .WhereElementIsNotElementType()
                .GetElementCount();

            // 族 & 未使用族
            var families = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .ToList();
            int familyCount = families.Count;

            // 计算未使用族：收集所有已放置实例的 TypeId
            var placedTypeIds = new HashSet<ElementId>(
                new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .WhereElementIsNotElementType()
                    .ToElements()
                    .Cast<FamilyInstance>()
                    .Select(fi => fi.GetTypeId())
            );

            int unusedFamilyCount = families.Count(f => !f.GetFamilySymbolIds().Any(sid => placedTypeIds.Contains(sid)));

            // 链接
            int linkCount = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .GetElementCount();

            return new JObject
            {
                ["elementCount"] = elementCount,
                ["viewCount"] = viewCount,
                ["sheetCount"] = sheetCount,
                ["warningCount"] = warningCount,
                ["warningSeverityAvailable"] = false,
                ["warningSeverityNote"] = "Revit warnings do not expose a language-independent generic severity. Use get_warnings_summary and review FailureDefinitionId/affected elements.",
                ["groupTypeCount"] = groupTypeCount,
                ["groupInstanceCount"] = groupInstanceCount,
                ["familyCount"] = familyCount,
                ["unusedFamilyCount"] = unusedFamilyCount,
                ["linkCount"] = linkCount
            };
        }
    }
}

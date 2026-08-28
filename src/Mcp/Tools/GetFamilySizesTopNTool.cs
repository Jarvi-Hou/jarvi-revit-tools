using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 获取体积最大的 N 个族文件。
    /// 复用 GetFamilySizesMbTool.ScanFamilySizes 静态方法。
    /// </summary>
    public class GetFamilySizesTopNTool : IRevitTool
    {
        public string Name => "get_family_sizes_top_n";
        public string Description =>
            "在限定扫描数量内，获取保存副本后 RFA 文件体积最大的 N 个族。使用较慢的临时文件测量方式。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["n"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Number of top families to return (default 10).",
                    ["default"] = 10
                },
                ["maxFamilies"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Maximum editable families to measure (default 50, maximum 100).",
                    ["default"] = 50
                }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            int n = 10;
            if (input != null)
            {
                var token = input["n"];
                if (token != null && token.Type != JTokenType.Null)
                    n = (int)token;
            }
            int maxFamilies = 50;
            if (input != null && input["maxFamilies"] != null && input["maxFamilies"].Type != JTokenType.Null)
                maxFamilies = (int)input["maxFamilies"];
            if (n < 1 || n > 50)
                throw new ArgumentOutOfRangeException("n", "n must be between 1 and 50.");
            if (maxFamilies < 1 || maxFamilies > 100)
                throw new ArgumentOutOfRangeException("maxFamilies", "maxFamilies must be between 1 and 100.");
            if (n > maxFamilies) n = maxFamilies;

            var allSizes = GetFamilySizesMbTool.ScanFamilySizes(doc, maxFamilies);

            var topN = allSizes
                .Where(s => s.SizeMB.HasValue)
                .OrderByDescending(s => s.SizeMB.Value)
                .Take(n)
                .ToList();

            var arr = new JArray();
            foreach (var s in topN)
            {
                arr.Add(new JObject
                {
                    ["id"] = s.Id,
                    ["name"] = s.Name ?? (JToken)JValue.CreateNull(),
                    ["sizeMB"] = Math.Round(s.SizeMB.Value, 2),
                    ["category"] = s.Category ?? (JToken)JValue.CreateNull()
                });
            }

            return new JObject
            {
                ["topN"] = arr,
                ["totalMeasured"] = allSizes.Count,
                ["scanLimit"] = maxFamilies,
                ["scopeNote"] = "Ranking applies only to the measured subset; it is not necessarily the project-wide top N when total families exceed scanLimit."
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 处理碰撞检测结果：按严重程度分级（critical/major/minor）或按类别对分组。
    /// 输入为 run_clash_detection 输出的 clashes 数组。
    /// </summary>
    public class GetClashReportTool : IRevitTool
    {
        public string Name => "get_clash_report";
        public string Description =>
            "整理 run_clash_detection 的结果。只有带精确相交体积的 solid 结果才按可配置阈值作启发式分级；bbox 或无体积证据的结果列为 unverified，不冒充轻微碰撞。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["clashes"] = new JObject
                {
                    ["type"] = "array",
                    ["description"] = "The 'clashes' array from run_clash_detection output."
                },
                ["groupBy"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "'severity' (default) or 'category'.",
                    ["default"] = "severity"
                },
                ["majorVolumeM3"] = new JObject
                {
                    ["type"] = "number",
                    ["default"] = 0.01,
                    ["description"] = "Heuristic solid-intersection volume threshold for major severity."
                },
                ["criticalVolumeM3"] = new JObject
                {
                    ["type"] = "number",
                    ["default"] = 0.1,
                    ["description"] = "Heuristic solid-intersection volume threshold for critical severity."
                }
            },
            ["required"] = new JArray { "clashes" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            JArray clashes = input["clashes"] as JArray;
            if (clashes == null || clashes.Count == 0)
            {
                return new JObject
                {
                    ["summary"] = new JObject
                    {
                        ["totalClashes"] = 0,
                        ["bySeverity"] = new JObject
                        {
                            ["critical"] = 0,
                            ["major"] = 0,
                            ["minor"] = 0,
                            ["unverified"] = 0
                        },
                        ["byCategoryPair"] = new JArray()
                    },
                    ["groups"] = new JArray()
                };
            }

            string groupBy = (string)input["groupBy"] ?? "severity";
            if (groupBy != "severity" && groupBy != "category")
                throw new ArgumentException("groupBy must be 'severity' or 'category'.");
            double majorThreshold = input["majorVolumeM3"] == null ? 0.01 : (double)input["majorVolumeM3"];
            double criticalThreshold = input["criticalVolumeM3"] == null ? 0.1 : (double)input["criticalVolumeM3"];
            if (majorThreshold <= 0 || criticalThreshold <= majorThreshold)
                throw new ArgumentException("Thresholds must satisfy 0 < majorVolumeM3 < criticalVolumeM3.");

            // 统计：严重程度分级 + 类别对统计
            int critical = 0, major = 0, minor = 0, unverified = 0;
            var catPairCounts = new Dictionary<string, int>();

            foreach (var c in clashes)
            {
                // 严重程度
                var volToken = c["intersectionVolume_m3"];
                string severity;
                if (volToken == null || volToken.Type == JTokenType.Null)
                    severity = "unverified";
                else
                {
                    double vol = (double)volToken;
                    if (vol > criticalThreshold)
                        severity = "critical";
                    else if (vol > majorThreshold)
                        severity = "major";
                    else
                        severity = "minor";
                }

                switch (severity)
                {
                    case "critical": critical++; break;
                    case "major": major++; break;
                    case "minor": minor++; break;
                    default: unverified++; break;
                }

                // 类别对
                var eA = c["elementA"] as JObject;
                var eB = c["elementB"] as JObject;
                string catA = (string)eA?["category"] ?? "?";
                string catB = (string)eB?["category"] ?? "?";
                string pairKey = string.Compare(catA, catB, StringComparison.Ordinal) <= 0
                    ? $"{catA} vs {catB}"
                    : $"{catB} vs {catA}";

                catPairCounts.TryGetValue(pairKey, out int cnt);
                catPairCounts[pairKey] = cnt + 1;
            }

            // 构造 groups
            JArray groupsArr;

            if (groupBy == "category")
            {
                groupsArr = new JArray(
                    catPairCounts.OrderByDescending(kv => kv.Value)
                        .Select(kv =>
                        {
                            var groupClashes = new JArray(
                                clashes.Where(c =>
                                {
                                    var ea = c["elementA"] as JObject;
                                    var eb = c["elementB"] as JObject;
                                    string a = (string)ea?["category"] ?? "?";
                                    string b = (string)eb?["category"] ?? "?";
                                    string pk = string.Compare(a, b, StringComparison.Ordinal) <= 0
                                        ? $"{a} vs {b}" : $"{b} vs {a}";
                                    return pk == kv.Key;
                                })
                                .OrderByDescending(c =>
                                {
                                    var v = c["intersectionVolume_m3"];
                                    return v?.Type == JTokenType.Null ? 0 : (double)(v ?? 0);
                                })
                            );

                            return new JObject
                            {
                                ["groupKey"] = kv.Key,
                                ["clashes"] = groupClashes
                            };
                        })
                );
            }
            else
            {
                // 默认按 severity 分组
                var severityGroups = new[] { "critical", "major", "minor", "unverified" };
                groupsArr = new JArray(
                    severityGroups
                        .Select(sg =>
                        {
                            var groupClashes = new JArray(
                                clashes.Where(c =>
                                {
                                    var v = c["intersectionVolume_m3"];
                                    if (v == null || v.Type == JTokenType.Null)
                                        return sg == "unverified";
                                    double vol = (double)v;
                                    if (sg == "critical") return vol > criticalThreshold;
                                    if (sg == "major") return vol > majorThreshold && vol <= criticalThreshold;
                                    if (sg == "minor") return vol <= majorThreshold;
                                    return false;
                                })
                                .OrderByDescending(c =>
                                {
                                    var v = c["intersectionVolume_m3"];
                                    return v?.Type == JTokenType.Null ? 0 : (double)(v ?? 0);
                                })
                            );

                            return new JObject
                            {
                                ["groupKey"] = sg,
                                ["clashes"] = groupClashes
                            };
                        })
                        .Where(g => ((JArray)g["clashes"]).Count > 0)
                );
            }

            return new JObject
            {
                ["summary"] = new JObject
                {
                    ["totalClashes"] = clashes.Count,
                    ["bySeverity"] = new JObject
                    {
                        ["critical"] = critical,
                        ["major"] = major,
                        ["minor"] = minor,
                        ["unverified"] = unverified
                    },
                    ["severityBasis"] = "Heuristic classification from exact solid intersection volume only; bbox-only results remain unverified.",
                    ["majorVolumeM3"] = majorThreshold,
                    ["criticalVolumeM3"] = criticalThreshold,
                    ["byCategoryPair"] = new JArray(
                        catPairCounts.OrderByDescending(kv => kv.Value)
                            .Select(kv => new JObject
                            {
                                ["pair"] = kv.Key,
                                ["count"] = kv.Value
                            })
                    )
                },
                ["groups"] = groupsArr
            };
        }
    }
}

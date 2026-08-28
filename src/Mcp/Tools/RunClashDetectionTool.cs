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
    /// 碰撞检测：对两个 BuiltInCategory 的元素进行两两碰撞检测。
    /// 支持 bbox 快速模式和 solid 精确模式，含 maxClashes 上限防内存爆炸。
    /// 同类别自检时自动去重避免 (X,Y) 和 (Y,X) 重复。
    /// </summary>
    public class RunClashDetectionTool : IRevitTool
    {
        public string Name => "run_clash_detection";
        public string Description =>
            "碰撞检测：对两个 BuiltInCategory 的元素进行两两碰撞检测。bbox(快速)或 solid(精确)模式。默认上限 1000 个碰撞。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["categoryA"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "BuiltInCategory for first set, e.g. 'OST_Walls'."
                },
                ["categoryB"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "BuiltInCategory for second set, e.g. 'OST_Columns'."
                },
                ["mode"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray { "bbox", "solid" },
                    ["description"] = "'bbox' (default, fast) or 'solid' (precise, slow).",
                    ["default"] = "bbox"
                },
                ["maxClashes"] = new JObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["maximum"] = 10000,
                    ["description"] = "Maximum clashes to return, 1..10000 (default 1000).",
                    ["default"] = 1000
                }
            },
            ["required"] = new JArray { "categoryA", "categoryB" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            string catAStr = (string)input["categoryA"];
            string catBStr = (string)input["categoryB"];
            string mode = (string)input["mode"] ?? "bbox";
            mode = mode.Trim().ToLowerInvariant();
            int maxClashes = (int)(input["maxClashes"]?.Value<long>() ?? 1000);
            if (mode != "bbox" && mode != "solid")
                throw new ArgumentException("'mode' must be 'bbox' or 'solid'.");
            if (maxClashes < 1 || maxClashes > 10000)
                throw new ArgumentOutOfRangeException("maxClashes", "'maxClashes' must be between 1 and 10000.");

            if (!Enum.TryParse(catAStr, ignoreCase: true, out BuiltInCategory bicA))
                throw new ArgumentException($"Invalid BuiltInCategory: {catAStr}");
            if (!Enum.TryParse(catBStr, ignoreCase: true, out BuiltInCategory bicB))
                throw new ArgumentException($"Invalid BuiltInCategory: {catBStr}");

            bool sameCategory = (bicA == bicB);

            var elemsA = new FilteredElementCollector(doc)
                .OfCategory(bicA)
                .WhereElementIsNotElementType()
                .ToElements();

            var elemsB = sameCategory
                ? elemsA
                : new FilteredElementCollector(doc)
                    .OfCategory(bicB)
                    .WhereElementIsNotElementType()
                    .ToElements();

            // 空集合快速返回
            if (elemsA.Count == 0 || elemsB.Count == 0)
            {
                return new JObject
                {
                    ["clashes"] = new JArray(),
                    ["returnedCount"] = 0,
                    ["mode"] = mode,
                    ["truncated"] = false,
                    ["warning"] = $"No elements found: {catAStr}={elemsA.Count}, {catBStr}={elemsB.Count}"
                };
            }

            var idsB = elemsB.Select(e => e.Id).ToHashSet();

            // 去重用（同类别自检）
            var seenPairs = new HashSet<(long, long)>();
            var clashes = new List<JObject>();

            bool truncated = false;
            int solidFailureCount = 0;

            foreach (var eA in elemsA)
            {
                if (truncated) break;

                var bbA = eA.get_BoundingBox(null);
                if (bbA == null) continue;

                long idA = eA.Id.Value;

                // 用 BoundingBoxIntersectsFilter 快速筛选候选
                var outline = new Outline(bbA.Min, bbA.Max);
                var bbFilter = new BoundingBoxIntersectsFilter(outline);

                var candidates = new FilteredElementCollector(doc, idsB.ToList())
                    .WherePasses(bbFilter)
                    .Where(e => e.Id.Value != idA)
                    .ToList();

                foreach (var eB in candidates)
                {
                    long idB = eB.Id.Value;

                    // 同类别去重
                    if (sameCategory)
                    {
                        var key = (Math.Min(idA, idB), Math.Max(idA, idB));
                        if (!seenPairs.Add(key))
                            continue; // 已计算过
                    }

                    JObject clash = null;

                    if (mode == "solid")
                    {
                        bool failed;
                        clash = DetectSolidClash(eA, eB, out failed);
                        if (failed) solidFailureCount++;
                    }
                    else
                    {
                        // bbox 模式
                        var bbB = eB.get_BoundingBox(null);
                        if (bbB == null) continue;

                        XYZ center = new XYZ(
                            (bbA.Min.X + bbA.Max.X + bbB.Min.X + bbB.Max.X) / 4,
                            (bbA.Min.Y + bbA.Max.Y + bbB.Min.Y + bbB.Max.Y) / 4,
                            (bbA.Min.Z + bbA.Max.Z + bbB.Min.Z + bbB.Max.Z) / 4
                        );

                        clash = new JObject
                        {
                            ["elementA"] = new JObject
                            {
                                ["id"] = idA,
                                ["name"] = SafeName(eA),
                                ["category"] = eA.Category?.Name ?? ""
                            },
                            ["elementB"] = new JObject
                            {
                                ["id"] = idB,
                                ["name"] = SafeName(eB),
                                ["category"] = eB.Category?.Name ?? ""
                            },
                            ["intersectionVolume_m3"] = JValue.CreateNull(),
                            ["clashCenter"] = new JObject
                            {
                                ["x"] = Math.Round(center.X * 0.3048, 3),
                                ["y"] = Math.Round(center.Y * 0.3048, 3),
                                ["z"] = Math.Round(center.Z * 0.3048, 3)
                            },
                            ["overlapBoundingBox"] = new JObject
                            {
                                ["min"] = new JObject
                                {
                                    ["x"] = Math.Round(Math.Max(bbA.Min.X, bbB.Min.X) * 0.3048, 3),
                                    ["y"] = Math.Round(Math.Max(bbA.Min.Y, bbB.Min.Y) * 0.3048, 3),
                                    ["z"] = Math.Round(Math.Max(bbA.Min.Z, bbB.Min.Z) * 0.3048, 3)
                                },
                                ["max"] = new JObject
                                {
                                    ["x"] = Math.Round(Math.Min(bbA.Max.X, bbB.Max.X) * 0.3048, 3),
                                    ["y"] = Math.Round(Math.Min(bbA.Max.Y, bbB.Max.Y) * 0.3048, 3),
                                    ["z"] = Math.Round(Math.Min(bbA.Max.Z, bbB.Max.Z) * 0.3048, 3)
                                }
                            }
                        };
                    }

                    if (clash != null)
                    {
                        clashes.Add(clash);
                        if (clashes.Count >= maxClashes)
                        {
                            truncated = true;
                            break;
                        }
                    }
                }
            }

            return new JObject
            {
                ["clashes"] = new JArray(clashes),
                ["returnedCount"] = clashes.Count,
                ["mode"] = mode,
                ["truncated"] = truncated,
                ["completeScan"] = !truncated,
                ["solidFailedPairCount"] = solidFailureCount,
                ["scope"] = "active_host_document_only",
                ["warning"] = mode == "bbox"
                    ? "Bounding-box overlaps are screening candidates, not verified solid clashes. Revit links are not included."
                    : "Only the main extracted solid is tested. Failed geometry pairs are reported separately; Revit links are not included."
            };
        }

        /// <summary>
        /// Solid 模式的精确碰撞检测
        /// </summary>
        private static JObject DetectSolidClash(Element eA, Element eB, out bool failed)
        {
            failed = false;
            try
            {
                var solidA = SolidHelper.GetMainSolid(eA);
                var solidB = SolidHelper.GetMainSolid(eB);
                if (solidA == null || solidB == null)
                {
                    failed = true;
                    return null;
                }

                var inter = BooleanOperationsUtils.ExecuteBooleanOperation(
                    solidA, solidB, BooleanOperationsType.Intersect);

                if (inter == null || inter.Volume < 1e-6)
                    return null;

                double volM3 = inter.Volume * 0.02832;
                XYZ center = inter.ComputeCentroid();

                var bbA = eA.get_BoundingBox(null);
                var bbB = eB.get_BoundingBox(null);

                return new JObject
                {
                    ["elementA"] = new JObject
                    {
                        ["id"] = eA.Id.Value,
                        ["name"] = SafeName(eA),
                        ["category"] = eA.Category?.Name ?? ""
                    },
                    ["elementB"] = new JObject
                    {
                        ["id"] = eB.Id.Value,
                        ["name"] = SafeName(eB),
                        ["category"] = eB.Category?.Name ?? ""
                    },
                    ["intersectionVolume_m3"] = Math.Round(volM3, 6),
                    ["clashCenter"] = new JObject
                    {
                        ["x"] = Math.Round(center.X * 0.3048, 3),
                        ["y"] = Math.Round(center.Y * 0.3048, 3),
                        ["z"] = Math.Round(center.Z * 0.3048, 3)
                    },
                    ["overlapBoundingBox"] = (bbA != null && bbB != null)
                        ? (JToken)new JObject
                        {
                            ["min"] = new JObject
                            {
                                ["x"] = Math.Round(Math.Max(bbA.Min.X, bbB.Min.X) * 0.3048, 3),
                                ["y"] = Math.Round(Math.Max(bbA.Min.Y, bbB.Min.Y) * 0.3048, 3),
                                ["z"] = Math.Round(Math.Max(bbA.Min.Z, bbB.Min.Z) * 0.3048, 3)
                            },
                            ["max"] = new JObject
                            {
                                ["x"] = Math.Round(Math.Min(bbA.Max.X, bbB.Max.X) * 0.3048, 3),
                                ["y"] = Math.Round(Math.Min(bbA.Max.Y, bbB.Max.Y) * 0.3048, 3),
                                ["z"] = Math.Round(Math.Min(bbA.Max.Z, bbB.Max.Z) * 0.3048, 3)
                            }
                        }
                        : JValue.CreateNull()
                };
            }
            catch
            {
                failed = true;
                return null;
            }
        }

        private static string SafeName(Element e)
        {
            try { return e.Name ?? ""; }
            catch { return ""; }
        }
    }
}

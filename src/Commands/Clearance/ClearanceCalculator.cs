using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using JarviTools.Commands.Common;

namespace JarviTools.Commands.Clearance
{
    /// <summary>单个构件的净高结果。长度字段一律米。</summary>
    internal class ClearanceResult
    {
        public string Source;        // "本模型" 或链接名
        public bool IsLinked;
        public ElementId Id;         // 链接构件时是链接文档内的 Id（仅展示用）
        public string Category;
        public string TypeLabel;
        public string SystemName;
        public XYZ LowestPoint;      // 世界坐标（英尺），链接已变换
        public double BottomAbsM;    // 底部绝对标高（相对项目零点）
        public double ClearPrimaryM; // 净高（主基准，含偏移）
        public double ClearCompareM; // 净高（对比基准），无对比基准时为 double.NaN
        public string GridLabel;
    }

    /// <summary>计算范围参数。长度全部英尺。</summary>
    internal class ClearanceScope
    {
        public double DatumZFt;                       // 主基准（标高 + 偏移）
        public double CompareZFt = double.NaN;        // 对比基准（无偏移）
        public double ZTopFt = double.MaxValue;       // 上边界（上一层标高），无则 MaxValue
        public bool HasRect;
        public double MinXFt, MinYFt, MaxXFt, MaxYFt; // 框选范围（HasRect 时有效）
        public HashSet<ElementId> SelectionIds;       // 仅"当前选择"模式非 null
        public List<BuiltInCategory> Categories;
        public bool IncludeLinks;
        public bool ExcludeRisers;                    // 排除竖直立管/竖管
    }

    internal static class ClearanceCalculator
    {
        private const double FT_TO_M = 0.3048;
        private const double MIN_ABOVE_FT = 0.003; // 底部须高于基准约 1mm，排除本层地面板

        /// <summary>返回 null = 用户取消。结果已按主基准净高升序排列。
        /// skippedRisers 输出被当作竖直立管排除的构件数（供汇总提示）。</summary>
        public static List<ClearanceResult> Run(
            Document doc, ClearanceScope scope, GridLocator grids,
            Action<int, int> progress, Func<bool> cancelled, out int skippedRisers)
        {
            skippedRisers = 0;
            var results = new List<ClearanceResult>();
            var catFilter = new ElementMulticategoryFilter(scope.Categories);

            // ---- 待处理清单：宿主 + 链接 ----
            // 注意：Transform.Identity 每次访问返回新实例，不能用引用比较判断是否链接，
            // 必须显式携带 isLinked 标记（Item4）。
            var work = new List<Tuple<Element, Transform, string, bool>>();

            foreach (Element e in Collect(doc, catFilter))
            {
                if (scope.SelectionIds != null && !scope.SelectionIds.Contains(e.Id)) continue;
                work.Add(Tuple.Create(e, Transform.Identity, "本模型", false));
            }

            if (scope.IncludeLinks && scope.SelectionIds == null)
            {
                foreach (RevitLinkInstance link in new FilteredElementCollector(doc)
                             .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    Document ldoc = link.GetLinkDocument();
                    if (ldoc == null) continue; // 链接未载入
                    Transform tf = link.GetTotalTransform();
                    string name = ldoc.Title;
                    foreach (Element e in Collect(ldoc, catFilter))
                        work.Add(Tuple.Create(e, tf, name, true));
                }
            }

            // ---- 逐个计算 ----
            int done = 0;
            foreach (var item in work)
            {
                done++;
                if ((done & 31) == 0)
                {
                    if (progress != null) progress(done, work.Count);
                    if (cancelled != null && cancelled()) return null;
                }

                Element e = item.Item1;
                Transform tf = item.Item2;
                bool isLinked = item.Item4;

                // 包围盒粗筛（Z 带 + XY 框选），避免对无关构件做几何提取
                BoundingBoxXYZ bb = e.get_BoundingBox(null);
                if (bb == null) continue;
                double bbMinX, bbMinY, bbMinZ, bbMaxX, bbMaxY, bbMaxZ;
                WorldBounds(bb, tf, out bbMinX, out bbMinY, out bbMinZ, out bbMaxX, out bbMaxY, out bbMaxZ);

                bool skipZBand = scope.SelectionIds != null; // 手选模式不做 Z 带过滤
                if (!skipZBand)
                {
                    if (bbMaxZ < scope.DatumZFt) continue; // 全在基准以下
                    if (bbMinZ > scope.ZTopFt) continue;   // 全在上层以上
                }
                if (scope.HasRect)
                {
                    if (bbMaxX < scope.MinXFt || bbMinX > scope.MaxXFt) continue;
                    if (bbMaxY < scope.MinYFt || bbMinY > scope.MaxYFt) continue;
                }

                // 竖直立管/竖管排除：其贴地下端会被当成最低点，造成假的最低净高。
                // 放在范围过滤之后，使 skippedRisers 只统计确实落在本次分析范围内的立管，
                // 避免其它楼层/框选区外的立管把汇总"已排除竖直立管 N 段"的数字撑大。
                if (scope.ExcludeRisers && IsVerticalRiser(e)) { skippedRisers++; continue; }

                // 真实几何最低点
                XYZ lowest;
                if (!GeometryUtil.TryGetLowestPoint(e, tf, out lowest)) continue;
                if (!skipZBand)
                {
                    if (lowest.Z <= scope.DatumZFt + MIN_ABOVE_FT) continue;
                    if (lowest.Z > scope.ZTopFt) continue;
                }

                results.Add(new ClearanceResult
                {
                    Source = item.Item3,
                    IsLinked = isLinked,
                    Id = e.Id,
                    Category = e.Category != null ? e.Category.Name : "?",
                    TypeLabel = GetTypeLabel(e),
                    SystemName = GetSystemName(e),
                    LowestPoint = lowest,
                    BottomAbsM = lowest.Z * FT_TO_M,
                    ClearPrimaryM = (lowest.Z - scope.DatumZFt) * FT_TO_M,
                    ClearCompareM = double.IsNaN(scope.CompareZFt)
                        ? double.NaN
                        : (lowest.Z - scope.CompareZFt) * FT_TO_M,
                    GridLabel = grids.Locate(lowest)
                });
            }

            if (progress != null) progress(work.Count, work.Count);
            results.Sort((a, b) => a.ClearPrimaryM.CompareTo(b.ClearPrimaryM));
            return results;
        }

        private static IList<Element> Collect(Document doc, ElementFilter catFilter)
        {
            using (var collector = new FilteredElementCollector(doc))
            {
                return collector.WherePasses(catFilter).WhereElementIsNotElementType().ToElements();
            }
        }

        private static void WorldBounds(BoundingBoxXYZ bb, Transform tf,
            out double minX, out double minY, out double minZ,
            out double maxX, out double maxY, out double maxZ)
        {
            minX = minY = minZ = double.MaxValue;
            maxX = maxY = maxZ = double.MinValue;
            for (int i = 0; i < 8; i++)
            {
                double x = ((i & 1) == 0) ? bb.Min.X : bb.Max.X;
                double y = ((i & 2) == 0) ? bb.Min.Y : bb.Max.Y;
                double z = ((i & 4) == 0) ? bb.Min.Z : bb.Max.Z;
                XYZ p = tf.OfPoint(bb.Transform.OfPoint(new XYZ(x, y, z)));
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
                if (p.Z > maxZ) maxZ = p.Z;
            }
        }

        /// <summary>近竖直的线性风/水管（立管、竖管、竖直支管），非净空障碍。</summary>
        private static bool IsVerticalRiser(Element e)
        {
            var mc = e as MEPCurve; // Duct/Pipe/CableTray/Conduit
            if (mc == null) return false;
            var lc = e.Location as LocationCurve;
            if (lc == null) return false;
            var line = lc.Curve as Line;
            if (line == null) return false;
            return Math.Abs(line.Direction.Z) > 0.966; // 与竖直夹角 < 15°
        }

        private static string GetTypeLabel(Element e)
        {
            var fi = e as FamilyInstance;
            if (fi != null && fi.Symbol != null)
                return fi.Symbol.FamilyName + "-" + fi.Symbol.Name;
            return e.Name;
        }

        private static string GetSystemName(Element e)
        {
            Parameter p = e.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM);
            if (p != null)
            {
                string v = p.AsString();
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return "";
        }
    }
}

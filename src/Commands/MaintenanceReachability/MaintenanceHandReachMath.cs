using System;
using System.Collections.Generic;
using System.Linq;

namespace JarviTools.Commands.MaintenanceReachability
{
    // HandReach 纯数学核心：不依赖 Revit，可独立单测。
    // 算法与 2026-08-18 原型（5B-handreach-prototype-code.md）逐行对应，
    // 并已用 5B-handreach-results-20260818.json 的三台设备数值反推验证。

    internal static class MaintenanceHandReachMath
    {
        private const double Epsilon = 1e-9;

        /// <summary>
        /// 从若干天花顶面（每个顶面用奇偶填充边界环表达）提取并集的真实外边界。
        /// 相邻天花的共边两侧都在并集内部，因此不会被误生成为侧墙；重复顶面边界
        /// 也会按稳定端点键去重。返回段的 Inward 始终指向天花投影内部。
        /// </summary>
        public static List<HandReachVirtualBoundarySegment> BuildVirtualBoundarySegments(
            IList<List<List<MaintenancePoint2>>> footprints,
            double probeOffsetMm)
        {
            if (footprints == null) throw new ArgumentNullException("footprints");
            if (double.IsNaN(probeOffsetMm) || double.IsInfinity(probeOffsetMm) ||
                probeOffsetMm <= 0.0)
                throw new ArgumentOutOfRangeException("probeOffsetMm");

            var output = new List<HandReachVirtualBoundarySegment>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int footprintIndex = 0; footprintIndex < footprints.Count; footprintIndex++)
            {
                List<List<MaintenancePoint2>> loops = footprints[footprintIndex];
                if (loops == null) continue;
                for (int loopIndex = 0; loopIndex < loops.Count; loopIndex++)
                {
                    List<MaintenancePoint2> loop = loops[loopIndex];
                    if (loop == null || loop.Count < 3) continue;
                    for (int segmentIndex = 0; segmentIndex < loop.Count; segmentIndex++)
                    {
                        MaintenancePoint2 start = loop[segmentIndex];
                        MaintenancePoint2 end = loop[(segmentIndex + 1) % loop.Count];
                        MaintenancePoint2 delta = end - start;
                        if (delta.Length() <= 1.0) continue;

                        // 先用其他边的端点/交点拆分长边。这样即便相邻天花只共享
                        // 一段共线边，也只删除共享子段，不会误删长边两端的外墙。
                        List<double> cuts = BuildBoundarySegmentCuts(
                            start, end, footprints);
                        for (int cutIndex = 0; cutIndex < cuts.Count - 1; cutIndex++)
                        {
                            double t0 = cuts[cutIndex];
                            double t1 = cuts[cutIndex + 1];
                            MaintenancePoint2 pieceStart = start + delta * t0;
                            MaintenancePoint2 pieceEnd = start + delta * t1;
                            MaintenancePoint2 pieceDelta = pieceEnd - pieceStart;
                            double length = pieceDelta.Length();
                            if (length <= 1.0) continue;

                            MaintenancePoint2 tangent = pieceDelta.Normalize();
                            MaintenancePoint2 left = tangent.LeftNormal();
                            MaintenancePoint2 midpoint = (pieceStart + pieceEnd) * 0.5;
                            double localProbe = Math.Min(probeOffsetMm,
                                Math.Max(1.0, length * 0.10));
                            bool leftInside = PointInsideAnyFootprint(
                                midpoint + left * localProbe, footprints);
                            bool rightInside = PointInsideAnyFootprint(
                                midpoint - left * localProbe, footprints);

                            // 两侧都在内部的是相邻天花共边；两侧都在外部的是退化、
                            // 重叠或无法定向的线段。二者都不能作为虚拟侧墙。
                            if (leftInside == rightInside) continue;

                            string stableKey = BuildBoundarySegmentStableKey(
                                pieceStart, pieceEnd);
                            if (!seen.Add(stableKey)) continue;
                            output.Add(new HandReachVirtualBoundarySegment
                            {
                                FootprintIndex = footprintIndex,
                                LoopIndex = loopIndex,
                                SegmentIndex = segmentIndex,
                                Start = pieceStart,
                                End = pieceEnd,
                                Tangent = tangent,
                                Inward = leftInside ? left : left * -1.0,
                                LengthMm = length,
                                StableKey = stableKey
                            });
                        }
                    }
                }
            }
            return output
                .OrderBy(x => x.StableKey, StringComparer.Ordinal)
                .ToList();
        }

        private static List<double> BuildBoundarySegmentCuts(
            MaintenancePoint2 start,
            MaintenancePoint2 end,
            IEnumerable<List<List<MaintenancePoint2>>> footprints)
        {
            const double pointToleranceMm = 0.5;
            var cuts = new List<double> { 0.0, 1.0 };
            MaintenancePoint2 r = end - start;
            double rLengthSquared = r.X * r.X + r.Y * r.Y;
            double rLength = Math.Sqrt(rLengthSquared);
            foreach (List<List<MaintenancePoint2>> loops in footprints)
            {
                if (loops == null) continue;
                foreach (List<MaintenancePoint2> loop in loops)
                {
                    if (loop == null || loop.Count < 2) continue;
                    for (int i = 0; i < loop.Count; i++)
                    {
                        MaintenancePoint2 a = loop[i];
                        MaintenancePoint2 b = loop[(i + 1) % loop.Count];
                        MaintenancePoint2 s = b - a;
                        double crossRs = r.X * s.Y - r.Y * s.X;
                        MaintenancePoint2 q = a - start;
                        double crossQr = q.X * r.Y - q.Y * r.X;
                        if (Math.Abs(crossRs) <= Epsilon)
                        {
                            if (Math.Abs(crossQr) <= pointToleranceMm * rLength)
                            {
                                AddBoundaryCut(cuts, start, r, rLengthSquared,
                                    a, pointToleranceMm);
                                AddBoundaryCut(cuts, start, r, rLengthSquared,
                                    b, pointToleranceMm);
                            }
                            continue;
                        }

                        double t = (q.X * s.Y - q.Y * s.X) / crossRs;
                        double u = (q.X * r.Y - q.Y * r.X) / crossRs;
                        if (t >= -Epsilon && t <= 1.0 + Epsilon &&
                            u >= -Epsilon && u <= 1.0 + Epsilon)
                            cuts.Add(Math.Max(0.0, Math.Min(1.0, t)));
                    }
                }
            }
            return cuts
                .OrderBy(x => x)
                .Aggregate(new List<double>(), (list, value) =>
                {
                    if (list.Count == 0 ||
                        Math.Abs(list[list.Count - 1] - value) > 1e-8)
                        list.Add(value);
                    return list;
                });
        }

        private static void AddBoundaryCut(
            ICollection<double> cuts,
            MaintenancePoint2 segmentStart,
            MaintenancePoint2 segmentDelta,
            double segmentLengthSquared,
            MaintenancePoint2 point,
            double toleranceMm)
        {
            if (segmentLengthSquared <= Epsilon) return;
            double t = ((point.X - segmentStart.X) * segmentDelta.X +
                        (point.Y - segmentStart.Y) * segmentDelta.Y) /
                       segmentLengthSquared;
            double toleranceT = toleranceMm / Math.Sqrt(segmentLengthSquared);
            if (t >= -toleranceT && t <= 1.0 + toleranceT)
                cuts.Add(Math.Max(0.0, Math.Min(1.0, t)));
        }

        private static bool PointInsideAnyFootprint(
            MaintenancePoint2 point,
            IEnumerable<List<List<MaintenancePoint2>>> footprints)
        {
            foreach (List<List<MaintenancePoint2>> loops in footprints)
            {
                if (loops != null && loops.Count > 0 &&
                    PointInsideFilledLoops(point, loops))
                    return true;
            }
            return false;
        }

        private static string BuildBoundarySegmentStableKey(
            MaintenancePoint2 start,
            MaintenancePoint2 end)
        {
            long ax = (long)Math.Round(start.X * 10.0);
            long ay = (long)Math.Round(start.Y * 10.0);
            long bx = (long)Math.Round(end.X * 10.0);
            long by = (long)Math.Round(end.Y * 10.0);
            bool reverse = ax > bx || ax == bx && ay > by;
            return reverse
                ? bx + "," + by + "|" + ax + "," + ay
                : ax + "," + ay + "|" + bx + "," + by;
        }

        /// <summary>
        /// 450×450 检修口最近边缘点。距离从口真实最近边缘量到目标点，
        /// 不用口中心。返回 edgeX/edgeY（mm）与水平距离（mm）。
        /// 与原型完全一致：目标投影在口外取方形边界最近点；在口内取四条边中最近一条。
        /// </summary>
        public static void NearestEdge(
            double centerX,
            double centerY,
            double targetX,
            double targetY,
            double halfSizeMm,
            out double edgeX,
            out double edgeY,
            out double horizontalMm)
        {
            double dx = targetX - centerX;
            double dy = targetY - centerY;
            double ex;
            double ey;
            if (Math.Abs(dx) > halfSizeMm || Math.Abs(dy) > halfSizeMm)
            {
                ex = Math.Max(-halfSizeMm, Math.Min(halfSizeMm, dx));
                ey = Math.Max(-halfSizeMm, Math.Min(halfSizeMm, dy));
            }
            else
            {
                double qx = halfSizeMm - Math.Abs(dx);
                double qy = halfSizeMm - Math.Abs(dy);
                if (qx <= qy)
                {
                    ex = dx >= 0.0 ? halfSizeMm : -halfSizeMm;
                    ey = dy;
                }
                else
                {
                    ex = dx;
                    ey = dy >= 0.0 ? halfSizeMm : -halfSizeMm;
                }
            }
            edgeX = centerX + ex;
            edgeY = centerY + ey;
            double hx = targetX - edgeX;
            double hy = targetY - edgeY;
            horizontalMm = Math.Sqrt(hx * hx + hy * hy);
        }

        /// <summary>斜向实际距离：口边缘点与目标点的三维距离。</summary>
        public static double ObliqueDistance(
            double edgeX,
            double edgeY,
            double edgeZ,
            double targetX,
            double targetY,
            double targetZ)
        {
            double dx = targetX - edgeX;
            double dy = targetY - edgeY;
            double dz = targetZ - edgeZ;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>操作距离分级：≤300 双手/重件；301–400 单手工具；401–500 仅简单检查；>500 淘汰。</summary>
        public static HandReachDistanceGrade GradeDistance(double distanceMm)
        {
            if (distanceMm <= 300.0) return HandReachDistanceGrade.AWithin300;
            if (distanceMm <= 400.0) return HandReachDistanceGrade.BWithin400;
            if (distanceMm <= 500.0) return HandReachDistanceGrade.CWithin500;
            return HandReachDistanceGrade.RejectedOver500;
        }

        /// <summary>
        /// 侧墙伸手正式硬上限仍为500mm。只有显式人工复核时，
        /// 才允许500~600mm候选继续完成洞口、通道和梯具审计；结果只能为橙色。
        /// </summary>
        public static bool IsSideWallDistanceCandidateEligible(
            double distanceMm,
            HandReachOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            double limit = ResolveSideWallDistanceLimitMm(options);
            return distanceMm <= limit + 1e-6;
        }

        public static double ResolveSideWallDistanceLimitMm(HandReachOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            return options.AllowSideWallDistanceOver500Review
                ? options.SideWallReviewMaxDistanceMm
                : options.MaxDistanceMm;
        }

        public static string GradeDistanceText(HandReachDistanceGrade grade)
        {
            switch (grade)
            {
                case HandReachDistanceGrade.AWithin300: return "A_le_300";
                case HandReachDistanceGrade.BWithin400: return "B_301_to_400";
                case HandReachDistanceGrade.CWithin500: return "C_401_to_500";
                default: return "Rejected_over_500";
            }
        }

        /// <summary>垂直高差分级：≤300 推荐；301–500 不阻断、橙色会审；>500 淘汰。</summary>
        public static HandReachVerticalGrade GradeVertical(double verticalMm)
        {
            if (verticalMm <= 300.0) return HandReachVerticalGrade.RecommendedWithin300;
            if (verticalMm <= 500.0) return HandReachVerticalGrade.AttentionWithin500;
            return HandReachVerticalGrade.RejectedOver500;
        }

        /// <summary>500 mm 是可进入后续候选链的闭区间硬边界；超过即淘汰。</summary>
        public static bool IsVerticalCandidateEligible(double verticalMm)
        {
            return GradeVertical(verticalMm) != HandReachVerticalGrade.RejectedOver500;
        }

        /// <summary>
        /// 设备检修面落在天花厚度附近时，450×450 天花口按“洞口下方直接伸手”分析，
        /// 不虚构人员钻入包络。允许检修面在模型中与天花顶面发生一个洞口厚度以内的交叠，
        /// 最终是否采用由结论层降为橙色目视复核。
        /// </summary>
        public static bool IsCeilingDirectReachMode(
            double ceilingTopMm,
            double serviceProxyZMm,
            double openingHeightMm)
        {
            ValidateFinite(ceilingTopMm, "ceilingTopMm");
            ValidateFinite(serviceProxyZMm, "serviceProxyZMm");
            ValidateFinite(openingHeightMm, "openingHeightMm");
            if (openingHeightMm <= 0.0)
                throw new ArgumentOutOfRangeException("openingHeightMm");
            return serviceProxyZMm >= ceilingTopMm - openingHeightMm - Epsilon &&
                   serviceProxyZMm <= ceilingTopMm + openingHeightMm + Epsilon;
        }

        /// <summary>天花直接伸手从检修口的室内侧保守起算。</summary>
        public static double ResolveCeilingDirectReachStartZMm(
            double ceilingTopMm,
            double openingHeightMm)
        {
            ValidateFinite(ceilingTopMm, "ceilingTopMm");
            ValidateFinite(openingHeightMm, "openingHeightMm");
            if (openingHeightMm <= 0.0)
                throw new ArgumentOutOfRangeException("openingHeightMm");
            return ceilingTopMm - openingHeightMm;
        }

        /// <summary>检修面低于天花顶面表示模型发生交叠，直接伸手方案只能橙色复核。</summary>
        public static bool RequiresCeilingDirectReachOverlapReview(
            double modelVerticalDifferenceMm)
        {
            ValidateFinite(modelVerticalDifferenceMm, "modelVerticalDifferenceMm");
            return modelVerticalDifferenceMm < -Epsilon;
        }

        private static void ValidateFinite(double value, string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameterName);
        }

        /// <summary>
        /// 天花450人员钻入时，设备保持原高度。人体包络从天花向上延伸，优先在检修面
        /// 下方保留指定的最后伸手距离，但不会超过人体探入验算高度。
        /// </summary>
        public static double ResolveCeilingPersonnelEntryTopMm(
            double ceilingTopMm,
            double serviceProxyZMm,
            HandReachOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (serviceProxyZMm < ceilingTopMm)
                throw new ArgumentOutOfRangeException("serviceProxyZMm");
            return Math.Min(
                ceilingTopMm + options.CeilingPersonnelEntryRiseMm,
                Math.Max(
                    ceilingTopMm + options.OpeningHeightMm,
                    serviceProxyZMm - options.CeilingPersonnelFinalReachGapMm));
        }

        /// <summary>返回目标投影在方形人员包络顶面内的最近点。</summary>
        public static void NearestPointInSquare(
            double centerX,
            double centerY,
            double targetX,
            double targetY,
            double halfSizeMm,
            out double pointX,
            out double pointY,
            out double horizontalMm)
        {
            if (halfSizeMm <= 0.0) throw new ArgumentOutOfRangeException("halfSizeMm");
            pointX = Math.Max(centerX - halfSizeMm,
                Math.Min(centerX + halfSizeMm, targetX));
            pointY = Math.Max(centerY - halfSizeMm,
                Math.Min(centerY + halfSizeMm, targetY));
            double dx = pointX - targetX;
            double dy = pointY - targetY;
            horizontalMm = Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// 覆盖不完整或任一将被写入的最佳候选自身证据不完整时，快照不得审批。
        /// 已淘汰/未选择的其他候选可保留未验证记录，但不得连坐一个完整通过的最佳候选。
        /// </summary>
        public static bool IsFormalSnapshotApprovable(
            bool coverageComplete,
            IEnumerable<bool> selectedCandidateAuditStates)
        {
            if (!coverageComplete || selectedCandidateAuditStates == null) return false;
            List<bool> states = selectedCandidateAuditStates.ToList();
            return states.Count > 0 && states.All(x => x);
        }

        public static string GradeVerticalText(HandReachVerticalGrade grade)
        {
            switch (grade)
            {
                case HandReachVerticalGrade.RecommendedWithin300: return "recommended_within_300";
                case HandReachVerticalGrade.AttentionWithin500: return "attention_301_to_500";
                case HandReachVerticalGrade.PersonnelEntryNotDistanceLimited:
                    return "ceiling_personnel_entry_not_distance_limited";
                default: return "rejected_over_500";
            }
        }

        /// <summary>
        /// 天花正式契约固定为 450×450；侧墙正式默认也是 450×450，
        /// 但显式 SideWallOnly 复核允许 400×400 缩小口。最终操作伸手段默认 200 mm。
        /// 400×400 不得用于天花或 Auto 回退，避免把缩小侧墙口误当作人员入口。
        /// </summary>
        public static void ValidateFixedContract(HandReachOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            bool standard450 = Math.Abs(options.HatchSizeMm - 450.0) <= 1e-6;
            bool reducedSideWall400 = Math.Abs(options.HatchSizeMm - 400.0) <= 1e-6 &&
                options.OpeningPreference == HandReachOpeningPreference.SideWallOnly;
            if (!standard450 && !reducedSideWall400)
                throw new ArgumentException(
                    "天花与自动分析只允许450×450 mm；显式SideWallOnly可使用400×400或450×450 mm侧墙口。",
                    "options");
            if (Math.Abs(options.DefaultCorridorDiameterMm - 200.0) > 1e-6)
                throw new ArgumentException("HandReach 正式契约默认通道必须为 200 mm。", "options");
            if (options.CorridorTestDiametersMm == null ||
                !options.CorridorTestDiametersMm.Any(x => Math.Abs(x - 200.0) <= 1e-6))
                throw new ArgumentException("通道测试档位必须包含 200 mm。", "options");
            if (options.GridSpacingMm <= 0.0 || options.GridPointsPerAxis < 1)
                throw new ArgumentException("HandReach 网格参数必须为正数。", "options");
            if (Math.Abs(options.MaxDistanceMm - 500.0) > 1e-6)
                throw new ArgumentException("HandReach 正式伸手距离硬上限必须保持500mm。", "options");
            if (Math.Abs(options.SideWallReviewMaxDistanceMm - 600.0) > 1e-6)
                throw new ArgumentException("侧墙橙色复核距离上限固定为600mm。", "options");
            if (options.CeilingPersonnelEntryRiseMm <= 0.0 ||
                options.CeilingPersonnelFinalReachGapMm < 0.0)
                throw new ArgumentException(
                    "天花人员钻入包络参数必须为有效正值。", "options");
            if (options.AllowSideWallDistanceOver500Review &&
                options.OpeningPreference != HandReachOpeningPreference.SideWallOnly)
                throw new ArgumentException(
                    "500~600mm橙色复核只允许用于显式SideWallOnly分析。", "options");
        }

        /// <summary>
        /// 操作区 length 轴必须沿梯向，width 轴为其左侧垂线。
        /// 输出均为单位向量，供 Revit 几何层直接使用。
        /// </summary>
        public static void OperationZoneAxes(
            double ladderX,
            double ladderY,
            out double lengthAxisX,
            out double lengthAxisY,
            out double widthAxisX,
            out double widthAxisY)
        {
            double length = Math.Sqrt(ladderX * ladderX + ladderY * ladderY);
            if (length <= Epsilon)
                throw new ArgumentException("梯具方向不能为零向量。");
            lengthAxisX = ladderX / length;
            lengthAxisY = ladderY / length;
            widthAxisX = -lengthAxisY;
            widthAxisY = lengthAxisX;
        }

        public static bool ConnectivityAgrees(int fourNeighborRegionCount, int eightNeighborRegionCount)
        {
            return fourNeighborRegionCount == eightNeighborRegionCount;
        }

        /// <summary>
        /// 四/八邻接只影响可行网格的区域分组，不应推翻一个已经独立完成全链几何验证的
        /// 天花候选（直接伸手或人员钻入）。该例外只允许降级为橙色待复核；侧墙方案、无候选或候选自身
        /// 证据不完整时仍保持失败关闭。
        /// </summary>
        public static bool CanReviewConnectivityDisagreement(
            bool connectivityAgreed,
            bool ceilingPersonnelEntry,
            bool hasSelectedOpening,
            bool selectedCandidateAuditComplete)
        {
            return !connectivityAgreed &&
                   ceilingPersonnelEntry &&
                   hasSelectedOpening &&
                   selectedCandidateAuditComplete;
        }

        /// <summary>
        /// 返回人字梯四只梯脚相对候选中心的 XY 偏移（mm），与几何服务的构造尺寸一致。
        /// 行顺序为前左、前右、后左、后右。
        /// </summary>
        public static double[,] AFrameFootOffsets(
            double ladderHeightMm,
            double alongX,
            double alongY)
        {
            if (ladderHeightMm <= Epsilon)
                throw new ArgumentException("梯具高度必须为正数。", "ladderHeightMm");
            double ax, ay, wx, wy;
            OperationZoneAxes(alongX, alongY, out ax, out ay, out wx, out wy);
            double halfSpread = Math.Max(450.0, Math.Min(700.0, ladderHeightMm * 0.22));
            const double halfWidth = 300.0;
            return new[,]
            {
                { ax * halfSpread - wx * halfWidth, ay * halfSpread - wy * halfWidth },
                { ax * halfSpread + wx * halfWidth, ay * halfSpread + wy * halfWidth },
                { -ax * halfSpread - wx * halfWidth, -ay * halfSpread - wy * halfWidth },
                { -ax * halfSpread + wx * halfWidth, -ay * halfSpread + wy * halfWidth }
            };
        }

        /// <summary>
        /// 保守验证轴对齐方形是否完整落在单个真实面边界内。loops 使用奇偶规则表达
        /// 外轮廓与内洞；任一边界接触/穿越、内洞落入口内、凹口切入口内均拒绝。
        /// </summary>
        public static bool RectangleFullyContainedInFaceLoops(
            double centerX,
            double centerY,
            double halfSizeMm,
            IList<List<MaintenancePoint2>> loops)
        {
            if (halfSizeMm <= Epsilon || loops == null || loops.Count == 0 ||
                loops.Any(x => x == null || x.Count < 3))
                return false;

            var rectangle = new[]
            {
                new MaintenancePoint2(centerX - halfSizeMm, centerY - halfSizeMm),
                new MaintenancePoint2(centerX + halfSizeMm, centerY - halfSizeMm),
                new MaintenancePoint2(centerX + halfSizeMm, centerY + halfSizeMm),
                new MaintenancePoint2(centerX - halfSizeMm, centerY + halfSizeMm)
            };
            if (rectangle.Any(x => !PointInsideFilledLoops(x, loops))) return false;

            foreach (List<MaintenancePoint2> loop in loops)
            {
                for (int i = 0; i < loop.Count; i++)
                {
                    MaintenancePoint2 a = loop[i];
                    MaintenancePoint2 b = loop[(i + 1) % loop.Count];
                    for (int r = 0; r < rectangle.Length; r++)
                    {
                        MaintenancePoint2 c = rectangle[r];
                        MaintenancePoint2 d = rectangle[(r + 1) % rectangle.Length];
                        if (SegmentsIntersectOrTouch(a, b, c, d)) return false;
                    }
                    if (a.X > centerX - halfSizeMm + Epsilon &&
                        a.X < centerX + halfSizeMm - Epsilon &&
                        a.Y > centerY - halfSizeMm + Epsilon &&
                        a.Y < centerY + halfSizeMm - Epsilon)
                        return false;
                }
            }
            return true;
        }

        private static bool PointInsideFilledLoops(
            MaintenancePoint2 point,
            IEnumerable<List<MaintenancePoint2>> loops)
        {
            bool inside = false;
            foreach (List<MaintenancePoint2> loop in loops)
            {
                for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
                {
                    MaintenancePoint2 a = loop[j];
                    MaintenancePoint2 b = loop[i];
                    if (PointOnSegment(point, a, b)) return false;
                    bool crosses = (a.Y > point.Y) != (b.Y > point.Y) &&
                                   point.X < (b.X - a.X) * (point.Y - a.Y) /
                                   (b.Y - a.Y) + a.X;
                    if (crosses) inside = !inside;
                }
            }
            return inside;
        }

        private static bool SegmentsIntersectOrTouch(
            MaintenancePoint2 a,
            MaintenancePoint2 b,
            MaintenancePoint2 c,
            MaintenancePoint2 d)
        {
            double abC = Cross(a, b, c);
            double abD = Cross(a, b, d);
            double cdA = Cross(c, d, a);
            double cdB = Cross(c, d, b);
            if (((abC > Epsilon && abD < -Epsilon) ||
                 (abC < -Epsilon && abD > Epsilon)) &&
                ((cdA > Epsilon && cdB < -Epsilon) ||
                 (cdA < -Epsilon && cdB > Epsilon)))
                return true;
            return (Math.Abs(abC) <= Epsilon && PointOnSegment(c, a, b)) ||
                   (Math.Abs(abD) <= Epsilon && PointOnSegment(d, a, b)) ||
                   (Math.Abs(cdA) <= Epsilon && PointOnSegment(a, c, d)) ||
                   (Math.Abs(cdB) <= Epsilon && PointOnSegment(b, c, d));
        }

        private static bool PointOnSegment(
            MaintenancePoint2 point,
            MaintenancePoint2 a,
            MaintenancePoint2 b)
        {
            if (Math.Abs(Cross(a, b, point)) > Epsilon) return false;
            return point.X >= Math.Min(a.X, b.X) - Epsilon &&
                   point.X <= Math.Max(a.X, b.X) + Epsilon &&
                   point.Y >= Math.Min(a.Y, b.Y) - Epsilon &&
                   point.Y <= Math.Max(a.Y, b.Y) + Epsilon;
        }

        private static double Cross(
            MaintenancePoint2 a,
            MaintenancePoint2 b,
            MaintenancePoint2 point)
        {
            return (b.X - a.X) * (point.Y - a.Y) -
                   (b.Y - a.Y) * (point.X - a.X);
        }

        /// <summary>
        /// 40mm 网格相邻可行点合并为连续区域。四邻接或八邻接（diagonal）。
        /// 返回每个连通分量内的 packed key 列表（((long)ix&lt;&lt;32)|(uint)iy），按点数降序。
        /// 与原型 Components 函数一致。
        /// </summary>
        public static List<List<long>> MergeRegions(
            ISet<long> keys,
            int nx,
            int ny,
            bool diagonal)
        {
            var remain = new HashSet<long>(keys);
            var components = new List<List<long>>();
            int[] dirs = diagonal
                ? new[] { -1, -1, -1, 0, -1, 1, 0, -1, 0, 1, 1, -1, 1, 0, 1, 1 }
                : new[] { -1, 0, 0, -1, 0, 1, 1, 0 };

            while (remain.Count > 0)
            {
                long seed = remain.First();
                remain.Remove(seed);
                var component = new List<long>();
                var queue = new Queue<long>();
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    long key = queue.Dequeue();
                    component.Add(key);
                    int ix = (int)(key >> 32);
                    int iy = (int)(key & 0xffffffffL);
                    for (int d = 0; d < dirs.Length; d += 2)
                    {
                        int jx = ix + dirs[d];
                        int jy = iy + dirs[d + 1];
                        if (jx < 0 || jx >= nx || jy < 0 || jy >= ny) continue;
                        long neighbor = Pack(jx, jy);
                        if (remain.Remove(neighbor)) queue.Enqueue(neighbor);
                    }
                }
                components.Add(component);
            }
            return components.OrderByDescending(x => x.Count).ToList();
        }

        public static long Pack(int ix, int iy)
        {
            return ((long)ix << 32) | (uint)iy;
        }

        public static int UnpackIx(long key)
        {
            return (int)(key >> 32);
        }

        public static int UnpackIy(long key)
        {
            return (int)(key & 0xffffffffL);
        }

        /// <summary>把网格索引换算成中心坐标（mm）。</summary>
        public static void CellCenter(
            int ix,
            int iy,
            double startX,
            double startY,
            double spacingMm,
            out double x,
            out double y)
        {
            x = startX + ix * spacingMm;
            y = startY + iy * spacingMm;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace JarviTools.Commands.MaintenanceReachability
{
    internal enum OpeningPlaneKind
    {
        CeilingHorizontal = 0,
        SideWallVertical = 1
    }

    internal enum OpeningPreference
    {
        AutoPreferSideWall = 0,
        SideWallOnly = 1,
        CeilingOnly = 2
    }

    // HandReach 领域模型使用带前缀名称；数值与上面的纯策略枚举保持一一对应。
    internal enum HandReachOpeningPlaneKind
    {
        CeilingHorizontal = (int)OpeningPlaneKind.CeilingHorizontal,
        SideWallVertical = (int)OpeningPlaneKind.SideWallVertical
    }

    internal enum HandReachOpeningPreference
    {
        AutoPreferSideWall = (int)OpeningPreference.AutoPreferSideWall,
        SideWallOnly = (int)OpeningPreference.SideWallOnly,
        CeilingOnly = (int)OpeningPreference.CeilingOnly
    }

    /// <summary>
    /// HandReach 检修口的纯逻辑契约。侧墙默认450×450，并允许显式400×400缩小备选；
    /// 天花固定450×450。600×600爬入式检修门仍是独立方案，900×900转身区不是
    /// HandReach 方口的通过前提。
    /// </summary>
    internal sealed class HandReachOpeningContract
    {
        public OpeningPlaneKind PlaneKind { get; set; }
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public double CorridorDiameterMm { get; set; }
        public bool RequiresHumanPassage { get; set; }
        public bool AllowsPartialBodyEntry { get; set; }
        public bool RequiresHumanDoor600By600 { get; set; }
        public bool RequiresTurnZone900 { get; set; }
        public bool RequiresOperatorAccessToOpeningFace { get; set; }
    }

    /// <summary>
    /// 候选排序只接收已完成几何审计的硬结论。排序策略不会把不可行候选
    /// 因为侧墙优先而提升为正式候选。
    /// </summary>
    internal sealed class HandReachOpeningCandidateRank
    {
        public string StableKey { get; set; }
        public OpeningPlaneKind PlaneKind { get; set; }
        public bool IsHardFeasible { get; set; }
        public double EdgeDistanceMm { get; set; }
        public double MaxClearDiameterMm { get; set; }
    }

    /// <summary>侧墙400/450与天花450检修口的纯逻辑策略，不依赖 Revit。</summary>
    internal static class MaintenanceHandReachOpeningPolicy
    {
        public const double StandardOpeningSizeMm = 450.0;
        public const double ReducedSideWallOpeningSizeMm = 400.0;
        public const double StandardCorridorDiameterMm = 200.0;

        private const double DimensionToleranceMm = 1e-6;

        public static HandReachOpeningContract GetContract(OpeningPlaneKind planeKind)
        {
            return GetContract(planeKind, StandardOpeningSizeMm);
        }

        public static HandReachOpeningContract GetContract(
            OpeningPlaneKind planeKind,
            double openingSizeMm)
        {
            ValidatePlaneKind(planeKind);
            ValidateOpeningSize(planeKind, openingSizeMm);
            return new HandReachOpeningContract
            {
                PlaneKind = planeKind,
                WidthMm = openingSizeMm,
                HeightMm = openingSizeMm,
                CorridorDiameterMm = StandardCorridorDiameterMm,
                RequiresHumanPassage = planeKind == OpeningPlaneKind.CeilingHorizontal,
                AllowsPartialBodyEntry = true,
                RequiresHumanDoor600By600 = false,
                RequiresTurnZone900 = false,
                RequiresOperatorAccessToOpeningFace = true
            };
        }

        /// <summary>
        /// 在竖向墙面的局部坐标中计算真实最近边缘。U 沿墙，V 沿竖向；
        /// 调用者负责局部 UV 与世界 XYZ 之间的变换。
        /// </summary>
        public static void NearestSideWallOpeningEdgeLocalUv(
            double centerU,
            double centerV,
            double targetU,
            double targetV,
            out double edgeU,
            out double edgeV,
            out double distanceMm)
        {
            NearestSideWallOpeningEdgeLocalUv(
                centerU,
                centerV,
                targetU,
                targetV,
                StandardOpeningSizeMm,
                out edgeU,
                out edgeV,
                out distanceMm);
        }

        public static void NearestSideWallOpeningEdgeLocalUv(
            double centerU,
            double centerV,
            double targetU,
            double targetV,
            double openingSizeMm,
            out double edgeU,
            out double edgeV,
            out double distanceMm)
        {
            ValidateFinite(centerU, "centerU");
            ValidateFinite(centerV, "centerV");
            ValidateFinite(targetU, "targetU");
            ValidateFinite(targetV, "targetV");
            ValidateOpeningSize(OpeningPlaneKind.SideWallVertical, openingSizeMm);

            MaintenanceHandReachMath.NearestEdge(
                centerU,
                centerV,
                targetU,
                targetV,
                openingSizeMm / 2.0,
                out edgeU,
                out edgeV,
                out distanceMm);
        }

        /// <summary>
        /// 一维净有效带是否完整容纳指定开口尺寸。effectiveBandMm 必须已经扣除
        /// 构造边框和施工余量；本方法不会把毛尺寸误当净尺寸。
        /// </summary>
        public static bool EffectiveBandFitsOpening(double effectiveBandMm, double openingExtentMm)
        {
            ValidatePositiveFinite(effectiveBandMm, "effectiveBandMm");
            ValidatePositiveFinite(openingExtentMm, "openingExtentMm");
            return effectiveBandMm + DimensionToleranceMm >= openingExtentMm;
        }

        public static bool IsPlaneAllowed(OpeningPlaneKind planeKind, OpeningPreference preference)
        {
            ValidatePlaneKind(planeKind);
            ValidatePreference(preference);

            switch (preference)
            {
                case OpeningPreference.AutoPreferSideWall:
                    return true;
                case OpeningPreference.SideWallOnly:
                    return planeKind == OpeningPlaneKind.SideWallVertical;
                case OpeningPreference.CeilingOnly:
                    return planeKind == OpeningPlaneKind.CeilingHorizontal;
                default:
                    throw new ArgumentOutOfRangeException("preference");
            }
        }

        /// <summary>
        /// 返回已经过滤硬不可行项后的稳定顺序：Auto 时侧墙优先于天花，
        /// 同平面按真实开口边缘距离、再按稳定键排序。
        /// </summary>
        public static List<HandReachOpeningCandidateRank> OrderFeasibleCandidates(
            IEnumerable<HandReachOpeningCandidateRank> candidates,
            OpeningPreference preference)
        {
            if (candidates == null) throw new ArgumentNullException("candidates");
            ValidatePreference(preference);

            List<HandReachOpeningCandidateRank> materialized = candidates.ToList();
            foreach (HandReachOpeningCandidateRank candidate in materialized)
                ValidateCandidate(candidate);

            return materialized
                .Where(candidate => candidate.IsHardFeasible && IsPlaneAllowed(candidate.PlaneKind, preference))
                .OrderBy(candidate => PreferenceRank(candidate.PlaneKind, preference))
                .ThenBy(candidate => candidate.EdgeDistanceMm)
                .ThenByDescending(candidate => candidate.MaxClearDiameterMm)
                .ThenBy(candidate => candidate.StableKey, StringComparer.Ordinal)
                .ToList();
        }

        private static int PreferenceRank(OpeningPlaneKind planeKind, OpeningPreference preference)
        {
            if (preference == OpeningPreference.AutoPreferSideWall)
                return planeKind == OpeningPlaneKind.SideWallVertical ? 0 : 1;
            return 0;
        }

        private static void ValidateCandidate(HandReachOpeningCandidateRank candidate)
        {
            if (candidate == null) throw new ArgumentException("候选不得为 null。", "candidate");
            ValidatePlaneKind(candidate.PlaneKind);
            if (string.IsNullOrWhiteSpace(candidate.StableKey))
                throw new ArgumentException("候选必须提供非空稳定键。", "candidate");
            ValidateFinite(candidate.EdgeDistanceMm, "candidate.EdgeDistanceMm");
            if (candidate.EdgeDistanceMm < 0.0)
                throw new ArgumentOutOfRangeException("candidate", "候选边缘距离不得小于 0。");
            ValidateFinite(candidate.MaxClearDiameterMm, "candidate.MaxClearDiameterMm");
            if (candidate.MaxClearDiameterMm < 0.0)
                throw new ArgumentOutOfRangeException("candidate", "最大已验证通道直径不得小于 0。");
        }

        private static void ValidatePlaneKind(OpeningPlaneKind planeKind)
        {
            if (!Enum.IsDefined(typeof(OpeningPlaneKind), planeKind))
                throw new ArgumentOutOfRangeException("planeKind");
        }

        private static void ValidatePreference(OpeningPreference preference)
        {
            if (!Enum.IsDefined(typeof(OpeningPreference), preference))
                throw new ArgumentOutOfRangeException("preference");
        }

        private static void ValidateOpeningSize(
            OpeningPlaneKind planeKind,
            double openingSizeMm)
        {
            ValidatePositiveFinite(openingSizeMm, "openingSizeMm");
            bool standard = Math.Abs(openingSizeMm - StandardOpeningSizeMm) <=
                DimensionToleranceMm;
            bool reducedSideWall = planeKind == OpeningPlaneKind.SideWallVertical &&
                Math.Abs(openingSizeMm - ReducedSideWallOpeningSizeMm) <=
                    DimensionToleranceMm;
            if (!standard && !reducedSideWall)
                throw new ArgumentException(
                    "天花只允许450×450 mm；侧墙HandReach允许400×400或450×450 mm。",
                    "openingSizeMm");
        }

        private static void ValidatePositiveFinite(double value, string parameterName)
        {
            ValidateFinite(value, parameterName);
            if (value <= 0.0) throw new ArgumentOutOfRangeException(parameterName);
        }

        private static void ValidateFinite(double value, string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

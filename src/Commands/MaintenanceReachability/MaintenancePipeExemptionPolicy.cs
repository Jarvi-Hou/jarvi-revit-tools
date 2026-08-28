using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JarviTools.Commands.MaintenanceReachability
{
    internal enum MaintenancePipeCategoryKind
    {
        Other,
        PipeCurve,
        PipeFitting,
        PipeAccessory
    }

    internal sealed class MaintenanceBounds3Mm
    {
        public double MinX;
        public double MinY;
        public double MinZ;
        public double MaxX;
        public double MaxY;
        public double MaxZ;

        public bool IsValid
        {
            get
            {
                return IsFinite(MinX) && IsFinite(MinY) && IsFinite(MinZ) &&
                       IsFinite(MaxX) && IsFinite(MaxY) && IsFinite(MaxZ) &&
                       MinX <= MaxX && MinY <= MaxY && MinZ <= MaxZ;
            }
        }

        public double LongestExtentMm
        {
            get
            {
                return !IsValid
                    ? double.NaN
                    : Math.Max(MaxX - MinX, Math.Max(MaxY - MinY, MaxZ - MinZ));
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    internal sealed class MaintenancePipeExemptionInput
    {
        public MaintenancePipeCategoryKind Category;
        public bool SameSourceModel;
        public bool SystemEvidenceReliable;
        public string SystemEvidence;
        public double LengthMm;
        public double DiameterMm;
        public MaintenanceBounds3Mm ElementBounds;
        public MaintenanceBounds3Mm TargetBounds;
        public readonly List<MaintenancePoint3> EndPoints = new List<MaintenancePoint3>();
        public double NearestOtherTargetDistanceMm = double.PositiveInfinity;
    }

    internal sealed class MaintenancePipeExemptionDecision
    {
        public bool IsExempt;
        public string SystemKind;
        public string ReasonCode;
        public string Reason;
        public double DistanceMm;

        public MaintenancePipeExemptionDecision()
        {
            SystemKind = string.Empty;
            ReasonCode = string.Empty;
            Reason = string.Empty;
            DistanceMm = double.NaN;
        }
    }

    /// <summary>
    /// Pure, conservative policy for target-local refrigerant/condensate branches.
    /// A system label alone is never sufficient: the element must be small, short,
    /// endpoint-near, contained in the target neighbourhood and unambiguously owned
    /// by one equipment target.  Ambiguous evidence remains an obstacle.
    /// </summary>
    internal static class MaintenancePipeExemptionPolicy
    {
        internal const string PolicyVersion = "target_local_pipe_branch_v1";
        internal const double MaxNominalDiameterMm = 100.0;
        internal const double MaxPipeCurveLengthMm = 1800.0;
        internal const double MaxFittingOrAccessoryExtentMm = 650.0;
        internal const double MaxNearEndpointDistanceMm = 300.0;
        internal const double MaxBranchReachFromTargetMm = 1200.0;
        internal const double MinUniqueOwnershipMarginMm = 250.0;

        internal static MaintenancePipeExemptionDecision Evaluate(
            MaintenancePipeExemptionInput input)
        {
            if (input == null) return Reject("missing_input", "缺少局部支管判断输入。");
            if (input.Category != MaintenancePipeCategoryKind.PipeCurve &&
                input.Category != MaintenancePipeCategoryKind.PipeFitting &&
                input.Category != MaintenancePipeCategoryKind.PipeAccessory)
                return Reject("unsupported_category", "仅管道、管件和管道附件可进入局部支管豁免判断。");
            if (!input.SameSourceModel)
                return Reject("different_source_model", "管线与设备不在同一宿主或同一链接实例中，空间邻近不足以证明归属。");
            if (!input.SystemEvidenceReliable)
                return Reject("unreliable_system_evidence", "未取得内置系统类型或连接器 MEPSystem 的可靠证据。");

            string systemKind;
            if (!TryClassifySystemEvidence(input.SystemEvidence, out systemKind))
                return Reject("system_not_exempt", "可靠系统证据不属于冷媒管或冷凝水管。");
            if (input.ElementBounds == null || input.TargetBounds == null ||
                !input.ElementBounds.IsValid || !input.TargetBounds.IsValid)
                return Reject("invalid_bounds", "管线或设备包围盒无效，不能建立空间归属。");
            if (!IsFinitePositive(input.DiameterMm) || input.DiameterMm > MaxNominalDiameterMm)
                return Reject("diameter_out_of_range", "管径缺失或超过局部支管上限。");
            if (input.EndPoints.Count == 0)
                return Reject("missing_endpoints", "缺少曲线端点或连接器位置，不能仅凭包围盒豁免。");

            double effectiveLength = input.Category == MaintenancePipeCategoryKind.PipeCurve
                ? input.LengthMm
                : input.ElementBounds.LongestExtentMm;
            double maximumLength = input.Category == MaintenancePipeCategoryKind.PipeCurve
                ? MaxPipeCurveLengthMm
                : MaxFittingOrAccessoryExtentMm;
            if (!IsFinitePositive(effectiveLength) || effectiveLength > maximumLength ||
                input.ElementBounds.LongestExtentMm > maximumLength)
                return Reject("branch_too_long", "构件过长，不能把穿越区域的主管当作设备局部支管。");

            double nearestEndpoint = input.EndPoints
                .Select(x => DistancePointToBounds(x, input.TargetBounds))
                .Min();
            if (nearestEndpoint > MaxNearEndpointDistanceMm)
                return Reject("no_near_endpoint", "没有端点或连接器落在设备明确近端范围内。");
            double farthestEndpoint = input.EndPoints
                .Select(x => DistancePointToBounds(x, input.TargetBounds))
                .Max();
            if (farthestEndpoint > MaxBranchReachFromTargetMm ||
                !ContainedByExpandedTarget(
                    input.ElementBounds,
                    input.TargetBounds,
                    MaxBranchReachFromTargetMm))
                return Reject("branch_leaves_target_neighbourhood", "支管超出设备局部邻域，不能全构件豁免。");
            if (IsFinite(input.NearestOtherTargetDistanceMm) &&
                input.NearestOtherTargetDistanceMm <
                    nearestEndpoint + MinUniqueOwnershipMarginMm)
                return Reject("ambiguous_target_ownership", "该管线同样接近另一设备，空间归属不唯一。");

            return new MaintenancePipeExemptionDecision
            {
                IsExempt = true,
                SystemKind = systemKind,
                ReasonCode = "target_local_short_branch",
                DistanceMm = nearestEndpoint,
                Reason = PolicyVersion + "；可靠系统=" + systemKind +
                         "；近端距离=" + nearestEndpoint.ToString("0.0", CultureInfo.InvariantCulture) +
                         "mm；长度=" + effectiveLength.ToString("0.0", CultureInfo.InvariantCulture) +
                         "mm；管径=" + input.DiameterMm.ToString("0.0", CultureInfo.InvariantCulture) +
                         "mm；唯一设备空间归属已确认"
            };
        }

        internal static bool TryClassifySystemEvidence(string value, out string systemKind)
        {
            systemKind = string.Empty;
            string normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0) return false;
            if (normalized.Contains("冷凝水"))
            {
                systemKind = "condensate";
                return true;
            }
            // “冷媒水”通常是水系统命名，不能因宽泛包含“冷媒”而误豁免。
            if (normalized.Contains("冷媒") && !normalized.Contains("冷媒水"))
            {
                systemKind = "refrigerant";
                return true;
            }
            return false;
        }

        internal static double DistancePointToBounds(
            MaintenancePoint3 point,
            MaintenanceBounds3Mm bounds)
        {
            if (bounds == null || !bounds.IsValid) return double.PositiveInfinity;
            double dx = point.X < bounds.MinX
                ? bounds.MinX - point.X
                : (point.X > bounds.MaxX ? point.X - bounds.MaxX : 0.0);
            double dy = point.Y < bounds.MinY
                ? bounds.MinY - point.Y
                : (point.Y > bounds.MaxY ? point.Y - bounds.MaxY : 0.0);
            double dz = point.Z < bounds.MinZ
                ? bounds.MinZ - point.Z
                : (point.Z > bounds.MaxZ ? point.Z - bounds.MaxZ : 0.0);
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        internal static double DistanceBoundsToBounds(
            MaintenanceBounds3Mm left,
            MaintenanceBounds3Mm right)
        {
            if (left == null || right == null || !left.IsValid || !right.IsValid)
                return double.PositiveInfinity;
            double dx = left.MaxX < right.MinX
                ? right.MinX - left.MaxX
                : (right.MaxX < left.MinX ? left.MinX - right.MaxX : 0.0);
            double dy = left.MaxY < right.MinY
                ? right.MinY - left.MaxY
                : (right.MaxY < left.MinY ? left.MinY - right.MaxY : 0.0);
            double dz = left.MaxZ < right.MinZ
                ? right.MinZ - left.MaxZ
                : (right.MaxZ < left.MinZ ? left.MinZ - right.MaxZ : 0.0);
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        internal static string BuildStoredEvidenceSignature(
            MaintenancePipeExemptionEvidence evidence)
        {
            if (evidence == null || evidence.Element == null) return "pipe_exemption|missing";
            return string.Join("|", new[]
            {
                "pipe_exemption",
                PolicyVersion,
                evidence.GroupKey ?? string.Empty,
                evidence.TargetKey ?? string.Empty,
                evidence.Element.GetStableKey(),
                evidence.CategoryKind ?? string.Empty,
                evidence.SystemKind ?? string.Empty,
                evidence.SystemTypeEvidence ?? string.Empty,
                evidence.SystemEvidenceSource ?? string.Empty,
                evidence.ReasonCode ?? string.Empty,
                evidence.Reason ?? string.Empty,
                Math.Round(evidence.DistanceMm, 1).ToString("0.0", CultureInfo.InvariantCulture),
                Math.Round(evidence.LengthMm, 1).ToString("0.0", CultureInfo.InvariantCulture),
                Math.Round(evidence.DiameterMm, 1).ToString("0.0", CultureInfo.InvariantCulture)
            });
        }

        internal static string BuildLiveSystemEvidenceSignature(
            MaintenancePipeExemptionEvidence evidence,
            bool currentSystemReliable,
            string currentSystemEvidence,
            string currentSystemEvidenceSource)
        {
            if (evidence == null || evidence.Element == null) return "pipe_exemption|missing";
            return string.Join("|", new[]
            {
                "pipe_exemption",
                PolicyVersion,
                evidence.GroupKey ?? string.Empty,
                evidence.TargetKey ?? string.Empty,
                evidence.Element.GetStableKey(),
                evidence.ReasonCode ?? string.Empty,
                currentSystemReliable ? "reliable" : "unreliable_or_missing",
                currentSystemEvidence ?? string.Empty,
                currentSystemEvidenceSource ?? string.Empty
            });
        }

        private static bool ContainedByExpandedTarget(
            MaintenanceBounds3Mm element,
            MaintenanceBounds3Mm target,
            double expansionMm)
        {
            return element.MinX >= target.MinX - expansionMm &&
                   element.MinY >= target.MinY - expansionMm &&
                   element.MinZ >= target.MinZ - expansionMm &&
                   element.MaxX <= target.MaxX + expansionMm &&
                   element.MaxY <= target.MaxY + expansionMm &&
                   element.MaxZ <= target.MaxZ + expansionMm;
        }

        private static MaintenancePipeExemptionDecision Reject(string code, string reason)
        {
            return new MaintenancePipeExemptionDecision
            {
                IsExempt = false,
                ReasonCode = code,
                Reason = reason ?? string.Empty
            };
        }

        private static bool IsFinitePositive(double value)
        {
            return IsFinite(value) && value > 0.0;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}

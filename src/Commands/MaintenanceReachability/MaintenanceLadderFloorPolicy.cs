using System;
using System.Collections.Generic;
using System.Linq;

namespace JarviTools.Commands.MaintenanceReachability
{
    internal enum MaintenanceFloorSupportState
    {
        Clear,
        Missing,
        Unverified
    }

    internal sealed class MaintenanceFloorSupportSample
    {
        public MaintenanceFloorSupportState State;
        public double ElevationMm;
        public string SourceKey;
        public string Reason;

        public MaintenanceFloorSupportSample()
        {
            ElevationMm = double.NaN;
            SourceKey = string.Empty;
            Reason = string.Empty;
        }
    }

    internal sealed class MaintenanceLadderFloorDecision
    {
        public MaintenanceFloorSupportState State;
        public double FloorElevationMm;
        public string ReasonCode;
        public string Reason;
        public readonly List<string> SourceKeys = new List<string>();

        public MaintenanceLadderFloorDecision()
        {
            FloorElevationMm = double.NaN;
            ReasonCode = string.Empty;
            Reason = string.Empty;
        }

        public bool IsClear
        {
            get { return State == MaintenanceFloorSupportState.Clear; }
        }
    }

    /// <summary>
    /// Pure geometry/status contract shared by the Revit floor-face probe and tests.
    /// Coordinates and elevations are millimetres.  The point formula intentionally
    /// mirrors MaintenanceGeometryService's ladder solids.
    /// </summary>
    internal static class MaintenanceLadderFloorPolicy
    {
        internal const double MaximumSupportDeltaMm = 10.0;

        internal static List<MaintenancePoint2> BuildSupportPoints(
            MaintenanceLadderType ladderType,
            MaintenancePoint2 planCenter,
            MaintenancePoint2 alongDirection,
            double ladderFloorMm,
            double ladderTopMm)
        {
            var output = new List<MaintenancePoint2>();
            MaintenancePoint2 along = alongDirection.Normalize();
            if (along.Length() <= 1e-9) along = new MaintenancePoint2(1.0, 0.0);
            MaintenancePoint2 across = along.LeftNormal();
            double heightMm = ladderTopMm - ladderFloorMm;
            if (!IsFinite(heightMm) || heightMm <= 0.0) return output;

            if (ladderType == MaintenanceLadderType.AFrame)
            {
                double halfSpreadMm = Clamp(heightMm * 0.22, 450.0, 700.0);
                const double halfWidthMm = 300.0;
                MaintenancePoint2 front = planCenter + along * halfSpreadMm;
                MaintenancePoint2 rear = planCenter - along * halfSpreadMm;
                output.Add(planCenter);
                output.Add(front - across * halfWidthMm);
                output.Add(front + across * halfWidthMm);
                output.Add(rear - across * halfWidthMm);
                output.Add(rear + across * halfWidthMm);
                return output;
            }

            if (ladderType == MaintenanceLadderType.Straight)
            {
                double totalRunMm = Clamp(heightMm * 0.23, 450.0, 900.0);
                const double halfWidthMm = 300.0;
                MaintenancePoint2 bottomCenter = planCenter - along * (totalRunMm * 0.5);
                output.Add(bottomCenter - across * halfWidthMm);
                output.Add(bottomCenter + across * halfWidthMm);
            }
            return output;
        }

        internal static List<MaintenancePoint2> BuildOperationZoneSupportPoints(
            MaintenancePoint2 center,
            MaintenancePoint2 lengthDirection,
            double lengthMm,
            double widthMm)
        {
            var output = new List<MaintenancePoint2>();
            MaintenancePoint2 along = lengthDirection.Normalize();
            if (along.Length() <= 1e-9) along = new MaintenancePoint2(1.0, 0.0);
            MaintenancePoint2 across = along.LeftNormal();
            if (!IsFinite(lengthMm) || !IsFinite(widthMm) ||
                lengthMm <= 0.0 || widthMm <= 0.0) return output;
            MaintenancePoint2 halfLength = along * (lengthMm * 0.5);
            MaintenancePoint2 halfWidth = across * (widthMm * 0.5);
            output.Add(center);
            output.Add(center - halfLength - halfWidth);
            output.Add(center + halfLength - halfWidth);
            output.Add(center + halfLength + halfWidth);
            output.Add(center - halfLength + halfWidth);
            return output;
        }

        internal static MaintenanceLadderFloorDecision Evaluate(
            IEnumerable<MaintenanceFloorSupportSample> samples,
            int expectedPointCount,
            double maximumSupportDeltaMm = MaximumSupportDeltaMm)
        {
            List<MaintenanceFloorSupportSample> values = samples == null
                ? new List<MaintenanceFloorSupportSample>()
                : samples.Where(x => x != null).ToList();
            if (values.Any(x => x.State == MaintenanceFloorSupportState.Unverified))
            {
                MaintenanceFloorSupportSample first = values.First(
                    x => x.State == MaintenanceFloorSupportState.Unverified);
                return Failure(
                    MaintenanceFloorSupportState.Unverified,
                    "ladder_floor_support_unverified",
                    string.IsNullOrWhiteSpace(first.Reason)
                        ? "梯脚下方楼板几何无法完整验证。"
                        : first.Reason,
                    values);
            }

            if (expectedPointCount <= 0 || values.Count != expectedPointCount ||
                values.Any(x => x.State != MaintenanceFloorSupportState.Clear ||
                                !IsFinite(x.ElevationMm)))
            {
                return Failure(
                    MaintenanceFloorSupportState.Missing,
                    "ladder_floor_support_missing",
                    "至少一个必要梯脚点下方没有真实楼板上表面支撑。",
                    values);
            }

            double minimum = values.Min(x => x.ElevationMm);
            double maximum = values.Max(x => x.ElevationMm);
            if (maximum - minimum > maximumSupportDeltaMm + 1e-9)
            {
                return Failure(
                    MaintenanceFloorSupportState.Missing,
                    "ladder_floor_support_uneven",
                    "梯具支撑点楼面高差超过 " +
                    maximumSupportDeltaMm.ToString("0.###") + " mm。",
                    values);
            }

            var result = new MaintenanceLadderFloorDecision
            {
                State = MaintenanceFloorSupportState.Clear,
                FloorElevationMm = maximum,
                ReasonCode = "ladder_floor_support_clear",
                Reason = "全部必要支撑点均命中真实楼板上表面，且高差不超过 10 mm。"
            };
            AddSourceKeys(result.SourceKeys, values);
            return result;
        }

        private static MaintenanceLadderFloorDecision Failure(
            MaintenanceFloorSupportState state,
            string reasonCode,
            string reason,
            IEnumerable<MaintenanceFloorSupportSample> samples)
        {
            var result = new MaintenanceLadderFloorDecision
            {
                State = state,
                ReasonCode = reasonCode ?? string.Empty,
                Reason = reason ?? string.Empty
            };
            AddSourceKeys(result.SourceKeys, samples);
            return result;
        }

        private static void AddSourceKeys(
            ICollection<string> target,
            IEnumerable<MaintenanceFloorSupportSample> samples)
        {
            foreach (string key in (samples ?? Enumerable.Empty<MaintenanceFloorSupportSample>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.SourceKey))
                .Select(x => x.SourceKey)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal))
                target.Add(key);
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}

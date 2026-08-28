using System;
using System.Collections.Generic;
using System.Linq;

namespace JarviTools.Commands.MaintenanceReachability
{
    internal sealed class MaintenanceSharedCeilingEntryAlternative
    {
        public string CandidateKey;
        public string GroupKey;
        public MaintenanceAccessProfile Profile;
        public MaintenanceCandidateStatus Status;
        public bool AllTargetsComplete;
        public MaintenancePoint3 EntryCenter;
        public double OpeningWidthMm;
        public double OpeningHeightMm;
        public int CoveredTargetCount;
        public double MaxRouteLengthMm;
        public readonly List<string> TargetKeys;

        public MaintenanceSharedCeilingEntryAlternative()
        {
            CandidateKey = string.Empty;
            GroupKey = string.Empty;
            TargetKeys = new List<string>();
        }
    }

    /// <summary>
    /// Pure decision policy for reviewing whether one selected ceiling hatch can
    /// serve two or more devices. Geometry remains owned by the route service;
    /// this class only interprets the retained route evidence.
    /// </summary>
    internal static class MaintenanceSharedCeilingEntryPolicy
    {
        internal const string PolicyVersion = "selected_ceiling_union_450_entry_v1";
        internal const double DefaultHatchSizeMm = 450.0;

        internal static string BuildCombinedGroupKey(IEnumerable<string> groupKeys)
        {
            List<string> keys = (groupKeys ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            if (keys.Count == 0) return "选中天花共用入口";
            return string.Join("+", keys);
        }

        internal static List<MaintenanceSharedCeilingEntryAlternative> FindAlternatives(
            IEnumerable<MaintenanceCandidateEvaluation> evaluations)
        {
            var output = new List<MaintenanceSharedCeilingEntryAlternative>();
            IEnumerable<MaintenanceCandidateEvaluation> routes =
                (evaluations ?? Enumerable.Empty<MaintenanceCandidateEvaluation>())
                .Where(IsReachableCeilingRoute);

            foreach (IGrouping<string, MaintenanceCandidateEvaluation> group in routes
                .GroupBy(x => string.Join("|", new[]
                {
                    x.GroupKey ?? string.Empty,
                    x.CandidateKey ?? string.Empty,
                    x.Profile.ToString()
                }), StringComparer.Ordinal))
            {
                List<MaintenanceCandidateEvaluation> bestByTarget = group
                    .GroupBy(x => x.TargetKey ?? string.Empty, StringComparer.Ordinal)
                    .Select(x => x
                        .OrderBy(y => y.Status == MaintenanceCandidateStatus.Feasible ? 0 : 1)
                        .ThenBy(y => y.Stage == MaintenanceCandidateStage.Complete ? 0 : 1)
                        .ThenBy(y => y.RouteLengthMm)
                        .ThenBy(y => y.EvaluationKey, StringComparer.Ordinal)
                        .First())
                    .Where(x => !string.IsNullOrWhiteSpace(x.TargetKey))
                    .OrderBy(x => x.TargetKey, StringComparer.Ordinal)
                    .ToList();
                if (bestByTarget.Count < 2) continue;

                MaintenanceCandidateEvaluation first = bestByTarget[0];
                bool allComplete = bestByTarget.All(x =>
                    x.Stage == MaintenanceCandidateStage.Complete);
                bool allFeasible = allComplete && bestByTarget.All(x =>
                    x.Status == MaintenanceCandidateStatus.Feasible);
                var alternative = new MaintenanceSharedCeilingEntryAlternative
                {
                    CandidateKey = first.CandidateKey ?? string.Empty,
                    GroupKey = first.GroupKey ?? string.Empty,
                    Profile = first.Profile,
                    Status = allFeasible
                        ? MaintenanceCandidateStatus.Feasible
                        : MaintenanceCandidateStatus.Unverified,
                    AllTargetsComplete = allComplete,
                    EntryCenter = first.EntryCenter,
                    OpeningWidthMm = first.OpeningWidthMm,
                    OpeningHeightMm = first.OpeningHeightMm,
                    CoveredTargetCount = bestByTarget.Count,
                    MaxRouteLengthMm = bestByTarget.Max(x => x.RouteLengthMm)
                };
                alternative.TargetKeys.AddRange(bestByTarget.Select(x => x.TargetKey));
                output.Add(alternative);
            }

            return output
                .OrderByDescending(x => x.CoveredTargetCount)
                .ThenBy(x => x.Status == MaintenanceCandidateStatus.Feasible ? 0 : 1)
                .ThenBy(x => x.Profile == MaintenanceAccessProfile.Full700 ? 0 : 1)
                .ThenBy(x => x.MaxRouteLengthMm)
                .ThenBy(x => x.CandidateKey, StringComparer.Ordinal)
                .ToList();
        }

        internal static void ApplyCoveredTargetCounts(
            IEnumerable<MaintenanceSharedCeilingEntryAlternative> alternatives,
            IEnumerable<MaintenanceCandidateEvaluation> evaluations)
        {
            if (alternatives == null || evaluations == null) return;
            var coverage = alternatives
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.CandidateKey))
                .GroupBy(x => string.Join("|", new[]
                {
                    x.GroupKey ?? string.Empty,
                    x.CandidateKey,
                    x.Profile.ToString()
                }), StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Max(y => y.CoveredTargetCount), StringComparer.Ordinal);
            foreach (MaintenanceCandidateEvaluation evaluation in evaluations.Where(x => x != null))
            {
                string key = string.Join("|", new[]
                {
                    evaluation.GroupKey ?? string.Empty,
                    evaluation.CandidateKey ?? string.Empty,
                    evaluation.Profile.ToString()
                });
                int count;
                if (coverage.TryGetValue(key, out count))
                    evaluation.CoveredTargetCount = Math.Max(evaluation.CoveredTargetCount, count);
            }
        }

        private static bool IsReachableCeilingRoute(MaintenanceCandidateEvaluation evaluation)
        {
            return evaluation != null &&
                   evaluation.Scope == MaintenanceCandidateScope.Route &&
                   evaluation.EntryType == MaintenanceEntryType.CeilingHatch &&
                   !string.IsNullOrWhiteSpace(evaluation.CandidateKey) &&
                   !string.IsNullOrWhiteSpace(evaluation.TargetKey) &&
                   evaluation.Status != MaintenanceCandidateStatus.Rejected &&
                   (evaluation.Stage == MaintenanceCandidateStage.ServicePocket ||
                    evaluation.Stage == MaintenanceCandidateStage.Complete);
        }
    }
}

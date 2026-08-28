using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace JarviTools.Commands.MaintenanceReachability
{
    internal static class MaintenanceCandidateAudit
    {
        internal static string BuildEvaluationKey(
            MaintenanceCandidateScope scope,
            string candidateKey,
            string targetKey,
            MaintenanceAccessProfile profile)
        {
            return scope + "|" + (candidateKey ?? string.Empty) + "|" +
                   (targetKey ?? string.Empty) + "|" + profile;
        }

        internal static void FinalizeForReporting(
            IList<MaintenanceCandidateEvaluation> evaluations)
        {
            if (evaluations == null) throw new ArgumentNullException("evaluations");

            foreach (MaintenanceCandidateEvaluation item in evaluations.Where(x => x != null))
            {
                item.EvaluationKey = BuildEvaluationKey(
                    item.Scope,
                    item.CandidateKey,
                    item.TargetKey,
                    item.Profile);

                if (item.Scope != MaintenanceCandidateScope.Route) continue;
            }

            ApplyEntryEvidenceToRoutes(evaluations);

            foreach (IGrouping<string, MaintenanceCandidateEvaluation> group in evaluations
                .Where(x => x != null && x.Scope == MaintenanceCandidateScope.Route)
                .GroupBy(x => (x.GroupKey ?? string.Empty) + "|" + (x.TargetKey ?? string.Empty),
                    StringComparer.Ordinal))
            {
                MaintenanceCandidateEvaluation selected = group.FirstOrDefault(x => x.IsSelected);
                int rank = 1;
                foreach (MaintenanceCandidateEvaluation item in OrderForReporting(group))
                {
                    item.Rank = rank++;
                    if (item.IsSelected)
                    {
                        if (item.Status == MaintenanceCandidateStatus.Rejected)
                        {
                            item.SelectionReason =
                                "原选择规则曾选中该方案，但补充候选审计已在“" + item.Stage +
                                "”阶段判定不通过；该选择已失效，不得作为汇报推荐，必须重新选择。";
                            continue;
                        }
                        if (item.Status == MaintenanceCandidateStatus.Unverified)
                        {
                            item.SelectionReason =
                                "原选择规则曾选中该方案，但补充候选审计仍有无法验证的证据；当前只能作为待复核方案，不能表述为已经通过。";
                            continue;
                        }
                        if (item.EntryType == MaintenanceEntryType.CeilingHatch)
                        {
                            item.SelectionReason = item.Profile == MaintenanceAccessProfile.Full700
                                ? "700 mm 档位没有可用侧墙入口，因此按原 80 mm 天花网格，以到目标的 Manhattan 距离、Y/X 稳定顺序取首个通过转身区和梯具检查的位置。路线长度未参与选择。"
                                : "700 mm 完整链路未通过，600 mm 档位也没有可用侧墙入口，因此按原 80 mm 天花网格，以到目标的 Manhattan 距离、Y/X 稳定顺序取首个可行位置；该受限档位仍需专业复核。路线长度未参与选择。";
                        }
                        else if (item.Profile == MaintenanceAccessProfile.Full700)
                        {
                            item.SelectionReason =
                                "700 mm 完整通行档位已通过并选择侧墙入口；同档位按可覆盖的未分配设备数、人字梯优先和稳定候选键执行贪心选择。路线长度未参与选择。";
                        }
                        else
                        {
                            item.SelectionReason =
                                "700 mm 完整链路未通过，采用 600 mm 受限档位的侧墙入口；同档位按可覆盖的未分配设备数、复用 700 mm 已有入口、人字梯优先和稳定候选键执行贪心选择。该受限档位仍需专业复核，路线长度未参与选择。";
                        }
                        continue;
                    }
                    if (selected != null)
                    {
                        item.DominatedByCandidateKey = selected.CandidateKey ?? string.Empty;
                        item.DominatedByEvaluationKey = selected.EvaluationKey ?? string.Empty;
                    }
                    if ((item.Status == MaintenanceCandidateStatus.Feasible ||
                         item.Status == MaintenanceCandidateStatus.Unverified) &&
                        string.IsNullOrWhiteSpace(item.SelectionReason))
                    {
                        item.SelectionReason = item.Status == MaintenanceCandidateStatus.Feasible
                            ? "几何可行，但按当前入口选择策略低于最终方案，因此作为备选保留；路线长度未参与当前选择。"
                            : "存在可到达路线但仍有待复核证据，未优先于最终方案。";
                    }
                }
            }
        }

        internal static List<MaintenancePoint2> SelectSpatialRepresentatives(
            IEnumerable<MaintenancePoint2> samples,
            MaintenancePoint2 anchor,
            double bucketSizeMm,
            int maxCount,
            out int deduplicatedCount,
            out int omittedCount)
        {
            if (samples == null) throw new ArgumentNullException("samples");
            if (bucketSizeMm <= 0.0) throw new ArgumentOutOfRangeException("bucketSizeMm");
            if (maxCount < 1) throw new ArgumentOutOfRangeException("maxCount");

            List<MaintenancePoint2> representatives = samples
                .GroupBy(x => SpatialBucketKey(x, bucketSizeMm), StringComparer.Ordinal)
                .Select(x => x
                    .OrderBy(y => y.DistanceTo(anchor))
                    .ThenBy(y => y.X)
                    .ThenBy(y => y.Y)
                    .First())
                .OrderBy(x => x.DistanceTo(anchor))
                .ThenBy(x => x.X)
                .ThenBy(x => x.Y)
                .ToList();
            deduplicatedCount = representatives.Count;
            List<MaintenancePoint2> retained = representatives.Take(maxCount).ToList();
            omittedCount = Math.Max(0, deduplicatedCount - retained.Count);
            return retained;
        }

        internal static string ComputeFingerprint(MaintenanceAnalysisResult result)
        {
            if (result == null) throw new ArgumentNullException("result");
            var signatures = new List<string>
            {
                "JarviTools.MaintenanceCandidateAudit.v8",
                "doorWidthMm=" + result.DoorWidthMm.ToString("0.###", CultureInfo.InvariantCulture),
                "doorHeightMm=" + result.DoorHeightMm.ToString("0.###", CultureInfo.InvariantCulture),
                "ceilingHatchSizeMm=" + result.CeilingHatchSizeMm.ToString(
                    "0.###", CultureInfo.InvariantCulture),
                "sharedCeilingEntryReview=" +
                    (result.SharedCeilingEntryReview ? "true" : "false"),
                "sharedCeilingEntryPolicy=" +
                    MaintenanceSharedCeilingEntryPolicy.PolicyVersion,
                "turnZonePolicy=" + MaintenanceTurnZonePolicy.PolicyVersion,
                "turnZoneFull700Mm=" + MaintenanceTurnZonePolicy
                    .GetValidationWidthMm(MaintenanceAccessProfile.Full700)
                    .ToString("0.###", CultureInfo.InvariantCulture),
                "turnZoneLimited600Mm=" + MaintenanceTurnZonePolicy
                    .GetValidationWidthMm(MaintenanceAccessProfile.Limited600)
                    .ToString("0.###", CultureInfo.InvariantCulture),
                "doorSwingPolicy=" + MaintenanceDoorSwingPolicy.PolicyVersion,
                "openingHostWallPolicy=" + MaintenanceOpeningHostWallPolicy.PolicyVersion,
                result.CandidateAuditComplete ? "complete" : "incomplete",
                result.CandidateAuditScopeDefinition ?? string.Empty,
                result.CandidateAuditScopeDescription ?? string.Empty,
                result.CandidateAuditRoutePolicy ?? string.Empty,
                result.CandidateAuditSelectionPolicy ?? string.Empty,
                result.CandidateAuditDisplayRankingPolicy ?? string.Empty,
                result.CandidateAuditAllPathsEnumerated ? "all_paths" : "representative_paths",
                MaintenanceLinkScopePolicy.BuildSignature(result.LinkScope)
            };
            foreach (MaintenanceCandidateSearchStats coverage in result.CandidateSearchStats
                .Where(x => x != null)
                .OrderBy(x => x.GroupKey, StringComparer.Ordinal)
                .ThenBy(x => x.TargetKey, StringComparer.Ordinal)
                .ThenBy(x => x.Profile)
                .ThenBy(x => x.EntryType))
            {
                signatures.Add(string.Join("|", new[]
                {
                    "coverage",
                    coverage.GroupKey ?? string.Empty,
                    coverage.TargetKey ?? string.Empty,
                    coverage.Profile.ToString(),
                    coverage.EntryType.ToString(),
                    coverage.RawSampleCount.ToString(CultureInfo.InvariantCulture),
                    coverage.EligibleSampleCount.ToString(CultureInfo.InvariantCulture),
                    coverage.DeduplicatedCount.ToString(CultureInfo.InvariantCulture),
                    coverage.RetainedCount.ToString(CultureInfo.InvariantCulture),
                    coverage.OmittedCount.ToString(CultureInfo.InvariantCulture),
                    coverage.Truncated ? "truncated" : "complete",
                    coverage.RepresentativeSpacingMm.ToString("0.0", CultureInfo.InvariantCulture),
                    coverage.AlgorithmVersion ?? string.Empty
                }));
            }
            foreach (MaintenanceCandidateEvaluation item in result.CandidateEvaluations
                .Where(x => x != null)
                .OrderBy(x => x.EvaluationKey, StringComparer.Ordinal))
            {
                signatures.Add(string.Join("|", new[]
                {
                    item.EvaluationKey ?? string.Empty,
                    item.Status.ToString(),
                    item.Stage.ToString(),
                    item.IsSelected ? "selected" : "not_selected",
                    item.ReasonCode ?? string.Empty,
                    item.DominatedByCandidateKey ?? string.Empty,
                    item.DominatedByEvaluationKey ?? string.Empty,
                    item.CoveredTargetCount.ToString(CultureInfo.InvariantCulture),
                    item.SourceSampleCount.ToString(CultureInfo.InvariantCulture),
                    Math.Round(item.OpeningWidthMm, 1)
                        .ToString("0.0", CultureInfo.InvariantCulture),
                    Math.Round(item.OpeningHeightMm, 1)
                        .ToString("0.0", CultureInfo.InvariantCulture),
                    item.DoorHingeSide.ToString(),
                    item.LeftDoorSwingStatus.ToString(),
                    item.RightDoorSwingStatus.ToString(),
                    string.Join(",", item.OpeningHostSourceKeys
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(x => x, StringComparer.Ordinal)),
                    string.Join(",", item.LeftDoorSwingBlockers
                        .Where(x => x != null)
                        .Select(x => x.GetStableKey())
                        .OrderBy(x => x, StringComparer.Ordinal)),
                    string.Join(",", item.RightDoorSwingBlockers
                        .Where(x => x != null)
                        .Select(x => x.GetStableKey())
                        .OrderBy(x => x, StringComparer.Ordinal)),
                    double.IsNaN(item.LadderFloorMm) || double.IsInfinity(item.LadderFloorMm)
                        ? "ladder_floor_missing"
                        : Math.Round(item.LadderFloorMm, 1)
                            .ToString("0.0", CultureInfo.InvariantCulture),
                    string.Join(",", item.LadderSupportSourceKeys
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(x => x, StringComparer.Ordinal)),
                    Math.Round(item.RouteLengthMm, 1).ToString("0.0", CultureInfo.InvariantCulture),
                    string.Join(",", item.Route.Select(PointSignature)),
                    string.Join(",", item.Blockers
                        .Where(x => x != null)
                        .Select(x => x.GetStableKey())
                        .OrderBy(x => x, StringComparer.Ordinal))
                }));
            }
            foreach (MaintenancePipeExemptionEvidence evidence in result.ExemptPipeEvidence
                .Where(x => x != null && x.Element != null)
                .OrderBy(x => x.GroupKey, StringComparer.Ordinal)
                .ThenBy(x => x.TargetKey, StringComparer.Ordinal)
                .ThenBy(x => x.Element.GetStableKey(), StringComparer.Ordinal))
            {
                signatures.Add(string.Join("|", new[]
                {
                    "pipe_exemption",
                    MaintenancePipeExemptionPolicy.PolicyVersion,
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
                }));
            }
            byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", signatures));
            using (SHA256 algorithm = SHA256.Create())
            {
                return string.Concat(algorithm.ComputeHash(bytes)
                    .Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        internal static List<MaintenanceCandidateEvaluation> OrderForReporting(
            IEnumerable<MaintenanceCandidateEvaluation> evaluations)
        {
            if (evaluations == null) return new List<MaintenanceCandidateEvaluation>();
            return evaluations
                .Where(x => x != null)
                .OrderByDescending(x => x.IsSelected)
                .ThenBy(x => StatusOrder(x.Status))
                .ThenBy(x => x.Profile == MaintenanceAccessProfile.Full700 ? 0 : 1)
                .ThenBy(x => x.EntryType == MaintenanceEntryType.WallDoor ? 0 : 1)
                .ThenBy(x => x.LadderType == MaintenanceLadderType.AFrame ? 0 : 1)
                .ThenBy(x => x.RouteLengthMm <= 0.0 ? double.MaxValue : x.RouteLengthMm)
                .ThenBy(x => x.CandidateKey, StringComparer.Ordinal)
                .ThenBy(x => x.EvaluationKey, StringComparer.Ordinal)
                .ToList();
        }

        private static int StatusOrder(MaintenanceCandidateStatus status)
        {
            switch (status)
            {
                case MaintenanceCandidateStatus.Feasible: return 0;
                case MaintenanceCandidateStatus.Unverified: return 1;
                default: return 2;
            }
        }

        private static void ApplyEntryEvidenceToRoutes(
            IEnumerable<MaintenanceCandidateEvaluation> evaluations)
        {
            List<MaintenanceCandidateEvaluation> rows = evaluations
                .Where(x => x != null)
                .ToList();
            foreach (MaintenanceCandidateEvaluation route in rows.Where(x =>
                x.Scope == MaintenanceCandidateScope.Route &&
                !string.IsNullOrWhiteSpace(x.CandidateKey)))
            {
                MaintenanceCandidateEvaluation entry = rows
                    .Where(x => x.Scope == MaintenanceCandidateScope.Entry &&
                                x.Profile == route.Profile &&
                                string.Equals(x.GroupKey, route.GroupKey, StringComparison.Ordinal) &&
                                string.Equals(x.CandidateKey, route.CandidateKey, StringComparison.Ordinal) &&
                                (route.EntryType == MaintenanceEntryType.CeilingHatch ||
                                 string.IsNullOrWhiteSpace(x.TargetKey) ||
                                 string.Equals(x.TargetKey, route.TargetKey, StringComparison.Ordinal)) &&
                                x.Status != MaintenanceCandidateStatus.Feasible)
                    .OrderBy(x => x.Status == MaintenanceCandidateStatus.Rejected ? 0 : 1)
                    .ThenBy(x => x.Stage)
                    .FirstOrDefault();
                if (entry == null) continue;
                if (route.Status == MaintenanceCandidateStatus.Rejected)
                {
                    if (entry.Status == MaintenanceCandidateStatus.Rejected && entry.Stage < route.Stage)
                    {
                        route.Stage = entry.Stage;
                        route.ReasonCode = "entry_audit_" + (entry.ReasonCode ?? string.Empty);
                        route.Reason = "入口审计先于路线阶段失败：" +
                                       (entry.Reason ?? "入口证据未通过。");
                    }
                    else if (entry.Status == MaintenanceCandidateStatus.Unverified)
                    {
                        route.Reason = "入口阶段仍有无法验证的证据；此外，" +
                                       (route.Reason ?? "路线阶段未通过。");
                    }
                    AddMissingBlockers(route.Blockers, entry.Blockers);
                    continue;
                }

                route.Status = entry.Status;
                route.Stage = entry.Stage;
                route.ReasonCode = "entry_audit_" + (entry.ReasonCode ?? string.Empty);
                route.Reason = "路线本身可形成，但入口审计未通过：" +
                               (entry.Reason ?? "入口证据需复核。");
                AddMissingBlockers(route.Blockers, entry.Blockers);
            }
        }

        private static void AddMissingBlockers(
            IList<MaintenanceElementRef> target,
            IEnumerable<MaintenanceElementRef> source)
        {
            if (target == null || source == null) return;
            foreach (MaintenanceElementRef blocker in source.Where(x => x != null))
            {
                string key = blocker.GetStableKey();
                if (!target.Any(x => x != null &&
                    string.Equals(x.GetStableKey(), key, StringComparison.Ordinal)))
                    target.Add(blocker);
            }
        }

        internal static string SpatialBucketKey(MaintenancePoint2 point, double bucketSizeMm)
        {
            long x = (long)Math.Floor(point.X / bucketSizeMm);
            long y = (long)Math.Floor(point.Y / bucketSizeMm);
            return x.ToString(CultureInfo.InvariantCulture) + ":" +
                   y.ToString(CultureInfo.InvariantCulture);
        }

        private static string PointSignature(MaintenancePoint3 point)
        {
            return Math.Round(point.X, 1).ToString("0.0", CultureInfo.InvariantCulture) + "," +
                   Math.Round(point.Y, 1).ToString("0.0", CultureInfo.InvariantCulture) + "," +
                   Math.Round(point.Z, 1).ToString("0.0", CultureInfo.InvariantCulture);
        }
    }
}

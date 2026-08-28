using System;
using System.Collections.Generic;
using System.Linq;
using JarviTools.Commands.MaintenanceReachability;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    internal static class MaintenanceCandidateJson
    {
        internal static JObject BuildPage(
            MaintenanceAnalysisResult result,
            string groupKey,
            string targetKey,
            string scope,
            string status,
            string entryType,
            bool selectedOnly,
            bool includeRoutePoints,
            int limit,
            int offset)
        {
            if (result == null) throw new ArgumentNullException("result");
            if (!result.CandidateAuditEnabled)
                throw new InvalidOperationException(
                    "当前快照未保留候选台账。请先调用 analyze_maintenance_route_candidates。");
            if (limit < 1 || limit > 50) throw new ArgumentOutOfRangeException("limit");
            if (offset < 0) throw new ArgumentOutOfRangeException("offset");

            List<MaintenanceCandidateEvaluation> analysisRouteRows = result.CandidateEvaluations
                .Where(x => x != null && x.Scope == MaintenanceCandidateScope.Route)
                .ToList();
            IEnumerable<MaintenanceCandidateEvaluation> filteredRouteRows = analysisRouteRows;
            if (!string.IsNullOrWhiteSpace(groupKey))
                filteredRouteRows = filteredRouteRows.Where(x =>
                    string.Equals(x.GroupKey, groupKey.Trim(), StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(targetKey))
                filteredRouteRows = filteredRouteRows.Where(x =>
                    string.Equals(x.TargetKey, targetKey.Trim(), StringComparison.Ordinal));
            List<MaintenanceCandidateEvaluation> selectionHealthRows = filteredRouteRows.ToList();

            IEnumerable<MaintenanceCandidateEvaluation> query = result.CandidateEvaluations
                .Where(x => x != null);
            if (!string.IsNullOrWhiteSpace(groupKey))
                query = query.Where(x => string.Equals(x.GroupKey, groupKey.Trim(), StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(targetKey))
                query = query.Where(x => string.Equals(x.TargetKey, targetKey.Trim(), StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(scope))
            {
                MaintenanceCandidateScope value;
                if (!Enum.TryParse(scope, true, out value))
                    throw new ArgumentException("scope must be Entry or Route.");
                query = query.Where(x => x.Scope == value);
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                MaintenanceCandidateStatus value;
                if (!Enum.TryParse(status, true, out value))
                    throw new ArgumentException("status must be Rejected, Unverified or Feasible.");
                query = query.Where(x => x.Status == value);
            }
            if (!string.IsNullOrWhiteSpace(entryType))
            {
                MaintenanceEntryType value;
                if (!Enum.TryParse(entryType, true, out value))
                    throw new ArgumentException("entryType must be WallDoor or CeilingHatch.");
                query = query.Where(x => x.EntryType == value);
            }
            if (selectedOnly) query = query.Where(x => x.IsSelected);

            List<MaintenanceCandidateEvaluation> all = query
                .OrderBy(x => x.GroupKey, StringComparer.Ordinal)
                .ThenBy(x => x.TargetKey, StringComparer.Ordinal)
                .ThenBy(x => x.Scope == MaintenanceCandidateScope.Route ? 0 : 1)
                .ThenByDescending(x => x.IsSelected)
                .ThenBy(x => x.Rank <= 0 ? int.MaxValue : x.Rank)
                .ThenBy(x => x.CandidateKey, StringComparer.Ordinal)
                .ThenBy(x => x.EvaluationKey, StringComparer.Ordinal)
                .ToList();
            List<MaintenanceCandidateEvaluation> page = all.Skip(offset).Take(limit).ToList();
            bool pageHasMore = offset + page.Count < all.Count;
            IEnumerable<MaintenanceCandidateSearchStats> coverageQuery = result.CandidateSearchStats;
            if (!string.IsNullOrWhiteSpace(groupKey))
                coverageQuery = coverageQuery.Where(x =>
                    string.Equals(x.GroupKey, groupKey.Trim(), StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(targetKey))
                coverageQuery = coverageQuery.Where(x =>
                    string.IsNullOrWhiteSpace(x.TargetKey) ||
                    string.Equals(x.TargetKey, targetKey.Trim(), StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(entryType))
                coverageQuery = coverageQuery.Where(x =>
                    string.Equals(x.EntryType.ToString(), entryType.Trim(), StringComparison.OrdinalIgnoreCase));
            List<MaintenanceCandidateSearchStats> coverage = coverageQuery.ToList();

            return new JObject
            {
                ["analysisId"] = result.AnalysisId,
                ["evidenceScopeDefinition"] = result.EvidenceScopeDefinition,
                ["scopeDefinition"] = result.CandidateAuditScopeDefinition,
                ["scopeDescription"] = result.CandidateAuditScopeDescription,
                ["allPathsEnumerated"] = result.CandidateAuditAllPathsEnumerated,
                ["routePolicy"] = result.CandidateAuditRoutePolicy,
                ["selectionPolicy"] = result.CandidateAuditSelectionPolicy,
                ["displayRankingPolicy"] = result.CandidateAuditDisplayRankingPolicy,
                ["candidateAuditFingerprint"] = result.CandidateAuditFingerprint,
                ["doorWidthMm"] = result.DoorWidthMm,
                ["doorHeightMm"] = result.DoorHeightMm,
                ["auditComplete"] = result.CandidateAuditComplete,
                ["auditStrategy"] = result.CandidateAuditStrategy,
                ["analysisInvalidatedSelectedCount"] = analysisRouteRows.Count(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Rejected),
                ["analysisUnverifiedSelectedCount"] = analysisRouteRows.Count(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Unverified),
                ["analysisRequiresReselection"] = analysisRouteRows.Any(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Rejected),
                ["analysisRequiresSelectedReview"] = analysisRouteRows.Any(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Unverified),
                ["filteredInvalidatedSelectedCount"] = selectionHealthRows.Count(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Rejected),
                ["filteredUnverifiedSelectedCount"] = selectionHealthRows.Count(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Unverified),
                ["filteredRequiresReselection"] = selectionHealthRows.Any(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Rejected),
                ["filteredRequiresSelectedReview"] = selectionHealthRows.Any(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Unverified),
                ["invalidatedSelectedCount"] = selectionHealthRows.Count(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Rejected),
                ["unverifiedSelectedCount"] = selectionHealthRows.Count(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Unverified),
                ["requiresReselection"] = selectionHealthRows.Any(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Rejected),
                ["requiresSelectedReview"] = selectionHealthRows.Any(x =>
                    x.IsSelected && x.Status == MaintenanceCandidateStatus.Unverified),
                ["rawSampleCount"] = coverage.Sum(x => x.RawSampleCount),
                ["eligibleSampleCount"] = coverage.Sum(x => x.EligibleSampleCount),
                ["deduplicatedCount"] = coverage.Sum(x => x.DeduplicatedCount),
                ["retainedCount"] = coverage.Sum(x => x.RetainedCount),
                ["omittedCount"] = coverage.Sum(x => x.OmittedCount),
                ["truncated"] = coverage.Any(x => x.Truncated),
                ["presentationHint"] = "坐标、构件键和内部候选键用于 AI 追溯，不应直接放进项目经理 PPT。",
                ["total"] = all.Count,
                ["returned"] = page.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["pageHasMore"] = pageHasMore,
                ["nextOffset"] = pageHasMore ? (JToken)(offset + page.Count) : JValue.CreateNull(),
                ["routePointsIncluded"] = includeRoutePoints,
                ["exemptPipeEvidence"] = BuildExemptPipeEvidence(
                    result,
                    groupKey,
                    targetKey),
                ["statusCounts"] = JObject.FromObject(all
                    .GroupBy(x => x.Status.ToString())
                    .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal)),
                ["stageCounts"] = JObject.FromObject(all
                    .GroupBy(x => x.Stage.ToString())
                    .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal)),
                ["candidates"] = new JArray(page.Select(x => BuildCandidate(x, includeRoutePoints)))
            };
        }

        internal static JArray BuildSearchStats(MaintenanceAnalysisResult result)
        {
            return new JArray(result.CandidateSearchStats.Select(x => new JObject
            {
                ["group"] = x.GroupKey,
                ["targetKey"] = string.IsNullOrWhiteSpace(x.TargetKey) ? null : x.TargetKey,
                ["profile"] = x.Profile.ToString(),
                ["entryType"] = x.EntryType.ToString(),
                ["rawSampleCount"] = x.RawSampleCount,
                ["eligibleSampleCount"] = x.EligibleSampleCount,
                ["deduplicatedCount"] = x.DeduplicatedCount,
                ["retainedCount"] = x.RetainedCount,
                ["omittedCount"] = x.OmittedCount,
                ["truncated"] = x.Truncated,
                ["representativeSpacingMm"] = Math.Round(x.RepresentativeSpacingMm, 1),
                ["allPathsEnumerated"] = x.AllPathsEnumerated,
                ["algorithmVersion"] = x.AlgorithmVersion,
                ["sampled"] = x.SampledCount,
                ["retainedEntries"] = x.RetainedEntryCount,
                ["evaluatedRoutes"] = x.EvaluatedRouteCount,
                ["rejected"] = x.RejectedCount,
                ["unverified"] = x.UnverifiedCount,
                ["feasible"] = x.FeasibleCount,
                ["selected"] = x.SelectedCount,
                ["complete"] = x.Complete,
                ["strategy"] = x.Strategy
            }));
        }

        internal static JArray BuildExemptPipeEvidence(
            MaintenanceAnalysisResult result,
            string groupKey,
            string targetKey)
        {
            if (result == null) return new JArray();
            IEnumerable<MaintenancePipeExemptionEvidence> query = result.ExemptPipeEvidence
                .Where(x => x != null && x.Element != null);
            if (!string.IsNullOrWhiteSpace(groupKey))
                query = query.Where(x => string.Equals(
                    x.GroupKey,
                    groupKey.Trim(),
                    StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(targetKey))
                query = query.Where(x => string.Equals(
                    x.TargetKey,
                    targetKey.Trim(),
                    StringComparison.Ordinal));
            return new JArray(query
                .OrderBy(x => x.GroupKey, StringComparer.Ordinal)
                .ThenBy(x => x.TargetKey, StringComparer.Ordinal)
                .ThenBy(x => x.Element.GetStableKey(), StringComparer.Ordinal)
                .Select(x => new JObject
                {
                    ["group"] = x.GroupKey,
                    ["targetKey"] = x.TargetKey,
                    ["key"] = x.Element.GetStableKey(),
                    ["elementId"] = x.Element.ElementId,
                    ["linkInstanceId"] = x.Element.LinkInstanceId.HasValue
                        ? (JToken)x.Element.LinkInstanceId.Value
                        : JValue.CreateNull(),
                    ["uniqueId"] = x.Element.UniqueId,
                    ["documentTitle"] = x.Element.DocumentTitle,
                    ["category"] = x.Element.Category,
                    ["name"] = x.Element.Name,
                    ["categoryKind"] = x.CategoryKind,
                    ["systemKind"] = x.SystemKind,
                    ["systemTypeEvidence"] = x.SystemTypeEvidence,
                    ["systemEvidenceSource"] = x.SystemEvidenceSource,
                    ["reasonCode"] = x.ReasonCode,
                    ["reason"] = x.Reason,
                    ["distanceMm"] = Math.Round(x.DistanceMm, 1),
                    ["lengthMm"] = Math.Round(x.LengthMm, 1),
                    ["diameterMm"] = Math.Round(x.DiameterMm, 1)
                }));
        }

        private static JObject BuildCandidate(
            MaintenanceCandidateEvaluation item,
            bool includeRoutePoints)
        {
            MaintenanceElementRef primaryBlocker = item.Blockers.FirstOrDefault();
            var output = new JObject
            {
                ["evaluationKey"] = item.EvaluationKey,
                ["candidateKey"] = item.CandidateKey,
                ["scope"] = item.Scope.ToString(),
                ["group"] = item.GroupKey,
                ["targetKey"] = string.IsNullOrWhiteSpace(item.TargetKey) ? null : item.TargetKey,
                ["status"] = item.Status.ToString(),
                ["selected"] = item.IsSelected,
                ["selectionStatus"] = item.Scope == MaintenanceCandidateScope.Entry
                    ? "not_applicable"
                    : (item.IsSelected
                        ? "selected"
                        : (item.Status != MaintenanceCandidateStatus.Rejected
                            ? "eligible_not_selected"
                            : "rejected")),
                ["rank"] = item.Rank <= 0 ? null : (JToken)item.Rank,
                ["reportRank"] = item.Rank <= 0 ? null : (JToken)item.Rank,
                ["profile"] = item.Profile.ToString(),
                ["entryType"] = item.EntryType.ToString(),
                ["ladderType"] = item.LadderType.ToString(),
                ["ladderFloorMm"] = double.IsNaN(item.LadderFloorMm) ||
                                    double.IsInfinity(item.LadderFloorMm)
                    ? JValue.CreateNull()
                    : (JToken)Math.Round(item.LadderFloorMm, 1),
                ["ladderSupportSourceKeys"] = new JArray(
                    item.LadderSupportSourceKeys
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(x => x, StringComparer.Ordinal)),
                ["openingHostSourceKeys"] = new JArray(
                    item.OpeningHostSourceKeys
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(x => x, StringComparer.Ordinal)),
                ["stage"] = item.Stage.ToString(),
                ["reasonCode"] = item.ReasonCode,
                ["reason"] = item.Reason,
                ["selectionReason"] = string.IsNullOrWhiteSpace(item.SelectionReason)
                    ? null
                    : item.SelectionReason,
                ["dominatedByCandidateKey"] = string.IsNullOrWhiteSpace(item.DominatedByCandidateKey)
                    ? null
                    : item.DominatedByCandidateKey,
                ["dominatedByEvaluationKey"] = string.IsNullOrWhiteSpace(item.DominatedByEvaluationKey)
                    ? null
                    : item.DominatedByEvaluationKey,
                ["sourceSampleCount"] = item.SourceSampleCount,
                ["entryCenterMm"] = new JObject
                {
                    ["x"] = Math.Round(item.EntryCenter.X, 1),
                    ["y"] = Math.Round(item.EntryCenter.Y, 1),
                    ["z"] = Math.Round(item.EntryCenter.Z, 1)
                },
                ["openingWidthMm"] = Math.Round(item.OpeningWidthMm, 1),
                ["openingHeightMm"] = Math.Round(item.OpeningHeightMm, 1),
                ["doorHingeSide"] = item.DoorHingeSide == MaintenanceDoorHingeSide.None
                    ? null
                    : item.DoorHingeSide.ToString(),
                ["leftOutwardSwingStatus"] = item.LeftDoorSwingStatus.ToString(),
                ["rightOutwardSwingStatus"] = item.RightDoorSwingStatus.ToString(),
                ["leftOutwardSwingBlockers"] = new JArray(
                    item.LeftDoorSwingBlockers.Select(x => new JObject
                    {
                        ["key"] = x.GetStableKey(),
                        ["uniqueId"] = x.UniqueId,
                        ["category"] = x.Category,
                        ["name"] = x.Name
                    })),
                ["rightOutwardSwingBlockers"] = new JArray(
                    item.RightDoorSwingBlockers.Select(x => new JObject
                    {
                        ["key"] = x.GetStableKey(),
                        ["uniqueId"] = x.UniqueId,
                        ["category"] = x.Category,
                        ["name"] = x.Name
                    })),
                ["boundaryLoopIndex"] = item.BoundaryLoopIndex,
                ["boundarySegmentIndex"] = item.BoundarySegmentIndex,
                ["coveredTargetCount"] = item.CoveredTargetCount,
                ["hasRoute"] = item.Route.Count > 0,
                ["routeLengthMm"] = Math.Round(item.RouteLengthMm, 1),
                ["routeTurnCount"] = Math.Max(0, item.Route.Count - 2),
                ["primaryBlocker"] = primaryBlocker == null ? null : new JObject
                {
                    ["key"] = primaryBlocker.GetStableKey(),
                    ["uniqueId"] = primaryBlocker.UniqueId,
                    ["category"] = primaryBlocker.Category,
                    ["name"] = primaryBlocker.Name
                },
                ["blockers"] = new JArray(item.Blockers.Select(x => new JObject
                {
                    ["key"] = x.GetStableKey(),
                    ["uniqueId"] = x.UniqueId,
                    ["category"] = x.Category,
                    ["name"] = x.Name
                }))
            };
            if (includeRoutePoints)
                output["routePointsMm"] = new JArray(item.Route.Select(x => new JObject
                {
                    ["x"] = Math.Round(x.X, 1),
                    ["y"] = Math.Round(x.Y, 1),
                    ["z"] = Math.Round(x.Z, 1)
                }));
            return output;
        }
    }
}

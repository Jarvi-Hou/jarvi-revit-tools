using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace JarviTools.Commands.MaintenanceReachability
{
    internal enum MaintenanceManagedViewPurpose
    {
        FormalReachability = 1,
        AiInternalAnalysis = 2
    }

    internal static class MaintenanceRouteEvidenceCoveragePolicy
    {
        internal const string ScopeDefinition =
            "route_plenum_whitelist_plus_OST_Walls_host_and_loaded_links";

        internal static bool IsComplete(
            bool wallCollectionAttempted,
            int wallCollectionFailureCount)
        {
            return wallCollectionAttempted && wallCollectionFailureCount == 0;
        }
    }

    internal static class MaintenanceManualStatePolicy
    {
        internal static bool ShouldInheritConclusion(
            string savedEvidenceFingerprint,
            string currentEvidenceFingerprint,
            string savedDecisionReason,
            string currentDecisionReason)
        {
            return !string.IsNullOrWhiteSpace(savedEvidenceFingerprint) &&
                   !string.IsNullOrWhiteSpace(currentEvidenceFingerprint) &&
                   string.Equals(
                       savedEvidenceFingerprint.Trim(),
                       currentEvidenceFingerprint.Trim(),
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       (savedDecisionReason ?? string.Empty).Trim(),
                       (currentDecisionReason ?? string.Empty).Trim(),
                       StringComparison.Ordinal);
        }

        internal static string ResolveProfessionalNote(
            string generatedNote,
            string savedProfessionalNote)
        {
            return string.IsNullOrWhiteSpace(savedProfessionalNote)
                ? generatedNote
                : savedProfessionalNote;
        }
    }

    internal static class MaintenanceLegacySchemeViewPolicy
    {
        internal static List<int> ResolveManagedSchemes(
            int currentSchemeNo,
            IEnumerable<int> legacySchemeNos,
            bool includeLegacy)
        {
            IEnumerable<int> schemes = new[] { currentSchemeNo };
            if (includeLegacy)
                schemes = schemes.Concat(legacySchemeNos ?? Enumerable.Empty<int>());
            return schemes
                .Where(x => x > 0)
                .Distinct()
                .OrderBy(x => x)
                .ToList();
        }
    }

    /// <summary>
    /// Revit-free policy for selecting and validating a retained side-wall option.
    /// Keeping these rules pure makes the fail-closed geometry contract testable.
    /// </summary>
    internal static class MaintenanceWallAlternativePolicy
    {
        internal static MaintenanceWallAlternativeResult SelectPreferred(
            IEnumerable<MaintenanceWallAlternativeResult> candidates)
        {
            return (candidates ?? Enumerable.Empty<MaintenanceWallAlternativeResult>())
                .Where(x => x != null)
                .Where(x => x.Status == MaintenanceWallAlternativeStatus.Available ||
                            x.Status == MaintenanceWallAlternativeStatus.AvailablePendingReview)
                .OrderBy(x => StatusRank(x.Status))
                .ThenBy(x => x.Profile == MaintenanceAccessProfile.Full700 ? 0 : 1)
                .ThenBy(x => x.RouteLengthMm <= 0.0 ? double.MaxValue : x.RouteLengthMm)
                .ThenBy(x => x.SelectedEntry == null ? string.Empty : x.SelectedEntry.CandidateKey,
                    StringComparer.Ordinal)
                .FirstOrDefault();
        }

        internal static bool IsRenderGeometryComplete(
            IEnumerable<MaintenanceRenderItem> renderItems)
        {
            List<MaintenanceRenderItem> items = (renderItems ??
                Enumerable.Empty<MaintenanceRenderItem>()).Where(x => x != null).ToList();
            if (items.Count == 0) return false;
            var roles = new HashSet<MaintenanceComponentRole>(items.Select(x => x.Role));
            if (!roles.Contains(MaintenanceComponentRole.WallDoor) ||
                (!roles.Contains(MaintenanceComponentRole.AFrameLadder) &&
                 !roles.Contains(MaintenanceComponentRole.StraightLadder)) ||
                !roles.Contains(MaintenanceComponentRole.EntryTurnZone) ||
                !roles.Contains(MaintenanceComponentRole.AccessRoute) ||
                !roles.Contains(MaintenanceComponentRole.HumanEnvelope) ||
                !roles.Contains(MaintenanceComponentRole.ServicePocket) ||
                !roles.Contains(MaintenanceComponentRole.TargetEquipment) ||
                !roles.Contains(MaintenanceComponentRole.VirtualBoundaryWall))
                return false;
            foreach (MaintenanceRenderItem item in items)
            {
                if (!IsFinite(item.Center.X) || !IsFinite(item.Center.Y) ||
                    !IsFinite(item.Center.Z) || !IsFinite(item.WidthMm) ||
                    !IsFinite(item.DepthMm) || !IsFinite(item.HeightMm))
                    return false;

                if (item.Role == MaintenanceComponentRole.AFrameLadder ||
                    item.Role == MaintenanceComponentRole.StraightLadder ||
                    item.Role == MaintenanceComponentRole.AccessRoute ||
                    item.Role == MaintenanceComponentRole.HumanEnvelope)
                {
                    if (item.Points.Count < 2 || item.WidthMm <= 0.0 || item.HeightMm <= 0.0)
                        return false;
                    continue;
                }

                if (item.GeometryType == MaintenanceRenderGeometryType.ExtrudedPolygon)
                {
                    if (DistinctPointCount(item.Points) < 3 || item.HeightMm <= 0.0)
                        return false;
                    continue;
                }

                if (item.GeometryType == MaintenanceRenderGeometryType.Polyline)
                {
                    if (item.Points.Count < 2 || item.WidthMm <= 0.0 || item.HeightMm <= 0.0)
                        return false;
                    continue;
                }

                if (item.WidthMm <= 0.0 || item.DepthMm <= 0.0 || item.HeightMm <= 0.0)
                    return false;
            }
            return true;
        }

        internal static string ComputeFingerprint(
            IEnumerable<MaintenanceWallAlternativeResult> alternatives)
        {
            var builder = new StringBuilder();
            foreach (MaintenanceWallAlternativeResult alternative in
                (alternatives ?? Enumerable.Empty<MaintenanceWallAlternativeResult>())
                    .Where(x => x != null)
                    .OrderBy(x => x.GroupKey, StringComparer.Ordinal)
                    .ThenBy(x => x.DeviceNo, StringComparer.Ordinal)
                    .ThenBy(x => x.TargetKey, StringComparer.Ordinal))
            {
                Append(builder, alternative.AlternativeKey);
                Append(builder, alternative.GroupKey);
                Append(builder, alternative.TargetKey);
                Append(builder, alternative.DeviceNo);
                Append(builder, alternative.SchemeNo.ToString(CultureInfo.InvariantCulture));
                Append(builder, alternative.Status.ToString());
                Append(builder, alternative.CanVisualize ? "1" : "0");
                Append(builder, alternative.SameAsRouteFormal ? "1" : "0");
                Append(builder, alternative.Profile.ToString());
                Append(builder, alternative.EntryType.ToString());
                Append(builder, alternative.LadderType.ToString());
                Append(builder, alternative.Decision.ToString());
                Append(builder, Round(alternative.RouteLengthMm));
                Append(builder, alternative.SelectedEntry == null
                    ? string.Empty
                    : alternative.SelectedEntry.CandidateKey);
                Append(builder, alternative.SelectedEntry == null
                    ? string.Empty
                    : Round(alternative.SelectedEntry.OpeningWidthMm));
                Append(builder, alternative.SelectedEntry == null
                    ? string.Empty
                    : Round(alternative.SelectedEntry.OpeningHeightMm));
                foreach (MaintenanceRenderItem item in alternative.RenderItems
                    .Where(x => x != null)
                    .OrderBy(x => x.RenderKey, StringComparer.Ordinal))
                {
                    Append(builder, item.RenderKey);
                    Append(builder, ((int)item.GeometryType).ToString(CultureInfo.InvariantCulture));
                    Append(builder, ((int)item.Role).ToString(CultureInfo.InvariantCulture));
                    Append(builder, Round(item.Center.X));
                    Append(builder, Round(item.Center.Y));
                    Append(builder, Round(item.Center.Z));
                    Append(builder, Round(item.WidthMm));
                    Append(builder, Round(item.DepthMm));
                    Append(builder, Round(item.HeightMm));
                    foreach (MaintenancePoint3 point in item.Points)
                    {
                        Append(builder, Round(point.X));
                        Append(builder, Round(point.Y));
                        Append(builder, Round(point.Z));
                    }
                }
            }
            byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty)
                    .ToLowerInvariant();
        }

        private static int StatusRank(MaintenanceWallAlternativeStatus status)
        {
            switch (status)
            {
                case MaintenanceWallAlternativeStatus.Available: return 0;
                case MaintenanceWallAlternativeStatus.AvailablePendingReview: return 1;
                default: return 2;
            }
        }

        private static int DistinctPointCount(IEnumerable<MaintenancePoint3> points)
        {
            return (points ?? Enumerable.Empty<MaintenancePoint3>())
                .Select(x => Round(x.X) + "|" + Round(x.Y) + "|" + Round(x.Z))
                .Distinct(StringComparer.Ordinal)
                .Count();
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string Round(double value)
        {
            return Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);
        }

        private static void Append(StringBuilder builder, string value)
        {
            builder.Append(value ?? string.Empty).Append('\n');
        }
    }

    internal static class MaintenanceManagedViewPolicy
    {
        internal const string FormalManagedViewOwnerId =
            "JarviTools.MaintenanceHandReach.View.v1";
        internal const string FormalReachabilityTypeName =
            "三维-空间可达性分析";
        internal const string AiInternalAnalysisTypeName =
            "三维-AI内部分析";

        internal static string ResolveViewFamilyTypeName(
            MaintenanceManagedViewPurpose purpose)
        {
            switch (purpose)
            {
                case MaintenanceManagedViewPurpose.FormalReachability:
                    return FormalReachabilityTypeName;
                case MaintenanceManagedViewPurpose.AiInternalAnalysis:
                    return AiInternalAnalysisTypeName;
                default:
                    throw new ArgumentOutOfRangeException("purpose");
            }
        }

        internal static bool IsExactOwner(
            string expectedOwner,
            string expectedIdentity,
            string actualOwner,
            string actualIdentity)
        {
            return !string.IsNullOrWhiteSpace(expectedOwner) &&
                   !string.IsNullOrWhiteSpace(expectedIdentity) &&
                   string.Equals(expectedOwner, actualOwner, StringComparison.Ordinal) &&
                   string.Equals(expectedIdentity, actualIdentity, StringComparison.Ordinal);
        }

        internal static bool IsDedicatedSchemeView(
            string owner,
            string identity)
        {
            string safeOwner = owner ?? string.Empty;
            string safeIdentity = identity ?? string.Empty;
            if (string.Equals(
                    safeOwner,
                    FormalManagedViewOwnerId,
                    StringComparison.Ordinal))
            {
                return safeIdentity.StartsWith(
                           "handreach|",
                           StringComparison.Ordinal) &&
                       safeIdentity.IndexOf(
                           "|Scheme",
                           StringComparison.Ordinal) >= 0;
            }
            if (string.Equals(
                    safeOwner,
                    "JarviTools.MaintenanceWallAlternative.View.v1",
                    StringComparison.Ordinal))
            {
                return safeIdentity.StartsWith(
                    "wall-alternative|",
                    StringComparison.Ordinal);
            }
            return false;
        }

        internal static string BuildAvailableName(
            string desiredName,
            ISet<string> occupiedNames)
        {
            string desired = string.IsNullOrWhiteSpace(desiredName)
                ? "OpenRevit-管理视图"
                : desiredName.Trim();
            if (occupiedNames == null || !occupiedNames.Contains(desired)) return desired;
            for (int index = 1; index <= 999; index++)
            {
                string candidate = desired + " [OpenRevit " + index.ToString("00") + "]";
                if (!occupiedNames.Contains(candidate)) return candidate;
            }
            throw new InvalidOperationException("无法为 OpenRevit 管理视图分配不冲突的名称。");
        }

        internal static string BuildAiAnalysisViewName(string groupKey)
        {
            string group = NormalizeGroupKey(groupKey);
            return string.IsNullOrWhiteSpace(group)
                ? string.Empty
                : "天花" + group + "-维修可达";
        }

        internal static string BuildEquipmentOverviewViewName(string groupKey)
        {
            string group = NormalizeGroupKey(groupKey);
            return string.IsNullOrWhiteSpace(group)
                ? string.Empty
                : "天花" + group + "-设备方案总览";
        }

        internal static string BuildEquipmentOverviewViewIdentity(string groupKey)
        {
            string group = NormalizeIdentityPart(groupKey);
            return string.IsNullOrWhiteSpace(group)
                ? string.Empty
                : "handreach-overview|" + group;
        }

        internal static bool IsFormalMaintenanceApplicationId(
            string applicationId)
        {
            string value = applicationId ?? string.Empty;
            return string.Equals(
                       value,
                       "JarviTools.MaintenanceReachability.v1",
                       StringComparison.Ordinal) ||
                   string.Equals(
                       value,
                       "JarviTools.MaintenanceHandReach.v1",
                       StringComparison.Ordinal) ||
                   string.Equals(
                       value,
                       "JarviTools.MaintenanceWallAlternative.v1",
                       StringComparison.Ordinal);
        }

        internal static string BuildFloorOverviewViewName(string groupKey)
        {
            string floorKey = ResolveFloorKey(groupKey);
            return string.IsNullOrWhiteSpace(floorKey)
                ? string.Empty
                : "楼层" + floorKey + "-整体可达";
        }

        internal static bool GroupBelongsToFloor(string groupKey, string floorKey)
        {
            string resolved = ResolveFloorKey(groupKey);
            return !string.IsNullOrWhiteSpace(resolved) &&
                   string.Equals(resolved, NormalizeFloorKey(floorKey),
                       StringComparison.Ordinal);
        }

        internal static string ResolveFloorKey(string groupKey)
        {
            string group = NormalizeGroupKey(groupKey).ToUpperInvariant();
            if (group.Length == 0) return string.Empty;

            // Basement groups: B1A / B1F -> B1F.
            if (group[0] == 'B')
            {
                int end = 1;
                while (end < group.Length && char.IsDigit(group[end])) end++;
                if (end > 1) return group.Substring(0, end) + "F";
            }

            // Typical annotated ceilings: 6F / 8A / 5B -> 6F / 8F / 5F.
            int digitEnd = 0;
            while (digitEnd < group.Length && char.IsDigit(group[digitEnd])) digitEnd++;
            if (digitEnd > 0) return group.Substring(0, digitEnd) + "F";
            return string.Empty;
        }

        private static string NormalizeGroupKey(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static string NormalizeIdentityPart(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Replace('|', '/')
                    .Replace('\r', ' ')
                    .Replace('\n', ' ')
                    .Trim();
        }

        private static string NormalizeFloorKey(string value)
        {
            string floor = string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();
            if (floor.StartsWith("楼层", StringComparison.Ordinal))
                floor = floor.Substring(2);
            if (floor.EndsWith("-整体可达", StringComparison.Ordinal))
                floor = floor.Substring(0, floor.Length - "-整体可达".Length);
            return floor;
        }
    }

    internal static class MaintenanceDeviceIdentityPolicy
    {
        internal static Dictionary<string, string> ResolveDeviceNumbers(
            IEnumerable<string> orderedStableTargetKeys,
            IDictionary<string, string> existingAssignments,
            IDictionary<string, string> requestedAssignments)
        {
            List<string> keys = (orderedStableTargetKeys ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var output = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (IGrouping<string, string> bucket in keys.GroupBy(
                GetBucket, StringComparer.Ordinal))
            {
                var reserved = new HashSet<string>(
                    (existingAssignments ?? new Dictionary<string, string>())
                        .Where(x => string.Equals(GetBucket(x.Key), bucket.Key,
                            StringComparison.Ordinal))
                        .Select(x => NormalizeDeviceNo(x.Value))
                        .Where(x => !string.IsNullOrWhiteSpace(x)),
                    StringComparer.Ordinal);
                var claimed = new HashSet<string>(StringComparer.Ordinal);
                foreach (string key in bucket)
                {
                    string existing;
                    if (existingAssignments != null &&
                        existingAssignments.TryGetValue(key, out existing))
                    {
                        string normalized = NormalizeDeviceNo(existing);
                        if (!string.IsNullOrWhiteSpace(normalized) && claimed.Add(normalized))
                            output[key] = normalized;
                    }
                }
                foreach (string key in bucket.Where(x => !output.ContainsKey(x)))
                {
                    string requested;
                    string normalized = requestedAssignments != null &&
                                        requestedAssignments.TryGetValue(key, out requested)
                        ? NormalizeDeviceNo(requested)
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(normalized) || reserved.Contains(normalized) ||
                        claimed.Contains(normalized))
                    {
                        int candidate = 1;
                        do { normalized = candidate++.ToString("00", CultureInfo.InvariantCulture); }
                        while (reserved.Contains(normalized) || claimed.Contains(normalized));
                    }
                    claimed.Add(normalized);
                    output[key] = normalized;
                }
            }
            return output;
        }

        internal static string BuildBucketedTargetKey(string groupKey, string stableTargetKey)
        {
            return (groupKey ?? string.Empty) + "\u001f" +
                   (stableTargetKey ?? string.Empty);
        }

        internal static string NormalizeDeviceNo(string value)
        {
            int number;
            string safe = (value ?? string.Empty).Trim();
            return int.TryParse(safe, NumberStyles.Integer, CultureInfo.InvariantCulture,
                       out number) && number >= 0
                ? number.ToString("00", CultureInfo.InvariantCulture)
                : safe;
        }

        private static string GetBucket(string key)
        {
            string safe = key ?? string.Empty;
            int separator = safe.IndexOf('\u001f');
            return separator < 0 ? string.Empty : safe.Substring(0, separator);
        }
    }

    internal static class MaintenanceBooleanIntersectionPolicy
    {
        internal static bool RequiresUnverified(bool booleanResultReturned)
        {
            return !booleanResultReturned;
        }
    }

    internal static class MaintenanceEvidenceCollectionPolicy
    {
        internal static void ApplyFailClosedGate(MaintenanceAnalysisResult result)
        {
            if (result == null || result.EvidenceCollectionComplete) return;
            foreach (MaintenanceTargetResult target in result.TargetResults
                .Where(x => x != null && x.Decision == MaintenanceDecision.Pass))
            {
                target.Decision = MaintenanceDecision.PendingReview;
                target.DecisionReason =
                    "障碍证据采集不完整，原几何通过结果已降级为待确认；排除采集失败并重算前不得判为可维修。";
                foreach (MaintenanceRenderItem item in target.RenderItems.Where(x => x != null))
                {
                    item.Decision = MaintenanceDecision.PendingReview;
                    if (item.Role == MaintenanceComponentRole.ServicePocket)
                    {
                        item.Parameters.MaintenanceConclusion = "待确认";
                        item.Parameters.DecisionReason = target.DecisionReason;
                    }
                }
            }
            foreach (MaintenanceCandidateEvaluation evaluation in
                result.CandidateEvaluations.Where(x => x != null &&
                    x.Status == MaintenanceCandidateStatus.Feasible))
            {
                evaluation.Status = MaintenanceCandidateStatus.Unverified;
                evaluation.ReasonCode = "evidence_collection_incomplete";
                evaluation.Reason =
                    "Obstacle evidence collection is incomplete; feasible status is withheld.";
            }
            foreach (MaintenanceWallAlternativeResult alternative in
                result.WallAlternatives.Where(x => x != null && x.CanVisualize))
            {
                alternative.CanVisualize = false;
                alternative.Status = MaintenanceWallAlternativeStatus
                    .UnavailableEvidenceCollectionIncomplete;
                alternative.Reason =
                    "障碍证据采集不完整，侧墙备选禁止建模；请排除采集失败后重新分析。";
                alternative.RenderItems.Clear();
                alternative.Route.Clear();
                alternative.GeometryFingerprint = string.Empty;
            }
        }
    }

    internal static class MaintenanceTargetIdentityPolicy
    {
        internal static bool IsSameTarget(
            string expectedGroup,
            string expectedTargetHash,
            string expectedMaintenanceTarget,
            string expectedDeviceNo,
            string actualGroup,
            string actualTargetHash,
            string actualMaintenanceTarget,
            string actualDeviceNo,
            bool allowUniqueLegacyPairMatch)
        {
            if (!string.Equals(expectedGroup, actualGroup, StringComparison.Ordinal))
                return false;
            if (!string.IsNullOrWhiteSpace(expectedTargetHash) &&
                !string.IsNullOrWhiteSpace(actualTargetHash))
                return string.Equals(expectedTargetHash, actualTargetHash,
                    StringComparison.Ordinal);
            return allowUniqueLegacyPairMatch &&
                   !string.IsNullOrWhiteSpace(expectedMaintenanceTarget) &&
                   !string.IsNullOrWhiteSpace(actualMaintenanceTarget) &&
                   !string.IsNullOrWhiteSpace(expectedDeviceNo) &&
                   !string.IsNullOrWhiteSpace(actualDeviceNo) &&
                   string.Equals(expectedMaintenanceTarget, actualMaintenanceTarget,
                       StringComparison.Ordinal) &&
                   string.Equals(expectedDeviceNo, actualDeviceNo,
                       StringComparison.Ordinal);
        }
    }

    internal static class MaintenanceApprovalSerialPolicy
    {
        internal static long ResolveReusableOwnVisualizationSerial(
            bool sameSnapshot,
            bool approvalConsumed,
            long currentSerial,
            long visualizedSerial)
        {
            return sameSnapshot && approvalConsumed && currentSerial == visualizedSerial
                ? currentSerial
                : -1L;
        }

        internal static bool IsAllowedOwnVisualizationSerial(
            long currentSerial,
            long allowedSerial)
        {
            return allowedSerial >= 0L && currentSerial == allowedSerial;
        }
    }

    internal static class MaintenanceDirectShapeIdentityPolicy
    {
        internal static string BuildStableBasis(MaintenanceRenderItem item)
        {
            if (item == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(item.RenderKey))
                return item.RenderKey.Trim();
            MaintenanceInstanceParameters values = item.Parameters ??
                new MaintenanceInstanceParameters();
            return string.Join("|", new[]
            {
                values.CeilingGroup ?? string.Empty,
                values.EntryGroup ?? string.Empty,
                ((int)item.Role).ToString(CultureInfo.InvariantCulture),
                values.MaintenanceTarget ?? string.Empty,
                item.TargetKey ?? string.Empty
            });
        }

        internal static string ComputeTargetHash(string stableTargetKey)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(stableTargetKey ?? string.Empty);
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes), 0, 12)
                    .Replace("-", string.Empty).ToLowerInvariant();
        }

        internal static List<string> GetTargetHashes(MaintenanceRenderItem item)
        {
            if (item == null ||
                item.Role == MaintenanceComponentRole.VirtualBoundaryWall)
                return new List<string>();
            return new[] { item.TargetKey }
                .Concat(item.SourceKeys ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(ComputeTargetHash)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
        }

        internal static bool ContainsTargetHash(
            IEnumerable<string> storedHashes,
            string stableTargetKey)
        {
            string expected = ComputeTargetHash(stableTargetKey);
            return (storedHashes ?? Enumerable.Empty<string>())
                .Any(x => string.Equals(x, expected, StringComparison.Ordinal));
        }
    }

    internal static class MaintenanceFormalReusePolicy
    {
        internal static bool ShouldReuse(
            bool sameAsRouteFormal,
            bool matchingFormalShapesExist,
            bool formalRoleSetComplete)
        {
            return sameAsRouteFormal && matchingFormalShapesExist &&
                   formalRoleSetComplete;
        }

        internal static bool MustRejectIncompleteFormal(
            bool sameAsRouteFormal,
            bool matchingFormalShapesExist,
            bool formalRoleSetComplete)
        {
            return sameAsRouteFormal && matchingFormalShapesExist &&
                   !formalRoleSetComplete;
        }

        internal static bool MustRejectPotentialDuplicate(
            bool sameAsRouteFormal,
            bool sameTargetFormalShapesExist,
            bool matchingFormalShapesExist,
            bool matchingFormalRoleSetComplete)
        {
            return sameAsRouteFormal && sameTargetFormalShapesExist &&
                   (!matchingFormalShapesExist ||
                    !matchingFormalRoleSetComplete);
        }
    }
}

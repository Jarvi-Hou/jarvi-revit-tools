using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using JarviTools.Commands.Plenum;

namespace JarviTools.Commands.MaintenanceReachability
{
    /// <summary>
    /// Formal maintenance-access analysis.  The service deliberately keeps the
    /// decision chain together: one selected entry owns its opening, ladder,
    /// turn zone and routes; service pockets are always generated from the same
    /// immutable result used for rendering.
    /// </summary>
    internal static class MaintenanceAnalysisService
    {
        internal static readonly List<string> LastDiagnostics = new List<string>();
        private static readonly Dictionary<int, string> LastBlockedByCell =
            new Dictionary<int, string>();
        private const double MmPerFoot = 304.8;
        private const double GridMm = 40.0;
        private const double FullBodyCeilingHatchSizeMm =
            MaintenanceSharedCeilingEntryPolicy.DefaultHatchSizeMm;
        private const double TurnHeightMm = 800.0;
        private const double PocketWidthMm = 900.0;
        private const double PocketHeightMm = 800.0;
        private const double CollisionLiftMm = 2.0;
        private const double HatchRepresentativeSpacingMm = 400.0;
        private const int GroupClosingRadiusCells = 5;

        private sealed class ProfileSpec
        {
            public MaintenanceAccessProfile Profile;
            public double DiameterMm;
            public double HeightMm;
            public double RadiusMm;
            public double GridSafetyMm;
        }

        private static readonly ProfileSpec Full700 = new ProfileSpec
        {
            Profile = MaintenanceAccessProfile.Full700,
            DiameterMm = 700.0,
            HeightMm = 700.0,
            RadiusMm = 350.0,
            GridSafetyMm = 379.0
        };

        private static readonly ProfileSpec Limited600 = new ProfileSpec
        {
            Profile = MaintenanceAccessProfile.Limited600,
            DiameterMm = 600.0,
            HeightMm = 600.0,
            RadiusMm = 300.0,
            GridSafetyMm = 329.0
        };

        private sealed class Triangle2
        {
            public MaintenancePoint2 A;
            public MaintenancePoint2 B;
            public MaintenancePoint2 C;
        }

        private sealed class Mask
        {
            public double OriginX;
            public double OriginY;
            public int Width;
            public int Height;
            public double Cell;
            public bool[] Filled;

            public bool Inside(int x, int y)
            {
                return x >= 0 && x < Width && y >= 0 && y < Height;
            }

            public bool Get(int x, int y)
            {
                return Inside(x, y) && Filled[y * Width + x];
            }

            public void Set(int x, int y, bool value)
            {
                if (Inside(x, y)) Filled[y * Width + x] = value;
            }

            public MaintenancePoint2 Center(int x, int y)
            {
                return new MaintenancePoint2(
                    OriginX + (x + 0.5) * Cell,
                    OriginY + (y + 0.5) * Cell);
            }

            public bool Contains(MaintenancePoint2 point)
            {
                int x = (int)Math.Floor((point.X - OriginX) / Cell);
                int y = (int)Math.Floor((point.Y - OriginY) / Cell);
                return Get(x, y);
            }
        }

        private sealed class GroupInput
        {
            public string Key;
            public readonly List<Element> Ceilings = new List<Element>();
        }

        private sealed class TargetWork
        {
            public MaintenanceAccessProfile Profile;
            public MaintenanceTarget Target;
            public PlenumAnalysisService.Candidate Candidate;
            public GridCell Goal;
            public bool HasGoal;
            public MaintenanceCollisionResult PocketCollision;
            public bool PocketInside;
            public XYZ Supply;
            public XYZ ServiceSide;
            public XYZ PocketCenterBottom;
            public bool SupplyDirectionInferred;
            public bool EntryGeometryUnverified;
            public bool GridGeometryUnverified;
            public MaintenanceGrid Grid;
            public int[] ComponentLabels;
            public readonly HashSet<string> ExemptSourceKeys =
                new HashSet<string>(StringComparer.Ordinal);
            public readonly List<MaintenanceElementRef> EntryBlockers =
                new List<MaintenanceElementRef>();
        }

        private sealed class EntryWork
        {
            public MaintenanceAccessProfile Profile;
            public MaintenanceEntryCandidate Candidate;
            public GridCell Start;
            public int Component;
            public XYZ WallPoint;
            public XYZ Tangent;
            public XYZ Inward;
            public XYZ LadderPlanCenter;
            public XYZ LadderAlong;
            public double LadderFloorFt = double.NaN;
            public readonly List<string> CoveredTargets = new List<string>();
        }

        private sealed class ChainWork
        {
            public ProfileSpec Spec;
            public MaintenanceGrid Grid;
            public TargetWork Target;
            public EntryWork Entry;
        }

        private sealed class WallAlternativeWork
        {
            public ChainWork Chain;
            public MaintenanceCollisionState RouteState;
            public double RouteLengthMm;
        }

        private sealed class HatchCandidateOutcome
        {
            public EntryWork Entry;
            public MaintenanceCandidateStage Stage;
            public string ReasonCode;
            public string Reason;
            public object CollisionEvidence;
            public bool FootprintPassed;
            public bool TurnPassed;
            public bool OpeningPassed;
            public bool AFramePassed;
            public bool StraightPassed;
        }

        internal sealed class ProfileContract
        {
            public MaintenanceAccessProfile Profile;
            public double DiameterMm;
            public double HeightMm;
            public double RadiusMm;
            public double GridSafetyMm;
            public double DoorWidthMm;
            public double DoorHeightMm;
            public double TurnValidationWidthMm;
        }

        internal static ProfileContract GetProfileContract(MaintenanceAccessProfile profile)
        {
            return GetProfileContract(profile, new MaintenanceAnalysisOptions());
        }

        internal static ProfileContract GetProfileContract(
            MaintenanceAccessProfile profile,
            MaintenanceAnalysisOptions options)
        {
            options = options ?? new MaintenanceAnalysisOptions();
            MaintenanceAnalysisOptions.ValidateDoorDimensions(
                options.DoorWidthMm,
                options.DoorHeightMm);
            ProfileSpec spec = GetProfile(profile);
            return new ProfileContract
            {
                Profile = spec.Profile,
                DiameterMm = spec.DiameterMm,
                HeightMm = spec.HeightMm,
                RadiusMm = spec.RadiusMm,
                GridSafetyMm = spec.GridSafetyMm,
                DoorWidthMm = options.DoorWidthMm,
                DoorHeightMm = options.DoorHeightMm,
                TurnValidationWidthMm =
                    MaintenanceTurnZonePolicy.GetValidationWidthMm(spec.Profile)
            };
        }

        private sealed class Edge2
        {
            public int Id;
            public int X0;
            public int Y0;
            public int X1;
            public int Y1;
            public bool Used;
        }

        public static MaintenanceAnalysisResult Analyze(
            Document doc,
            ICollection<ElementId> selectedCeilingIds)
        {
            return Analyze(doc, selectedCeilingIds, new MaintenanceAnalysisOptions());
        }

        internal static MaintenanceAnalysisResult Analyze(
            Document doc,
            ICollection<ElementId> selectedCeilingIds,
            MaintenanceAnalysisOptions options)
        {
            LastDiagnostics.Clear();
            LastBlockedByCell.Clear();
            if (doc == null) throw new ArgumentNullException("doc");
            if (selectedCeilingIds == null || selectedCeilingIds.Count == 0)
                throw new InvalidOperationException("请先选择至少一块天花板。");

            options = options ?? new MaintenanceAnalysisOptions();
            List<GroupInput> inputs = ResolveGroups(
                doc,
                selectedCeilingIds,
                options.StrictCeilingSelection,
                options.CombineSelectedCeilingsForSharedEntry);
            if (inputs.Count == 0)
                throw new InvalidOperationException("选中图元中没有可分析的天花板。");

            MaintenanceAnalysisOptions.ValidateDoorDimensions(
                options.DoorWidthMm,
                options.DoorHeightMm);
            var result = new MaintenanceAnalysisResult
            {
                DoorWidthMm = options.DoorWidthMm,
                DoorHeightMm = options.DoorHeightMm,
                CeilingHatchSizeMm = FullBodyCeilingHatchSizeMm,
                SharedCeilingEntryReview = options.CombineSelectedCeilingsForSharedEntry,
                EvidenceScopeDefinition =
                    MaintenanceRouteEvidenceCoveragePolicy.ScopeDefinition,
                CandidateAuditEnabled = options.PreserveCandidateAudit,
                CandidateAuditComplete = options.PreserveCandidateAudit,
                CandidateAuditStrategy = options.PreserveCandidateAudit
                    ? "reportable_candidate_schemes"
                    : "selected_route_only",
                CandidateAuditScopeDefinition = options.PreserveCandidateAudit
                    ? "reportable_candidate_schemes"
                    : string.Empty,
                CandidateAuditScopeDescription = options.PreserveCandidateAudit
                    ? "Retains representative entry schemes and one deterministic route per retained entry-target-profile; it does not enumerate all mathematical paths."
                    : string.Empty,
                CandidateAuditAllPathsEnumerated = false,
                CandidateAuditRoutePolicy = options.PreserveCandidateAudit
                    ? "one_deterministic_astar_path_per_retained_entry_target_profile"
                    : string.Empty,
                CandidateAuditSelectionPolicy = options.PreserveCandidateAudit
                    ? "wall_minimum_cover_then_450mm_hatch_80mm_manhattan_first_feasible_stable_y_then_x"
                    : string.Empty,
                CandidateAuditDisplayRankingPolicy = options.PreserveCandidateAudit
                    ? "selected_first_then_status_profile_entry_type_route_length_stable_key"
                    : string.Empty
            };
            result.LinkScope = MaintenanceLinkScopeService.Resolve(
                doc,
                options.RelevantLinkInstanceIds);
            MaintenanceLinkScopeService.AddScopeLimitation(
                result.CoverageLimitations,
                result.LinkScope);
            AddEvidenceSources(
                result,
                MaintenanceLinkScopeService.RelevantLinkEvidenceSources(
                    doc,
                    result.LinkScope));
            if (result.LinkScope.Explicit)
            {
                result.EvidenceScopeDefinition +=
                    "; explicitRelevantLinks=" + result.LinkScope.RelevantLinks.Count +
                    "; outOfScopeLinks=" + result.LinkScope.OutOfScopeLinks.Count;
                if (result.CoverageLimitations.Count > 0)
                    result.Warnings.Add(result.CoverageLimitations.Last());
            }
            result.ModelFingerprint = MaintenanceLedgerSyncService.GetModelFingerprint(doc);
            foreach (GroupInput input in inputs)
                AnalyzeGroup(doc, input, result, options);
            if (result.TargetResults.Count == 0)
                result.Warnings.Add("选定天花分组内没有找到可维修的机械设备。");
            if (!result.EvidenceCollectionComplete)
                MaintenanceEvidenceCollectionPolicy.ApplyFailClosedGate(result);
            result.EvidenceFingerprint = ComputeEvidenceFingerprint(doc, result);
            foreach (MaintenanceRenderItem item in result.RenderItems)
                item.EvidenceFingerprint = result.EvidenceFingerprint;
            foreach (MaintenanceWallAlternativeResult alternative in result.WallAlternatives)
                foreach (MaintenanceRenderItem item in alternative.RenderItems)
                    item.EvidenceFingerprint = result.EvidenceFingerprint;
            result.WallAlternativeFingerprint =
                MaintenanceWallAlternativePolicy.ComputeFingerprint(result.WallAlternatives);
            if (result.CandidateAuditEnabled)
            {
                MaintenanceCandidateAudit.FinalizeForReporting(result.CandidateEvaluations);
                if (result.SharedCeilingEntryReview)
                {
                    result.SharedCeilingEntryAlternatives.AddRange(
                        MaintenanceSharedCeilingEntryPolicy.FindAlternatives(
                            result.CandidateEvaluations));
                    MaintenanceSharedCeilingEntryPolicy.ApplyCoveredTargetCounts(
                        result.SharedCeilingEntryAlternatives,
                        result.CandidateEvaluations);
                }
                result.CandidateAuditFingerprint =
                    MaintenanceCandidateAudit.ComputeFingerprint(result);
                RefreshCandidateSearchStats(result);
            }
            return result;
        }

        private static List<GroupInput> ResolveGroups(
            Document doc,
            ICollection<ElementId> selectedCeilingIds,
            bool strictCeilingSelection,
            bool combineSelectedCeilingsForSharedEntry)
        {
            List<Element> selected = selectedCeilingIds
                .Select(doc.GetElement)
                .Where(IsCeiling)
                .ToList();
            if (combineSelectedCeilingsForSharedEntry)
            {
                var combined = new GroupInput
                {
                    Key = MaintenanceSharedCeilingEntryPolicy.BuildCombinedGroupKey(
                        selected.Select(x =>
                        {
                            string comments = ReadComments(x);
                            return string.IsNullOrWhiteSpace(comments)
                                ? "天花" + x.Id.Value
                                : comments.Trim();
                        }))
                };
                combined.Ceilings.AddRange(selected
                    .Distinct(new ElementIdComparer())
                    .OrderBy(x => x.Id.Value));
                return combined.Ceilings.Count == 0
                    ? new List<GroupInput>()
                    : new List<GroupInput> { combined };
            }
            var requested = new Dictionary<string, List<Element>>(StringComparer.Ordinal);
            foreach (Element ceiling in selected)
            {
                string comments = ReadComments(ceiling);
                string key = string.IsNullOrWhiteSpace(comments)
                    ? "#" + ceiling.Id.Value
                    : comments.Trim();
                List<Element> list;
                if (!requested.TryGetValue(key, out list))
                {
                    list = new List<Element>();
                    requested[key] = list;
                }
                list.Add(ceiling);
            }

            List<Element> allCeilings = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Ceilings)
                .WhereElementIsNotElementType()
                .ToElements()
                .ToList();
            var output = new List<GroupInput>();
            foreach (KeyValuePair<string, List<Element>> pair in requested)
            {
                var input = new GroupInput { Key = pair.Key.TrimStart('#') };
                if (strictCeilingSelection ||
                    pair.Key.StartsWith("#", StringComparison.Ordinal))
                    input.Ceilings.AddRange(pair.Value);
                else
                    input.Ceilings.AddRange(allCeilings.Where(
                        x => string.Equals(ReadComments(x).Trim(), pair.Key, StringComparison.Ordinal)));
                List<Element> distinct = input.Ceilings
                    .Distinct(new ElementIdComparer())
                    .ToList();
                input.Ceilings.Clear();
                input.Ceilings.AddRange(distinct);
                input.Ceilings.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
                output.Add(input);
            }
            return output.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
        }

        private static void AnalyzeGroup(
            Document doc,
            GroupInput input,
            MaintenanceAnalysisResult result,
            MaintenanceAnalysisOptions options)
        {
            List<PlanarFace> faces = new List<PlanarFace>();
            foreach (Element ceiling in input.Ceilings)
                faces.AddRange(FindHighestHorizontalFaces(ceiling));
            if (faces.Count == 0)
                throw new InvalidOperationException("天花分组“" + input.Key + "”找不到水平顶面。");

            double topFt = faces.Max(x => x.Origin.Z);
            if (faces.Any(x => Math.Abs(x.Origin.Z - topFt) * MmPerFoot > 10.0))
                throw new InvalidOperationException(
                    "天花分组“" + input.Key + "”内顶面标高差超过 10 mm，请分组后再分析。");

            List<Triangle2> triangles = BuildTriangles(faces, topFt);
            if (triangles.Count == 0)
                throw new InvalidOperationException("天花分组“" + input.Key + "”无法生成平面轮廓。");
            Mask footprint = BuildClosedMask(triangles);
            List<List<MaintenancePoint2>> loops = ExtractOuterLoops(footprint);
            if (loops.Count == 0)
                throw new InvalidOperationException("天花分组“" + input.Key + "”无法提取外轮廓。");

            double minX = triangles.SelectMany(TrianglePoints).Min(x => x.X);
            double minY = triangles.SelectMany(TrianglePoints).Min(x => x.Y);
            double maxX = triangles.SelectMany(TrianglePoints).Max(x => x.X);
            double maxY = triangles.SelectMany(TrianglePoints).Max(x => x.Y);
            var roi = new PlenumAnalysisService.Bounds3
            {
                MinX = (minX - 3000.0) / MmPerFoot,
                MinY = (minY - 3000.0) / MmPerFoot,
                MinZ = topFt - 4000.0 / MmPerFoot,
                MaxX = (maxX + 3000.0) / MmPerFoot,
                MaxY = (maxY + 3000.0) / MmPerFoot,
                MaxZ = topFt + 2000.0 / MmPerFoot
            };
            var candidateProbe = new PlenumAnalysisResult();
            List<PlenumAnalysisService.Candidate> candidates =
                PlenumAnalysisService.CollectCandidates(
                    doc,
                    roi,
                    candidateProbe,
                    result.LinkScope);
            candidates.AddRange(MaintenanceWallObstacleCollector.Collect(
                doc,
                roi,
                candidateProbe.CandidateCollectionFailures,
                result.LinkScope));
            candidates = candidates
                .Where(x => x != null &&
                            !string.IsNullOrWhiteSpace(x.SourceKey) &&
                            CandidateIsInLinkScope(x, result.LinkScope))
                .GroupBy(x => x.SourceKey, StringComparer.Ordinal)
                .Select(x => x.First())
                .ToList();
            RegisterCollectionFailures(
                result, input.Key, candidateProbe.CandidateCollectionFailures);
            // 所有候选（含最终可能判定为设备近端局部支管的管线）都必须进入
            // EvidenceSources。豁免是目标设备级的碰撞忽略键，不再全组删除。
            AddEvidenceSources(result, candidates.Select(ToElementRef));

            double structureFt = ResolveStructureBottom(candidates, topFt, footprint);
            double floorFt = ResolveFloorTop(candidates, topFt, footprint);
            var group = new MaintenanceCeilingGroup
            {
                GroupKey = input.Key,
                CeilingTopMm = topFt * MmPerFoot,
                StructureBottomMm = structureFt * MmPerFoot
            };
            foreach (Element ceiling in input.Ceilings)
                group.CeilingSources.Add(ToElementRef(doc, ceiling));
            var ignoredGroupCeilingKeys = new HashSet<string>(
                input.Ceilings.Select(x =>
                    MaintenanceStableIdentity.HostElementKey(x.UniqueId)),
                StringComparer.Ordinal);
            AddEvidenceSources(result, group.CeilingSources);
            foreach (List<MaintenancePoint2> loop in loops)
                group.BoundaryLoops.Add(loop);
            result.Groups.Add(group);

            List<MaintenanceElementRef> safeGridUnverifiedBlockers;
            bool safeGridHasUnverifiedGeometry;
            MaintenanceGrid safeGrid = BuildSafeGrid(
                footprint,
                topFt,
                candidates,
                Full700,
                out safeGridUnverifiedBlockers,
                out safeGridHasUnverifiedGeometry);
            int safeCellCount = 0;
            for (int diagnosticY = 0; diagnosticY < safeGrid.Height; diagnosticY++)
            for (int diagnosticX = 0; diagnosticX < safeGrid.Width; diagnosticX++)
                if (safeGrid.IsWalkable(diagnosticX, diagnosticY)) safeCellCount++;
            int componentCount;
            int[] componentLabels = MaintenancePathfinder.BuildComponentLabels(
                safeGrid,
                out componentCount);
            List<TargetWork> targets = BuildTargets(
                input.Key,
                candidates,
                footprint,
                safeGrid,
                topFt,
                structureFt,
                result,
                Full700);
            foreach (TargetWork target in targets)
            {
                if (target.Grid == null)
                {
                    target.Grid = safeGrid;
                    target.ComponentLabels = componentLabels;
                    target.GridGeometryUnverified = safeGridHasUnverifiedGeometry;
                    AddBlockers(target.EntryBlockers, safeGridUnverifiedBlockers);
                }
            }
            LastDiagnostics.Add(input.Key + ": footprintCells=" + footprint.Filled.Count(x => x) +
                ", safeCells=" + safeCellCount + ", components=" + componentCount +
                ", candidates=" + candidates.Count);
            foreach (TargetWork diagnosticTarget in targets)
            {
                double nearestSafe = double.PositiveInfinity;
                MaintenancePoint2 nearestSafePoint = new MaintenancePoint2();
                var nearbyBlockers = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int diagnosticY = 0; diagnosticY < safeGrid.Height; diagnosticY++)
                for (int diagnosticX = 0; diagnosticX < safeGrid.Width; diagnosticX++)
                {
                    if (!safeGrid.IsWalkable(diagnosticX, diagnosticY)) continue;
                    MaintenancePoint2 diagnosticPoint = safeGrid.CellCenter(
                        new GridCell(diagnosticX, diagnosticY));
                    double diagnosticDistance = diagnosticPoint.DistanceTo(
                        ToPoint2Mm(diagnosticTarget.PocketCenterBottom));
                    if (diagnosticDistance < nearestSafe)
                    {
                        nearestSafe = diagnosticDistance;
                        nearestSafePoint = diagnosticPoint;
                    }
                }
                for (int diagnosticY = 0; diagnosticY < safeGrid.Height; diagnosticY++)
                for (int diagnosticX = 0; diagnosticX < safeGrid.Width; diagnosticX++)
                {
                    MaintenancePoint2 diagnosticPoint = safeGrid.CellCenter(
                        new GridCell(diagnosticX, diagnosticY));
                    if (diagnosticPoint.DistanceTo(ToPoint2Mm(diagnosticTarget.PocketCenterBottom)) > 1500.0)
                        continue;
                    string blocker;
                    if (!LastBlockedByCell.TryGetValue(
                        safeGrid.ToIndex(diagnosticX, diagnosticY), out blocker)) continue;
                    int count;
                    nearbyBlockers.TryGetValue(blocker, out count);
                    nearbyBlockers[blocker] = count + 1;
                }
                LastDiagnostics.Add(input.Key + ": target=" + diagnosticTarget.Target.TargetKey +
                    ", goal=" + diagnosticTarget.HasGoal +
                    ", nearestSafeMm=" + (double.IsInfinity(nearestSafe) ? -1.0 : Math.Round(nearestSafe, 0)) +
                    ", nearestSafePoint=" + (double.IsInfinity(nearestSafe)
                        ? "none"
                        : Math.Round(nearestSafePoint.X, 0) + "," + Math.Round(nearestSafePoint.Y, 0)) +
                    ", pocketInside=" + diagnosticTarget.PocketInside +
                    ", pocket=" + diagnosticTarget.PocketCollision.State +
                    ", blocker=" + diagnosticTarget.PocketCollision.BlockerKey +
                    ", nearbyBlocked=" + string.Join(";", nearbyBlockers
                        .OrderByDescending(x => x.Value).Take(4)
                        .Select(x => x.Key + "=" + x.Value)));
            }
            foreach (TargetWork work in targets) group.Targets.Add(work.Target);

            List<EntryWork> wallEntries = BuildWallEntries(
                input.Key,
                loops,
                footprint,
                safeGrid,
                componentLabels,
                topFt,
                floorFt,
                candidates,
                targets,
                Full700,
                result);
            if (result.CandidateAuditEnabled)
                RecordRouteEvaluations(input.Key, wallEntries, targets, safeGrid, candidates, Full700, result);
            LastDiagnostics.Add(input.Key + ": wallEntries=" + wallEntries.Count +
                ", aFrame=" + wallEntries.Count(x => x.Candidate.LadderType == MaintenanceLadderType.AFrame) +
                ", straight=" + wallEntries.Count(x => x.Candidate.LadderType == MaintenanceLadderType.Straight));
            List<WallAlternativeWork> wallAlternativeWorks = CaptureWallAlternatives(
                wallEntries,
                targets,
                safeGrid,
                candidates,
                Full700);
            Dictionary<string, EntryWork> assigned = SelectMinimumWallEntries(
                wallEntries,
                targets,
                safeGrid,
                componentLabels);
            var fullHatchEntries = new List<EntryWork>();

            foreach (TargetWork target in targets)
            {
                EntryWork entry;
                bool hasWallEntry = assigned.TryGetValue(target.Target.TargetKey, out entry);
                if (!hasWallEntry || result.CandidateAuditEnabled ||
                    options.CombineSelectedCeilingsForSharedEntry)
                {
                    EntryWork hatchEntry = BuildBestHatchEntry(
                        input.Key,
                        target,
                        footprint,
                        target.Grid,
                        target.ComponentLabels,
                        topFt,
                        floorFt,
                        ignoredGroupCeilingKeys,
                        candidates,
                        Full700,
                        result,
                        options.MaxHatchCandidatesPerTarget,
                        !hasWallEntry || options.CombineSelectedCeilingsForSharedEntry,
                        options.CombineSelectedCeilingsForSharedEntry,
                        fullHatchEntries);
                    if (!hasWallEntry && hatchEntry != null)
                    {
                        entry = hatchEntry;
                        assigned[target.Target.TargetKey] = hatchEntry;
                    }
                    LastDiagnostics.Add(input.Key + ": hatch target=" + target.Target.TargetKey +
                        ", result=" + (hatchEntry == null ? "none" : hatchEntry.Candidate.LadderType.ToString()) +
                        ", selected=" + (!hasWallEntry && hatchEntry != null));
                }
            }
            if (options.CombineSelectedCeilingsForSharedEntry)
                RecordSharedHatchRouteEvaluations(
                    input.Key,
                    fullHatchEntries,
                    targets,
                    safeGrid,
                    candidates,
                    Full700,
                    result);

            var selectedChains = new Dictionary<string, ChainWork>(StringComparer.Ordinal);
            var unverifiedChains = new Dictionary<string, ChainWork>(StringComparer.Ordinal);
            var limitedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (TargetWork target in targets)
            {
                EntryWork entry;
                assigned.TryGetValue(target.Target.TargetKey, out entry);
                MaintenanceCollisionState routeState = GetRouteValidationState(
                    target,
                    entry,
                    target.Grid,
                    candidates,
                    Full700);
                bool full700EntryIsWallDoor = entry != null &&
                    entry.Candidate != null &&
                    entry.Candidate.EntryType == MaintenanceEntryType.WallDoor;
                if (options.CombineSelectedCeilingsForSharedEntry ||
                    MaintenanceDoorOpeningPolicy.ShouldEvaluateLimited600Wall(
                        full700EntryIsWallDoor,
                        routeState == MaintenanceCollisionState.Clear))
                    limitedKeys.Add(target.Target.TargetKey);
                if (routeState == MaintenanceCollisionState.Clear)
                {
                    selectedChains[target.Target.TargetKey] = new ChainWork
                    {
                        Spec = Full700,
                        Grid = target.Grid,
                        Target = target,
                        Entry = entry
                    };
                }
                else
                {
                    if (routeState == MaintenanceCollisionState.Unverified)
                    {
                        unverifiedChains[target.Target.TargetKey] = new ChainWork
                        {
                            Spec = Full700,
                            Grid = target.Grid,
                            Target = target,
                            Entry = entry
                        };
                    }
                }
            }

            MaintenanceGrid limitedGrid = null;
            int limitedComponentCount = 0;
            int[] limitedLabels = null;
            if (limitedKeys.Count > 0)
            {
                List<MaintenanceElementRef> limitedGridUnverifiedBlockers;
                bool limitedGridHasUnverifiedGeometry;
                limitedGrid = BuildSafeGrid(
                    footprint,
                    topFt,
                    candidates,
                    Limited600,
                    out limitedGridUnverifiedBlockers,
                    out limitedGridHasUnverifiedGeometry);
                limitedLabels = MaintenancePathfinder.BuildComponentLabels(
                    limitedGrid,
                    out limitedComponentCount);
                List<TargetWork> limitedTargets = BuildTargets(
                    input.Key,
                    candidates,
                    footprint,
                    limitedGrid,
                    topFt,
                    structureFt,
                    result,
                    Limited600)
                    .Where(x => limitedKeys.Contains(x.Target.TargetKey))
                    .ToList();
                foreach (TargetWork target in limitedTargets)
                {
                    if (target.Grid == null)
                    {
                        target.Grid = limitedGrid;
                        target.ComponentLabels = limitedLabels;
                        target.GridGeometryUnverified = limitedGridHasUnverifiedGeometry;
                        AddBlockers(target.EntryBlockers, limitedGridUnverifiedBlockers);
                    }
                }
                List<EntryWork> limitedWallEntries = BuildWallEntries(
                    input.Key,
                    loops,
                    footprint,
                    limitedGrid,
                    limitedLabels,
                    topFt,
                    floorFt,
                    candidates,
                    limitedTargets,
                    Limited600,
                    result);
                if (result.CandidateAuditEnabled)
                    RecordRouteEvaluations(input.Key, limitedWallEntries, limitedTargets, limitedGrid, candidates, Limited600, result);
                wallAlternativeWorks.AddRange(CaptureWallAlternatives(
                    limitedWallEntries,
                    limitedTargets,
                    limitedGrid,
                    candidates,
                    Limited600));
                var preferredEntryKeys = new HashSet<string>(
                    assigned.Values.Where(x => x != null).Select(x => x.Candidate.CandidateKey),
                    StringComparer.Ordinal);
                Dictionary<string, EntryWork> limitedAssigned = SelectMinimumWallEntries(
                    limitedWallEntries,
                    limitedTargets,
                    limitedGrid,
                    limitedLabels,
                    preferredEntryKeys);
                var limitedHatchEntries = new List<EntryWork>();
                foreach (TargetWork target in limitedTargets)
                {
                    EntryWork entry;
                    bool hasWallEntry = limitedAssigned.TryGetValue(target.Target.TargetKey, out entry);
                    if (!hasWallEntry || result.CandidateAuditEnabled ||
                        options.CombineSelectedCeilingsForSharedEntry)
                    {
                        EntryWork hatchEntry = BuildBestHatchEntry(
                            input.Key,
                            target,
                            footprint,
                            target.Grid,
                            target.ComponentLabels,
                            topFt,
                            floorFt,
                            ignoredGroupCeilingKeys,
                            candidates,
                            Limited600,
                            result,
                            options.MaxHatchCandidatesPerTarget,
                            !hasWallEntry || options.CombineSelectedCeilingsForSharedEntry,
                            options.CombineSelectedCeilingsForSharedEntry,
                            limitedHatchEntries);
                        if (!hasWallEntry && hatchEntry != null)
                        {
                            entry = hatchEntry;
                            limitedAssigned[target.Target.TargetKey] = hatchEntry;
                        }
                    }
                    MaintenanceCollisionState routeState = GetRouteValidationState(
                        target,
                        entry,
                        target.Grid,
                        candidates,
                        Limited600);
                    LastDiagnostics.Add(input.Key + ": limited600 target=" + target.Target.TargetKey +
                        ", goal=" + target.HasGoal + ", entry=" + (entry == null ? "none" : entry.Candidate.CandidateKey) +
                        ", routeState=" + routeState);
                    TargetWork fullTarget = targets.FirstOrDefault(x => string.Equals(
                        x.Target.TargetKey,
                        target.Target.TargetKey,
                        StringComparison.Ordinal));
                    if (fullTarget != null)
                    {
                        fullTarget.EntryGeometryUnverified |= target.EntryGeometryUnverified;
                        fullTarget.GridGeometryUnverified |= target.GridGeometryUnverified;
                        AddBlockers(fullTarget.EntryBlockers, target.EntryBlockers);
                    }
                    bool full700ResultAlreadySelected =
                        selectedChains.ContainsKey(target.Target.TargetKey);
                    if (routeState == MaintenanceCollisionState.Clear &&
                        entry != null && entry.Candidate != null &&
                        MaintenanceDoorOpeningPolicy.ShouldSelectLimited600Result(
                            full700ResultAlreadySelected,
                            entry.Candidate.EntryType))
                    {
                        selectedChains[target.Target.TargetKey] = new ChainWork
                        {
                            Spec = Limited600,
                            Grid = target.Grid,
                            Target = target,
                            Entry = entry
                        };
                    }
                    else if (routeState == MaintenanceCollisionState.Unverified &&
                        !unverifiedChains.ContainsKey(target.Target.TargetKey))
                    {
                        unverifiedChains[target.Target.TargetKey] = new ChainWork
                        {
                            Spec = Limited600,
                            Grid = target.Grid,
                            Target = target,
                            Entry = entry
                        };
                    }
                }
                if (options.CombineSelectedCeilingsForSharedEntry)
                    RecordSharedHatchRouteEvaluations(
                        input.Key,
                        limitedHatchEntries,
                        limitedTargets,
                        limitedGrid,
                        candidates,
                        Limited600,
                        result);
                LastDiagnostics.Add(input.Key + ": limited600 components=" + limitedComponentCount +
                    ", wallEntries=" + limitedWallEntries.Count);
            }

            foreach (TargetWork target in targets)
            {
                if (selectedChains.ContainsKey(target.Target.TargetKey)) continue;
                ChainWork unverified;
                if (unverifiedChains.TryGetValue(target.Target.TargetKey, out unverified))
                {
                    selectedChains[target.Target.TargetKey] = unverified;
                    continue;
                }
                selectedChains[target.Target.TargetKey] = new ChainWork
                {
                    Spec = Full700,
                    Grid = target.Grid,
                    Target = target,
                    Entry = null
                };
            }

            BuildWallAlternativeResults(
                group,
                targets,
                selectedChains,
                wallAlternativeWorks,
                candidates,
                floorFt,
                result);

            var entryNames = selectedChains.Values
                .Where(x => x.Entry != null)
                .Select(x => x.Entry)
                .OrderBy(x => x.Profile == MaintenanceAccessProfile.Full700 ? 0 : 1)
                .GroupBy(x => x.Candidate.CandidateKey)
                .Select(x => x.First())
                .OrderBy(x => x.Candidate.EntryType)
                .ThenBy(x => x.Candidate.Center.X)
                .ThenBy(x => x.Candidate.Center.Y)
                .ToList();
            var friendlyEntry = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < entryNames.Count; i++)
                friendlyEntry[entryNames[i].Candidate.CandidateKey] =
                    input.Key + "-入口" + (i + 1).ToString("00");
            Dictionary<string, EntryWork> renderEntryByKey = selectedChains.Values
                .Where(x => x.Entry != null)
                .Select(x => x.Entry)
                .GroupBy(x => x.Candidate.CandidateKey)
                .ToDictionary(
                    x => x.Key,
                    x => x.OrderBy(y => y.Profile == MaintenanceAccessProfile.Full700 ? 0 : 1).First(),
                    StringComparer.Ordinal);
            Dictionary<string, List<MaintenanceTarget>> renderTargetsByEntryKey = selectedChains.Values
                .Where(x => x.Entry != null && x.Target != null && x.Target.Target != null)
                .GroupBy(x => x.Entry.Candidate.CandidateKey)
                .ToDictionary(
                    x => x.Key,
                    x => x.Select(y => y.Target.Target)
                        .GroupBy(y => y.TargetKey, StringComparer.Ordinal)
                        .Select(y => y.First())
                        .OrderBy(y => y.GetDisplayName(), StringComparer.Ordinal)
                        .ToList(),
                    StringComparer.Ordinal);

            var renderedEntries = new HashSet<string>(StringComparer.Ordinal);
            foreach (TargetWork target in targets)
            {
                ChainWork chain = selectedChains[target.Target.TargetKey];
                EntryWork entry = chain.Entry;
                MaintenanceTargetResult targetResult = AnalyzeTargetRoute(
                    input.Key,
                    chain.Target,
                    entry,
                    chain.Grid,
                    candidates,
                    friendlyEntry,
                    result.AnalysisId,
                    chain.Spec);
                result.TargetResults.Add(targetResult);
                result.RenderItems.AddRange(targetResult.RenderItems);
                if (entry != null && renderedEntries.Add(entry.Candidate.CandidateKey))
                {
                    List<MaintenanceTarget> sharedTargets =
                        renderTargetsByEntryKey[entry.Candidate.CandidateKey];
                    result.RenderItems.AddRange(BuildEntryRenderItems(
                        group,
                        renderEntryByKey[entry.Candidate.CandidateKey],
                        friendlyEntry[entry.Candidate.CandidateKey],
                        string.Join("、", sharedTargets.Select(x =>
                            x.GetDisplayName().Replace(" | ", "｜"))),
                        sharedTargets.Select(x => x.TargetKey),
                        floorFt,
                        result.AnalysisId));
                }
            }

            if (result.CandidateAuditEnabled)
                MarkSelectedRouteEvaluations(input.Key, selectedChains, result.CandidateEvaluations);

            result.RenderItems.AddRange(BuildVirtualWallItems(
                group,
                selectedChains.Values.Where(x => x.Entry != null).Select(x => x.Entry).ToList(),
                friendlyEntry,
                result.AnalysisId));
            if (componentCount > 1)
                result.Warnings.Add(
                    "天花分组“" + input.Key + "”内的 700 mm 人体可通行空间分成 " +
                    componentCount + " 个不连通区。");
        }

        private static List<TargetWork> BuildTargets(
            string groupKey,
            IList<PlenumAnalysisService.Candidate> candidates,
            Mask footprint,
            MaintenanceGrid grid,
            double topFt,
            double structureFt,
            MaintenanceAnalysisResult result,
            ProfileSpec spec)
        {
            var output = new List<TargetWork>();
            List<PlenumAnalysisService.Candidate> targetCandidates = candidates
                .Where(x => x.Category == BuiltInCategory.OST_MechanicalEquipment)
                .OrderBy(x => x.SourceKey, StringComparer.Ordinal)
                .ToList();
            foreach (PlenumAnalysisService.Candidate candidate in targetCandidates)
            {
                if (candidate.WorldBounds == null) continue;
                double cx = (candidate.WorldBounds.MinX + candidate.WorldBounds.MaxX) * 0.5;
                double cy = (candidate.WorldBounds.MinY + candidate.WorldBounds.MaxY) * 0.5;
                if (!footprint.Contains(new MaintenancePoint2(cx * MmPerFoot, cy * MmPerFoot))) continue;
                if (candidate.WorldBounds.MaxZ < topFt - 1.0 / MmPerFoot ||
                    candidate.WorldBounds.MinZ > structureFt) continue;

                bool inferred;
                XYZ supply = ResolveSupplyDirection(candidate, out inferred);
                XYZ service = XYZ.BasisZ.CrossProduct(supply).Normalize();
                XYZ pocketBottom = ResolveServicePocketCenter(candidate, supply, service, topFt);
                HashSet<string> exemptSourceKeys = CollectTargetLocalPipeExemptions(
                    groupKey,
                    candidate,
                    targetCandidates,
                    candidates,
                    result);
                MaintenanceGrid targetGrid = grid;
                int[] targetLabels = null;
                List<MaintenanceElementRef> targetGridUnverifiedBlockers = null;
                bool targetGridHasUnverifiedGeometry = false;
                if (exemptSourceKeys.Count > 0)
                {
                    targetGrid = BuildSafeGrid(
                        footprint,
                        topFt,
                        candidates,
                        spec,
                        out targetGridUnverifiedBlockers,
                        out targetGridHasUnverifiedGeometry,
                        exemptSourceKeys,
                        false);
                    int targetComponentCount;
                    targetLabels = MaintenancePathfinder.BuildComponentLabels(
                        targetGrid,
                        out targetComponentCount);
                }
                Solid pocket = MaintenanceGeometryService.MakeBox(
                    pocketBottom,
                    PocketWidthMm / MmPerFoot,
                    PocketWidthMm / MmPerFoot,
                    (PocketHeightMm - CollisionLiftMm) / MmPerFoot,
                    supply);
                bool inside = RectangleInsideMask(
                    footprint,
                    ToPoint2Mm(pocketBottom),
                    supply,
                    PocketWidthMm,
                    PocketWidthMm);
                var ignored = new HashSet<string>(exemptSourceKeys, StringComparer.Ordinal)
                {
                    candidate.SourceKey
                };
                MaintenanceCollisionResult collision = inside
                    ? MaintenanceGeometryService.Validate(pocket, candidates, ignored)
                    : new MaintenanceCollisionResult
                    {
                        State = MaintenanceCollisionState.Conflict,
                        Reason = "service pocket extends outside the ceiling group"
                    };

                GridCell seed = targetGrid.WorldToCell(
                    pocketBottom.X * MmPerFoot,
                    pocketBottom.Y * MmPerFoot);
                GridCell goal;
                double serviceFaceProjectionMm =
                    (pocketBottom.X * service.X + pocketBottom.Y * service.Y) * MmPerFoot -
                    (PocketWidthMm * 0.5 + CollisionLiftMm);
                bool hasGoal = FindNearestWalkableOnServiceSide(
                    targetGrid,
                    seed,
                    Math.Max(15, (int)Math.Ceiling(1500.0 / targetGrid.CellSize)),
                    service,
                    serviceFaceProjectionMm,
                    out goal);
                var target = new MaintenanceTarget
                {
                    TargetKey = candidate.SourceKey,
                    Source = ToElementRef(candidate),
                    EquipmentName = ResolveEquipmentName(candidate.Element),
                    Mark = ReadMark(candidate.Element),
                    Center = new MaintenancePoint3(
                        cx * MmPerFoot,
                        cy * MmPerFoot,
                        ((candidate.WorldBounds.MinZ + candidate.WorldBounds.MaxZ) * 0.5) * MmPerFoot),
                    SupplyDirection = new MaintenancePoint2(supply.X, supply.Y),
                    ServiceSideDirection = new MaintenancePoint2(service.X, service.Y),
                    ServicePocketCenter = new MaintenancePoint3(
                        pocketBottom.X * MmPerFoot,
                        pocketBottom.Y * MmPerFoot,
                        topFt * MmPerFoot + PocketHeightMm * 0.5),
                    ServicePocketWidthMm = PocketWidthMm,
                    ServicePocketDepthMm = PocketWidthMm,
                    ServicePocketHeightMm = PocketHeightMm
                };
                if (inferred && spec.Profile == MaintenanceAccessProfile.Full700)
                    result.Warnings.Add(
                        "设备“" + target.GetDisplayName() + "”找不到送风连接器，检修左侧按设备外形方向推断。");
                var targetWork = new TargetWork
                {
                    Profile = spec.Profile,
                    Target = target,
                    Candidate = candidate,
                    Goal = goal,
                    HasGoal = hasGoal,
                    PocketCollision = collision,
                    PocketInside = inside,
                    Supply = supply,
                    ServiceSide = service,
                    PocketCenterBottom = pocketBottom,
                    SupplyDirectionInferred = inferred,
                    Grid = exemptSourceKeys.Count == 0 ? null : targetGrid,
                    ComponentLabels = targetLabels,
                    GridGeometryUnverified = targetGridHasUnverifiedGeometry
                };
                foreach (string key in exemptSourceKeys) targetWork.ExemptSourceKeys.Add(key);
                if (targetGridUnverifiedBlockers != null)
                    AddBlockers(targetWork.EntryBlockers, targetGridUnverifiedBlockers);
                output.Add(targetWork);
            }
            for (int index = 0; index < output.Count; index++)
                output[index].Target.DeviceNo = (index + 1).ToString("00");
            return output;
        }

        private static List<EntryWork> BuildWallEntries(
            string groupKey,
            IList<List<MaintenancePoint2>> loops,
            Mask footprint,
            MaintenanceGrid grid,
            int[] labels,
            double topFt,
            double floorFt,
            IList<PlenumAnalysisService.Candidate> candidates,
            IList<TargetWork> targets,
            ProfileSpec spec,
            MaintenanceAnalysisResult result)
        {
            var entries = new List<EntryWork>();
            int sampledCount = 0;
            int footprintPass = 0;
            int turnPass = 0;
            int openingPass = 0;
            int framePass = 0;
            int doorSwingPass = 0;
            int portalPass = 0;
            int startPass = 0;
            int ladderPass = 0;
            for (int loopIndex = 0; loopIndex < loops.Count; loopIndex++)
            {
                List<MaintenancePoint2> loop = loops[loopIndex];
                for (int segmentIndex = 0; segmentIndex < loop.Count; segmentIndex++)
                {
                    MaintenancePoint2 a = loop[segmentIndex];
                    MaintenancePoint2 b = loop[(segmentIndex + 1) % loop.Count];
                    MaintenancePoint2 tangent2 = (b - a).Normalize();
                    double length = a.DistanceTo(b);
                    double requiredSegmentLengthMm = result.DoorWidthMm + 200.0;
                    if (length < requiredSegmentLengthMm) continue;
                    MaintenancePoint2 left = tangent2.LeftNormal();
                    double usable = length - requiredSegmentLengthMm;
                    int sampleCount = Math.Max(1,
                        (int)Math.Floor(usable / 400.0) + 1);
                    for (int sample = 0; sample < sampleCount; sample++)
                    {
                        sampledCount++;
                        double distance = requiredSegmentLengthMm * 0.5 +
                                          (sample + 0.5) * usable / sampleCount;
                        MaintenancePoint2 p2 = a + tangent2 * distance;
                        string candidateKey = groupKey + "|W|" + loopIndex + "|" +
                                              segmentIndex + "|" +
                                              Math.Round(p2.X, 0) + "|" + Math.Round(p2.Y, 0);
                        MaintenanceCandidateEvaluation audit = NewEntryEvaluation(
                            result,
                            candidateKey,
                            groupKey,
                            spec,
                            MaintenanceEntryType.WallDoor,
                            p2,
                            topFt * MmPerFoot + result.DoorHeightMm * 0.5,
                            loopIndex,
                            segmentIndex);
                        if (!MaintenanceDoorOpeningPolicy.SupportsAccessProfile(
                                result.DoorWidthMm,
                                result.DoorHeightMm,
                                spec.DiameterMm,
                                spec.HeightMm))
                        {
                            RejectEntryEvaluation(
                                result,
                                audit,
                                MaintenanceCandidateStage.Opening,
                                "door_opening_smaller_than_access_profile",
                                MaintenanceDoorOpeningPolicy.BuildRejectionReason(
                                    result.DoorWidthMm,
                                    result.DoorHeightMm,
                                    spec.DiameterMm,
                                    spec.HeightMm),
                                null,
                                candidates);
                            continue;
                        }
                        bool leftInside = footprint.Contains(p2 + left * 200.0);
                        bool rightInside = footprint.Contains(p2 - left * 200.0);
                        if (leftInside == rightInside)
                        {
                            RejectEntryEvaluation(result, audit, MaintenanceCandidateStage.Footprint,
                                "boundary_orientation_invalid",
                                "候选点无法确定清晰的墙内外方向，不能形成侧墙入口。",
                                null,
                                candidates);
                            continue;
                        }
                        MaintenancePoint2 inward2 = leftInside ? left : left * -1.0;
                        XYZ p = new XYZ(p2.X / MmPerFoot, p2.Y / MmPerFoot, topFt);
                        XYZ tangent = new XYZ(tangent2.X, tangent2.Y, 0.0);
                        XYZ inward = new XYZ(inward2.X, inward2.Y, 0.0);
                        XYZ turnBottom = p + inward.Multiply(500.0 / MmPerFoot) +
                                         XYZ.BasisZ.Multiply(CollisionLiftMm / MmPerFoot);
                        double turnValidationWidthMm =
                            MaintenanceTurnZonePolicy.GetValidationWidthMm(spec.Profile);
                        if (!RectangleInsideMask(
                            footprint,
                            ToPoint2Mm(turnBottom),
                            tangent,
                            turnValidationWidthMm,
                            turnValidationWidthMm))
                        {
                            RejectEntryEvaluation(result, audit, MaintenanceCandidateStage.Footprint,
                                "turn_zone_outside_footprint",
                                "入口内侧转身区超出当前天花分组轮廓。",
                                null,
                                candidates);
                            continue;
                        }
                        footprintPass++;

                        Solid turn = MaintenanceGeometryService.MakeBox(
                            turnBottom,
                            turnValidationWidthMm / MmPerFoot,
                            turnValidationWidthMm / MmPerFoot,
                            (TurnHeightMm - CollisionLiftMm) / MmPerFoot,
                            tangent);
                        MaintenanceCollisionResult turnCollision =
                            MaintenanceGeometryService.Validate(turn, candidates, null);
                        if (!turnCollision.IsClear)
                        {
                            RecordEntryCollision(targets, turnCollision, candidates);
                            RejectEntryEvaluation(result, audit, MaintenanceCandidateStage.TurnZone,
                                "turn_zone_conflict",
                                "入口内侧转身区与模型构件冲突。",
                                turnCollision,
                                candidates);
                            continue;
                        }
                        turnPass++;
                        Solid opening = MaintenanceGeometryService.MakeBox(
                            p + XYZ.BasisZ.Multiply(CollisionLiftMm / MmPerFoot),
                            result.DoorWidthMm / MmPerFoot,
                            220.0 / MmPerFoot,
                            (result.DoorHeightMm - CollisionLiftMm) / MmPerFoot,
                            tangent);
                        HashSet<string> openingHostSourceKeys =
                            ResolveOpeningHostWallKeys(opening, tangent, candidates);
                        if (audit != null)
                        {
                            foreach (string sourceKey in openingHostSourceKeys
                                .OrderBy(x => x, StringComparer.Ordinal))
                                audit.OpeningHostSourceKeys.Add(sourceKey);
                        }
                        MaintenanceCollisionResult openingCollision =
                            MaintenanceGeometryService.Validate(
                                opening,
                                candidates,
                                openingHostSourceKeys);
                        if (!openingCollision.IsClear)
                        {
                            RecordEntryCollision(targets, openingCollision, candidates);
                            RejectEntryEvaluation(result, audit, MaintenanceCandidateStage.Opening,
                                "opening_conflict",
                                "侧墙检修门洞口与模型构件冲突。",
                                openingCollision,
                                candidates);
                            continue;
                        }
                        openingPass++;
                        List<Solid> frame = MaintenanceGeometryService.BuildDoorFrame(
                            p,
                            tangent,
                            result.DoorWidthMm / MmPerFoot,
                            160.0 / MmPerFoot,
                            result.DoorHeightMm / MmPerFoot,
                            50.0 / MmPerFoot);
                        MaintenanceCollisionResult frameCollision =
                            MaintenanceGeometryService.Validate(
                                frame,
                                candidates,
                                openingHostSourceKeys);
                        if (!frameCollision.IsClear)
                        {
                            RecordEntryCollision(targets, frameCollision, candidates);
                            RejectEntryEvaluation(result, audit, MaintenanceCandidateStage.DoorFrame,
                                "door_frame_conflict",
                                "侧墙检修门框与模型构件冲突。",
                                frameCollision,
                                candidates);
                            continue;
                        }
                        framePass++;
                        double swingLiftFt = CollisionLiftMm / MmPerFoot;
                        Solid leftSwing = MaintenanceGeometryService.BuildOutwardDoorSwingEnvelope(
                            p + XYZ.BasisZ.Multiply(swingLiftFt),
                            inward,
                            result.DoorWidthMm / MmPerFoot,
                            MaintenanceDoorSwingPolicy.LeafThicknessMm / MmPerFoot,
                            (result.DoorHeightMm - CollisionLiftMm) / MmPerFoot,
                            MaintenanceDoorSwingPolicy.OutboardOffsetMm / MmPerFoot,
                            MaintenanceDoorHingeSide.Left);
                        Solid rightSwing = MaintenanceGeometryService.BuildOutwardDoorSwingEnvelope(
                            p + XYZ.BasisZ.Multiply(swingLiftFt),
                            inward,
                            result.DoorWidthMm / MmPerFoot,
                            MaintenanceDoorSwingPolicy.LeafThicknessMm / MmPerFoot,
                            (result.DoorHeightMm - CollisionLiftMm) / MmPerFoot,
                            MaintenanceDoorSwingPolicy.OutboardOffsetMm / MmPerFoot,
                            MaintenanceDoorHingeSide.Right);
                        MaintenanceCollisionResult leftSwingCollision =
                            MaintenanceGeometryService.Validate(
                                leftSwing,
                                candidates,
                                openingHostSourceKeys);
                        MaintenanceCollisionResult rightSwingCollision =
                            MaintenanceGeometryService.Validate(
                                rightSwing,
                                candidates,
                                openingHostSourceKeys);
                        MaintenanceDoorSwingStatus leftSwingStatus =
                            ToDoorSwingStatus(leftSwingCollision);
                        MaintenanceDoorSwingStatus rightSwingStatus =
                            ToDoorSwingStatus(rightSwingCollision);
                        if (audit != null)
                        {
                            audit.LeftDoorSwingStatus = leftSwingStatus;
                            audit.RightDoorSwingStatus = rightSwingStatus;
                            AddBlocker(audit.LeftDoorSwingBlockers, candidates, leftSwingCollision);
                            AddBlocker(audit.RightDoorSwingBlockers, candidates, rightSwingCollision);
                        }
                        MaintenanceDoorHingeSide selectedHinge =
                            MaintenanceDoorSwingPolicy.Select(leftSwingStatus, rightSwingStatus);
                        if (selectedHinge == MaintenanceDoorHingeSide.None)
                        {
                            RecordEntryCollision(targets, leftSwingCollision, candidates);
                            RecordEntryCollision(targets, rightSwingCollision, candidates);
                            RejectEntryEvaluation(
                                result,
                                audit,
                                MaintenanceCandidateStage.DoorSwing,
                                "outward_door_swing_conflict",
                                "左铰链、右铰链向外开启 90° 的门扇扫掠均未通过模型碰撞检查。",
                                new[] { leftSwingCollision, rightSwingCollision },
                                candidates);
                            continue;
                        }
                        doorSwingPass++;
                        XYZ outside = p - inward.Multiply(400.0 / MmPerFoot) +
                                      XYZ.BasisZ.Multiply(spec.HeightMm * 0.5 / MmPerFoot);
                        XYZ insidePoint = p + inward.Multiply(500.0 / MmPerFoot) +
                                          XYZ.BasisZ.Multiply(spec.HeightMm * 0.5 / MmPerFoot);
                        Solid portal = MaintenanceGeometryService.MakeHorizontalCapsule(
                            outside,
                            insidePoint,
                            spec.RadiusMm / MmPerFoot,
                            spec.HeightMm / MmPerFoot);
                        MaintenanceCollisionResult portalCollision =
                            MaintenanceGeometryService.Validate(
                                portal,
                                candidates,
                                openingHostSourceKeys);
                        if (!portalCollision.IsClear)
                        {
                            RecordEntryCollision(targets, portalCollision, candidates);
                            RejectEntryEvaluation(result, audit, MaintenanceCandidateStage.Portal,
                                "portal_conflict",
                                "人体穿越入口的包络与模型构件冲突。",
                                portalCollision,
                                candidates);
                            continue;
                        }
                        portalPass++;

                        GridCell startSeed = grid.WorldToCell(
                            turnBottom.X * MmPerFoot,
                            turnBottom.Y * MmPerFoot);
                        GridCell start;
                        if (!MaintenancePathfinder.FindNearestWalkable(grid, startSeed, 20, out start))
                        {
                            RejectEntryEvaluation(result, audit, MaintenanceCandidateStage.StartCell,
                                "no_walkable_start",
                                "入口内侧找不到可通行的人体包络起点。",
                                null,
                                candidates);
                            continue;
                        }
                        startPass++;
                        int component = labels[grid.ToIndex(start.X, start.Y)];
                        if (component < 0)
                        {
                            RejectEntryEvaluation(result, audit, MaintenanceCandidateStage.Connectivity,
                                "entry_outside_connected_space",
                                "入口起点不属于任何可通行连通区。",
                                null,
                                candidates);
                            continue;
                        }

                        XYZ outward = -inward;
                        XYZ ladderCenter = p + outward.Multiply(800.0 / MmPerFoot);
                        MaintenanceLadderType ladderType;
                        XYZ ladderAlong;
                        double ladderFloorFt;
                        List<string> ladderSupportSourceKeys;
                        string ladderFailureCode;
                        string ladderFailureReason;
                        List<MaintenanceCollisionResult> ladderRejections;
                        if (!TryValidateLadder(
                            p,
                            tangent,
                            outward,
                            floorFt,
                            topFt,
                            candidates,
                            targets,
                            openingHostSourceKeys,
                            out ladderType,
                            out ladderCenter,
                            out ladderAlong,
                            out ladderFloorFt,
                            out ladderSupportSourceKeys,
                            out ladderFailureCode,
                            out ladderFailureReason,
                            out ladderRejections))
                        {
                            RejectEntryEvaluation(result, audit, MaintenanceCandidateStage.Ladder,
                                ladderFailureCode,
                                ladderFailureReason,
                                ladderRejections,
                                candidates);
                            continue;
                        }
                        double validatedLadderBottomFt = ladderFloorFt + 25.0 / MmPerFoot;
                        double validatedLadderTopFt = topFt + 80.0 / MmPerFoot;
                        List<Solid> validatedLadder = ladderType == MaintenanceLadderType.AFrame
                            ? MaintenanceGeometryService.BuildAFrameLadder(
                                ladderCenter,
                                ladderAlong,
                                validatedLadderBottomFt,
                                validatedLadderTopFt)
                            : MaintenanceGeometryService.BuildStraightLadder(
                                ladderCenter,
                                ladderAlong,
                                validatedLadderBottomFt,
                                validatedLadderTopFt);
                        MaintenanceCollisionResult leftDoorLadderCollision = null;
                        MaintenanceCollisionResult rightDoorLadderCollision = null;
                        if (leftSwingStatus == MaintenanceDoorSwingStatus.Clear)
                        {
                            Solid leftOpenLeaf = MaintenanceGeometryService.BuildOutwardOpenDoorLeaf(
                                p + XYZ.BasisZ.Multiply(swingLiftFt),
                                inward,
                                result.DoorWidthMm / MmPerFoot,
                                MaintenanceDoorSwingPolicy.LeafThicknessMm / MmPerFoot,
                                (result.DoorHeightMm - CollisionLiftMm) / MmPerFoot,
                                MaintenanceDoorSwingPolicy.OutboardOffsetMm / MmPerFoot,
                                MaintenanceDoorHingeSide.Left);
                            leftDoorLadderCollision = MaintenanceGeometryService.ValidateGeneratedBodies(
                                new[] { leftOpenLeaf },
                                validatedLadder,
                                "左铰链向外全开门扇与梯具冲突");
                            if (!leftDoorLadderCollision.IsClear)
                                leftSwingStatus = ToDoorSwingStatus(leftDoorLadderCollision);
                        }
                        if (rightSwingStatus == MaintenanceDoorSwingStatus.Clear)
                        {
                            Solid rightOpenLeaf = MaintenanceGeometryService.BuildOutwardOpenDoorLeaf(
                                p + XYZ.BasisZ.Multiply(swingLiftFt),
                                inward,
                                result.DoorWidthMm / MmPerFoot,
                                MaintenanceDoorSwingPolicy.LeafThicknessMm / MmPerFoot,
                                (result.DoorHeightMm - CollisionLiftMm) / MmPerFoot,
                                MaintenanceDoorSwingPolicy.OutboardOffsetMm / MmPerFoot,
                                MaintenanceDoorHingeSide.Right);
                            rightDoorLadderCollision = MaintenanceGeometryService.ValidateGeneratedBodies(
                                new[] { rightOpenLeaf },
                                validatedLadder,
                                "右铰链向外全开门扇与梯具冲突");
                            if (!rightDoorLadderCollision.IsClear)
                                rightSwingStatus = ToDoorSwingStatus(rightDoorLadderCollision);
                        }
                        selectedHinge = MaintenanceDoorSwingPolicy.Select(
                            leftSwingStatus,
                            rightSwingStatus);
                        if (audit != null)
                        {
                            audit.LeftDoorSwingStatus = leftSwingStatus;
                            audit.RightDoorSwingStatus = rightSwingStatus;
                        }
                        if (selectedHinge == MaintenanceDoorHingeSide.None)
                        {
                            RejectEntryEvaluation(
                                result,
                                audit,
                                MaintenanceCandidateStage.DoorSwing,
                                "outward_door_swing_ladder_conflict",
                                "门扇向外全开后与已验证梯具不相容，左右铰链均不能形成完整入口链。",
                                new[] { leftDoorLadderCollision, rightDoorLadderCollision },
                                candidates);
                            continue;
                        }
                        ladderPass++;
                        var dto = new MaintenanceEntryCandidate
                        {
                            CandidateKey = candidateKey,
                            GroupKey = groupKey,
                            EntryType = MaintenanceEntryType.WallDoor,
                            LadderType = ladderType,
                            BoundaryLoopIndex = loopIndex,
                            BoundarySegmentIndex = segmentIndex,
                            Center = new MaintenancePoint3(
                                p2.X,
                                p2.Y,
                                topFt * MmPerFoot + result.DoorHeightMm * 0.5),
                            InwardDirection = inward2,
                            OpeningWidthMm = result.DoorWidthMm,
                            OpeningHeightMm = result.DoorHeightMm,
                            DoorHingeSide = selectedHinge,
                            LeftDoorSwingStatus = leftSwingStatus,
                            RightDoorSwingStatus = rightSwingStatus,
                            LadderFloorMm = ladderFloorFt * MmPerFoot,
                            IsFeasible = true
                        };
                        foreach (string sourceKey in ladderSupportSourceKeys)
                            dto.LadderSupportSourceKeys.Add(sourceKey);
                        foreach (string sourceKey in openingHostSourceKeys
                            .OrderBy(x => x, StringComparer.Ordinal))
                            dto.OpeningHostSourceKeys.Add(sourceKey);
                        AddBlocker(dto.LeftDoorSwingBlockers, candidates, leftSwingCollision);
                        AddBlocker(dto.RightDoorSwingBlockers, candidates, rightSwingCollision);
                        var work = new EntryWork
                        {
                            Profile = spec.Profile,
                            Candidate = dto,
                            Start = start,
                            Component = component,
                            WallPoint = p,
                            Tangent = tangent,
                            Inward = inward,
                            LadderPlanCenter = ladderCenter,
                            LadderAlong = ladderAlong,
                            LadderFloorFt = ladderFloorFt
                        };
                        foreach (TargetWork target in targets)
                        {
                            if (!target.HasGoal || target.Grid == null ||
                                target.ComponentLabels == null) continue;
                            int targetStartComponent = target.ComponentLabels[
                                target.Grid.ToIndex(start.X, start.Y)];
                            int targetComponent = target.ComponentLabels[
                                target.Grid.ToIndex(target.Goal.X, target.Goal.Y)];
                            if (targetStartComponent >= 0 &&
                                targetStartComponent == targetComponent)
                                work.CoveredTargets.Add(target.Target.TargetKey);
                        }
                        if (work.CoveredTargets.Count > 0)
                        {
                            dto.CoveredTargetCount = work.CoveredTargets.Count;
                            entries.Add(work);
                            CompleteEntryEvaluation(result, audit, dto, ladderRejections, candidates);
                        }
                        else
                        {
                            RejectEntryEvaluation(result, audit, MaintenanceCandidateStage.Connectivity,
                                "no_target_in_component",
                                "入口可进入，但与任何设备检修侧都不在同一可通行区。",
                                null,
                                candidates);
                        }
                    }
                }
            }
            LastDiagnostics.Add(groupKey + ": wallStages sampled=" + sampledCount +
                ", footprint=" + footprintPass + ", turn=" + turnPass +
                ", opening=" + openingPass + ", frame=" + framePass +
                ", swing=" + doorSwingPass +
                ", portal=" + portalPass +
                ", start=" + startPass + ", ladder=" + ladderPass);
            RecordCandidateCoverage(
                result,
                groupKey,
                string.Empty,
                spec,
                MaintenanceEntryType.WallDoor,
                sampledCount,
                sampledCount,
                sampledCount,
                sampledCount,
                0,
                false,
                400.0,
                "wall_boundary_samples_400mm");
            return entries;
        }

        private static HashSet<string> ResolveOpeningHostWallKeys(
            Solid opening,
            XYZ boundaryTangent,
            IList<PlenumAnalysisService.Candidate> candidates)
        {
            var output = new HashSet<string>(StringComparer.Ordinal);
            if (opening == null || boundaryTangent == null || candidates == null)
                return output;

            // The first exact collision may be exempted only when it is the one
            // physical wall aligned with this boundary segment. A second wall or
            // any non-wall obstacle remains a real collision and is not ignored.
            MaintenanceCollisionResult collision =
                MaintenanceGeometryService.Validate(opening, candidates, null);
            if (collision == null ||
                collision.State != MaintenanceCollisionState.Conflict ||
                string.IsNullOrWhiteSpace(collision.BlockerKey))
                return output;

            PlenumAnalysisService.Candidate wall = candidates.FirstOrDefault(x =>
                x != null &&
                string.Equals(x.SourceKey, collision.BlockerKey, StringComparison.Ordinal));
            if (!IsAlignedOpeningHostWall(wall, boundaryTangent))
                return output;
            output.Add(wall.SourceKey);
            return output;
        }

        private static bool IsAlignedOpeningHostWall(
            PlenumAnalysisService.Candidate candidate,
            XYZ boundaryTangent)
        {
            if (candidate == null ||
                candidate.Category != BuiltInCategory.OST_Walls ||
                candidate.Element == null ||
                boundaryTangent == null)
                return false;
            try
            {
                LocationCurve location = candidate.Element.Location as LocationCurve;
                Curve curve = location == null ? null : location.Curve;
                if (curve == null || !curve.IsBound) return false;
                XYZ wallDirection = curve.GetEndPoint(1) - curve.GetEndPoint(0);
                Transform toHost = candidate.ToHost ?? Transform.Identity;
                wallDirection = toHost.OfVector(wallDirection);
                XYZ wallHorizontal = new XYZ(
                    wallDirection.X,
                    wallDirection.Y,
                    0.0);
                XYZ boundaryHorizontal = new XYZ(
                    boundaryTangent.X,
                    boundaryTangent.Y,
                    0.0);
                if (wallHorizontal.GetLength() <= 1e-9 ||
                    boundaryHorizontal.GetLength() <= 1e-9)
                    return false;
                double dot = wallHorizontal.Normalize().DotProduct(
                    boundaryHorizontal.Normalize());
                return MaintenanceOpeningHostWallPolicy.IsDirectionAligned(dot);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryValidateLadder(
            XYZ wallPoint,
            XYZ tangent,
            XYZ outward,
            double floorFt,
            double topFt,
            IList<PlenumAnalysisService.Candidate> candidates,
            IList<TargetWork> affectedTargets,
            ISet<string> openingHostSourceKeys,
            out MaintenanceLadderType type,
            out XYZ planCenter,
            out XYZ along,
            out double supportFloorFt,
            out List<string> supportSourceKeys,
            out string failureCode,
            out string failureReason,
            out List<MaintenanceCollisionResult> rejectionCollisions)
        {
            type = MaintenanceLadderType.None;
            rejectionCollisions = new List<MaintenanceCollisionResult>();
            supportFloorFt = double.NaN;
            supportSourceKeys = new List<string>();
            failureCode = "all_ladders_conflict";
            failureReason = "人字梯和一字梯及其操作区均无法无碰撞布置。";
            along = outward;
            planCenter = wallPoint + outward.Multiply(800.0 / MmPerFoot);
            double ladderTop = topFt + 80.0 / MmPerFoot;
            XYZ aZonePlan = wallPoint + outward.Multiply(1250.0 / MmPerFoot);
            List<MaintenancePoint2> aZoneSupports =
                MaintenanceLadderFloorPolicy.BuildOperationZoneSupportPoints(
                    ToPoint2Mm(aZonePlan),
                    new MaintenancePoint2(tangent.X, tangent.Y),
                    1200.0,
                    2500.0);
            MaintenanceLadderFloorDecision aSupport = ResolveLadderFloorSupport(
                MaintenanceLadderType.AFrame,
                planCenter,
                outward,
                floorFt,
                topFt,
                aZoneSupports,
                candidates);
            MaintenanceCollisionResult aSupportResult = ToSupportCollision(aSupport);
            MaintenanceCollisionResult aZoneResult = null;
            MaintenanceCollisionResult aLadderResult = null;
            if (aSupport.IsClear)
            {
                supportFloorFt = aSupport.FloorElevationMm / MmPerFoot;
                // Rod geometry has an 18 mm radius. Lift its centreline by
                // 25 mm after resolving the real support face, so the validated
                // feet remain above (and do not numerically penetrate) the slab.
                double ladderFloor = supportFloorFt + 25.0 / MmPerFoot;
                Solid aZone = MaintenanceGeometryService.MakeBox(
                    new XYZ(aZonePlan.X, aZonePlan.Y, ladderFloor),
                    1200.0 / MmPerFoot,
                    2500.0 / MmPerFoot,
                    ladderTop - ladderFloor,
                    tangent);
                List<Solid> aLadder = MaintenanceGeometryService.BuildAFrameLadder(
                    planCenter,
                    outward,
                    ladderFloor,
                    ladderTop);
                // The outside operation zone starts at the door wall plane, so
                // overlapping the exact owner wall is expected. The physical
                // ladder body below still checks every wall and MEP obstacle.
                aZoneResult = MaintenanceGeometryService.Validate(
                    aZone,
                    candidates,
                    openingHostSourceKeys);
                aLadderResult = MaintenanceGeometryService.Validate(aLadder, candidates, null);
                if (aZoneResult.IsClear && aLadderResult.IsClear)
                {
                    type = MaintenanceLadderType.AFrame;
                    supportSourceKeys.AddRange(aSupport.SourceKeys);
                    return true;
                }
                rejectionCollisions.Add(aZoneResult);
                rejectionCollisions.Add(aLadderResult);
            }
            else
            {
                rejectionCollisions.Add(aSupportResult);
            }

            planCenter = wallPoint + outward.Multiply(550.0 / MmPerFoot);
            along = -outward;
            XYZ straightZonePlan = wallPoint + outward.Multiply(800.0 / MmPerFoot);
            List<MaintenancePoint2> straightZoneSupports =
                MaintenanceLadderFloorPolicy.BuildOperationZoneSupportPoints(
                    ToPoint2Mm(straightZonePlan),
                    new MaintenancePoint2(tangent.X, tangent.Y),
                    1000.0,
                    1600.0);
            MaintenanceLadderFloorDecision straightSupport = ResolveLadderFloorSupport(
                MaintenanceLadderType.Straight,
                planCenter,
                along,
                floorFt,
                topFt,
                straightZoneSupports,
                candidates);
            MaintenanceCollisionResult straightSupportResult = ToSupportCollision(straightSupport);
            MaintenanceCollisionResult straightZoneResult = null;
            MaintenanceCollisionResult straightResult = null;
            if (straightSupport.IsClear)
            {
                supportFloorFt = straightSupport.FloorElevationMm / MmPerFoot;
                double ladderFloor = supportFloorFt + 25.0 / MmPerFoot;
                Solid straightZone = MaintenanceGeometryService.MakeBox(
                    new XYZ(straightZonePlan.X, straightZonePlan.Y, ladderFloor),
                    1000.0 / MmPerFoot,
                    1600.0 / MmPerFoot,
                    ladderTop - ladderFloor,
                    tangent);
                List<Solid> straight = MaintenanceGeometryService.BuildStraightLadder(
                    planCenter,
                    along,
                    ladderFloor,
                    ladderTop);
                straightZoneResult = MaintenanceGeometryService.Validate(
                    straightZone,
                    candidates,
                    openingHostSourceKeys);
                straightResult = MaintenanceGeometryService.Validate(straight, candidates, null);
                if (straightZoneResult.IsClear && straightResult.IsClear)
                {
                    type = MaintenanceLadderType.Straight;
                    supportSourceKeys.AddRange(straightSupport.SourceKeys);
                    return true;
                }
                rejectionCollisions.Add(straightZoneResult);
                rejectionCollisions.Add(straightResult);
            }
            else
            {
                rejectionCollisions.Add(straightSupportResult);
            }
            foreach (MaintenanceCollisionResult rejection in rejectionCollisions)
                RecordEntryCollision(affectedTargets, rejection, candidates);
            MaintenanceCollisionResult firstUnverified = rejectionCollisions.FirstOrDefault(
                x => x != null && x.State == MaintenanceCollisionState.Unverified);
            if (firstUnverified != null)
            {
                failureCode = "ladder_floor_or_geometry_unverified";
                failureReason = "至少一种梯具的楼面支撑或碰撞几何无法完整验证，不能正式放行。";
            }
            else if (!aSupport.IsClear && !straightSupport.IsClear)
            {
                failureCode = string.Equals(
                    aSupport.ReasonCode,
                    straightSupport.ReasonCode,
                    StringComparison.Ordinal)
                    ? aSupport.ReasonCode
                    : "ladder_floor_support_missing";
                failureReason = "人字梯和一字梯均缺少完整、平整的真实楼板支撑。";
            }
            if (LastDiagnostics.Count(x => x.StartsWith("ladderReject", StringComparison.Ordinal)) < 12)
                LastDiagnostics.Add("ladderReject p=" +
                    Math.Round(wallPoint.X * MmPerFoot, 0) + "," +
                    Math.Round(wallPoint.Y * MmPerFoot, 0) +
                    " aSupport=" + aSupport.State +
                    " aZone=" + CollisionDiagnostic(aZoneResult) +
                    " aGeom=" + CollisionDiagnostic(aLadderResult) +
                    " sSupport=" + straightSupport.State +
                    " sZone=" + CollisionDiagnostic(straightZoneResult) +
                    " sGeom=" + CollisionDiagnostic(straightResult));
            return false;
        }

        private static MaintenanceLadderFloorDecision ResolveLadderFloorSupport(
            MaintenanceLadderType ladderType,
            XYZ planCenter,
            XYZ along,
            double seedFloorFt,
            double topFt,
            IEnumerable<MaintenancePoint2> operationZoneSupportPoints,
            IList<PlenumAnalysisService.Candidate> candidates)
        {
            double workingSurfaceMm = seedFloorFt * MmPerFoot;
            MaintenanceLadderFloorDecision last = null;
            List<MaintenancePoint2> zonePoints = operationZoneSupportPoints == null
                ? new List<MaintenancePoint2>()
                : operationZoneSupportPoints.ToList();
            for (int iteration = 0; iteration < 4; iteration++)
            {
                List<MaintenancePoint2> points =
                    MaintenanceLadderFloorPolicy.BuildSupportPoints(
                        ladderType,
                        ToPoint2Mm(planCenter),
                        new MaintenancePoint2(along.X, along.Y),
                        workingSurfaceMm + 25.0,
                        topFt * MmPerFoot + 80.0);
                points.AddRange(zonePoints);
                List<MaintenanceFloorSupportSample> samples = points
                    .Select(x => ResolveFloorSupportAtPoint(x, topFt, candidates))
                    .ToList();
                last = MaintenanceLadderFloorPolicy.Evaluate(samples, points.Count);
                if (!last.IsClear) return last;
                if (Math.Abs(last.FloorElevationMm - workingSurfaceMm) <= 0.1)
                    return last;
                workingSurfaceMm = last.FloorElevationMm;
            }

            var nonConvergent = new MaintenanceLadderFloorDecision
            {
                State = MaintenanceFloorSupportState.Unverified,
                ReasonCode = "ladder_floor_support_unverified",
                Reason = "梯脚位置随局部楼面标高迭代后未稳定，不能验证真实支撑。"
            };
            if (last != null)
                foreach (string key in last.SourceKeys) nonConvergent.SourceKeys.Add(key);
            return nonConvergent;
        }

        private static MaintenanceFloorSupportSample ResolveFloorSupportAtPoint(
            MaintenancePoint2 pointMm,
            double topFt,
            IList<PlenumAnalysisService.Candidate> candidates)
        {
            const double xyToleranceMm = 2.0;
            double xFt = pointMm.X / MmPerFoot;
            double yFt = pointMm.Y / MmPerFoot;
            double minimumFt = topFt - 4000.0 / MmPerFoot;
            double maximumFt = topFt - 50.0 / MmPerFoot;
            double padFt = xyToleranceMm / MmPerFoot;
            double bestFt = double.NegativeInfinity;
            string bestSourceKey = string.Empty;
            string unknownSourceKey = string.Empty;
            string unknownReason = string.Empty;

            if (candidates == null)
                return NewFloorSupportSample(
                    MaintenanceFloorSupportState.Unverified,
                    double.NaN,
                    string.Empty,
                    "楼板候选集合不可用，无法验证梯具支撑。");

            foreach (PlenumAnalysisService.Candidate candidate in candidates
                .Where(x => x != null && x.Category == BuiltInCategory.OST_Floors)
                .OrderBy(x => x.SourceKey, StringComparer.Ordinal))
            {
                if (candidate.WorldBounds == null)
                {
                    if (string.IsNullOrEmpty(unknownReason))
                    {
                        unknownSourceKey = candidate.SourceKey ?? string.Empty;
                        unknownReason = "楼板候选缺少宿主坐标包围盒，无法排除其与梯脚相关。";
                    }
                    continue;
                }
                if (!candidate.WorldBounds.ContainsVertical(
                    xFt, yFt, minimumFt, maximumFt, padFt)) continue;

                bool linked = candidate.Source != null &&
                              candidate.Source.LinkInstanceId.HasValue;
                if ((linked && (candidate.ToHost == null || candidate.FromHost == null)) ||
                    candidate.Solids == null || candidate.Solids.Count == 0 ||
                    candidate.MeshCount > 0 ||
                    !string.IsNullOrEmpty(candidate.GeometryError) ||
                    candidate.WorldSolidBounds == null ||
                    candidate.WorldSolidBounds.Count != candidate.Solids.Count ||
                    candidate.WorldSolidBounds.Any(x => x == null))
                {
                    if (string.IsNullOrEmpty(unknownReason))
                    {
                        unknownSourceKey = candidate.SourceKey ?? string.Empty;
                        unknownReason = "梯脚下方楼板实体或链接变换无法完整验证。";
                    }
                    continue;
                }

                Transform toHost = candidate.ToHost ?? Transform.Identity;
                Transform fromHost = candidate.FromHost ?? Transform.Identity;
                Line sourceLine;
                try
                {
                    sourceLine = Line.CreateBound(
                        fromHost.OfPoint(new XYZ(xFt, yFt, minimumFt - padFt)),
                        fromHost.OfPoint(new XYZ(xFt, yFt, maximumFt + padFt)));
                }
                catch (Exception ex)
                {
                    if (string.IsNullOrEmpty(unknownReason))
                    {
                        unknownSourceKey = candidate.SourceKey ?? string.Empty;
                        unknownReason = "梯脚竖向探针无法转换到楼板来源模型：" +
                                        ex.GetType().Name;
                    }
                    continue;
                }

                for (int solidIndex = 0; solidIndex < candidate.Solids.Count; solidIndex++)
                {
                    PlenumAnalysisService.Bounds3 bounds =
                        candidate.WorldSolidBounds[solidIndex];
                    if (!bounds.ContainsVertical(
                        xFt, yFt, minimumFt, maximumFt, padFt)) continue;
                    Solid solid = candidate.Solids[solidIndex];
                    if (solid == null)
                    {
                        if (string.IsNullOrEmpty(unknownReason))
                        {
                            unknownSourceKey = candidate.SourceKey ?? string.Empty;
                            unknownReason = "梯脚下方楼板候选包含空实体。";
                        }
                        continue;
                    }

                    foreach (Face face in solid.Faces)
                    {
                        try
                        {
                            PlanarFace planar = face as PlanarFace;
                            if (planar != null)
                            {
                                XYZ planarNormal = toHost.OfVector(planar.FaceNormal);
                                if (planarNormal == null || planarNormal.GetLength() <= 1e-9 ||
                                    planarNormal.Normalize().Z < 0.7) continue;
                            }

                            IntersectionResultArray intersections;
                            face.Intersect(sourceLine, out intersections);
                            if (intersections == null) continue;
                            for (int hitIndex = 0; hitIndex < intersections.Size; hitIndex++)
                            {
                                IntersectionResult hit = intersections.get_Item(hitIndex);
                                if (hit == null || hit.XYZPoint == null) continue;
                                UV uv = hit.UVPoint;
                                if (uv == null)
                                {
                                    IntersectionResult projection = face.Project(hit.XYZPoint);
                                    if (projection == null || projection.UVPoint == null ||
                                        projection.Distance > padFt) continue;
                                    uv = projection.UVPoint;
                                }
                                if (!face.IsInside(uv)) continue;
                                XYZ hostPoint = toHost.OfPoint(hit.XYZPoint);
                                if (hostPoint == null ||
                                    Math.Abs(hostPoint.X - xFt) > padFt ||
                                    Math.Abs(hostPoint.Y - yFt) > padFt ||
                                    hostPoint.Z < minimumFt - padFt ||
                                    hostPoint.Z > maximumFt + padFt) continue;
                                XYZ hostNormal = toHost.OfVector(face.ComputeNormal(uv));
                                if (hostNormal == null || hostNormal.GetLength() <= 1e-9 ||
                                    hostNormal.Normalize().Z < 0.7) continue;
                                if (hostPoint.Z > bestFt)
                                {
                                    bestFt = hostPoint.Z;
                                    bestSourceKey = candidate.SourceKey ?? string.Empty;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (string.IsNullOrEmpty(unknownReason))
                            {
                                unknownSourceKey = candidate.SourceKey ?? string.Empty;
                                unknownReason = "梯脚与真实楼板面求交失败：" +
                                                ex.GetType().Name;
                            }
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(unknownReason))
                return NewFloorSupportSample(
                    MaintenanceFloorSupportState.Unverified,
                    double.NaN,
                    unknownSourceKey,
                    unknownReason);
            if (!double.IsNegativeInfinity(bestFt))
                return NewFloorSupportSample(
                    MaintenanceFloorSupportState.Clear,
                    bestFt * MmPerFoot,
                    bestSourceKey,
                    "梯具支撑点已命中真实楼板上表面。");
            return NewFloorSupportSample(
                MaintenanceFloorSupportState.Missing,
                double.NaN,
                string.Empty,
                "梯具支撑点下方没有命中真实楼板上表面。");
        }

        private static MaintenanceFloorSupportSample NewFloorSupportSample(
            MaintenanceFloorSupportState state,
            double elevationMm,
            string sourceKey,
            string reason)
        {
            return new MaintenanceFloorSupportSample
            {
                State = state,
                ElevationMm = elevationMm,
                SourceKey = sourceKey ?? string.Empty,
                Reason = reason ?? string.Empty
            };
        }

        private static MaintenanceCollisionResult ToSupportCollision(
            MaintenanceLadderFloorDecision decision)
        {
            if (decision == null)
                return new MaintenanceCollisionResult
                {
                    State = MaintenanceCollisionState.Unverified,
                    Reason = "ladder floor support decision is unavailable"
                };
            return new MaintenanceCollisionResult
            {
                State = decision.State == MaintenanceFloorSupportState.Clear
                    ? MaintenanceCollisionState.Clear
                    : (decision.State == MaintenanceFloorSupportState.Unverified
                        ? MaintenanceCollisionState.Unverified
                        : MaintenanceCollisionState.Conflict),
                BlockerKey = decision.SourceKeys.FirstOrDefault(),
                Reason = decision.Reason
            };
        }

        private static string CollisionDiagnostic(MaintenanceCollisionResult collision)
        {
            return collision == null
                ? "not_run"
                : collision.State + "/" + (collision.BlockerKey ?? string.Empty);
        }

        private static Dictionary<string, EntryWork> SelectMinimumWallEntries(
            IList<EntryWork> entries,
            IList<TargetWork> targets,
            MaintenanceGrid grid,
            int[] labels,
            ISet<string> preferredEntryKeys = null)
        {
            var assigned = new Dictionary<string, EntryWork>(StringComparer.Ordinal);
            var uncovered = new HashSet<string>(
                targets.Where(x => x.HasGoal).Select(x => x.Target.TargetKey),
                StringComparer.Ordinal);
            var available = entries.ToList();
            while (uncovered.Count > 0)
            {
                EntryWork best = available
                    .Select(x => new
                    {
                        Entry = x,
                        Count = x.CoveredTargets.Count(uncovered.Contains),
                        AFrame = x.Candidate.LadderType == MaintenanceLadderType.AFrame ? 1 : 0,
                        Preferred = preferredEntryKeys != null &&
                            preferredEntryKeys.Contains(x.Candidate.CandidateKey) ? 1 : 0
                    })
                    .Where(x => x.Count > 0)
                    .OrderByDescending(x => x.Count)
                    .ThenByDescending(x => x.Preferred)
                    .ThenByDescending(x => x.AFrame)
                    .ThenBy(x => x.Entry.Candidate.CandidateKey, StringComparer.Ordinal)
                    .Select(x => x.Entry)
                    .FirstOrDefault();
                if (best == null) break;
                foreach (string targetKey in best.CoveredTargets.Where(uncovered.Contains).ToList())
                {
                    assigned[targetKey] = best;
                    uncovered.Remove(targetKey);
                }
                available.Remove(best);
            }
            return assigned;
        }

        private static List<WallAlternativeWork> CaptureWallAlternatives(
            IList<EntryWork> entries,
            IList<TargetWork> targets,
            MaintenanceGrid grid,
            IList<PlenumAnalysisService.Candidate> candidates,
            ProfileSpec spec)
        {
            var output = new List<WallAlternativeWork>();
            foreach (TargetWork target in targets.Where(x => x != null && x.Target != null))
            {
                WallAlternativeWork best = entries
                    .Where(x => x != null && x.Candidate != null &&
                                x.Candidate.EntryType == MaintenanceEntryType.WallDoor &&
                                x.CoveredTargets.Contains(target.Target.TargetKey))
                    .Select(x =>
                    {
                        MaintenanceCollisionState state = GetRouteValidationState(
                            target, x, target.Grid ?? grid, candidates, spec);
                        return new WallAlternativeWork
                        {
                            Chain = new ChainWork
                            {
                                Spec = spec,
                                Grid = target.Grid ?? grid,
                                Target = target,
                                Entry = x
                            },
                            RouteState = state,
                            RouteLengthMm = CalculateRouteLengthMm(
                                target, x, target.Grid ?? grid)
                        };
                    })
                    .Where(x => x.RouteState == MaintenanceCollisionState.Clear ||
                                x.RouteState == MaintenanceCollisionState.Unverified)
                    .OrderBy(x => x.RouteState == MaintenanceCollisionState.Clear ? 0 : 1)
                    .ThenBy(x => x.RouteLengthMm <= 0.0 ? double.MaxValue : x.RouteLengthMm)
                    .ThenByDescending(x => x.Chain.Entry.Candidate.LadderType ==
                        MaintenanceLadderType.AFrame)
                    .ThenBy(x => x.Chain.Entry.Candidate.CandidateKey, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (best != null) output.Add(best);
            }
            return output;
        }

        private static double CalculateRouteLengthMm(
            TargetWork target,
            EntryWork entry,
            MaintenanceGrid grid)
        {
            if (target == null || entry == null || grid == null || !target.HasGoal)
                return 0.0;
            List<GridCell> route = MaintenancePathfinder.FindPath8(
                grid, entry.Start, target.Goal);
            if (route.Count == 0) return 0.0;
            return MaintenancePathfinder.CalculatePathLength(
                grid,
                MaintenancePathfinder.SimplifyPath(grid, route));
        }

        private static void BuildWallAlternativeResults(
            MaintenanceCeilingGroup group,
            IList<TargetWork> fullTargets,
            IDictionary<string, ChainWork> selectedChains,
            IList<WallAlternativeWork> works,
            IList<PlenumAnalysisService.Candidate> candidates,
            double floorFt,
            MaintenanceAnalysisResult result)
        {
            foreach (TargetWork fullTarget in fullTargets
                .Where(x => x != null && x.Target != null)
                .OrderBy(x => x.Target.DeviceNo, StringComparer.Ordinal))
            {
                string targetKey = fullTarget.Target.TargetKey;
                string deviceNo = fullTarget.Target.DeviceNo;
                string alternativeKey = group.GroupKey + "|" + targetKey + "|side-wall-alternative";
                List<MaintenanceWallAlternativeResult> choices = works
                    .Where(x => x != null && x.Chain != null && x.Chain.Target != null &&
                                x.Chain.Target.Target != null && x.Chain.Entry != null &&
                                string.Equals(x.Chain.Target.Target.TargetKey, targetKey,
                                    StringComparison.Ordinal))
                    .Select(x => new MaintenanceWallAlternativeResult
                    {
                        AlternativeKey = alternativeKey,
                        GroupKey = group.GroupKey,
                        TargetKey = targetKey,
                        DeviceNo = deviceNo,
                        Status = x.RouteState == MaintenanceCollisionState.Clear
                            ? MaintenanceWallAlternativeStatus.Available
                            : MaintenanceWallAlternativeStatus.AvailablePendingReview,
                        CanVisualize = true,
                        Profile = x.Chain.Spec.Profile,
                        EntryType = x.Chain.Entry.Candidate.EntryType,
                        LadderType = x.Chain.Entry.Candidate.LadderType,
                        RouteLengthMm = x.RouteLengthMm,
                        SelectedEntry = x.Chain.Entry.Candidate
                    })
                    .ToList();
                MaintenanceWallAlternativeResult selected =
                    MaintenanceWallAlternativePolicy.SelectPreferred(choices);
                if (selected == null)
                {
                    selected = new MaintenanceWallAlternativeResult
                    {
                        AlternativeKey = alternativeKey,
                        GroupKey = group.GroupKey,
                        TargetKey = targetKey,
                        DeviceNo = deviceNo,
                        Status = MaintenanceWallAlternativeStatus.UnavailableNoModelableWall,
                        CanVisualize = false,
                        EntryType = MaintenanceEntryType.WallDoor,
                        Reason = "未找到同时具备侧墙门、梯具、转身区和设备路线完整几何的可建模侧墙备选；未生成猜测模型。"
                    };
                    result.WallAlternatives.Add(selected);
                    continue;
                }

                WallAlternativeWork selectedWork = works.First(x =>
                    x != null && x.Chain != null && x.Chain.Entry != null &&
                    x.Chain.Target != null && x.Chain.Target.Target != null &&
                    string.Equals(x.Chain.Target.Target.TargetKey, targetKey,
                        StringComparison.Ordinal) &&
                    x.Chain.Spec.Profile == selected.Profile &&
                    string.Equals(x.Chain.Entry.Candidate.CandidateKey,
                        selected.SelectedEntry.CandidateKey,
                        StringComparison.Ordinal));
                ChainWork formal;
                selectedChains.TryGetValue(targetKey, out formal);
                selected.SameAsRouteFormal = formal != null && formal.Entry != null &&
                    formal.Entry.Candidate.EntryType == MaintenanceEntryType.WallDoor &&
                    formal.Spec.Profile == selected.Profile &&
                    string.Equals(formal.Entry.Candidate.CandidateKey,
                        selected.SelectedEntry.CandidateKey,
                        StringComparison.Ordinal);
                string entryGroup = BuildWallAlternativeEntryGroup(
                    group.GroupKey, deviceNo, 0);
                var names = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { selectedWork.Chain.Entry.Candidate.CandidateKey, entryGroup }
                };
                MaintenanceTargetResult route = AnalyzeTargetRoute(
                    group.GroupKey,
                    selectedWork.Chain.Target,
                    selectedWork.Chain.Entry,
                    selectedWork.Chain.Grid,
                    candidates,
                    names,
                    result.AnalysisId,
                    selectedWork.Chain.Spec);
                selected.EntryGroup = entryGroup;
                selected.ViewName = BuildWallAlternativeViewName(
                    group.GroupKey, deviceNo, 0);
                selected.Decision = route.Decision;
                selected.DecisionReason = route.DecisionReason;
                selected.RouteLengthMm = route.RouteLengthMm;
                selected.Reason = selected.Status == MaintenanceWallAlternativeStatus.Available
                    && route.Decision == MaintenanceDecision.Pass
                    ? "保留的最佳可建模侧墙备选，完整几何验证通过。"
                    : "保留的最佳可建模侧墙备选含未验证或专业判断项，显示后仍需人工复核。";
                if (route.Decision != MaintenanceDecision.Pass)
                    selected.Status = MaintenanceWallAlternativeStatus.AvailablePendingReview;
                foreach (MaintenancePoint3 point in route.Route) selected.Route.Add(point);
                foreach (MaintenanceElementRef blocker in route.Blockers) selected.Blockers.Add(blocker);
                selected.RenderItems.AddRange(route.RenderItems);
                selected.RenderItems.AddRange(BuildEntryRenderItems(
                    group,
                    selectedWork.Chain.Entry,
                    entryGroup,
                    fullTarget.Target.GetDisplayName().Replace(" | ", "｜"),
                    new[] { targetKey },
                    floorFt,
                    result.AnalysisId));
                selected.RenderItems.AddRange(BuildVirtualWallItems(
                    group,
                    new[] { selectedWork.Chain.Entry },
                    names,
                    result.AnalysisId));
                foreach (MaintenanceRenderItem item in selected.RenderItems.Where(x => x != null))
                {
                    item.RenderKey = alternativeKey + "|" + (item.RenderKey ?? string.Empty);
                    item.AnalysisId = result.AnalysisId;
                    item.TargetKey = targetKey;
                    item.Parameters.CeilingGroup = group.GroupKey;
                    item.Parameters.EntryGroup = entryGroup;
                    item.Parameters.MaintenanceTarget =
                        fullTarget.Target.GetDisplayName().Replace(" | ", "｜");
                    if (!item.SourceKeys.Contains(targetKey)) item.SourceKeys.Add(targetKey);
                }
                if (!MaintenanceWallAlternativePolicy.IsRenderGeometryComplete(
                    selected.RenderItems))
                {
                    selected.RenderItems.Clear();
                    selected.Route.Clear();
                    selected.Status = MaintenanceWallAlternativeStatus.UnavailableIncompleteGeometry;
                    selected.CanVisualize = false;
                    selected.Reason = "侧墙备选缺少可验证的门、梯具、路线或边界几何；已拒绝生成猜测模型。";
                }
                else
                {
                    selected.CanVisualize = true;
                    selected.GeometryFingerprint =
                        MaintenanceWallAlternativePolicy.ComputeFingerprint(new[] { selected });
                }
                result.WallAlternatives.Add(selected);
            }
        }

        internal static string BuildWallAlternativeEntryGroup(
            string groupKey,
            string deviceNo,
            int schemeNo)
        {
            return (groupKey ?? string.Empty) + "-设备" + (deviceNo ?? string.Empty) +
                   "-方案" + Math.Max(0, schemeNo).ToString("00") + "-侧墙备选";
        }

        internal static string BuildWallAlternativeViewName(
            string groupKey,
            string deviceNo,
            int schemeNo)
        {
            return "天花" + (groupKey ?? string.Empty) + "-设备" + (deviceNo ?? string.Empty) +
                   "-方案" + Math.Max(0, schemeNo).ToString("00") + "-侧墙备选";
        }

        private static EntryWork BuildBestHatchEntry(
            string groupKey,
            TargetWork target,
            Mask footprint,
            MaintenanceGrid grid,
            int[] labels,
            double topFt,
            double floorFt,
            ISet<string> ignoredGroupCeilingKeys,
            IList<PlenumAnalysisService.Candidate> candidates,
            ProfileSpec spec,
            MaintenanceAnalysisResult result,
            int maxCandidates,
            bool selectionRequired,
            bool sharedEntryReview,
            ICollection<EntryWork> retainedEntries)
        {
            if (target == null || target.Target == null || !target.HasGoal)
            {
                RecordCandidateCoverage(
                    result,
                    groupKey,
                    target == null || target.Target == null ? string.Empty : target.Target.TargetKey,
                    spec,
                    MaintenanceEntryType.CeilingHatch,
                    0,
                    0,
                    0,
                    0,
                    0,
                    false,
                    HatchRepresentativeSpacingMm,
                    "hatch_spatial_buckets_400mm_nearest_to_target");
                EnsureRouteEvaluation(groupKey, target, grid, candidates, spec, result);
                return null;
            }
            int component = labels[grid.ToIndex(target.Goal.X, target.Goal.Y)];
            if (component < 0)
            {
                RecordCandidateCoverage(
                    result,
                    groupKey,
                    target.Target.TargetKey,
                    spec,
                    MaintenanceEntryType.CeilingHatch,
                    0,
                    0,
                    0,
                    0,
                    0,
                    false,
                    HatchRepresentativeSpacingMm,
                    "hatch_spatial_buckets_400mm_nearest_to_target");
                EnsureRouteEvaluation(groupKey, target, grid, candidates, spec, result);
                return null;
            }

            int rawSampleCount = 0;
            var eligibleSamples = new List<GridCell>();
            for (int y = 0; y < grid.Height; y += 2)
            for (int x = 0; x < grid.Width; x += 2)
            {
                rawSampleCount++;
                if (!grid.IsWalkable(x, y)) continue;
                if (labels[grid.ToIndex(x, y)] != component) continue;
                eligibleSamples.Add(new GridCell(x, y));
            }

            // This is the legacy selection order.  The explicit Y/X keys make
            // the old stable row-major tie-break independent of LINQ details.
            List<GridCell> orderedSamples = eligibleSamples
                .OrderBy(x => Math.Abs(x.X - target.Goal.X) + Math.Abs(x.Y - target.Goal.Y))
                .ThenBy(x => x.Y)
                .ThenBy(x => x.X)
                .ToList();

            EntryWork selectedEntry = null;
            if (selectionRequired)
            {
                foreach (GridCell cell in orderedSamples)
                {
                    HatchCandidateOutcome outcome = EvaluateHatchCandidate(
                        groupKey,
                        target,
                        cell,
                        footprint,
                        grid,
                        component,
                        topFt,
                        floorFt,
                        ignoredGroupCeilingKeys,
                        candidates,
                        spec,
                        sharedEntryReview,
                        true,
                        !sharedEntryReview);
                    if (outcome.Entry == null) continue;
                    selectedEntry = outcome.Entry;
                    AddDistinctEntry(retainedEntries, selectedEntry);
                    break;
                }
            }

            if (!result.CandidateAuditEnabled) return selectedEntry;

            var sourceSamplesByBucket = orderedSamples
                .Select(grid.CellCenter)
                .GroupBy(x => MaintenanceCandidateAudit.SpatialBucketKey(
                    x,
                    HatchRepresentativeSpacingMm), StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
            int deduplicatedCount;
            int omittedCount;
            List<MaintenancePoint2> representativePoints =
                MaintenanceCandidateAudit.SelectSpatialRepresentatives(
                    orderedSamples.Select(grid.CellCenter),
                    grid.CellCenter(target.Goal),
                    HatchRepresentativeSpacingMm,
                    maxCandidates,
                    out deduplicatedCount,
                    out omittedCount);
            List<GridCell> samplesToEvaluate = representativePoints
                .Select(x => grid.WorldToCell(x.X, x.Y))
                .ToList();

            // A formally selected legacy 80 mm point is evidence that must never
            // disappear merely because its 400 mm bucket fell outside the cap.
            if (selectedEntry != null)
            {
                string selectedBucket = MaintenanceCandidateAudit.SpatialBucketKey(
                    grid.CellCenter(selectedEntry.Start),
                    HatchRepresentativeSpacingMm);
                int existingBucketIndex = samplesToEvaluate.FindIndex(x => string.Equals(
                    MaintenanceCandidateAudit.SpatialBucketKey(
                        grid.CellCenter(x),
                        HatchRepresentativeSpacingMm),
                    selectedBucket,
                    StringComparison.Ordinal));
                if (existingBucketIndex >= 0)
                    samplesToEvaluate[existingBucketIndex] = selectedEntry.Start;
                else
                    samplesToEvaluate.Add(selectedEntry.Start);
            }
            samplesToEvaluate = samplesToEvaluate
                .GroupBy(x => x.X.ToString(CultureInfo.InvariantCulture) + "|" +
                              x.Y.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal)
                .Select(x => x.First())
                .ToList();
            if (selectedEntry != null && samplesToEvaluate.Count > maxCandidates)
            {
                GridCell selectedCell = selectedEntry.Start;
                samplesToEvaluate = new[] { selectedCell }
                    .Concat(samplesToEvaluate
                        .Where(x => x != selectedCell)
                        .OrderBy(x => Math.Abs(x.X - target.Goal.X) +
                                      Math.Abs(x.Y - target.Goal.Y))
                        .ThenBy(x => x.Y)
                        .ThenBy(x => x.X)
                        .Take(Math.Max(0, maxCandidates - 1)))
                    .ToList();
            }
            samplesToEvaluate = samplesToEvaluate
                .OrderBy(x => Math.Abs(x.X - target.Goal.X) + Math.Abs(x.Y - target.Goal.Y))
                .ThenBy(x => x.Y)
                .ThenBy(x => x.X)
                .ToList();
            int retainedBucketCount = samplesToEvaluate
                .Select(x => MaintenanceCandidateAudit.SpatialBucketKey(
                    grid.CellCenter(x),
                    HatchRepresentativeSpacingMm))
                .Distinct(StringComparer.Ordinal)
                .Count();
            omittedCount = Math.Max(0, deduplicatedCount - retainedBucketCount);
            if (omittedCount > 0)
            {
                result.CandidateAuditComplete = false;
                result.Warnings.Add(
                    "天花检修口候选“" + target.Target.GetDisplayName() + " / " + spec.Profile +
                    "”共有 " + deduplicatedCount + " 个 400 mm 空间代表，当前保留 " +
                    retainedBucketCount + " 个，另有 " + omittedCount +
                    " 个未保留；候选台账已明确标记为截断。");
            }
            RecordCandidateCoverage(
                result,
                groupKey,
                target.Target.TargetKey,
                spec,
                MaintenanceEntryType.CeilingHatch,
                rawSampleCount,
                eligibleSamples.Count,
                deduplicatedCount,
                retainedBucketCount,
                omittedCount,
                omittedCount > 0,
                HatchRepresentativeSpacingMm,
                "hatch_spatial_buckets_400mm_nearest_to_target_selected_forced");

            int footprintPass = 0;
            int turnPass = 0;
            int openingPass = 0;
            int aFramePass = 0;
            int straightPass = 0;
            foreach (GridCell cell in samplesToEvaluate)
            {
                MaintenancePoint2 centerMm = grid.CellCenter(cell);
                string candidateKey = groupKey + "|H|" + Math.Round(centerMm.X, 0) + "|" + Math.Round(centerMm.Y, 0);
                MaintenanceCandidateEvaluation audit = NewEntryEvaluation(
                    result,
                    candidateKey,
                    groupKey,
                    spec,
                    MaintenanceEntryType.CeilingHatch,
                    centerMm,
                    topFt * MmPerFoot,
                    -1,
                    -1);
                if (audit != null)
                {
                    audit.TargetKey = target.Target.TargetKey;
                    int sourceSampleCount;
                    if (sourceSamplesByBucket.TryGetValue(
                        MaintenanceCandidateAudit.SpatialBucketKey(
                            centerMm,
                            HatchRepresentativeSpacingMm),
                        out sourceSampleCount))
                        audit.SourceSampleCount = sourceSampleCount;
                }
                HatchCandidateOutcome outcome = EvaluateHatchCandidate(
                    groupKey,
                    target,
                    cell,
                    footprint,
                    grid,
                    component,
                    topFt,
                    floorFt,
                    ignoredGroupCeilingKeys,
                    candidates,
                    spec,
                    true,
                    false,
                    !sharedEntryReview);
                if (outcome.FootprintPassed) footprintPass++;
                if (outcome.TurnPassed) turnPass++;
                if (outcome.OpeningPassed) openingPass++;
                if (outcome.AFramePassed) aFramePass++;
                if (outcome.StraightPassed) straightPass++;
                if (outcome.Entry == null)
                {
                    RejectEntryEvaluation(
                        result,
                        audit,
                        outcome.Stage,
                        outcome.ReasonCode,
                        outcome.Reason,
                        outcome.CollisionEvidence,
                        candidates);
                    continue;
                }
                CompleteEntryEvaluation(
                    result,
                    audit,
                    outcome.Entry.Candidate,
                    outcome.CollisionEvidence,
                    candidates);
                AddDistinctEntry(retainedEntries, outcome.Entry);
                result.CandidateEvaluations.Add(EvaluateRouteCandidate(
                        groupKey,
                        target,
                        outcome.Entry,
                        grid,
                        candidates,
                        spec));
            }

            if (selectedEntry != null && !HasRouteEvaluation(
                result,
                groupKey,
                target.Target.TargetKey,
                spec.Profile,
                selectedEntry.Candidate.CandidateKey))
            {
                result.CandidateEvaluations.Add(EvaluateRouteCandidate(
                    groupKey,
                    target,
                    selectedEntry,
                    grid,
                    candidates,
                    spec));
            }
            EnsureRouteEvaluation(groupKey, target, grid, candidates, spec, result);
            LastDiagnostics.Add(groupKey + ": hatchStages target=" + target.Target.TargetKey +
                ", raw=" + rawSampleCount + ", eligible=" + eligibleSamples.Count +
                ", retained=" + samplesToEvaluate.Count +
                ", footprint=" + footprintPass +
                ", turn=" + turnPass + ", opening=" + openingPass +
                ", aFrame=" + aFramePass +
                ", straight=" + straightPass);
            return selectedEntry;
        }

        private static HatchCandidateOutcome EvaluateHatchCandidate(
            string groupKey,
            TargetWork target,
            GridCell cell,
            Mask footprint,
            MaintenanceGrid grid,
            int component,
            double topFt,
            double floorFt,
            ISet<string> ignoredGroupCeilingKeys,
            IList<PlenumAnalysisService.Candidate> candidates,
            ProfileSpec spec,
            bool includeOpeningEvidence,
            bool recordTargetEvidence,
            bool ignoreTargetLocalPipes)
        {
            var outcome = new HatchCandidateOutcome();
            ISet<string> ignoredTargetPipes = target == null || !ignoreTargetLocalPipes
                ? null
                : target.ExemptSourceKeys;
            ISet<string> ignoredOpeningSources = CombineIgnoredSourceKeys(
                ignoredGroupCeilingKeys,
                ignoredTargetPipes);
            MaintenancePoint2 centerMm = grid.CellCenter(cell);
            double turnValidationWidthMm =
                MaintenanceTurnZonePolicy.GetValidationWidthMm(spec.Profile);
            if (!RectangleInsideMask(
                footprint,
                centerMm,
                XYZ.BasisX,
                turnValidationWidthMm,
                turnValidationWidthMm))
            {
                outcome.Stage = MaintenanceCandidateStage.Footprint;
                outcome.ReasonCode = "hatch_turn_zone_outside_footprint";
                outcome.Reason = "天花检修口下方转身区超出当前天花分组轮廓。";
                return outcome;
            }
            outcome.FootprintPassed = true;

            XYZ p = new XYZ(centerMm.X / MmPerFoot, centerMm.Y / MmPerFoot, topFt);
            Solid turn = MaintenanceGeometryService.MakeBox(
                p + XYZ.BasisZ.Multiply(CollisionLiftMm / MmPerFoot),
                turnValidationWidthMm / MmPerFoot,
                turnValidationWidthMm / MmPerFoot,
                (TurnHeightMm - CollisionLiftMm) / MmPerFoot,
                XYZ.BasisX);
            MaintenanceCollisionResult turnCollision =
                MaintenanceGeometryService.Validate(turn, candidates, ignoredTargetPipes);
            if (!turnCollision.IsClear)
            {
                if (recordTargetEvidence)
                    RecordEntryCollision(new[] { target }, turnCollision, candidates);
                outcome.Stage = MaintenanceCandidateStage.TurnZone;
                outcome.ReasonCode = "hatch_turn_zone_conflict";
                outcome.Reason = "天花检修口下方转身区与模型构件冲突。";
                outcome.CollisionEvidence = turnCollision;
                return outcome;
            }
            outcome.TurnPassed = true;

            if (includeOpeningEvidence)
            {
                Solid opening = MaintenanceGeometryService.MakeBox(
                    new XYZ(p.X, p.Y, topFt - 100.0 / MmPerFoot),
                    FullBodyCeilingHatchSizeMm / MmPerFoot,
                    FullBodyCeilingHatchSizeMm / MmPerFoot,
                    200.0 / MmPerFoot,
                    XYZ.BasisX);
                MaintenanceCollisionResult openingCollision =
                    MaintenanceGeometryService.Validate(
                        opening,
                        candidates,
                        ignoredOpeningSources);
                if (!openingCollision.IsClear)
                {
                    outcome.Stage = MaintenanceCandidateStage.Opening;
                    outcome.ReasonCode = "hatch_opening_conflict";
                    outcome.Reason = "天花检修口开口体与梁、设备或其他模型构件冲突。";
                    outcome.CollisionEvidence = openingCollision;
                    return outcome;
                }
            }
            outcome.OpeningPassed = true;

            MaintenanceLadderType ladderType = MaintenanceLadderType.None;
            XYZ ladderAlong = XYZ.BasisX;
            double ladderFloor = floorFt + 25.0 / MmPerFoot;
            double ladderTop = topFt + 80.0 / MmPerFoot;
            var ladderRejections = new List<MaintenanceCollisionResult>();
            foreach (XYZ direction in new[] { XYZ.BasisX, XYZ.BasisY })
            {
                Solid zone = MaintenanceGeometryService.MakeBox(
                    new XYZ(p.X, p.Y, ladderFloor),
                    1200.0 / MmPerFoot,
                    2500.0 / MmPerFoot,
                    ladderTop - ladderFloor,
                    XYZ.BasisZ.CrossProduct(direction));
                List<Solid> ladder = MaintenanceGeometryService.BuildAFrameLadder(
                    p,
                    direction,
                    ladderFloor,
                    ladderTop);
                MaintenanceCollisionResult zoneCollision =
                    MaintenanceGeometryService.Validate(zone, candidates, ignoredTargetPipes);
                MaintenanceCollisionResult ladderCollision =
                    MaintenanceGeometryService.Validate(ladder, candidates, ignoredTargetPipes);
                if (zoneCollision.IsClear && ladderCollision.IsClear)
                {
                    ladderType = MaintenanceLadderType.AFrame;
                    ladderAlong = direction;
                    outcome.AFramePassed = true;
                    break;
                }
                if (recordTargetEvidence)
                {
                    RecordEntryCollision(new[] { target }, zoneCollision, candidates);
                    RecordEntryCollision(new[] { target }, ladderCollision, candidates);
                }
                ladderRejections.Add(zoneCollision);
                ladderRejections.Add(ladderCollision);
            }
            if (ladderType == MaintenanceLadderType.None)
            {
                foreach (XYZ direction in new[] { XYZ.BasisX, XYZ.BasisY })
                {
                    Solid zone = MaintenanceGeometryService.MakeBox(
                        new XYZ(p.X, p.Y, ladderFloor),
                        1000.0 / MmPerFoot,
                        1600.0 / MmPerFoot,
                        ladderTop - ladderFloor,
                        XYZ.BasisZ.CrossProduct(direction));
                    List<Solid> ladder = MaintenanceGeometryService.BuildStraightLadder(
                        p,
                        direction,
                        ladderFloor,
                        ladderTop);
                    MaintenanceCollisionResult zoneCollision =
                        MaintenanceGeometryService.Validate(zone, candidates, ignoredTargetPipes);
                    MaintenanceCollisionResult ladderCollision =
                        MaintenanceGeometryService.Validate(ladder, candidates, ignoredTargetPipes);
                    if (zoneCollision.IsClear && ladderCollision.IsClear)
                    {
                        ladderType = MaintenanceLadderType.Straight;
                        ladderAlong = direction;
                        outcome.StraightPassed = true;
                        break;
                    }
                    if (recordTargetEvidence)
                    {
                        RecordEntryCollision(new[] { target }, zoneCollision, candidates);
                        RecordEntryCollision(new[] { target }, ladderCollision, candidates);
                    }
                    ladderRejections.Add(zoneCollision);
                    ladderRejections.Add(ladderCollision);
                }
            }
            if (ladderType == MaintenanceLadderType.None)
            {
                outcome.Stage = MaintenanceCandidateStage.Ladder;
                outcome.ReasonCode = "all_ladders_conflict";
                outcome.Reason = "人字梯和一字梯及其操作区均无法无碰撞布置。";
                outcome.CollisionEvidence = ladderRejections;
                return outcome;
            }

            string candidateKey = groupKey + "|H|" + Math.Round(centerMm.X, 0) + "|" +
                                  Math.Round(centerMm.Y, 0);
            var dto = new MaintenanceEntryCandidate
            {
                CandidateKey = candidateKey,
                GroupKey = groupKey,
                TargetKey = target.Target.TargetKey,
                EntryType = MaintenanceEntryType.CeilingHatch,
                LadderType = ladderType,
                Center = new MaintenancePoint3(centerMm.X, centerMm.Y, topFt * MmPerFoot),
                InwardDirection = new MaintenancePoint2(ladderAlong.X, ladderAlong.Y),
                OpeningWidthMm = FullBodyCeilingHatchSizeMm,
                OpeningHeightMm = FullBodyCeilingHatchSizeMm,
                CoveredTargetCount = 1,
                IsFeasible = true
            };
            var work = new EntryWork
            {
                Profile = spec.Profile,
                Candidate = dto,
                Start = cell,
                Component = component,
                WallPoint = p,
                Tangent = XYZ.BasisX,
                Inward = XYZ.BasisY,
                LadderPlanCenter = p,
                LadderAlong = ladderAlong
            };
            work.CoveredTargets.Add(target.Target.TargetKey);
            outcome.Entry = work;
            outcome.Stage = MaintenanceCandidateStage.Complete;
            outcome.ReasonCode = "entry_geometry_clear";
            outcome.Reason = "天花检修口候选通过转身区和梯具检查。";
            outcome.CollisionEvidence = ladderRejections;
            return outcome;
        }

        private static MaintenanceDoorSwingStatus ToDoorSwingStatus(
            MaintenanceCollisionResult collision)
        {
            if (collision == null) return MaintenanceDoorSwingStatus.Unverified;
            switch (collision.State)
            {
                case MaintenanceCollisionState.Clear:
                    return MaintenanceDoorSwingStatus.Clear;
                case MaintenanceCollisionState.Conflict:
                    return MaintenanceDoorSwingStatus.Conflict;
                default:
                    return MaintenanceDoorSwingStatus.Unverified;
            }
        }

        private static void RecordEntryCollision(
            IEnumerable<TargetWork> targets,
            MaintenanceCollisionResult collision,
            IList<PlenumAnalysisService.Candidate> candidates)
        {
            if (targets == null || collision == null || collision.IsClear) return;
            foreach (TargetWork target in targets.Where(x => x != null))
            {
                if (collision.State == MaintenanceCollisionState.Unverified)
                    target.EntryGeometryUnverified = true;
                if (target.EntryBlockers.Count >= 12) continue;
                AddBlocker(target.EntryBlockers, candidates, collision);
            }
        }

        private static MaintenanceCandidateEvaluation NewEntryEvaluation(
            MaintenanceAnalysisResult result,
            string candidateKey,
            string groupKey,
            ProfileSpec spec,
            MaintenanceEntryType entryType,
            MaintenancePoint2 center,
            double centerZMm,
            int loopIndex,
            int segmentIndex)
        {
            if (result == null || !result.CandidateAuditEnabled) return null;
            return new MaintenanceCandidateEvaluation
            {
                CandidateKey = candidateKey,
                GroupKey = groupKey,
                Scope = MaintenanceCandidateScope.Entry,
                Profile = spec.Profile,
                EntryType = entryType,
                LadderType = MaintenanceLadderType.None,
                Status = MaintenanceCandidateStatus.Rejected,
                Stage = MaintenanceCandidateStage.Sample,
                BoundaryLoopIndex = loopIndex,
                BoundarySegmentIndex = segmentIndex,
                EntryCenter = new MaintenancePoint3(center.X, center.Y, centerZMm),
                OpeningWidthMm = entryType == MaintenanceEntryType.WallDoor
                    ? result.DoorWidthMm
                    : FullBodyCeilingHatchSizeMm,
                OpeningHeightMm = entryType == MaintenanceEntryType.WallDoor
                    ? result.DoorHeightMm
                    : FullBodyCeilingHatchSizeMm
            };
        }

        private static void RejectEntryEvaluation(
            MaintenanceAnalysisResult result,
            MaintenanceCandidateEvaluation evaluation,
            MaintenanceCandidateStage stage,
            string reasonCode,
            string reason,
            object collisionEvidence,
            IList<PlenumAnalysisService.Candidate> candidates)
        {
            if (result == null || evaluation == null || !result.CandidateAuditEnabled) return;
            evaluation.Stage = stage;
            evaluation.Status = MaintenanceCandidateStatus.Rejected;
            evaluation.ReasonCode = reasonCode ?? string.Empty;
            evaluation.Reason = reason ?? string.Empty;
            bool unverified = AddCollisionEvidence(
                evaluation.Blockers,
                collisionEvidence,
                candidates);
            if (unverified)
            {
                evaluation.Status = MaintenanceCandidateStatus.Unverified;
                if (string.Equals(
                    evaluation.ReasonCode,
                    "all_ladders_conflict",
                    StringComparison.Ordinal))
                {
                    evaluation.ReasonCode = "all_ladders_unverified";
                }
                else if (evaluation.ReasonCode.EndsWith(
                    "_conflict",
                    StringComparison.Ordinal))
                {
                    evaluation.ReasonCode = evaluation.ReasonCode.Substring(
                        0,
                        evaluation.ReasonCode.Length - "_conflict".Length) + "_unverified";
                }
                evaluation.Reason = "该阶段模型几何无法完整验证，不能判定为已冲突，需专业复核。";
            }
            result.CandidateEvaluations.Add(evaluation);
        }

        private static void CompleteEntryEvaluation(
            MaintenanceAnalysisResult result,
            MaintenanceCandidateEvaluation evaluation,
            MaintenanceEntryCandidate candidate,
            object fallbackCollisionEvidence,
            IList<PlenumAnalysisService.Candidate> candidates)
        {
            if (result == null || evaluation == null || candidate == null || !result.CandidateAuditEnabled) return;
            evaluation.Stage = MaintenanceCandidateStage.Complete;
            evaluation.Status = MaintenanceCandidateStatus.Feasible;
            evaluation.LadderType = candidate.LadderType;
            evaluation.DoorHingeSide = candidate.DoorHingeSide;
            evaluation.LeftDoorSwingStatus = candidate.LeftDoorSwingStatus;
            evaluation.RightDoorSwingStatus = candidate.RightDoorSwingStatus;
            evaluation.LadderFloorMm = candidate.LadderFloorMm;
            foreach (string sourceKey in candidate.OpeningHostSourceKeys
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal))
                evaluation.OpeningHostSourceKeys.Add(sourceKey);
            foreach (string sourceKey in candidate.LadderSupportSourceKeys
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal))
                evaluation.LadderSupportSourceKeys.Add(sourceKey);
            AddBlockers(evaluation.LeftDoorSwingBlockers, candidate.LeftDoorSwingBlockers);
            AddBlockers(evaluation.RightDoorSwingBlockers, candidate.RightDoorSwingBlockers);
            evaluation.CoveredTargetCount = candidate.CoveredTargetCount;
            if (candidate.LadderType == MaintenanceLadderType.Straight)
            {
                bool fallbackUnverified = AddCollisionEvidence(
                    evaluation.Blockers,
                    fallbackCollisionEvidence,
                    candidates);
                evaluation.ReasonCode = fallbackUnverified
                    ? "straight_ladder_fallback_with_unverified_aframe"
                    : "straight_ladder_fallback";
                evaluation.Reason = fallbackUnverified
                    ? "一字梯方案几何可用；人字梯阶段的模型几何无法完整验证，不能判定为已冲突，需复核方案取舍。"
                    : "入口几何可用，但人字梯方案受阻，已降级为一字梯。";
            }
            else if (candidate.EntryType == MaintenanceEntryType.CeilingHatch)
            {
                evaluation.ReasonCode = "entry_geometry_clear";
                evaluation.Reason = "天花检修口候选的转身区、开口体审计证据和人字梯均通过几何检查。";
            }
            else
            {
                evaluation.ReasonCode = "entry_geometry_clear";
                evaluation.Reason = "入口、转身区、门洞、门框、" +
                                    (candidate.DoorHingeSide == MaintenanceDoorHingeSide.Right
                                        ? "右铰链向外门扇扫掠"
                                        : "左铰链向外门扇扫掠") +
                                    "、人体穿越包络和人字梯均通过几何检查。";
            }
            result.CandidateEvaluations.Add(evaluation);
        }

        private static bool AddCollisionEvidence(
            IList<MaintenanceElementRef> blockers,
            object collisionEvidence,
            IList<PlenumAnalysisService.Candidate> candidates)
        {
            if (blockers == null || collisionEvidence == null) return false;
            var collisions = collisionEvidence as IEnumerable<MaintenanceCollisionResult>;
            if (collisions == null)
            {
                MaintenanceCollisionResult single = collisionEvidence as MaintenanceCollisionResult;
                collisions = single == null
                    ? Enumerable.Empty<MaintenanceCollisionResult>()
                    : new[] { single };
            }
            bool unverified = false;
            foreach (MaintenanceCollisionResult collision in collisions.Where(x => x != null && !x.IsClear))
            {
                if (collision.State == MaintenanceCollisionState.Unverified) unverified = true;
                AddBlocker(blockers, candidates, collision);
            }
            return unverified;
        }

        private static void AddDistinctEntry(
            ICollection<EntryWork> entries,
            EntryWork entry)
        {
            if (entries == null || entry == null || entry.Candidate == null) return;
            if (entries.Any(x => x != null && x.Candidate != null && string.Equals(
                x.Candidate.CandidateKey,
                entry.Candidate.CandidateKey,
                StringComparison.Ordinal))) return;
            entries.Add(entry);
        }

        private static void RecordRouteEvaluations(
            string groupKey,
            IEnumerable<EntryWork> entries,
            IEnumerable<TargetWork> targets,
            MaintenanceGrid grid,
            IList<PlenumAnalysisService.Candidate> candidates,
            ProfileSpec spec,
            MaintenanceAnalysisResult result)
        {
            if (result == null || !result.CandidateAuditEnabled) return;
            foreach (EntryWork entry in (entries ?? Enumerable.Empty<EntryWork>())
                .Where(x => x != null)
                .OrderBy(x => x.Candidate.CandidateKey, StringComparer.Ordinal))
            foreach (TargetWork target in (targets ?? Enumerable.Empty<TargetWork>())
                .Where(x => x != null && x.Target != null)
                .OrderBy(x => x.Target.TargetKey, StringComparer.Ordinal))
            {
                result.CandidateEvaluations.Add(EvaluateRouteCandidate(
                    groupKey,
                    target,
                    entry,
                    target.Grid ?? grid,
                    candidates,
                    spec));
            }
        }

        private static void RecordSharedHatchRouteEvaluations(
            string groupKey,
            IEnumerable<EntryWork> entries,
            IEnumerable<TargetWork> targets,
            MaintenanceGrid grid,
            IList<PlenumAnalysisService.Candidate> candidates,
            ProfileSpec spec,
            MaintenanceAnalysisResult result)
        {
            if (result == null || !result.CandidateAuditEnabled) return;
            List<EntryWork> retained = (entries ?? Enumerable.Empty<EntryWork>())
                .Where(x => x != null && x.Candidate != null &&
                            x.Candidate.EntryType == MaintenanceEntryType.CeilingHatch)
                .GroupBy(x => x.Candidate.CandidateKey, StringComparer.Ordinal)
                .Select(x => x.First())
                .OrderBy(x => x.Candidate.CandidateKey, StringComparer.Ordinal)
                .ToList();
            List<TargetWork> targetList = (targets ?? Enumerable.Empty<TargetWork>())
                .Where(x => x != null && x.Target != null)
                .OrderBy(x => x.Target.TargetKey, StringComparer.Ordinal)
                .ToList();
            foreach (EntryWork entry in retained)
            foreach (TargetWork target in targetList)
            {
                if (HasRouteEvaluation(
                    result,
                    groupKey,
                    target.Target.TargetKey,
                    spec.Profile,
                    entry.Candidate.CandidateKey)) continue;
                result.CandidateEvaluations.Add(EvaluateRouteCandidate(
                    groupKey,
                    target,
                    entry,
                    target.Grid ?? grid,
                    candidates,
                    spec));
            }
        }

        private static bool HasRouteEvaluation(
            MaintenanceAnalysisResult result,
            string groupKey,
            string targetKey,
            MaintenanceAccessProfile profile,
            string candidateKey)
        {
            if (result == null) return false;
            return result.CandidateEvaluations.Any(x =>
                x != null &&
                x.Scope == MaintenanceCandidateScope.Route &&
                string.Equals(x.GroupKey, groupKey ?? string.Empty, StringComparison.Ordinal) &&
                string.Equals(x.TargetKey, targetKey ?? string.Empty, StringComparison.Ordinal) &&
                x.Profile == profile &&
                (candidateKey == null || string.Equals(
                    x.CandidateKey,
                    candidateKey,
                    StringComparison.Ordinal)));
        }

        private static void EnsureRouteEvaluation(
            string groupKey,
            TargetWork target,
            MaintenanceGrid grid,
            IList<PlenumAnalysisService.Candidate> candidates,
            ProfileSpec spec,
            MaintenanceAnalysisResult result)
        {
            if (result == null || !result.CandidateAuditEnabled) return;
            string targetKey = target == null || target.Target == null
                ? string.Empty
                : target.Target.TargetKey;
            if (HasRouteEvaluation(result, groupKey, targetKey, spec.Profile, null)) return;
            MaintenanceCandidateEvaluation missingRoute = EvaluateRouteCandidate(
                groupKey,
                target,
                null,
                grid,
                candidates,
                spec);
            bool entryEvidenceUnverified = result.CandidateEvaluations.Any(x =>
                x != null &&
                x.Scope == MaintenanceCandidateScope.Entry &&
                string.Equals(x.GroupKey, groupKey ?? string.Empty, StringComparison.Ordinal) &&
                string.Equals(x.TargetKey, targetKey, StringComparison.Ordinal) &&
                x.Profile == spec.Profile &&
                x.Status == MaintenanceCandidateStatus.Unverified);
            if (entryEvidenceUnverified &&
                missingRoute.Stage == MaintenanceCandidateStage.Connectivity)
            {
                missingRoute.Status = MaintenanceCandidateStatus.Unverified;
                missingRoute.ReasonCode = "entry_availability_unverified";
                missingRoute.Reason = "入口候选的模型几何无法完整验证，不能判定为确定无可用入口，需专业复核。";
            }
            result.CandidateEvaluations.Add(missingRoute);
        }

        private static MaintenanceCandidateEvaluation EvaluateRouteCandidate(
            string groupKey,
            TargetWork target,
            EntryWork entry,
            MaintenanceGrid grid,
            IList<PlenumAnalysisService.Candidate> candidates,
            ProfileSpec spec)
        {
            var output = new MaintenanceCandidateEvaluation
            {
                CandidateKey = entry == null || entry.Candidate == null
                    ? string.Empty
                    : entry.Candidate.CandidateKey,
                GroupKey = groupKey ?? string.Empty,
                TargetKey = target == null || target.Target == null
                    ? string.Empty
                    : target.Target.TargetKey,
                Scope = MaintenanceCandidateScope.Route,
                Profile = spec.Profile,
                EntryType = entry == null || entry.Candidate == null
                    ? MaintenanceEntryType.None
                    : entry.Candidate.EntryType,
                LadderType = entry == null || entry.Candidate == null
                    ? MaintenanceLadderType.None
                    : entry.Candidate.LadderType,
                EntryCenter = entry == null || entry.Candidate == null
                    ? new MaintenancePoint3()
                    : entry.Candidate.Center,
                OpeningWidthMm = entry == null || entry.Candidate == null
                    ? 0.0
                    : entry.Candidate.OpeningWidthMm,
                OpeningHeightMm = entry == null || entry.Candidate == null
                    ? 0.0
                    : entry.Candidate.OpeningHeightMm,
                DoorHingeSide = entry == null || entry.Candidate == null
                    ? MaintenanceDoorHingeSide.None
                    : entry.Candidate.DoorHingeSide,
                LeftDoorSwingStatus = entry == null || entry.Candidate == null
                    ? MaintenanceDoorSwingStatus.NotApplicable
                    : entry.Candidate.LeftDoorSwingStatus,
                RightDoorSwingStatus = entry == null || entry.Candidate == null
                    ? MaintenanceDoorSwingStatus.NotApplicable
                    : entry.Candidate.RightDoorSwingStatus,
                BoundaryLoopIndex = entry == null || entry.Candidate == null
                    ? -1
                    : entry.Candidate.BoundaryLoopIndex,
                BoundarySegmentIndex = entry == null || entry.Candidate == null
                    ? -1
                    : entry.Candidate.BoundarySegmentIndex,
                LadderFloorMm = entry == null || entry.Candidate == null
                    ? double.NaN
                    : entry.Candidate.LadderFloorMm,
                CoveredTargetCount = entry == null || entry.Candidate == null
                    ? 0
                    : entry.Candidate.CoveredTargetCount
            };
            if (entry != null && entry.Candidate != null)
            {
                foreach (string sourceKey in entry.Candidate.OpeningHostSourceKeys
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal))
                    output.OpeningHostSourceKeys.Add(sourceKey);
                foreach (string sourceKey in entry.Candidate.LadderSupportSourceKeys
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal))
                    output.LadderSupportSourceKeys.Add(sourceKey);
                AddBlockers(output.LeftDoorSwingBlockers, entry.Candidate.LeftDoorSwingBlockers);
                AddBlockers(output.RightDoorSwingBlockers, entry.Candidate.RightDoorSwingBlockers);
            }

            if (target == null || target.Target == null || !target.HasGoal)
            {
                bool targetGoalUnverified = target != null &&
                    (target.GridGeometryUnverified || target.EntryGeometryUnverified ||
                     target.SupplyDirectionInferred);
                output.Status = targetGoalUnverified
                    ? MaintenanceCandidateStatus.Unverified
                    : MaintenanceCandidateStatus.Rejected;
                output.Stage = MaintenanceCandidateStage.TargetGoal;
                output.ReasonCode = targetGoalUnverified
                    ? "target_service_goal_unverified"
                    : "target_service_goal_unavailable";
                output.Reason = targetGoalUnverified
                    ? "设备检修侧通行目标点无法完整验证，不能判定为确定不可达，需专业复核。"
                    : "设备检修侧没有可用的通行目标点，无法形成完整路线。";
                if (target != null)
                {
                    AddBlockers(output.Blockers, target.EntryBlockers);
                    AddBlocker(output.Blockers, candidates, target.PocketCollision);
                }
                return output;
            }
            if (entry == null || entry.Candidate == null || grid == null)
            {
                bool entryUnavailableUnverified = target != null &&
                    (target.EntryGeometryUnverified || target.GridGeometryUnverified);
                output.Status = entryUnavailableUnverified
                    ? MaintenanceCandidateStatus.Unverified
                    : MaintenanceCandidateStatus.Rejected;
                output.Stage = MaintenanceCandidateStage.Connectivity;
                output.ReasonCode = entryUnavailableUnverified
                    ? "entry_availability_unverified"
                    : "entry_unavailable";
                output.Reason = entryUnavailableUnverified
                    ? "入口候选的模型几何无法完整验证，不能判定为确定无可用入口，需专业复核。"
                    : "没有可用于该设备的入口候选。";
                return output;
            }

            AddBlockers(output.Blockers, entry.Candidate.Blockers);
            List<GridCell> routeCells = MaintenancePathfinder.FindPath8(grid, entry.Start, target.Goal);
            if (routeCells.Count == 0)
            {
                bool connectivityUnverified = target.GridGeometryUnverified;
                output.Status = connectivityUnverified
                    ? MaintenanceCandidateStatus.Unverified
                    : MaintenanceCandidateStatus.Rejected;
                output.Stage = MaintenanceCandidateStage.Connectivity;
                output.ReasonCode = connectivityUnverified
                    ? "entry_target_connectivity_unverified"
                    : "entry_target_disconnected";
                output.Reason = connectivityUnverified
                    ? "连通性依赖的模型几何无法完整验证，不能判定为确定断开，需专业复核。"
                    : "入口与设备检修侧不在同一个可通行区。";
                AddBlockers(output.Blockers, target.EntryBlockers);
                return output;
            }

            List<GridCell> simplified = MaintenancePathfinder.SimplifyPath(grid, routeCells);
            List<XYZ> routeFt = simplified.Select(x =>
            {
                MaintenancePoint2 p = grid.CellCenter(x);
                return new XYZ(
                    p.X / MmPerFoot,
                    p.Y / MmPerFoot,
                    (target.PocketCenterBottom.Z * MmPerFoot + spec.HeightMm * 0.5) / MmPerFoot);
            }).ToList();
            output.RouteLengthMm = MaintenancePathfinder.CalculatePathLength(grid, simplified);
            foreach (XYZ point in routeFt)
                output.Route.Add(new MaintenancePoint3(
                    point.X * MmPerFoot,
                    point.Y * MmPerFoot,
                    point.Z * MmPerFoot));

            List<Solid> routeBodies = MaintenanceGeometryService.BuildRoute(
                routeFt,
                spec.RadiusMm / MmPerFoot,
                spec.HeightMm / MmPerFoot);
            MaintenanceCollisionResult routeCollision =
                MaintenanceGeometryService.Validate(
                    routeBodies,
                    candidates,
                    target.ExemptSourceKeys);
            AddBlocker(output.Blockers, candidates, routeCollision);
            if (!routeCollision.IsClear)
            {
                output.Status = routeCollision.State == MaintenanceCollisionState.Unverified
                    ? MaintenanceCandidateStatus.Unverified
                    : MaintenanceCandidateStatus.Rejected;
                output.Stage = MaintenanceCandidateStage.Route;
                output.ReasonCode = routeCollision.State == MaintenanceCollisionState.Unverified
                    ? "route_geometry_unverified"
                    : "route_conflict";
                output.Reason = routeCollision.State == MaintenanceCollisionState.Unverified
                    ? "通行路线几何无法完整验证，需专业复核。"
                    : (spec.Profile == MaintenanceAccessProfile.Full700
                        ? "700 mm 人体包络通行路线与模型构件冲突。"
                        : "600 mm 受限人体包络通行路线与模型构件冲突。");
                return output;
            }

            AddBlocker(output.Blockers, candidates, target.PocketCollision);
            if (target.SupplyDirectionInferred)
            {
                output.Status = MaintenanceCandidateStatus.Unverified;
                output.Stage = MaintenanceCandidateStage.ServicePocket;
                output.ReasonCode = "service_side_inferred";
                output.Reason = "未读取到送风连接器，设备检修侧为外形推断，需复核真实方向。";
            }
            else if (spec.Profile == MaintenanceAccessProfile.Limited600)
            {
                output.Status = MaintenanceCandidateStatus.Unverified;
                output.Stage = MaintenanceCandidateStage.Complete;
                output.ReasonCode = "limited_600_requires_review";
                output.Reason = "600 mm 受限通行路线几何可达，但需确认人员能否完成维修动作。";
            }
            else if (!target.PocketInside)
            {
                output.Status = MaintenanceCandidateStatus.Unverified;
                output.Stage = MaintenanceCandidateStage.ServicePocket;
                output.ReasonCode = "service_pocket_outside_group";
                output.Reason = "设备检修区超出当前天花分组轮廓，需专业确认。";
            }
            else if (target.PocketCollision.State == MaintenanceCollisionState.Clear)
            {
                output.Status = MaintenanceCandidateStatus.Feasible;
                output.Stage = MaintenanceCandidateStage.Complete;
                output.ReasonCode = "complete_chain_clear";
                output.Reason = "入口、梯具、人体路线和设备检修区均通过几何检查。";
            }
            else
            {
                output.Status = MaintenanceCandidateStatus.Unverified;
                output.Stage = MaintenanceCandidateStage.ServicePocket;
                output.ReasonCode = target.PocketCollision.State == MaintenanceCollisionState.Unverified
                    ? "service_pocket_unverified"
                    : "service_pocket_conflict";
                output.Reason = "人员可以到达，但设备检修操作区受阻或无法完整验证，需专业确认。";
            }
            return output;
        }

        private static void MarkSelectedRouteEvaluations(
            string groupKey,
            IDictionary<string, ChainWork> selectedChains,
            IList<MaintenanceCandidateEvaluation> evaluations)
        {
            if (selectedChains == null || evaluations == null) return;
            foreach (KeyValuePair<string, ChainWork> pair in selectedChains)
            {
                ChainWork chain = pair.Value;
                if (chain == null || chain.Entry == null || chain.Entry.Candidate == null) continue;
                MaintenanceCandidateEvaluation selected = evaluations.FirstOrDefault(x =>
                    x != null &&
                    x.Scope == MaintenanceCandidateScope.Route &&
                    string.Equals(x.GroupKey, groupKey, StringComparison.Ordinal) &&
                    string.Equals(x.TargetKey, pair.Key, StringComparison.Ordinal) &&
                    string.Equals(x.CandidateKey, chain.Entry.Candidate.CandidateKey, StringComparison.Ordinal) &&
                    x.Profile == chain.Spec.Profile);
                if (selected != null) selected.IsSelected = true;
            }
        }

        private static void RecordCandidateCoverage(
            MaintenanceAnalysisResult result,
            string groupKey,
            string targetKey,
            ProfileSpec spec,
            MaintenanceEntryType entryType,
            int rawSampleCount,
            int eligibleSampleCount,
            int deduplicatedCount,
            int retainedCount,
            int omittedCount,
            bool truncated,
            double representativeSpacingMm,
            string algorithmVersion)
        {
            if (result == null || !result.CandidateAuditEnabled) return;
            result.CandidateSearchStats.Add(new MaintenanceCandidateSearchStats
            {
                GroupKey = groupKey ?? string.Empty,
                TargetKey = targetKey ?? string.Empty,
                Profile = spec.Profile,
                EntryType = entryType,
                RawSampleCount = rawSampleCount,
                EligibleSampleCount = eligibleSampleCount,
                DeduplicatedCount = deduplicatedCount,
                RetainedCount = retainedCount,
                OmittedCount = omittedCount,
                Truncated = truncated,
                RepresentativeSpacingMm = representativeSpacingMm,
                AllPathsEnumerated = false,
                AlgorithmVersion = algorithmVersion ?? string.Empty,
                SampledCount = rawSampleCount,
                Complete = !truncated,
                Strategy = result.CandidateAuditStrategy
            });
        }

        private static void RefreshCandidateSearchStats(MaintenanceAnalysisResult result)
        {
            foreach (MaintenanceCandidateSearchStats stat in result.CandidateSearchStats)
            {
                IEnumerable<MaintenanceCandidateEvaluation> matching = result.CandidateEvaluations
                    .Where(x => x != null &&
                                string.Equals(x.GroupKey, stat.GroupKey, StringComparison.Ordinal) &&
                                x.Profile == stat.Profile &&
                                x.EntryType == stat.EntryType);
                if (!string.IsNullOrWhiteSpace(stat.TargetKey))
                    matching = matching.Where(x =>
                        string.Equals(x.TargetKey, stat.TargetKey, StringComparison.Ordinal));
                List<MaintenanceCandidateEvaluation> rows = matching.ToList();
                stat.SampledCount = stat.RawSampleCount;
                stat.RetainedEntryCount = rows.Count(x => x.Scope == MaintenanceCandidateScope.Entry);
                stat.EvaluatedRouteCount = rows.Count(x => x.Scope == MaintenanceCandidateScope.Route);
                stat.RejectedCount = rows.Count(x => x.Status == MaintenanceCandidateStatus.Rejected);
                stat.UnverifiedCount = rows.Count(x => x.Status == MaintenanceCandidateStatus.Unverified);
                stat.FeasibleCount = rows.Count(x => x.Status == MaintenanceCandidateStatus.Feasible);
                stat.SelectedCount = rows.Count(x => x.IsSelected);
                stat.Complete = !stat.Truncated;
                stat.Strategy = result.CandidateAuditStrategy;
            }
            result.CandidateSearchStats.Sort((left, right) =>
            {
                int group = string.Compare(left.GroupKey, right.GroupKey, StringComparison.Ordinal);
                if (group != 0) return group;
                int target = string.Compare(left.TargetKey, right.TargetKey, StringComparison.Ordinal);
                if (target != 0) return target;
                int profile = left.Profile.CompareTo(right.Profile);
                return profile != 0 ? profile : left.EntryType.CompareTo(right.EntryType);
            });
        }

        private static MaintenanceCollisionState GetRouteValidationState(
            TargetWork target,
            EntryWork entry,
            MaintenanceGrid grid,
            IList<PlenumAnalysisService.Candidate> candidates,
            ProfileSpec spec)
        {
            if (target == null || entry == null || grid == null || !target.HasGoal)
                return MaintenanceCollisionState.Conflict;
            List<GridCell> routeCells = MaintenancePathfinder.FindPath8(
                grid,
                entry.Start,
                target.Goal);
            if (routeCells.Count == 0) return MaintenanceCollisionState.Conflict;
            List<GridCell> simplified = MaintenancePathfinder.SimplifyPath(grid, routeCells);
            List<XYZ> routeFt = simplified.Select(x =>
            {
                MaintenancePoint2 p = grid.CellCenter(x);
                return new XYZ(
                    p.X / MmPerFoot,
                    p.Y / MmPerFoot,
                    (target.PocketCenterBottom.Z * MmPerFoot + spec.HeightMm * 0.5) / MmPerFoot);
            }).ToList();
            List<Solid> routeBodies = MaintenanceGeometryService.BuildRoute(
                routeFt,
                spec.RadiusMm / MmPerFoot,
                spec.HeightMm / MmPerFoot);
            return MaintenanceGeometryService.Validate(
                routeBodies,
                candidates,
                target.ExemptSourceKeys).State;
        }

        private static MaintenanceTargetResult AnalyzeTargetRoute(
            string groupKey,
            TargetWork target,
            EntryWork entry,
            MaintenanceGrid grid,
            IList<PlenumAnalysisService.Candidate> candidates,
            IDictionary<string, string> entryNames,
            string analysisId,
            ProfileSpec spec)
        {
            var output = new MaintenanceTargetResult
            {
                GroupKey = groupKey,
                Target = target.Target,
                Profile = spec.Profile,
                SelectedEntry = entry == null ? null : entry.Candidate
            };
            if (entry != null)
                AddBlockers(output.Blockers, entry.Candidate.Blockers);
            AddBlocker(output.Blockers, candidates, target.PocketCollision);
            if (!target.HasGoal)
            {
                AddBlockers(output.Blockers, target.EntryBlockers);
                output.Decision = target.EntryGeometryUnverified || target.GridGeometryUnverified ||
                                  target.PocketCollision.State == MaintenanceCollisionState.Unverified ||
                                  target.SupplyDirectionInferred
                    ? MaintenanceDecision.PendingReview
                    : MaintenanceDecision.Fail;
                output.DecisionReason = output.Decision == MaintenanceDecision.PendingReview
                    ? "设备检修侧或可通行几何未能完整验证，不得自动判红，需专业复核。"
                    : "700 mm 与 600 mm 人体包络均无法到达设备检修侧。";
                AddTargetOnlyItems(output, target, string.Empty, analysisId);
                return output;
            }
            if (entry == null)
            {
                AddBlockers(output.Blockers, target.EntryBlockers);
                output.Decision = target.EntryGeometryUnverified ||
                                  target.GridGeometryUnverified ||
                                  target.SupplyDirectionInferred
                    ? MaintenanceDecision.PendingReview
                    : MaintenanceDecision.Fail;
                output.DecisionReason = output.Decision == MaintenanceDecision.PendingReview
                    ? "入口、梯具或转身几何存在无法精确验证的候选，需专业复核后再定结论。"
                    : "700 mm 与 600 mm 均无法形成完整的入口、梯具、转身和通行链。";
                AddTargetOnlyItems(output, target, string.Empty, analysisId);
                return output;
            }

            List<GridCell> routeCells = MaintenancePathfinder.FindPath8(grid, entry.Start, target.Goal);
            if (routeCells.Count == 0)
            {
                output.Decision = target.SupplyDirectionInferred || target.GridGeometryUnverified
                    ? MaintenanceDecision.PendingReview
                    : MaintenanceDecision.Fail;
                output.DecisionReason = target.SupplyDirectionInferred
                    ? "送风连接器缺失导致检修侧为外形推断，当前入口与推断位置不连通，需复核真实检修方向。"
                    : (target.GridGeometryUnverified
                        ? "通行栅格含无法精确验证的重叠几何，不得据此自动判红，需复核。"
                        : "入口与设备检修侧不在同一个可通行区。");
                AddTargetOnlyItems(output, target, entryNames[entry.Candidate.CandidateKey], analysisId);
                return output;
            }
            List<GridCell> simplified = MaintenancePathfinder.SimplifyPath(grid, routeCells);
            List<XYZ> routeFt = simplified.Select(x =>
            {
                MaintenancePoint2 p = grid.CellCenter(x);
                return new XYZ(
                    p.X / MmPerFoot,
                    p.Y / MmPerFoot,
                    (target.PocketCenterBottom.Z * MmPerFoot + spec.HeightMm * 0.5) / MmPerFoot);
            }).ToList();
            List<Solid> routeBodies = MaintenanceGeometryService.BuildRoute(
                routeFt,
                spec.RadiusMm / MmPerFoot,
                spec.HeightMm / MmPerFoot);
            MaintenanceCollisionResult routeCollision =
                MaintenanceGeometryService.Validate(
                    routeBodies,
                    candidates,
                    target.ExemptSourceKeys);
            AddBlocker(output.Blockers, candidates, routeCollision);
            if (!routeCollision.IsClear)
            {
                output.Decision = routeCollision.State == MaintenanceCollisionState.Unverified ||
                                  target.SupplyDirectionInferred
                    ? MaintenanceDecision.PendingReview
                    : MaintenanceDecision.Fail;
                output.DecisionReason = target.SupplyDirectionInferred
                    ? "送风连接器缺失，当前通行路线仅按外形推断的检修侧生成且受阻，需复核真实检修方向。"
                    : (routeCollision.State == MaintenanceCollisionState.Unverified
                        ? "通行路线几何无法完整验证，需专业确认。"
                        : (spec.Profile == MaintenanceAccessProfile.Full700
                            ? "700 mm 人体包络通行路线与障碍物冲突。"
                            : "600 mm 受限人体包络通行路线与障碍物冲突。"));
            }
            else if (target.SupplyDirectionInferred)
            {
                output.CompleteChainSucceeded = true;
                output.Decision = MaintenanceDecision.PendingReview;
                output.DecisionReason =
                    "未读取到送风连接器，检修左侧仅按设备外形长轴推断；即使几何通过也不得自动判为可维修。";
            }
            else if (spec.Profile == MaintenanceAccessProfile.Limited600)
            {
                output.CompleteChainSucceeded = true;
                output.Decision = MaintenanceDecision.PendingReview;
                output.DecisionReason = "600mm受限通行，需确认手能否完成维修";
            }
            else if (!target.PocketInside)
            {
                output.CompleteChainSucceeded = true;
                output.Decision = MaintenanceDecision.PendingReview;
                output.DecisionReason = "900×900×800 mm 设备检修区超出天花分组轮廓，需专业确认。";
            }
            else if (target.PocketCollision.State == MaintenanceCollisionState.Clear)
            {
                output.CompleteChainSucceeded = true;
                output.Decision = MaintenanceDecision.Pass;
                output.DecisionReason = "700 mm 通行路线和 900×900×800 mm 设备检修区均通过精确碰撞验证。";
            }
            else
            {
                output.CompleteChainSucceeded = true;
                output.Decision = MaintenanceDecision.PendingReview;
                string blocker = DescribeBlocker(candidates, target.PocketCollision.BlockerKey);
                output.DecisionReason = string.IsNullOrWhiteSpace(blocker)
                    ? "人可到达，但 900×900×800 mm 设备检修区未完全满足，需专业确认。"
                    : "人可到达，但设备检修区受“" + blocker + "”影响，需专业确认。";
            }
            output.RouteLengthMm = MaintenancePathfinder.CalculatePathLength(grid, simplified);
            foreach (XYZ p in routeFt)
                output.Route.Add(new MaintenancePoint3(
                    p.X * MmPerFoot,
                    p.Y * MmPerFoot,
                    p.Z * MmPerFoot));
            BuildTargetRenderItems(
                output,
                target,
                entryNames[entry.Candidate.CandidateKey],
                analysisId);
            return output;
        }

        private static void AddBlocker(
            IList<MaintenanceElementRef> output,
            IList<PlenumAnalysisService.Candidate> candidates,
            MaintenanceCollisionResult collision)
        {
            if (collision == null || string.IsNullOrWhiteSpace(collision.BlockerKey)) return;
            PlenumAnalysisService.Candidate candidate = candidates == null
                ? null
                : candidates.FirstOrDefault(x => string.Equals(
                    x.SourceKey,
                    collision.BlockerKey,
                    StringComparison.Ordinal));
            if (candidate == null) return;
            AddBlockers(output, new[] { ToElementRef(candidate) });
        }

        private static void AddBlockers(
            IList<MaintenanceElementRef> output,
            IEnumerable<MaintenanceElementRef> blockers)
        {
            if (output == null || blockers == null) return;
            var existing = new HashSet<string>(
                output.Where(x => x != null).Select(x => x.GetStableKey()),
                StringComparer.Ordinal);
            foreach (MaintenanceElementRef blocker in blockers.Where(x => x != null))
            {
                if (output.Count >= 24) break;
                if (existing.Add(blocker.GetStableKey())) output.Add(blocker);
            }
        }

        private static void AddTargetOnlyItems(
            MaintenanceTargetResult output,
            TargetWork target,
            string entryGroup,
            string analysisId)
        {
            BuildTargetRenderItems(output, target, entryGroup, analysisId);
        }

        private static void BuildTargetRenderItems(
            MaintenanceTargetResult output,
            TargetWork work,
            string entryGroup,
            string analysisId)
        {
            ProfileSpec spec = GetProfile(output.Profile);
            string display = work.Target.GetDisplayName().Replace(" | ", "｜");
            string conclusion = DecisionText(output.Decision);
            var pocket = NewItem(
                analysisId,
                work.Target.TargetKey + "|pocket",
                MaintenanceComponentRole.ServicePocket,
                output.Decision,
                work.Target.ServicePocketCenter,
                work.Target.SupplyDirection,
                PocketWidthMm,
                PocketWidthMm,
                PocketHeightMm,
                output.GroupKey,
                entryGroup,
                display,
                "设备检修区",
                conclusion,
                output.DecisionReason);
            pocket.GeometryType = MaintenanceRenderGeometryType.Box;
            pocket.Parameters.ComponentName = output.GroupKey + "-" +
                (string.IsNullOrWhiteSpace(entryGroup) ? "无入口" : entryGroup.Split('-').Last()) +
                "-设备检修区-" + SafeName(work.Target.Mark, work.Target.EquipmentName);
            pocket.SourceKeys.Add(work.Target.TargetKey);
            output.RenderItems.Add(pocket);

            PlenumAnalysisService.Bounds3 b = work.Candidate.WorldBounds;
            var target = NewItem(
                analysisId,
                work.Target.TargetKey + "|target",
                MaintenanceComponentRole.TargetEquipment,
                output.Decision,
                work.Target.Center,
                work.Target.SupplyDirection,
                Math.Max(80.0, (b.MaxX - b.MinX) * MmPerFoot),
                Math.Max(80.0, (b.MaxY - b.MinY) * MmPerFoot),
                Math.Max(80.0, (b.MaxZ - b.MinZ) * MmPerFoot),
                output.GroupKey,
                entryGroup,
                display,
                "维修对象",
                string.Empty,
                string.Empty);
            target.GeometryType = MaintenanceRenderGeometryType.Marker;
            target.Parameters.ComponentName = output.GroupKey + "-维修对象-" +
                SafeName(work.Target.Mark, work.Target.EquipmentName);
            target.SourceKeys.Add(work.Target.TargetKey);
            output.RenderItems.Add(target);

            if (output.Route.Count >= 2)
            {
                var route = NewItem(
                    analysisId,
                    work.Target.TargetKey + "|route|" + entryGroup,
                    MaintenanceComponentRole.AccessRoute,
                    output.Decision,
                    output.Route[0],
                    new MaintenancePoint2(1, 0),
                    spec.DiameterMm,
                    spec.DiameterMm,
                    spec.HeightMm,
                    output.GroupKey,
                    entryGroup,
                    display,
                    "维修路线",
                    string.Empty,
                    string.Empty);
                route.GeometryType = MaintenanceRenderGeometryType.Polyline;
                foreach (MaintenancePoint3 p in output.Route) route.Points.Add(p);
                route.Parameters.ComponentName = output.GroupKey + "-" + entryGroup.Split('-').Last() +
                    "-维修路线-" + SafeName(work.Target.Mark, work.Target.EquipmentName);
                route.SourceKeys.Add(work.Target.TargetKey);
                output.RenderItems.Add(route);
            }
        }

        private static List<MaintenanceRenderItem> BuildEntryRenderItems(
            MaintenanceCeilingGroup group,
            EntryWork entry,
            string entryGroup,
            string targetNames,
            IEnumerable<string> targetKeys,
            double floorFt,
            string analysisId)
        {
            var items = new List<MaintenanceRenderItem>();
            ProfileSpec spec = GetProfile(entry.Profile);
            string targetName = targetNames ?? string.Empty;
            double renderFloorFt = entry.Candidate.EntryType == MaintenanceEntryType.WallDoor &&
                                   !double.IsNaN(entry.LadderFloorFt) &&
                                   !double.IsInfinity(entry.LadderFloorFt)
                ? entry.LadderFloorFt
                : floorFt;
            MaintenancePoint2 tangent = new MaintenancePoint2(entry.Tangent.X, entry.Tangent.Y);
            if (entry.Candidate.EntryType == MaintenanceEntryType.WallDoor)
            {
                AddDoorFrameItems(items, group, entry, entryGroup, targetName, analysisId, tangent);
                var portal = NewItem(
                    analysisId,
                    entry.Candidate.CandidateKey + "|portal",
                    MaintenanceComponentRole.HumanEnvelope,
                    MaintenanceDecision.Pass,
                    entry.Candidate.Center,
                    tangent,
                    spec.DiameterMm,
                    spec.DiameterMm,
                    spec.HeightMm,
                    group.GroupKey,
                    entryGroup,
                    targetName,
                    "人员通行包络",
                    string.Empty,
                    string.Empty);
                portal.GeometryType = MaintenanceRenderGeometryType.Polyline;
                portal.Points.Add(new MaintenancePoint3(
                    entry.WallPoint.X * MmPerFoot - entry.Inward.X * 400.0,
                    entry.WallPoint.Y * MmPerFoot - entry.Inward.Y * 400.0,
                    group.CeilingTopMm + spec.HeightMm * 0.5));
                portal.Points.Add(new MaintenancePoint3(
                    entry.WallPoint.X * MmPerFoot + entry.Inward.X * 500.0,
                    entry.WallPoint.Y * MmPerFoot + entry.Inward.Y * 500.0,
                    group.CeilingTopMm + spec.HeightMm * 0.5));
                portal.Parameters.ComponentName = group.GroupKey + "-" + entryGroup.Split('-').Last() + "-穿门包络";
                items.Add(portal);
            }
            else
            {
                var hatch = NewItem(
                    analysisId,
                    entry.Candidate.CandidateKey + "|hatch",
                    MaintenanceComponentRole.CeilingHatch,
                    MaintenanceDecision.Pass,
                    new MaintenancePoint3(
                        entry.Candidate.Center.X,
                        entry.Candidate.Center.Y,
                        group.CeilingTopMm + 50.0),
                    new MaintenancePoint2(1, 0),
                    entry.Candidate.OpeningWidthMm,
                    entry.Candidate.OpeningHeightMm,
                    100.0,
                    group.GroupKey,
                    entryGroup,
                    targetName,
                    "天花检修口",
                    string.Empty,
                    string.Empty);
                hatch.GeometryType = MaintenanceRenderGeometryType.Box;
                hatch.Parameters.ComponentName = group.GroupKey + "-" + entryGroup.Split('-').Last() + "-天花检修口";
                items.Add(hatch);
            }

            MaintenanceComponentRole ladderRole = entry.Candidate.LadderType == MaintenanceLadderType.AFrame
                ? MaintenanceComponentRole.AFrameLadder
                : MaintenanceComponentRole.StraightLadder;
            var ladder = NewItem(
                analysisId,
                entry.Candidate.CandidateKey + "|ladder",
                ladderRole,
                MaintenanceDecision.Pass,
                new MaintenancePoint3(
                    entry.LadderPlanCenter.X * MmPerFoot,
                    entry.LadderPlanCenter.Y * MmPerFoot,
                    (renderFloorFt + group.CeilingTopMm / MmPerFoot) * MmPerFoot * 0.5),
                new MaintenancePoint2(entry.LadderAlong.X, entry.LadderAlong.Y),
                700.0,
                1500.0,
                group.CeilingTopMm - renderFloorFt * MmPerFoot,
                group.GroupKey,
                entryGroup,
                targetName,
                entry.Candidate.LadderType == MaintenanceLadderType.AFrame ? "人字梯" : "一字梯",
                string.Empty,
                string.Empty);
            ladder.GeometryType = MaintenanceRenderGeometryType.Polyline;
            ladder.Points.Add(new MaintenancePoint3(
                entry.LadderPlanCenter.X * MmPerFoot,
                entry.LadderPlanCenter.Y * MmPerFoot,
                renderFloorFt * MmPerFoot + 25.0));
            ladder.Points.Add(new MaintenancePoint3(
                entry.LadderPlanCenter.X * MmPerFoot,
                entry.LadderPlanCenter.Y * MmPerFoot,
                group.CeilingTopMm + 80.0));
            ladder.Parameters.ComponentName = group.GroupKey + "-" + entryGroup.Split('-').Last() + "-" +
                (entry.Candidate.LadderType == MaintenanceLadderType.AFrame ? "人字梯" : "一字梯");
            items.Add(ladder);

            MaintenancePoint3 turnCenter = entry.Candidate.EntryType == MaintenanceEntryType.WallDoor
                ? new MaintenancePoint3(
                    entry.WallPoint.X * MmPerFoot + entry.Inward.X * 500.0,
                    entry.WallPoint.Y * MmPerFoot + entry.Inward.Y * 500.0,
                    group.CeilingTopMm + TurnHeightMm * 0.5)
                : new MaintenancePoint3(
                    entry.Candidate.Center.X,
                    entry.Candidate.Center.Y,
                    group.CeilingTopMm + TurnHeightMm * 0.5);
            double turnValidationWidthMm =
                MaintenanceTurnZonePolicy.GetValidationWidthMm(entry.Profile);
            var turn = NewItem(
                analysisId,
                entry.Candidate.CandidateKey + "|turn",
                MaintenanceComponentRole.EntryTurnZone,
                MaintenanceDecision.Pass,
                turnCenter,
                tangent,
                turnValidationWidthMm,
                turnValidationWidthMm,
                TurnHeightMm,
                group.GroupKey,
                entryGroup,
                targetName,
                "入口转身区",
                string.Empty,
                string.Empty);
            turn.GeometryType = MaintenanceRenderGeometryType.Box;
            turn.Parameters.ComponentName = group.GroupKey + "-" + entryGroup.Split('-').Last() + "-入口转身区";
            items.Add(turn);
            List<string> sharedTargetKeys = targetKeys == null
                ? new List<string>()
                : targetKeys.Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToList();
            foreach (MaintenanceRenderItem item in items)
                foreach (string targetKey in sharedTargetKeys)
                    item.SourceKeys.Add(targetKey);
            return items;
        }

        private static void AddDoorFrameItems(
            IList<MaintenanceRenderItem> items,
            MaintenanceCeilingGroup group,
            EntryWork entry,
            string entryGroup,
            string targetName,
            string analysisId,
            MaintenancePoint2 tangent)
        {
            MaintenancePoint2 p = new MaintenancePoint2(
                entry.WallPoint.X * MmPerFoot,
                entry.WallPoint.Y * MmPerFoot);
            double doorWidthMm = entry.Candidate.OpeningWidthMm;
            double doorHeightMm = entry.Candidate.OpeningHeightMm;
            const double frameThicknessMm = 50.0;
            const double frameDepthMm = 160.0;
            for (int side = -1; side <= 1; side += 2)
            {
                MaintenancePoint2 c = p + tangent *
                    (side * (doorWidthMm + frameThicknessMm) * 0.5);
                var jamb = NewItem(
                    analysisId,
                    entry.Candidate.CandidateKey + "|door-jamb|" + side,
                    MaintenanceComponentRole.WallDoor,
                    MaintenanceDecision.Pass,
                    new MaintenancePoint3(
                        c.X,
                        c.Y,
                        group.CeilingTopMm + doorHeightMm * 0.5),
                    tangent,
                    frameThicknessMm,
                    frameDepthMm,
                    doorHeightMm,
                    group.GroupKey,
                    entryGroup,
                    targetName,
                    "侧墙检修门",
                    string.Empty,
                    string.Empty);
                jamb.GeometryType = MaintenanceRenderGeometryType.Box;
                jamb.Parameters.ComponentName = group.GroupKey + "-" + entryGroup.Split('-').Last() +
                    "-检修门框-" + (side < 0 ? "01" : "02");
                items.Add(jamb);
            }
            var head = NewItem(
                analysisId,
                entry.Candidate.CandidateKey + "|door-head",
                MaintenanceComponentRole.WallDoor,
                MaintenanceDecision.Pass,
                new MaintenancePoint3(
                    p.X,
                    p.Y,
                    group.CeilingTopMm + doorHeightMm + frameThicknessMm * 0.5),
                tangent,
                doorWidthMm + frameThicknessMm * 2.0,
                frameDepthMm,
                frameThicknessMm,
                group.GroupKey,
                entryGroup,
                targetName,
                "侧墙检修门",
                string.Empty,
                string.Empty);
            head.GeometryType = MaintenanceRenderGeometryType.Box;
            head.Parameters.ComponentName = group.GroupKey + "-" + entryGroup.Split('-').Last() + "-检修门框-03";
            items.Add(head);
            XYZ inward3 = new XYZ(entry.Inward.X, entry.Inward.Y, 0.0).Normalize();
            XYZ outward3 = -inward3;
            XYZ viewerRight3 = inward3.CrossProduct(XYZ.BasisZ).Normalize();
            bool leftHinge = entry.Candidate.DoorHingeSide == MaintenanceDoorHingeSide.Left;
            XYZ hinge3 = entry.WallPoint +
                         viewerRight3.Multiply((leftHinge ? -1.0 : 1.0) * doorWidthMm * 0.5 / MmPerFoot) +
                         outward3.Multiply(MaintenanceDoorSwingPolicy.OutboardOffsetMm / MmPerFoot);
            XYZ openLeafCenter3 = hinge3 + outward3.Multiply(doorWidthMm * 0.5 / MmPerFoot);
            string hingeLabel = leftHinge ? "左铰链向外90°" : "右铰链向外90°";
            var leaf = NewItem(
                analysisId,
                entry.Candidate.CandidateKey + "|door-leaf",
                MaintenanceComponentRole.WallDoor,
                MaintenanceDecision.Pass,
                new MaintenancePoint3(
                    openLeafCenter3.X * MmPerFoot,
                    openLeafCenter3.Y * MmPerFoot,
                    group.CeilingTopMm + doorHeightMm * 0.5),
                new MaintenancePoint2(outward3.X, outward3.Y),
                doorWidthMm,
                MaintenanceDoorSwingPolicy.LeafThicknessMm,
                doorHeightMm,
                group.GroupKey,
                entryGroup,
                targetName,
                "侧墙检修门",
                hingeLabel,
                string.Empty);
            leaf.GeometryType = MaintenanceRenderGeometryType.Box;
            leaf.Parameters.ComponentName = group.GroupKey + "-" + entryGroup.Split('-').Last() +
                                            "-检修门扇-" + hingeLabel;
            items.Add(leaf);
        }

        private static List<MaintenanceRenderItem> BuildVirtualWallItems(
            MaintenanceCeilingGroup group,
            IList<EntryWork> entries,
            IDictionary<string, string> entryNames,
            string analysisId)
        {
            var items = new List<MaintenanceRenderItem>();
            int wallIndex = 0;
            for (int loopIndex = 0; loopIndex < group.BoundaryLoops.Count; loopIndex++)
            {
                List<MaintenancePoint2> loop = group.BoundaryLoops[loopIndex];
                for (int segmentIndex = 0; segmentIndex < loop.Count; segmentIndex++)
                {
                    MaintenancePoint2 a = loop[segmentIndex];
                    MaintenancePoint2 b = loop[(segmentIndex + 1) % loop.Count];
                    List<EntryWork> cuts = entries
                        .Where(x => x.Candidate.EntryType == MaintenanceEntryType.WallDoor &&
                                    x.Candidate.BoundaryLoopIndex == loopIndex &&
                                    x.Candidate.BoundarySegmentIndex == segmentIndex)
                        .GroupBy(x => x.Candidate.CandidateKey)
                        .Select(x => x.First())
                        .ToList();
                    foreach (SegmentPiece piece in CutWallSegment(a, b, cuts))
                    {
                        double length = piece.A.DistanceTo(piece.B);
                        if (length < 20.0) continue;
                        MaintenancePoint2 direction = (piece.B - piece.A).Normalize();
                        MaintenancePoint2 center = (piece.A + piece.B) * 0.5;
                        var wall = NewItem(
                            analysisId,
                            group.GroupKey + "|wall|" + wallIndex,
                            MaintenanceComponentRole.VirtualBoundaryWall,
                            MaintenanceDecision.Pass,
                            new MaintenancePoint3(
                                center.X,
                                center.Y,
                                (group.CeilingTopMm + group.StructureBottomMm) * 0.5),
                            direction,
                            length,
                            100.0,
                            group.StructureBottomMm - group.CeilingTopMm,
                            group.GroupKey,
                            string.Empty,
                            string.Empty,
                            "虚拟边界墙",
                            string.Empty,
                            string.Empty);
                        wall.GeometryType = MaintenanceRenderGeometryType.Box;
                        wall.Parameters.ComponentName = group.GroupKey + "-虚拟边界墙-" + (++wallIndex).ToString("00");
                        foreach (MaintenanceElementRef source in group.CeilingSources)
                            wall.SourceKeys.Add(source.GetStableKey());
                        items.Add(wall);
                    }
                }
            }
            return items;
        }

        private sealed class SegmentPiece
        {
            public MaintenancePoint2 A;
            public MaintenancePoint2 B;
        }

        private static List<SegmentPiece> CutWallSegment(
            MaintenancePoint2 a,
            MaintenancePoint2 b,
            IList<EntryWork> cuts)
        {
            double length = a.DistanceTo(b);
            MaintenancePoint2 d = (b - a).Normalize();
            var intervals = new List<double[]>();
            foreach (EntryWork cut in cuts)
            {
                MaintenancePoint2 p = new MaintenancePoint2(
                    cut.WallPoint.X * MmPerFoot,
                    cut.WallPoint.Y * MmPerFoot);
                double at = Dot(p - a, d);
                intervals.Add(new[]
                {
                    Math.Max(0.0, at - cut.Candidate.OpeningWidthMm * 0.5),
                    Math.Min(length, at + cut.Candidate.OpeningWidthMm * 0.5)
                });
            }
            intervals = intervals.OrderBy(x => x[0]).ToList();
            var pieces = new List<SegmentPiece>();
            double cursor = 0.0;
            foreach (double[] interval in intervals)
            {
                if (interval[0] > cursor + 1.0)
                    pieces.Add(new SegmentPiece { A = a + d * cursor, B = a + d * interval[0] });
                cursor = Math.Max(cursor, interval[1]);
            }
            if (cursor < length - 1.0)
                pieces.Add(new SegmentPiece { A = a + d * cursor, B = b });
            if (intervals.Count == 0)
                pieces.Add(new SegmentPiece { A = a, B = b });
            return pieces;
        }

        private static MaintenanceRenderItem NewItem(
            string analysisId,
            string key,
            MaintenanceComponentRole role,
            MaintenanceDecision decision,
            MaintenancePoint3 center,
            MaintenancePoint2 direction,
            double width,
            double depth,
            double height,
            string group,
            string entry,
            string target,
            string roleName,
            string conclusion,
            string reason)
        {
            return new MaintenanceRenderItem
            {
                AnalysisId = analysisId,
                RenderKey = key,
                TargetKey = target,
                Role = role,
                Decision = decision,
                Center = center,
                Direction = direction,
                WidthMm = width,
                DepthMm = depth,
                HeightMm = height,
                Parameters = new MaintenanceInstanceParameters
                {
                    CeilingGroup = group,
                    EntryGroup = entry,
                    ComponentRole = roleName,
                    MaintenanceTarget = target,
                    MaintenanceConclusion = conclusion,
                    DecisionReason = reason,
                    ProfessionalNote = string.Empty
                }
            };
        }

        private static MaintenanceGrid BuildSafeGrid(
            Mask footprint,
            double topFt,
            IList<PlenumAnalysisService.Candidate> candidates,
            ProfileSpec spec,
            out List<MaintenanceElementRef> unverifiedBlockers,
            out bool hasUnverifiedGeometry,
            ISet<string> ignoredSourceKeys = null,
            bool recordBlockedCells = true)
        {
            unverifiedBlockers = new List<MaintenanceElementRef>();
            hasUnverifiedGeometry = false;
            var grid = new MaintenanceGrid(
                footprint.Width,
                footprint.Height,
                footprint.Cell,
                footprint.OriginX,
                footprint.OriginY,
                false);
            int radiusCells = (int)Math.Ceiling(spec.GridSafetyMm / footprint.Cell);
            var offsets = new List<int[]>();
            for (int dy = -radiusCells; dy <= radiusCells; dy++)
            for (int dx = -radiusCells; dx <= radiusCells; dx++)
                if (Math.Sqrt(dx * dx + dy * dy) * footprint.Cell <= spec.GridSafetyMm + 0.1)
                    offsets.Add(new[] { dx, dy });

            for (int y = 0; y < footprint.Height; y++)
            for (int x = 0; x < footprint.Width; x++)
            {
                if (!footprint.Get(x, y)) continue;
                bool contained = offsets.All(o => footprint.Get(x + o[0], y + o[1]));
                if (!contained) continue;
                MaintenancePoint2 c = footprint.Center(x, y);
                XYZ center = new XYZ(
                    c.X / MmPerFoot,
                    c.Y / MmPerFoot,
                    topFt + spec.HeightMm * 0.5 / MmPerFoot);
                Solid body = MaintenanceGeometryService.MakeHorizontalCapsule(
                    center,
                    center,
                    spec.RadiusMm / MmPerFoot,
                    spec.HeightMm / MmPerFoot);
                MaintenanceCollisionResult validation =
                    MaintenanceGeometryService.Validate(body, candidates, ignoredSourceKeys);
                if (validation.IsClear)
                    grid.SetWalkable(x, y, true);
                else
                {
                    if (recordBlockedCells)
                        LastBlockedByCell[grid.ToIndex(x, y)] =
                            string.IsNullOrWhiteSpace(validation.BlockerKey)
                                ? validation.State.ToString()
                                : validation.BlockerKey;
                    AddBlocker(unverifiedBlockers, candidates, validation);
                    if (validation.State == MaintenanceCollisionState.Unverified)
                    {
                        hasUnverifiedGeometry = true;
                    }
                }
            }
            return grid;
        }

        private static Mask BuildClosedMask(IList<Triangle2> triangles)
        {
            double minX = triangles.SelectMany(TrianglePoints).Min(x => x.X) - 500.0;
            double minY = triangles.SelectMany(TrianglePoints).Min(x => x.Y) - 500.0;
            double maxX = triangles.SelectMany(TrianglePoints).Max(x => x.X) + 500.0;
            double maxY = triangles.SelectMany(TrianglePoints).Max(x => x.Y) + 500.0;
            var mask = new Mask
            {
                OriginX = Math.Floor(minX / GridMm) * GridMm,
                OriginY = Math.Floor(minY / GridMm) * GridMm,
                Width = (int)Math.Ceiling((maxX - Math.Floor(minX / GridMm) * GridMm) / GridMm),
                Height = (int)Math.Ceiling((maxY - Math.Floor(minY / GridMm) * GridMm) / GridMm),
                Cell = GridMm
            };
            mask.Filled = new bool[mask.Width * mask.Height];
            for (int y = 0; y < mask.Height; y++)
            for (int x = 0; x < mask.Width; x++)
            {
                MaintenancePoint2 p = mask.Center(x, y);
                if (triangles.Any(t => PointInTriangle(p, t))) mask.Set(x, y, true);
            }
            bool[] dilated = Morph(mask, mask.Filled, GroupClosingRadiusCells, true);
            mask.Filled = Morph(mask, dilated, GroupClosingRadiusCells, false);
            return mask;
        }

        private static bool[] Morph(Mask mask, bool[] input, int radius, bool dilate)
        {
            var output = new bool[input.Length];
            for (int y = 0; y < mask.Height; y++)
            for (int x = 0; x < mask.Width; x++)
            {
                bool value = !dilate;
                for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int xx = x + dx;
                    int yy = y + dy;
                    bool sample = xx >= 0 && xx < mask.Width && yy >= 0 && yy < mask.Height &&
                                  input[yy * mask.Width + xx];
                    if (dilate && sample) { value = true; dy = radius + 1; break; }
                    if (!dilate && !sample) { value = false; dy = radius + 1; break; }
                }
                output[y * mask.Width + x] = value;
            }
            return output;
        }

        private static List<List<MaintenancePoint2>> ExtractOuterLoops(Mask mask)
        {
            var edges = new List<Edge2>();
            Action<int, int, int, int> add = (x0, y0, x1, y1) => edges.Add(new Edge2
            {
                Id = edges.Count,
                X0 = x0,
                Y0 = y0,
                X1 = x1,
                Y1 = y1
            });
            for (int y = 0; y < mask.Height; y++)
            for (int x = 0; x < mask.Width; x++)
            {
                if (!mask.Get(x, y)) continue;
                if (!mask.Get(x, y - 1)) add(x, y, x + 1, y);
                if (!mask.Get(x + 1, y)) add(x + 1, y, x + 1, y + 1);
                if (!mask.Get(x, y + 1)) add(x + 1, y + 1, x, y + 1);
                if (!mask.Get(x - 1, y)) add(x, y + 1, x, y);
            }
            var outgoing = edges.GroupBy(x => NodeKey(x.X0, x.Y0))
                .ToDictionary(x => x.Key, x => x.ToList());
            var loops = new List<List<MaintenancePoint2>>();
            foreach (Edge2 seed in edges)
            {
                if (seed.Used) continue;
                var nodes = new List<MaintenancePoint2>();
                Edge2 current = seed;
                int startX = seed.X0;
                int startY = seed.Y0;
                int guard = 0;
                while (current != null && !current.Used && guard++ < edges.Count + 5)
                {
                    current.Used = true;
                    nodes.Add(new MaintenancePoint2(
                        mask.OriginX + current.X0 * mask.Cell,
                        mask.OriginY + current.Y0 * mask.Cell));
                    if (current.X1 == startX && current.Y1 == startY) break;
                    List<Edge2> next;
                    if (!outgoing.TryGetValue(NodeKey(current.X1, current.Y1), out next)) break;
                    current = ChooseNext(current, next.Where(x => !x.Used).ToList());
                }
                nodes = SimplifyLoop(nodes, 75.0);
                if (nodes.Count >= 3 && Math.Abs(SignedArea(nodes)) > 10000.0)
                    loops.Add(nodes);
            }
            if (loops.Count == 0) return loops;
            List<MaintenancePoint2> largest = loops
                .OrderByDescending(x => Math.Abs(SignedArea(x)))
                .First();
            double outerSign = Math.Sign(SignedArea(largest));
            List<List<MaintenancePoint2>> outer = loops
                .Where(x => Math.Sign(SignedArea(x)) == outerSign)
                .OrderByDescending(x => Math.Abs(SignedArea(x)))
                .ToList();
            if (outerSign < 0.0)
                foreach (List<MaintenancePoint2> loop in outer) loop.Reverse();
            return outer;
        }

        private static Edge2 ChooseNext(Edge2 current, IList<Edge2> candidates)
        {
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];
            int dx = current.X1 - current.X0;
            int dy = current.Y1 - current.Y0;
            return candidates.OrderByDescending(x =>
            {
                int nx = x.X1 - x.X0;
                int ny = x.Y1 - x.Y0;
                return dx * ny - dy * nx;
            }).First();
        }

        private static List<MaintenancePoint2> SimplifyLoop(
            IList<MaintenancePoint2> source,
            double tolerance)
        {
            var clean = new List<MaintenancePoint2>();
            foreach (MaintenancePoint2 p in source)
            {
                if (clean.Count == 0 || clean[clean.Count - 1].DistanceTo(p) > 0.1)
                    clean.Add(p);
            }
            bool changed = true;
            while (changed && clean.Count > 3)
            {
                changed = false;
                for (int i = 0; i < clean.Count; i++)
                {
                    MaintenancePoint2 a = clean[(i - 1 + clean.Count) % clean.Count];
                    MaintenancePoint2 b = clean[i];
                    MaintenancePoint2 c = clean[(i + 1) % clean.Count];
                    if (Math.Abs(Cross(b - a, c - b)) <= 0.01)
                    {
                        clean.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }
            if (clean.Count <= 4) return clean;

            int first = 0;
            int opposite = 1;
            double farthest = 0.0;
            for (int i = 1; i < clean.Count; i++)
            {
                double distance = clean[first].DistanceTo(clean[i]);
                if (distance <= farthest) continue;
                farthest = distance;
                opposite = i;
            }
            var firstHalf = new List<MaintenancePoint2>();
            for (int i = first; i <= opposite; i++) firstHalf.Add(clean[i]);
            var secondHalf = new List<MaintenancePoint2>();
            for (int i = opposite; i < clean.Count; i++) secondHalf.Add(clean[i]);
            secondHalf.Add(clean[first]);
            List<MaintenancePoint2> simplifiedA = Rdp(firstHalf, tolerance);
            List<MaintenancePoint2> simplifiedB = Rdp(secondHalf, tolerance);
            var result = new List<MaintenancePoint2>(simplifiedA);
            for (int i = 1; i < simplifiedB.Count - 1; i++) result.Add(simplifiedB[i]);
            return result;
        }

        private static List<MaintenancePoint2> Rdp(
            IList<MaintenancePoint2> points,
            double tolerance)
        {
            if (points.Count <= 2) return points.ToList();
            var keep = new bool[points.Count];
            keep[0] = true;
            keep[points.Count - 1] = true;
            RdpRange(points, 0, points.Count - 1, tolerance, keep);
            var result = new List<MaintenancePoint2>();
            for (int i = 0; i < points.Count; i++)
                if (keep[i]) result.Add(points[i]);
            return result;
        }

        private static void RdpRange(
            IList<MaintenancePoint2> points,
            int first,
            int last,
            double tolerance,
            bool[] keep)
        {
            if (last <= first + 1) return;
            double maxDistance = -1.0;
            int farthest = -1;
            for (int i = first + 1; i < last; i++)
            {
                double distance = PointSegmentDistance(points[i], points[first], points[last]);
                if (distance <= maxDistance) continue;
                maxDistance = distance;
                farthest = i;
            }
            if (farthest < 0 || maxDistance <= tolerance) return;
            keep[farthest] = true;
            RdpRange(points, first, farthest, tolerance, keep);
            RdpRange(points, farthest, last, tolerance, keep);
        }

        private static List<Triangle2> BuildTriangles(
            IList<PlanarFace> faces,
            double highestZ)
        {
            var output = new List<Triangle2>();
            foreach (PlanarFace face in faces.Where(x => Math.Abs(x.Origin.Z - highestZ) * MmPerFoot <= 10.0))
            {
                Mesh mesh = face.Triangulate(0.5);
                for (int i = 0; i < mesh.NumTriangles; i++)
                {
                    MeshTriangle t = mesh.get_Triangle(i);
                    output.Add(new Triangle2
                    {
                        A = ToPoint2Mm(t.get_Vertex(0)),
                        B = ToPoint2Mm(t.get_Vertex(1)),
                        C = ToPoint2Mm(t.get_Vertex(2))
                    });
                }
            }
            return output;
        }

        private static List<PlanarFace> FindHighestHorizontalFaces(Element element)
        {
            var solids = new List<Solid>();
            CollectSolids(element.get_Geometry(new Options
            {
                DetailLevel = ViewDetailLevel.Fine,
                IncludeNonVisibleObjects = false,
                ComputeReferences = false
            }), Transform.Identity, solids);
            var faces = new List<PlanarFace>();
            foreach (Solid solid in solids)
            foreach (Face face in solid.Faces)
            {
                PlanarFace planar = face as PlanarFace;
                if (planar != null && planar.FaceNormal.Z > 0.999999) faces.Add(planar);
            }
            if (faces.Count == 0) return faces;
            double highest = faces.Max(x => x.Origin.Z);
            return faces.Where(x => Math.Abs(x.Origin.Z - highest) * MmPerFoot <= 1.0).ToList();
        }

        private static void CollectSolids(
            GeometryElement geometry,
            Transform transform,
            IList<Solid> output)
        {
            if (geometry == null) return;
            foreach (GeometryObject obj in geometry)
            {
                Solid solid = obj as Solid;
                if (solid != null && solid.Volume > 1e-9)
                {
                    output.Add(transform == null || transform.IsIdentity
                        ? solid
                        : SolidUtils.CreateTransformed(solid, transform));
                    continue;
                }
                GeometryInstance instance = obj as GeometryInstance;
                if (instance != null)
                    CollectSolids(
                        instance.GetSymbolGeometry(),
                        (transform ?? Transform.Identity).Multiply(instance.Transform),
                        output);
            }
        }

        private static HashSet<string> CollectTargetLocalPipeExemptions(
            string groupKey,
            PlenumAnalysisService.Candidate target,
            IList<PlenumAnalysisService.Candidate> allTargets,
            IList<PlenumAnalysisService.Candidate> candidates,
            MaintenanceAnalysisResult result)
        {
            var exemptKeys = new HashSet<string>(StringComparer.Ordinal);
            if (target == null || target.WorldBounds == null || candidates == null) return exemptKeys;
            MaintenanceBounds3Mm targetBounds = ToBoundsMm(target.WorldBounds);
            foreach (PlenumAnalysisService.Candidate pipe in candidates
                .Where(x => x != null && x.Element != null)
                .OrderBy(x => x.SourceKey, StringComparer.Ordinal))
            {
                MaintenancePipeCategoryKind category = GetPipeCategoryKind(pipe.Category);
                if (category == MaintenancePipeCategoryKind.Other) continue;

                string systemEvidence;
                string systemEvidenceSource;
                if (!TryReadReliablePipeSystemEvidence(
                    pipe.Element,
                    out systemEvidence,
                    out systemEvidenceSource))
                    continue;
                List<MaintenancePoint3> endPoints = GetPipeEndPointsMm(pipe);
                MaintenanceBounds3Mm pipeBounds = ToBoundsMm(pipe.WorldBounds);
                double lengthMm = GetPipeLengthMm(pipe, category, pipeBounds);
                double diameterMm = GetPipeDiameterMm(pipe.Element);
                double otherTargetDistanceMm = (allTargets ?? new List<PlenumAnalysisService.Candidate>())
                    .Where(x => x != null && x.WorldBounds != null &&
                                !string.Equals(x.SourceKey, target.SourceKey, StringComparison.Ordinal))
                    .Select(x => endPoints.Count == 0
                        ? MaintenancePipeExemptionPolicy.DistanceBoundsToBounds(
                            pipeBounds,
                            ToBoundsMm(x.WorldBounds))
                        : endPoints.Min(p => MaintenancePipeExemptionPolicy.DistancePointToBounds(
                            p,
                            ToBoundsMm(x.WorldBounds))))
                    .DefaultIfEmpty(double.PositiveInfinity)
                    .Min();
                var input = new MaintenancePipeExemptionInput
                {
                    Category = category,
                    SameSourceModel = SameSourceModel(pipe, target),
                    SystemEvidenceReliable = true,
                    SystemEvidence = systemEvidence,
                    LengthMm = lengthMm,
                    DiameterMm = diameterMm,
                    ElementBounds = pipeBounds,
                    TargetBounds = targetBounds,
                    NearestOtherTargetDistanceMm = otherTargetDistanceMm
                };
                input.EndPoints.AddRange(endPoints);
                MaintenancePipeExemptionDecision decision =
                    MaintenancePipeExemptionPolicy.Evaluate(input);
                if (!decision.IsExempt) continue;

                exemptKeys.Add(pipe.SourceKey);
                string targetKey = target.SourceKey ?? string.Empty;
                MaintenanceElementRef elementRef = ToElementRef(pipe);
                string elementKey = elementRef.GetStableKey();
                if (result != null && !result.ExemptPipeEvidence.Any(x =>
                    x != null &&
                    string.Equals(x.GroupKey, groupKey ?? string.Empty, StringComparison.Ordinal) &&
                    string.Equals(x.TargetKey, targetKey, StringComparison.Ordinal) &&
                    x.Element != null &&
                    string.Equals(x.Element.GetStableKey(), elementKey, StringComparison.Ordinal)))
                {
                    result.ExemptPipeEvidence.Add(new MaintenancePipeExemptionEvidence
                    {
                        GroupKey = groupKey ?? string.Empty,
                        TargetKey = targetKey,
                        Element = elementRef,
                        CategoryKind = category.ToString(),
                        SystemKind = decision.SystemKind,
                        SystemTypeEvidence = systemEvidence,
                        SystemEvidenceSource = systemEvidenceSource,
                        ReasonCode = decision.ReasonCode,
                        Reason = decision.Reason,
                        DistanceMm = decision.DistanceMm,
                        LengthMm = lengthMm,
                        DiameterMm = diameterMm
                    });
                }
            }
            return exemptKeys;
        }

        private static MaintenancePipeCategoryKind GetPipeCategoryKind(BuiltInCategory category)
        {
            if (category == BuiltInCategory.OST_PipeCurves)
                return MaintenancePipeCategoryKind.PipeCurve;
            if (category == BuiltInCategory.OST_PipeFitting)
                return MaintenancePipeCategoryKind.PipeFitting;
            if (category == BuiltInCategory.OST_PipeAccessory)
                return MaintenancePipeCategoryKind.PipeAccessory;
            return MaintenancePipeCategoryKind.Other;
        }

        private static ISet<string> CombineIgnoredSourceKeys(
            IEnumerable<string> first,
            IEnumerable<string> second)
        {
            var output = new HashSet<string>(StringComparer.Ordinal);
            if (first != null)
                foreach (string key in first.Where(x => !string.IsNullOrWhiteSpace(x)))
                    output.Add(key);
            if (second != null)
                foreach (string key in second.Where(x => !string.IsNullOrWhiteSpace(x)))
                    output.Add(key);
            return output.Count == 0 ? null : output;
        }

        private static bool SameSourceModel(
            PlenumAnalysisService.Candidate left,
            PlenumAnalysisService.Candidate right)
        {
            long? leftLink = left == null || left.Source == null
                ? null
                : left.Source.LinkInstanceId;
            long? rightLink = right == null || right.Source == null
                ? null
                : right.Source.LinkInstanceId;
            return leftLink == rightLink;
        }

        private static MaintenanceBounds3Mm ToBoundsMm(PlenumAnalysisService.Bounds3 bounds)
        {
            if (bounds == null) return null;
            return new MaintenanceBounds3Mm
            {
                MinX = bounds.MinX * MmPerFoot,
                MinY = bounds.MinY * MmPerFoot,
                MinZ = bounds.MinZ * MmPerFoot,
                MaxX = bounds.MaxX * MmPerFoot,
                MaxY = bounds.MaxY * MmPerFoot,
                MaxZ = bounds.MaxZ * MmPerFoot
            };
        }

        private static bool TryReadReliablePipeSystemEvidence(
            Element element,
            out string evidence,
            out string evidenceSource)
        {
            evidence = string.Empty;
            evidenceSource = string.Empty;
            if (element == null) return false;

            Parameter parameter = null;
            try
            {
                parameter = element.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
            }
            catch { }
            string parameterValue = ReadParameterElementOrDisplayValue(element.Document, parameter);
            if (!string.IsNullOrWhiteSpace(parameterValue))
            {
                string ignoredKind;
                if (!MaintenancePipeExemptionPolicy.TryClassifySystemEvidence(
                    parameterValue,
                    out ignoredKind))
                    return false;
                evidence = parameterValue.Trim();
                evidenceSource = "BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM";
                return true;
            }

            List<string> connectorSystemTypes = GetConnectors(element)
                .Select(ReadConnectorSystemTypeName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            if (connectorSystemTypes.Count == 0) return false;
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string value in connectorSystemTypes)
            {
                string kind;
                if (!MaintenancePipeExemptionPolicy.TryClassifySystemEvidence(value, out kind))
                    return false;
                kinds.Add(kind);
            }
            if (kinds.Count != 1) return false;
            evidence = string.Join(" + ", connectorSystemTypes);
            evidenceSource = "Connector.MEPSystem.Type";
            return true;
        }

        private static string ReadParameterElementOrDisplayValue(Document document, Parameter parameter)
        {
            if (parameter == null) return string.Empty;
            try
            {
                if (parameter.StorageType == StorageType.ElementId)
                {
                    ElementId id = parameter.AsElementId();
                    Element type = id == null || id == ElementId.InvalidElementId || document == null
                        ? null
                        : document.GetElement(id);
                    if (type != null && !string.IsNullOrWhiteSpace(type.Name)) return type.Name;
                }
                if (parameter.StorageType == StorageType.String)
                    return parameter.AsString() ?? string.Empty;
                return parameter.AsValueString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static List<Connector> GetConnectors(Element element)
        {
            var output = new List<Connector>();
            try
            {
                MEPCurve curve = element as MEPCurve;
                ConnectorManager manager = curve == null ? null : curve.ConnectorManager;
                FamilyInstance instance = element as FamilyInstance;
                if (manager == null && instance != null && instance.MEPModel != null)
                    manager = instance.MEPModel.ConnectorManager;
                if (manager == null || manager.Connectors == null) return output;
                foreach (Connector connector in manager.Connectors)
                    if (connector != null) output.Add(connector);
            }
            catch { }
            return output;
        }

        private static string ReadConnectorSystemTypeName(Connector connector)
        {
            if (connector == null) return string.Empty;
            try
            {
                MEPSystem system = connector.MEPSystem;
                if (system == null) return string.Empty;
                ElementId typeId = system.GetTypeId();
                Element type = typeId == null || typeId == ElementId.InvalidElementId
                    ? null
                    : system.Document.GetElement(typeId);
                return type == null ? string.Empty : type.Name ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static List<MaintenancePoint3> GetPipeEndPointsMm(
            PlenumAnalysisService.Candidate candidate)
        {
            var output = new List<MaintenancePoint3>();
            if (candidate == null || candidate.Element == null) return output;
            Transform toHost = candidate.ToHost ?? Transform.Identity;
            LocationCurve location = candidate.Element.Location as LocationCurve;
            if (location != null && location.Curve != null && location.Curve.IsBound)
            {
                try
                {
                    AddPoint(output, toHost.OfPoint(location.Curve.GetEndPoint(0)));
                    AddPoint(output, toHost.OfPoint(location.Curve.GetEndPoint(1)));
                }
                catch { }
            }
            foreach (Connector connector in GetConnectors(candidate.Element))
            {
                try { AddPoint(output, toHost.OfPoint(connector.Origin)); }
                catch { }
            }
            return output
                .GroupBy(x => Math.Round(x.X, 1).ToString(CultureInfo.InvariantCulture) + "|" +
                              Math.Round(x.Y, 1).ToString(CultureInfo.InvariantCulture) + "|" +
                              Math.Round(x.Z, 1).ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal)
                .Select(x => x.First())
                .ToList();
        }

        private static void AddPoint(ICollection<MaintenancePoint3> output, XYZ point)
        {
            if (output == null || point == null) return;
            output.Add(new MaintenancePoint3(
                point.X * MmPerFoot,
                point.Y * MmPerFoot,
                point.Z * MmPerFoot));
        }

        private static double GetPipeLengthMm(
            PlenumAnalysisService.Candidate candidate,
            MaintenancePipeCategoryKind category,
            MaintenanceBounds3Mm bounds)
        {
            if (category != MaintenancePipeCategoryKind.PipeCurve)
                return bounds == null ? double.NaN : bounds.LongestExtentMm;
            try
            {
                LocationCurve location = candidate.Element.Location as LocationCurve;
                return location == null || location.Curve == null
                    ? double.NaN
                    : location.Curve.Length * MmPerFoot;
            }
            catch
            {
                return double.NaN;
            }
        }

        private static double GetPipeDiameterMm(Element element)
        {
            double maximum = double.NaN;
            try
            {
                Parameter parameter = element == null
                    ? null
                    : element.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (parameter != null && parameter.StorageType == StorageType.Double)
                    maximum = parameter.AsDouble() * MmPerFoot;
            }
            catch { }
            foreach (Connector connector in GetConnectors(element))
            {
                double size = double.NaN;
                try
                {
                    size = connector.Shape == ConnectorProfileType.Round
                        ? connector.Radius * 2.0 * MmPerFoot
                        : Math.Max(connector.Width, connector.Height) * MmPerFoot;
                }
                catch { }
                if (!double.IsNaN(size) && !double.IsInfinity(size) && size > 0.0)
                    maximum = double.IsNaN(maximum) ? size : Math.Max(maximum, size);
            }
            return maximum;
        }

        private static double ResolveStructureBottom(
            IEnumerable<PlenumAnalysisService.Candidate> candidates,
            double topFt,
            Mask footprint)
        {
            List<double> levels = candidates
                .Where(x => x.State == PlenumState.Structure &&
                            (x.Category == BuiltInCategory.OST_Floors ||
                             x.Category == BuiltInCategory.OST_Roofs) &&
                            x.WorldBounds != null &&
                            x.WorldBounds.MinZ > topFt + 100.0 / MmPerFoot &&
                            CandidateTouchesMask(x, footprint))
                .Select(x => x.WorldBounds.MinZ)
                .ToList();
            if (levels.Count == 0)
                throw new InvalidOperationException("天花上方找不到可验证的楼板或屋面底。");
            return levels.Min();
        }

        private static double ResolveFloorTop(
            IEnumerable<PlenumAnalysisService.Candidate> candidates,
            double topFt,
            Mask footprint)
        {
            List<double> levels = candidates
                .Where(x => x.State == PlenumState.Structure &&
                            x.Category == BuiltInCategory.OST_Floors &&
                            x.WorldBounds != null &&
                            x.WorldBounds.MaxZ < topFt - 100.0 / MmPerFoot &&
                            CandidateTouchesMask(x, footprint))
                .Select(x => x.WorldBounds.MaxZ)
                .ToList();
            if (levels.Count == 0)
                throw new InvalidOperationException("天花下方找不到可架梯的楼面，无法验证梯子条件。");
            return levels.Max();
        }

        private static bool CandidateTouchesMask(
            PlenumAnalysisService.Candidate candidate,
            Mask mask)
        {
            if (candidate.WorldBounds == null) return false;
            double x0 = candidate.WorldBounds.MinX * MmPerFoot;
            double y0 = candidate.WorldBounds.MinY * MmPerFoot;
            double x1 = candidate.WorldBounds.MaxX * MmPerFoot;
            double y1 = candidate.WorldBounds.MaxY * MmPerFoot;
            double maskX0 = mask.OriginX;
            double maskY0 = mask.OriginY;
            double maskX1 = mask.OriginX + mask.Width * mask.Cell;
            double maskY1 = mask.OriginY + mask.Height * mask.Cell;
            if (!(x1 >= maskX0 && x0 <= maskX1 && y1 >= maskY0 && y0 <= maskY1))
                return false;
            if (candidate.Solids == null || candidate.Solids.Count == 0)
                return false;

            int stride = Math.Max(1, (int)Math.Round(400.0 / mask.Cell));
            Transform fromHost = candidate.FromHost ?? Transform.Identity;
            for (int y = 0; y < mask.Height; y += stride)
            for (int x = 0; x < mask.Width; x += stride)
            {
                if (!mask.Get(x, y)) continue;
                MaintenancePoint2 p = mask.Center(x, y);
                if (p.X < x0 || p.X > x1 || p.Y < y0 || p.Y > y1) continue;
                XYZ hostStart = new XYZ(
                    p.X / MmPerFoot,
                    p.Y / MmPerFoot,
                    candidate.WorldBounds.MinZ - 1.0);
                XYZ hostEnd = new XYZ(
                    p.X / MmPerFoot,
                    p.Y / MmPerFoot,
                    candidate.WorldBounds.MaxZ + 1.0);
                Line sourceLine = Line.CreateBound(
                    fromHost.OfPoint(hostStart),
                    fromHost.OfPoint(hostEnd));
                foreach (Solid solid in candidate.Solids)
                {
                    if (solid == null) continue;
                    try
                    {
                        SolidCurveIntersection intersection = solid.IntersectWithCurve(
                            sourceLine,
                            new SolidCurveIntersectionOptions());
                        if (intersection != null && intersection.SegmentCount > 0)
                            return true;
                    }
                    catch { }
                }
            }
            return false;
        }

        private static XYZ ResolveSupplyDirection(
            PlenumAnalysisService.Candidate candidate,
            out bool inferred)
        {
            inferred = false;
            FamilyInstance instance = candidate.Element as FamilyInstance;
            if (instance != null && instance.MEPModel != null &&
                instance.MEPModel.ConnectorManager != null)
            {
                foreach (Connector connector in instance.MEPModel.ConnectorManager.Connectors)
                {
                    try
                    {
                        if (connector.Domain != Domain.DomainHvac ||
                            connector.DuctSystemType != DuctSystemType.SupplyAir) continue;
                        XYZ direction = (candidate.ToHost ?? Transform.Identity)
                            .OfVector(connector.CoordinateSystem.BasisZ);
                        direction = new XYZ(direction.X, direction.Y, 0.0);
                        if (direction.GetLength() > 1e-8) return direction.Normalize();
                    }
                    catch { }
                }
            }
            inferred = true;
            if (candidate.WorldBounds != null)
            {
                double dx = candidate.WorldBounds.MaxX - candidate.WorldBounds.MinX;
                double dy = candidate.WorldBounds.MaxY - candidate.WorldBounds.MinY;
                return dx >= dy ? XYZ.BasisX : XYZ.BasisY;
            }
            return XYZ.BasisX;
        }

        private static XYZ ResolveServicePocketCenter(
            PlenumAnalysisService.Candidate candidate,
            XYZ supply,
            XYZ service,
            double topFt)
        {
            var vertices = new List<XYZ>();
            Transform toHost = candidate.ToHost ?? Transform.Identity;
            if (candidate.Solids != null)
            {
                foreach (Solid solid in candidate.Solids)
                foreach (Face face in solid.Faces)
                {
                    try
                    {
                        Mesh mesh = face.Triangulate(0.5);
                        foreach (XYZ vertex in mesh.Vertices)
                            vertices.Add(toHost.OfPoint(vertex));
                    }
                    catch { }
                }
            }
            if (vertices.Count == 0)
            {
                PlenumAnalysisService.Bounds3 b = candidate.WorldBounds;
                foreach (double x in new[] { b.MinX, b.MaxX })
                foreach (double y in new[] { b.MinY, b.MaxY })
                    vertices.Add(new XYZ(x, y, topFt));
            }
            double serviceMax = vertices.Max(x => x.DotProduct(service));
            double alongMid = (vertices.Min(x => x.DotProduct(supply)) +
                               vertices.Max(x => x.DotProduct(supply))) * 0.5;
            return service.Multiply(serviceMax + (PocketWidthMm * 0.5 + CollisionLiftMm) / MmPerFoot) +
                   supply.Multiply(alongMid) + XYZ.BasisZ.Multiply(topFt + CollisionLiftMm / MmPerFoot);
        }

        private static bool FindNearestWalkableOnServiceSide(
            MaintenanceGrid grid,
            GridCell seed,
            int maxRadius,
            XYZ serviceDirection,
            double minimumProjectionMm,
            out GridCell nearest)
        {
            nearest = seed;
            if (IsWalkableOnServiceSide(
                grid,
                seed.X,
                seed.Y,
                serviceDirection,
                minimumProjectionMm,
                out nearest)) return true;

            for (int radius = 1; radius <= maxRadius; radius++)
            {
                int minX = seed.X - radius;
                int maxX = seed.X + radius;
                int minY = seed.Y - radius;
                int maxY = seed.Y + radius;
                for (int x = minX; x <= maxX; x++)
                {
                    if (IsWalkableOnServiceSide(
                        grid, x, minY, serviceDirection, minimumProjectionMm, out nearest)) return true;
                    if (maxY != minY && IsWalkableOnServiceSide(
                        grid, x, maxY, serviceDirection, minimumProjectionMm, out nearest)) return true;
                }
                for (int y = minY + 1; y < maxY; y++)
                {
                    if (IsWalkableOnServiceSide(
                        grid, minX, y, serviceDirection, minimumProjectionMm, out nearest)) return true;
                    if (maxX != minX && IsWalkableOnServiceSide(
                        grid, maxX, y, serviceDirection, minimumProjectionMm, out nearest)) return true;
                }
            }
            return false;
        }

        private static bool IsWalkableOnServiceSide(
            MaintenanceGrid grid,
            int x,
            int y,
            XYZ serviceDirection,
            double minimumProjectionMm,
            out GridCell candidate)
        {
            candidate = new GridCell(x, y);
            if (!grid.IsWalkable(candidate)) return false;
            MaintenancePoint2 center = grid.CellCenter(candidate);
            double projection = center.X * serviceDirection.X + center.Y * serviceDirection.Y;
            return projection >= minimumProjectionMm - 0.1;
        }

        private static ProfileSpec GetProfile(MaintenanceAccessProfile profile)
        {
            return profile == MaintenanceAccessProfile.Limited600 ? Limited600 : Full700;
        }

        private static bool RectangleInsideMask(
            Mask mask,
            MaintenancePoint2 center,
            XYZ xDirection,
            double lengthMm,
            double widthMm)
        {
            XYZ x = new XYZ(xDirection.X, xDirection.Y, 0.0);
            if (x.GetLength() < 1e-8) x = XYZ.BasisX;
            x = x.Normalize();
            XYZ y = XYZ.BasisZ.CrossProduct(x).Normalize();
            int nx = Math.Max(2, (int)Math.Ceiling(lengthMm / mask.Cell));
            int ny = Math.Max(2, (int)Math.Ceiling(widthMm / mask.Cell));
            for (int ix = 0; ix <= nx; ix++)
            for (int iy = 0; iy <= ny; iy++)
            {
                double u = -lengthMm * 0.5 + lengthMm * ix / nx;
                double v = -widthMm * 0.5 + widthMm * iy / ny;
                var p = new MaintenancePoint2(
                    center.X + x.X * u + y.X * v,
                    center.Y + x.Y * u + y.Y * v);
                if (!mask.Contains(p)) return false;
            }
            return true;
        }

        private static string DescribeBlocker(
            IEnumerable<PlenumAnalysisService.Candidate> candidates,
            string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return string.Empty;
            PlenumAnalysisService.Candidate c = candidates.FirstOrDefault(
                x => string.Equals(x.SourceKey, key, StringComparison.Ordinal));
            if (c == null) return string.Empty;
            string category = c.Source == null ? string.Empty : c.Source.Category;
            string name = c.Source == null ? string.Empty : c.Source.Name;
            if (string.IsNullOrWhiteSpace(name)) return category;
            if (string.IsNullOrWhiteSpace(category)) return name;
            return category + "｜" + name;
        }

        private static void AddEvidenceSources(
            MaintenanceAnalysisResult result,
            IEnumerable<MaintenanceElementRef> sources)
        {
            if (result == null || sources == null) return;
            var keys = new HashSet<string>(
                result.EvidenceSources.Where(x => x != null).Select(x => x.GetStableKey()),
                StringComparer.Ordinal);
            foreach (MaintenanceElementRef source in sources.Where(x => x != null))
                if (keys.Add(source.GetStableKey())) result.EvidenceSources.Add(source);
        }

        private static bool CandidateIsInLinkScope(
            PlenumAnalysisService.Candidate candidate,
            MaintenanceLinkScopeSnapshot scope)
        {
            if (candidate == null) return false;
            PlenumSourceRef source = candidate.Source;
            return scope == null || source == null || scope.Includes(
                source.LinkInstanceId,
                source.LinkInstanceUniqueId);
        }

        private static void RegisterCollectionFailures(
            MaintenanceAnalysisResult result,
            string groupKey,
            IEnumerable<PlenumAnalysisService.CandidateCollectionFailure> failures)
        {
            if (result == null || failures == null) return;
            var linkScanKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (PlenumAnalysisService.CandidateCollectionFailure failure in failures)
            {
                if (failure == null) continue;
                PlenumSourceRef source = failure.Source;
                if (source != null && result.LinkScope != null &&
                    !result.LinkScope.Includes(
                        source.LinkInstanceId,
                        source.LinkInstanceUniqueId))
                    continue;
                string sourceKey = failure.SourceKey ?? string.Empty;
                if (IsLinkInstanceScanFailure(failure, source) &&
                    !linkScanKeys.Add(sourceKey))
                    continue;
                string reason = failure.Reason ?? string.Empty;
                if (result.CollectionFailures.Any(x => x != null &&
                    string.Equals(x.GroupKey, groupKey, StringComparison.Ordinal) &&
                    string.Equals(x.SourceKey, sourceKey, StringComparison.Ordinal) &&
                    string.Equals(x.Reason, reason, StringComparison.Ordinal)))
                    continue;
                result.EvidenceCollectionComplete = false;
                result.CollectionFailures.Add(new MaintenanceEvidenceCollectionFailure
                {
                    GroupKey = groupKey ?? string.Empty,
                    SourceKey = sourceKey,
                    LinkInstanceId = source == null ? null : source.LinkInstanceId,
                    LinkInstanceUniqueId = source == null
                        ? string.Empty
                        : source.LinkInstanceUniqueId ?? string.Empty,
                    ElementId = source == null ? 0L : source.ElementId,
                    Category = source == null ? failure.Category.ToString() : source.Category ?? string.Empty,
                    Reason = reason
                });
                result.Warnings.Add("天花分组“" + groupKey + "”证据收集不完整 " +
                                    sourceKey + "：" + reason +
                                    "；已禁止正式审批或写入。");
                if (source != null)
                    AddEvidenceSources(result, new[]
                    {
                        new MaintenanceElementRef
                        {
                            DocumentTitle = source.DocumentTitle ?? string.Empty,
                            LinkInstanceId = source.LinkInstanceId,
                            LinkInstanceUniqueId = source.LinkInstanceUniqueId ?? string.Empty,
                            ElementId = source.ElementId,
                            UniqueId = source.UniqueId ?? string.Empty,
                            Category = source.Category ?? string.Empty,
                            Name = source.Name ?? string.Empty
                        }
                    });
            }
        }

        private static bool IsLinkInstanceScanFailure(
            PlenumAnalysisService.CandidateCollectionFailure failure,
            PlenumSourceRef source)
        {
            return failure != null && source != null &&
                   source.LinkInstanceId.HasValue &&
                   source.ElementId == source.LinkInstanceId.Value &&
                   !string.IsNullOrWhiteSpace(failure.SourceKey) &&
                   failure.SourceKey.EndsWith(":*", StringComparison.Ordinal) &&
                   string.Equals(
                       source.BlockerKind,
                       "CollectionCoverage",
                       StringComparison.Ordinal);
        }

        /// <summary>
        /// Cheap, stable snapshot of every ceiling/obstacle source that participated
        /// in the analysis. Element.VersionGuid changes whenever the element changes;
        /// linked sources additionally include the link instance version/transform and
        /// source type version, so approval cannot be reused after relevant model edits.
        /// </summary>
        internal static string ComputeEvidenceFingerprint(
            Document doc,
            MaintenanceAnalysisResult result)
        {
            if (doc == null) throw new ArgumentNullException("doc");
            if (result == null) throw new ArgumentNullException("result");
            var signatures = new List<string>
            {
                "JarviTools.MaintenanceEvidence.v2",
                MaintenanceLedgerSyncService.GetModelFingerprint(doc),
                "changeSerial=" + MaintenanceDocumentChangeTracker.GetSerial(doc),
                "doorWidthMm=" + result.DoorWidthMm.ToString("0.###", CultureInfo.InvariantCulture),
                "doorHeightMm=" + result.DoorHeightMm.ToString("0.###", CultureInfo.InvariantCulture),
                "turnZonePolicy=" + MaintenanceTurnZonePolicy.PolicyVersion,
                "turnZoneFull700Mm=" + MaintenanceTurnZonePolicy
                    .GetValidationWidthMm(MaintenanceAccessProfile.Full700)
                    .ToString("0.###", CultureInfo.InvariantCulture),
                "turnZoneLimited600Mm=" + MaintenanceTurnZonePolicy
                    .GetValidationWidthMm(MaintenanceAccessProfile.Limited600)
                    .ToString("0.###", CultureInfo.InvariantCulture),
                "doorSwingPolicy=" + MaintenanceDoorSwingPolicy.PolicyVersion,
                "openingHostWallPolicy=" + MaintenanceOpeningHostWallPolicy.PolicyVersion,
                "doorSwingLeafThicknessMm=" + MaintenanceDoorSwingPolicy.LeafThicknessMm
                    .ToString("0.###", CultureInfo.InvariantCulture),
                "doorSwingOutboardOffsetMm=" + MaintenanceDoorSwingPolicy.OutboardOffsetMm
                    .ToString("0.###", CultureInfo.InvariantCulture),
                "evidenceScope=" + (result.EvidenceScopeDefinition ?? string.Empty),
                "evidenceCollectionComplete=" + result.EvidenceCollectionComplete,
                "linkScope=" + MaintenanceLinkScopePolicy.BuildSignature(result.LinkScope)
            };
            foreach (MaintenanceEvidenceCollectionFailure failure in result.CollectionFailures
                .Where(x => x != null)
                .OrderBy(x => x.GroupKey, StringComparer.Ordinal)
                .ThenBy(x => x.SourceKey, StringComparer.Ordinal)
                .ThenBy(x => x.Reason, StringComparer.Ordinal))
                signatures.Add("collectionFailure=" + failure.GroupKey + "|" +
                               failure.SourceKey + "|" + failure.Category + "|" + failure.Reason);
            foreach (MaintenanceElementRef source in result.EvidenceSources
                .Where(x => x != null)
                .OrderBy(x => x.GetStableKey(), StringComparer.Ordinal))
            {
                signatures.Add(BuildEvidenceSourceSignature(doc, source));
            }
            foreach (MaintenancePipeExemptionEvidence evidence in result.ExemptPipeEvidence
                .Where(x => x != null && x.Element != null)
                .OrderBy(x => x.GroupKey, StringComparer.Ordinal)
                .ThenBy(x => x.TargetKey, StringComparer.Ordinal)
                .ThenBy(x => x.Element.GetStableKey(), StringComparer.Ordinal))
            {
                signatures.Add(BuildLivePipeExemptionSignature(doc, evidence));
            }
            byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", signatures));
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(bytes);
                var text = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash)
                    text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        private static string BuildEvidenceSourceSignature(
            Document hostDocument,
            MaintenanceElementRef source)
        {
            Document sourceDocument = hostDocument;
            Element sourceElement = null;
            var parts = new List<string> { source.GetStableKey(), source.UniqueId ?? string.Empty };
            if (source.LinkInstanceId.HasValue)
            {
                RevitLinkInstance link = hostDocument.GetElement(
                    new ElementId(source.LinkInstanceId.Value)) as RevitLinkInstance;
                if (link == null)
                {
                    parts.Add("MISSING_LINK");
                    return string.Join("|", parts);
                }
                parts.Add(link.UniqueId ?? string.Empty);
                parts.Add(link.VersionGuid.ToString("D"));
                parts.Add(TransformSignature(link.GetTotalTransform()));
                sourceDocument = link.GetLinkDocument();
                if (sourceDocument == null)
                {
                    parts.Add("UNLOADED_LINK");
                    return string.Join("|", parts);
                }
            }
            try
            {
                if (!string.IsNullOrWhiteSpace(source.UniqueId))
                    sourceElement = sourceDocument.GetElement(source.UniqueId);
            }
            catch { }
            if (sourceElement == null)
                sourceElement = sourceDocument.GetElement(new ElementId(source.ElementId));
            if (sourceElement == null)
            {
                parts.Add("MISSING_ELEMENT");
                return string.Join("|", parts);
            }
            parts.Add(sourceElement.VersionGuid.ToString("D"));
            ElementId typeId = sourceElement.GetTypeId();
            Element type = typeId == null || typeId == ElementId.InvalidElementId
                ? null
                : sourceDocument.GetElement(typeId);
            parts.Add(type == null ? string.Empty : type.VersionGuid.ToString("D"));
            RevitLinkInstance hostLink = sourceElement as RevitLinkInstance;
            if (hostLink != null)
            {
                try
                {
                    parts.Add(hostLink.GetLinkDocument() == null ? "UNLOADED" : "LOADED");
                }
                catch (Exception exception)
                {
                    parts.Add("LINK_LOAD_STATE_ERROR:" + exception.GetType().Name);
                }
                try { parts.Add(TransformSignature(hostLink.GetTotalTransform())); }
                catch (Exception exception)
                {
                    parts.Add("LINK_TRANSFORM_ERROR:" + exception.GetType().Name);
                }
            }
            return string.Join("|", parts);
        }

        private static string BuildLivePipeExemptionSignature(
            Document hostDocument,
            MaintenancePipeExemptionEvidence evidence)
        {
            MaintenanceElementRef source = evidence.Element;
            Document sourceDocument = hostDocument;
            Element sourceElement = null;
            if (source.LinkInstanceId.HasValue)
            {
                RevitLinkInstance link = hostDocument.GetElement(
                    new ElementId(source.LinkInstanceId.Value)) as RevitLinkInstance;
                sourceDocument = link == null ? null : link.GetLinkDocument();
            }
            if (sourceDocument != null)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(source.UniqueId))
                        sourceElement = sourceDocument.GetElement(source.UniqueId);
                }
                catch { }
                if (sourceElement == null)
                    sourceElement = sourceDocument.GetElement(new ElementId(source.ElementId));
            }
            string currentSystem = string.Empty;
            string currentSystemSource = string.Empty;
            bool currentSystemReliable = TryReadReliablePipeSystemEvidence(
                sourceElement,
                out currentSystem,
                out currentSystemSource);
            return MaintenancePipeExemptionPolicy.BuildLiveSystemEvidenceSignature(
                evidence,
                currentSystemReliable,
                currentSystem,
                currentSystemSource);
        }

        private static string TransformSignature(Transform transform)
        {
            if (transform == null) return "NO_TRANSFORM";
            XYZ[] points = { transform.Origin, transform.BasisX, transform.BasisY, transform.BasisZ };
            return string.Join(",", points.SelectMany(x => new[] { x.X, x.Y, x.Z })
                .Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
        }

        private static MaintenanceElementRef ToElementRef(
            PlenumAnalysisService.Candidate candidate)
        {
            PlenumSourceRef source = candidate.Source;
            return new MaintenanceElementRef
            {
                DocumentTitle = source == null ? string.Empty : source.DocumentTitle,
                LinkInstanceId = source == null ? null : source.LinkInstanceId,
                LinkInstanceUniqueId = source == null
                    ? string.Empty
                    : source.LinkInstanceUniqueId ?? string.Empty,
                ElementId = source == null ? candidate.Element.Id.Value : source.ElementId,
                UniqueId = source == null ? candidate.Element.UniqueId : source.UniqueId,
                Category = source == null ? string.Empty : source.Category,
                Name = source == null ? candidate.Element.Name : source.Name
            };
        }

        private static MaintenanceElementRef ToElementRef(Document doc, Element element)
        {
            return new MaintenanceElementRef
            {
                DocumentTitle = doc.Title,
                ElementId = element.Id.Value,
                UniqueId = element.UniqueId,
                Category = element.Category == null ? string.Empty : element.Category.Name,
                Name = element.Name
            };
        }

        private static bool IsCeiling(Element element)
        {
            return element != null && element.Category != null &&
                   element.Category.Id.Value == (long)BuiltInCategory.OST_Ceilings;
        }

        private static string ReadComments(Element element)
        {
            Parameter parameter = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            return parameter == null ? string.Empty : (parameter.AsString() ?? string.Empty);
        }

        private static string ReadMark(Element element)
        {
            Parameter parameter = element == null
                ? null
                : element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            return parameter == null ? string.Empty : (parameter.AsString() ?? string.Empty);
        }

        private static string ResolveEquipmentName(Element element)
        {
            if (element == null) return string.Empty;
            ElementType type = element.Document.GetElement(element.GetTypeId()) as ElementType;
            return type == null || string.IsNullOrWhiteSpace(type.Name)
                ? element.Name
                : type.Name;
        }

        private static MaintenancePoint2 ToPoint2Mm(XYZ point)
        {
            return new MaintenancePoint2(point.X * MmPerFoot, point.Y * MmPerFoot);
        }

        private static IEnumerable<MaintenancePoint2> TrianglePoints(Triangle2 triangle)
        {
            yield return triangle.A;
            yield return triangle.B;
            yield return triangle.C;
        }

        private static bool PointInTriangle(MaintenancePoint2 p, Triangle2 t)
        {
            double d1 = Cross(p - t.A, t.B - t.A);
            double d2 = Cross(p - t.B, t.C - t.B);
            double d3 = Cross(p - t.C, t.A - t.C);
            bool neg = d1 < -0.01 || d2 < -0.01 || d3 < -0.01;
            bool pos = d1 > 0.01 || d2 > 0.01 || d3 > 0.01;
            return !(neg && pos);
        }

        private static double SignedArea(IList<MaintenancePoint2> polygon)
        {
            double sum = 0.0;
            for (int i = 0; i < polygon.Count; i++)
            {
                MaintenancePoint2 a = polygon[i];
                MaintenancePoint2 b = polygon[(i + 1) % polygon.Count];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return sum * 0.5;
        }

        private static double PointSegmentDistance(
            MaintenancePoint2 p,
            MaintenancePoint2 a,
            MaintenancePoint2 b)
        {
            MaintenancePoint2 ab = b - a;
            double den = Dot(ab, ab);
            if (den <= 1e-9) return p.DistanceTo(a);
            double t = Math.Max(0.0, Math.Min(1.0, Dot(p - a, ab) / den));
            return p.DistanceTo(a + ab * t);
        }

        private static double Cross(MaintenancePoint2 a, MaintenancePoint2 b)
        {
            return a.X * b.Y - a.Y * b.X;
        }

        private static double Dot(MaintenancePoint2 a, MaintenancePoint2 b)
        {
            return a.X * b.X + a.Y * b.Y;
        }

        private static long NodeKey(int x, int y)
        {
            return ((long)x << 32) ^ (uint)y;
        }

        private static string DecisionText(MaintenanceDecision decision)
        {
            switch (decision)
            {
                case MaintenanceDecision.Pass:
                    return MaintenanceParameterService.ConclusionMaintainable;
                case MaintenanceDecision.Fail:
                    return MaintenanceParameterService.ConclusionNotMaintainable;
                default:
                    return MaintenanceParameterService.ConclusionPending;
            }
        }

        private static string SafeName(string preferred, string fallback)
        {
            return !string.IsNullOrWhiteSpace(preferred)
                ? preferred.Trim()
                : (!string.IsNullOrWhiteSpace(fallback) ? fallback.Trim() : "设备");
        }

        private sealed class ElementIdComparer : IEqualityComparer<Element>
        {
            public bool Equals(Element x, Element y)
            {
                return ReferenceEquals(x, y) ||
                       (x != null && y != null && x.Id.Value == y.Id.Value);
            }

            public int GetHashCode(Element obj)
            {
                return obj == null ? 0 : obj.Id.Value.GetHashCode();
            }
        }
    }
}

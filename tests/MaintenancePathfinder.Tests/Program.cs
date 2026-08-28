using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using JarviTools.Commands.MaintenanceReachability;
using JarviTools.Core;
using JarviTools.Mcp.Tools;
using Newtonsoft.Json.Linq;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        Run("corner cutting is forbidden", CornerCutIsForbidden);
        Run("diagonal travel works when both sides are free", DiagonalWhenSidesFree);
        Run("components do not join at corners", ComponentsRespectCornerRule);
        Run("simplification does not cross a barrier", SimplificationDoesNotCrossBarrier);
        Run("randomized paths remain safe", RandomizedPathsRemainSafe);
        Run("nearest point and clone are stable", NearestPointAndClone);
        Run("candidate audit retains selected, alternate and rejected rows", CandidateAuditRetainsAllRows);
        Run("candidate audit ranking is deterministic", CandidateAuditRankingIsDeterministic);
        Run("hatch representatives are spatially deduplicated and capped", HatchRepresentativesAreStable);
        Run("600x600 door defaults reject Full700 and allow Limited600", DoorOpeningDefaultsAreFailClosed);
        Run("ceiling hatch contract is fixed at 450x450", CeilingHatchContractIs450);
        Run("selected ceiling group keys are stable across annotations", SharedCeilingGroupKeyIsStable);
        Run("one ceiling hatch must reach at least two targets before merge review", SharedCeilingEntryRequiresTwoReachedTargets);
        Run("600 wall pass is independent from a clear ceiling route", LimitedWallPassIsIndependent);
        Run("turn zone follows the access profile", TurnZoneFollowsAccessProfile);
        Run("both outward door hinges are evaluated deterministically", DoorSwingSelectsAValidHinge);
        Run("opening owner wall alignment is narrow and bidirectional", OpeningHostWallAlignmentIsNarrow);
        Run("candidate audit fingerprint is input-order stable", CandidateAuditFingerprintIsStable);
        Run("route cannot remain feasible when its entry audit fails", EntryAuditDowngradesRoute);
        Run("shared hatch route inherits entry failure from its source target", SharedHatchEntryFailureCrossesTargets);
        Run("candidate MCP JSON exposes an honest paginated contract", CandidateJsonContractIsHonest);
        Run("target-local refrigerant short branch is exempt", LocalRefrigerantBranchIsExempt);
        Run("long refrigerant main remains an obstacle", LongRefrigerantMainIsRejected);
        Run("parallel pipe without a near endpoint remains an obstacle", NearBoundsWithoutEndpointIsRejected);
        Run("ambiguous target ownership remains an obstacle", AmbiguousPipeOwnershipIsRejected);
        Run("cold-medium water name is not treated as refrigerant", ColdMediumWaterIsRejected);
        Run("local condensate fitting is exempt", LocalCondensateFittingIsExempt);
        Run("unsupported or unreliable pipe evidence remains an obstacle", UnsupportedAndUnreliableEvidenceIsRejected);
        Run("cross-model ownership and unknown pipe size fail closed", CrossModelAndUnknownSizeAreRejected);
        Run("candidate JSON exposes target-local pipe evidence", CandidateJsonExposesPipeEvidence);
        Run("xlsx sheet names remain unique after sanitizing", WorkbookSheetNamesRemainUnique);
        Run("CSV text cannot become an Excel formula", CsvFormulaInjectionIsNeutralized);
        Run("wall alternative selection is deterministic and fail closed", WallAlternativeSelectionIsDeterministic);
        Run("wall alternative requires every modelling role", WallAlternativeRequiresEveryRole);
        Run("wall alternative fingerprint is input-order stable", WallAlternativeFingerprintIsStable);
        Run("managed view ownership and collision naming are exact", ManagedViewIdentityIsExact);
        Run("stable device identity survives a front insertion", StableDeviceIdentitySurvivesFrontInsertion);
        Run("device numbering is independent per ceiling group", DeviceNumberingIsPerGroup);
        Run("legacy target identity never falls back to device only", LegacyTargetIdentityRequiresExactPair);
        Run("null boolean intersection is unverified", NullBooleanIntersectionIsUnverified);
        Run("incomplete evidence downgrades every feasible output", IncompleteEvidenceFailsClosed);
        Run("single-use approval can be renewed only at the own visualization serial", ApprovalRenewalSerialIsStrict);
        Run("DirectShape identity ignores evidence fingerprint changes", DirectShapeIdentityIgnoresEvidenceFingerprint);
        Run("same route formal geometry is reused without duplicate modelling", SameFormalGeometryReuseIsFailClosed);
        Run("formal target membership uses exact stable hashes", FormalTargetMembershipIsExact);
        Run("route evidence scope explicitly includes host and linked walls", RouteEvidenceScopeIncludesWalls);
        Run("route positive link scope excludes only explicit out-of-scope identities", RoutePositiveLinkScopeIsExact);
        Run("ladder support points match rendered ladder geometry", LadderSupportPointsMatchGeometry);
        Run("ladder support elevation tolerance is fail closed", LadderSupportToleranceIsFailClosed);
        Run("unverified ladder support cannot become a conflict or feasible candidate", UnverifiedLadderSupportStaysUnverified);
        Run("manual conclusions require the same evidence and decision reason", ManualConclusionInheritanceIsFresh);
        Run("legacy managed-view schemes remain explicitly scoped", LegacyManagedViewSchemesAreScoped);

        Console.WriteLine("TOTAL passed=" + _passed + " failed=" + _failed);
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("FAIL " + name + " :: " + ex.Message);
        }
    }

    private static void CornerCutIsForbidden()
    {
        var grid = new MaintenanceGrid(2, 2, 100, 0, 0, false);
        grid.SetWalkable(0, 0, true);
        grid.SetWalkable(1, 1, true);
        List<GridCell> path;
        double cost;
        Assert(!MaintenancePathfinder.TryFindPath8(grid, new GridCell(0, 0), new GridCell(1, 1), out path, out cost),
            "A diagonal gap between two blocked cells must not be traversable.");
    }

    private static void DiagonalWhenSidesFree()
    {
        var grid = new MaintenanceGrid(2, 2, 100, 0, 0, true);
        List<GridCell> path;
        double cost;
        Assert(MaintenancePathfinder.TryFindPath8(grid, new GridCell(0, 0), new GridCell(1, 1), out path, out cost),
            "The open grid should be traversable.");
        Assert(path.Count == 2, "Expected a direct diagonal path.");
        AssertAlmost(Math.Sqrt(2) * 100, cost, 1e-8, "Unexpected path cost.");
    }

    private static void ComponentsRespectCornerRule()
    {
        var grid = new MaintenanceGrid(4, 4, 40, 0, 0, false);
        grid.SetWalkable(0, 0, true);
        grid.SetWalkable(1, 0, true);
        grid.SetWalkable(0, 1, true);
        grid.SetWalkable(2, 2, true);
        grid.SetWalkable(3, 2, true);
        grid.SetWalkable(2, 3, true);

        int count;
        int[] labels = MaintenancePathfinder.BuildComponentLabels(grid, out count);
        Assert(count == 2, "Corner-only contact must create two components.");
        Assert(labels[grid.ToIndex(0, 0)] != labels[grid.ToIndex(2, 2)], "Components were incorrectly joined.");
    }

    private static void SimplificationDoesNotCrossBarrier()
    {
        var grid = new MaintenanceGrid(9, 7, 40, -100, 200, true);
        for (int y = 0; y <= 5; y++) grid.SetBlocked(4, y);

        var start = new GridCell(1, 2);
        var goal = new GridCell(7, 2);
        List<GridCell> raw;
        double rawCost;
        Assert(MaintenancePathfinder.TryFindPath8(grid, start, goal, out raw, out rawCost), "Detour path not found.");

        List<GridCell> simplified = MaintenancePathfinder.SimplifyPath(grid, raw);
        Assert(simplified.Count >= 3, "A barrier detour cannot simplify to one straight segment.");
        AssertPathSafe(grid, simplified);
        Assert(MaintenancePathfinder.CalculatePathLength(grid, simplified) <= rawCost + 1e-8,
            "Simplification unexpectedly lengthened the path.");
    }

    private static void RandomizedPathsRemainSafe()
    {
        var random = new Random(20260812);
        int reachable = 0;
        for (int trial = 0; trial < 160; trial++)
        {
            var grid = new MaintenanceGrid(20, 15, 40, 0, 0, true);
            for (int y = 0; y < grid.Height; y++)
                for (int x = 0; x < grid.Width; x++)
                    if (random.NextDouble() < 0.27) grid.SetBlocked(x, y);

            var start = new GridCell(0, 0);
            var goal = new GridCell(grid.Width - 1, grid.Height - 1);
            grid.SetWalkable(start, true);
            grid.SetWalkable(goal, true);

            List<GridCell> raw;
            double ignored;
            if (!MaintenancePathfinder.TryFindPath8(grid, start, goal, out raw, out ignored)) continue;
            reachable++;
            AssertPathSafe(grid, MaintenancePathfinder.SimplifyPath(grid, raw));
        }
        Assert(reachable >= 35, "Randomized test produced too few reachable cases: " + reachable);
    }

    private static void NearestPointAndClone()
    {
        var grid = new MaintenanceGrid(5, 5, 40, 0, 0, false);
        grid.SetWalkable(4, 2, true);
        GridCell nearest;
        Assert(!MaintenancePathfinder.FindNearestWalkable(grid, new GridCell(2, 2), 1, out nearest),
            "Point outside the radius was returned.");
        Assert(MaintenancePathfinder.FindNearestWalkable(grid, new GridCell(2, 2), 2, out nearest),
            "Nearest walkable point was not found.");
        Assert(nearest == new GridCell(4, 2), "Wrong nearest point.");

        MaintenanceGrid clone = grid.Clone();
        clone.SetBlocked(4, 2);
        Assert(grid.IsWalkable(4, 2), "Clone mutation changed the source grid.");
    }

    private static void CandidateAuditRetainsAllRows()
    {
        var selected = Candidate("W-01", MaintenanceCandidateStatus.Feasible, 4200, true);
        var alternate = Candidate("W-02", MaintenanceCandidateStatus.Feasible, 3500, false);
        var rejected = Candidate("W-03", MaintenanceCandidateStatus.Rejected, 0, false);
        rejected.Stage = MaintenanceCandidateStage.Ladder;
        rejected.ReasonCode = "ladder_conflict";
        rejected.Reason = "人字梯和一字梯均与设备冲突。";
        rejected.Blockers.Add(new MaintenanceElementRef { ElementId = 42, Name = "风管" });

        var rows = new List<MaintenanceCandidateEvaluation> { alternate, rejected, selected };
        MaintenanceCandidateAudit.FinalizeForReporting(rows);

        Assert(rows.Count == 3, "Candidate audit dropped a search row.");
        Assert(rows.Count(x => x.IsSelected) == 1, "Exactly one route candidate must be selected.");
        Assert(selected.Rank == 1, "Selected route must rank first for reporting.");
        Assert(alternate.SelectionReason.Contains("备选"),
            "Feasible alternate must explain why it was not selected.");
        Assert(alternate.DominatedByCandidateKey == selected.CandidateKey,
            "Feasible alternate must point to the selected candidate that dominated it.");
        Assert(alternate.DominatedByEvaluationKey == selected.EvaluationKey,
            "Feasible alternate must point to the exact selected evaluation, including profile.");
        Assert(rejected.ReasonCode == "ladder_conflict" && rejected.Blockers.Count == 1,
            "Rejected candidate lost its stage, reason or blocker evidence.");
        Assert(rows.All(x => !string.IsNullOrWhiteSpace(x.EvaluationKey)),
            "Every retained candidate needs a stable evaluation key.");
    }

    private static void CandidateAuditRankingIsDeterministic()
    {
        var source = new List<MaintenanceCandidateEvaluation>
        {
            Candidate("W-B", MaintenanceCandidateStatus.Feasible, 3000, false),
            Candidate("W-A", MaintenanceCandidateStatus.Feasible, 3000, false),
            Candidate("H-A", MaintenanceCandidateStatus.Unverified, 1200, false)
        };
        source[2].EntryType = MaintenanceEntryType.CeilingHatch;

        MaintenanceCandidateAudit.FinalizeForReporting(source);
        string first = string.Join(",", MaintenanceCandidateAudit.OrderForReporting(source)
            .Select(x => x.CandidateKey));
        source.Reverse();
        MaintenanceCandidateAudit.FinalizeForReporting(source);
        string second = string.Join(",", MaintenanceCandidateAudit.OrderForReporting(source)
            .Select(x => x.CandidateKey));

        Assert(first == second, "Candidate ranking changed with input order.");
        Assert(first == "W-A,W-B,H-A", "Unexpected deterministic ranking: " + first);
    }

    private static void HatchRepresentativesAreStable()
    {
        var samples = new List<MaintenancePoint2>
        {
            new MaintenancePoint2(10, 10),
            new MaintenancePoint2(390, 390),
            new MaintenancePoint2(410, 10),
            new MaintenancePoint2(810, 10)
        };
        int deduplicated;
        int omitted;
        List<MaintenancePoint2> first = MaintenanceCandidateAudit.SelectSpatialRepresentatives(
            samples,
            new MaintenancePoint2(500, 0),
            400,
            2,
            out deduplicated,
            out omitted);
        samples.Reverse();
        int deduplicatedAgain;
        int omittedAgain;
        List<MaintenancePoint2> second = MaintenanceCandidateAudit.SelectSpatialRepresentatives(
            samples,
            new MaintenancePoint2(500, 0),
            400,
            2,
            out deduplicatedAgain,
            out omittedAgain);

        Assert(deduplicated == 3 && omitted == 1,
            "Expected three 400 mm buckets with one omitted representative.");
        Assert(deduplicatedAgain == deduplicated && omittedAgain == omitted,
            "Coverage counts changed with input order.");
        Assert(string.Join(";", first.Select(PointKey)) == string.Join(";", second.Select(PointKey)),
            "Representative selection changed with input order.");
    }

    private static void CandidateAuditFingerprintIsStable()
    {
        var result = new MaintenanceAnalysisResult
        {
            CandidateAuditEnabled = true,
            CandidateAuditComplete = false,
            CandidateAuditScopeDefinition = "reportable_candidate_schemes",
            CandidateAuditScopeDescription = "test scope",
            CandidateAuditRoutePolicy = "one_deterministic_path",
            CandidateAuditSelectionPolicy = "selection_does_not_use_report_rank",
            CandidateAuditDisplayRankingPolicy = "selected_then_status"
        };
        result.CandidateEvaluations.Add(Candidate(
            "W-A", MaintenanceCandidateStatus.Feasible, 3200, true));
        result.CandidateEvaluations[0].LadderFloorMm = 1000.0;
        result.CandidateEvaluations[0].LadderSupportSourceKeys.Add("floor-b");
        result.CandidateEvaluations[0].LadderSupportSourceKeys.Add("floor-a");
        result.CandidateEvaluations.Add(Candidate(
            "H-A", MaintenanceCandidateStatus.Rejected, 0, false));
        result.CandidateEvaluations[1].Stage = MaintenanceCandidateStage.Opening;
        result.CandidateEvaluations[1].ReasonCode = "hatch_opening_conflict";
        result.CandidateEvaluations[1].Blockers.Add(
            new MaintenanceElementRef { ElementId = 88, Name = "结构梁" });
        result.CandidateSearchStats.Add(new MaintenanceCandidateSearchStats
        {
            GroupKey = "ROOM-01",
            TargetKey = "AHU-01",
            Profile = MaintenanceAccessProfile.Full700,
            EntryType = MaintenanceEntryType.CeilingHatch,
            RawSampleCount = 120,
            EligibleSampleCount = 80,
            DeduplicatedCount = 40,
            RetainedCount = 32,
            OmittedCount = 8,
            Truncated = true,
            AlgorithmVersion = "test-v1"
        });
        MaintenanceCandidateAudit.FinalizeForReporting(result.CandidateEvaluations);
        string first = MaintenanceCandidateAudit.ComputeFingerprint(result);
        result.CandidateEvaluations.Reverse();
        MaintenanceCandidateAudit.FinalizeForReporting(result.CandidateEvaluations);
        string second = MaintenanceCandidateAudit.ComputeFingerprint(result);
        Assert(first == second && first.Length == 64,
            "Candidate audit fingerprint must be a stable SHA-256 value.");
        MaintenanceCandidateEvaluation supported = result.CandidateEvaluations.First(
            x => x.CandidateKey == "W-A");
        supported.LadderFloorMm = 1001.0;
        string changedSupport = MaintenanceCandidateAudit.ComputeFingerprint(result);
        Assert(changedSupport != first,
            "Candidate audit fingerprint must change with the verified ladder floor.");
        supported.LadderFloorMm = 1000.0;
        supported.LadderSupportSourceKeys.Add("floor-c");
        string changedSupportSource = MaintenanceCandidateAudit.ComputeFingerprint(result);
        Assert(changedSupportSource != first,
            "Candidate audit fingerprint must change with the support floor identity.");
        supported.LadderSupportSourceKeys.Remove("floor-c");
        result.CandidateSearchStats[0].OmittedCount++;
        string changedCoverage = MaintenanceCandidateAudit.ComputeFingerprint(result);
        Assert(changedCoverage != first,
            "Candidate audit fingerprint must change when coverage completeness changes.");
        result.CandidateSearchStats[0].OmittedCount--;
        result.DoorWidthMm = 650.0;
        string changedDoorConfiguration = MaintenanceCandidateAudit.ComputeFingerprint(result);
        Assert(changedDoorConfiguration != first,
            "Candidate audit fingerprint must change with the configured door opening.");
        result.DoorWidthMm = MaintenanceAnalysisOptions.DefaultDoorWidthMm;
        supported.OpeningWidthMm = 650.0;
        string changedCandidateOpening = MaintenanceCandidateAudit.ComputeFingerprint(result);
        Assert(changedCandidateOpening != first,
            "Candidate audit fingerprint must change with a retained candidate opening.");
        supported.OpeningWidthMm = MaintenanceAnalysisOptions.DefaultDoorWidthMm;
        supported.OpeningHostSourceKeys.Add("wall-owner-a");
        string changedOpeningHost = MaintenanceCandidateAudit.ComputeFingerprint(result);
        Assert(changedOpeningHost != first,
            "Candidate audit fingerprint must change with the exact opening owner wall.");
    }

    private static void DoorOpeningDefaultsAreFailClosed()
    {
        var options = new MaintenanceAnalysisOptions();
        Assert(Math.Abs(options.DoorWidthMm - 600.0) < 1e-9 &&
               Math.Abs(options.DoorHeightMm - 600.0) < 1e-9,
            "Default side-wall door opening is not 600x600 mm.");
        Assert(MaintenanceDoorOpeningPolicy.SupportsAccessProfile(
                options.DoorWidthMm,
                options.DoorHeightMm,
                600.0,
                600.0),
            "A 600x600 mm door did not admit the Limited600 profile.");
        Assert(!MaintenanceDoorOpeningPolicy.SupportsAccessProfile(
                options.DoorWidthMm,
                options.DoorHeightMm,
                700.0,
                700.0),
            "A 600x600 mm door incorrectly admitted the Full700 profile.");
        Assert(MaintenanceDoorOpeningPolicy.SupportsAccessProfile(
                700.0,
                800.0,
                700.0,
                700.0),
            "A configured 700x800 mm door did not admit the Full700 profile.");
    }

    private static void CeilingHatchContractIs450()
    {
        var result = new MaintenanceAnalysisResult();
        Assert(Math.Abs(MaintenanceSharedCeilingEntryPolicy.DefaultHatchSizeMm - 450.0) < 1e-9 &&
               Math.Abs(result.CeilingHatchSizeMm - 450.0) < 1e-9,
            "Route analysis and result metadata must use the current 450x450 mm hatch rule.");
        Assert(!new MaintenanceAnalysisOptions().CombineSelectedCeilingsForSharedEntry,
            "Shared-entry review must remain opt-in for ordinary route analysis.");
    }

    private static void SharedCeilingGroupKeyIsStable()
    {
        string first = MaintenanceSharedCeilingEntryPolicy.BuildCombinedGroupKey(
            new[] { "7G", "7A", "7G", " " });
        string second = MaintenanceSharedCeilingEntryPolicy.BuildCombinedGroupKey(
            new[] { "7A", "7G" });
        Assert(first == "7A+7G" && second == first,
            "Selected ceilings from different annotations did not form one stable review group.");
    }

    private static void SharedCeilingEntryRequiresTwoReachedTargets()
    {
        var rows = new List<MaintenanceCandidateEvaluation>
        {
            SharedHatchRoute("H-01", "AHU-01", MaintenanceCandidateStatus.Feasible,
                MaintenanceCandidateStage.Complete, 1200.0),
            SharedHatchRoute("H-01", "AHU-02", MaintenanceCandidateStatus.Unverified,
                MaintenanceCandidateStage.ServicePocket, 1800.0),
            SharedHatchRoute("H-01", "AHU-03", MaintenanceCandidateStatus.Unverified,
                MaintenanceCandidateStage.TargetGoal, 0.0),
            SharedHatchRoute("H-02", "AHU-01", MaintenanceCandidateStatus.Feasible,
                MaintenanceCandidateStage.Complete, 1400.0),
            SharedHatchRoute("H-02", "AHU-02", MaintenanceCandidateStatus.Feasible,
                MaintenanceCandidateStage.Complete, 1600.0)
        };

        List<MaintenanceSharedCeilingEntryAlternative> alternatives =
            MaintenanceSharedCeilingEntryPolicy.FindAlternatives(rows);
        Assert(alternatives.Count == 2,
            "A candidate that only reached target-goal/connectivity stages was counted as a shared route.");
        Assert(alternatives[0].CandidateKey == "H-02" &&
               alternatives[0].Status == MaintenanceCandidateStatus.Feasible &&
               alternatives[0].AllTargetsComplete &&
               alternatives[0].CoveredTargetCount == 2,
            "Two complete target routes did not produce the preferred feasible shared-entry alternative.");
        MaintenanceSharedCeilingEntryAlternative review = alternatives.First(x =>
            x.CandidateKey == "H-01");
        Assert(review.Status == MaintenanceCandidateStatus.Unverified &&
               !review.AllTargetsComplete &&
               review.CoveredTargetCount == 2 &&
               Math.Abs(review.MaxRouteLengthMm - 1800.0) < 1e-9,
            "A route that reached two devices but retained service-pocket uncertainty was overstated.");
        MaintenanceSharedCeilingEntryPolicy.ApplyCoveredTargetCounts(alternatives, rows);
        Assert(rows.Where(x => x.CandidateKey == "H-01").All(x => x.CoveredTargetCount == 2),
            "Shared coverage count was not propagated to the auditable candidate rows.");
    }

    private static void LimitedWallPassIsIndependent()
    {
        Assert(MaintenanceDoorOpeningPolicy.ShouldEvaluateLimited600Wall(
                false,
                true),
            "A clear Full700 ceiling route incorrectly suppressed the Limited600 wall pass.");
        Assert(!MaintenanceDoorOpeningPolicy.ShouldEvaluateLimited600Wall(
                true,
                true),
            "A clear Full700 wall-door chain unnecessarily triggered the Limited600 wall pass.");
        Assert(MaintenanceDoorOpeningPolicy.ShouldEvaluateLimited600Wall(
                true,
                false),
            "A failed Full700 wall-door chain did not trigger the Limited600 wall pass.");
        Assert(MaintenanceDoorOpeningPolicy.ShouldSelectLimited600Result(
                true,
                MaintenanceEntryType.WallDoor),
            "A clear Limited600 wall door did not outrank an existing ceiling result.");
        Assert(!MaintenanceDoorOpeningPolicy.ShouldSelectLimited600Result(
                true,
                MaintenanceEntryType.CeilingHatch),
            "A Limited600 ceiling fallback incorrectly replaced a clear Full700 result.");
    }

    private static void TurnZoneFollowsAccessProfile()
    {
        Assert(Math.Abs(MaintenanceTurnZonePolicy.GetValidationWidthMm(
                MaintenanceAccessProfile.Full700) - 960.0) < 1e-9,
            "Full700 turn-zone validation width changed from 960 mm.");
        Assert(Math.Abs(MaintenanceTurnZonePolicy.GetValidationWidthMm(
                MaintenanceAccessProfile.Limited600) - 660.0) < 1e-9,
            "Limited600 did not use the 600 mm envelope plus 30 mm per side.");
    }

    private static void DoorSwingSelectsAValidHinge()
    {
        Assert(MaintenanceDoorSwingPolicy.Select(
                MaintenanceDoorSwingStatus.Clear,
                MaintenanceDoorSwingStatus.Clear) == MaintenanceDoorHingeSide.Right,
            "The reviewed right-hinge preference was not stable when both swings were clear.");
        Assert(MaintenanceDoorSwingPolicy.Select(
                MaintenanceDoorSwingStatus.Clear,
                MaintenanceDoorSwingStatus.Conflict) == MaintenanceDoorHingeSide.Left,
            "A clear left outward swing was discarded after the right swing conflicted.");
        Assert(MaintenanceDoorSwingPolicy.Select(
                MaintenanceDoorSwingStatus.Conflict,
                MaintenanceDoorSwingStatus.Clear) == MaintenanceDoorHingeSide.Right,
            "A clear right outward swing was discarded after the left swing conflicted.");
        Assert(MaintenanceDoorSwingPolicy.Select(
                MaintenanceDoorSwingStatus.Unverified,
                MaintenanceDoorSwingStatus.Conflict) == MaintenanceDoorHingeSide.None,
            "An unverified/conflicting pair incorrectly became a formal hinge selection.");
    }

    private static void OpeningHostWallAlignmentIsNarrow()
    {
        double nineDegrees = Math.Cos(9.0 * Math.PI / 180.0);
        double elevenDegrees = Math.Cos(11.0 * Math.PI / 180.0);
        Assert(MaintenanceOpeningHostWallPolicy.IsDirectionAligned(1.0),
            "A parallel wall direction must be accepted.");
        Assert(MaintenanceOpeningHostWallPolicy.IsDirectionAligned(-1.0),
            "The same wall in reverse curve order must be accepted.");
        Assert(MaintenanceOpeningHostWallPolicy.IsDirectionAligned(nineDegrees),
            "A wall within the 10 degree tolerance must be accepted.");
        Assert(!MaintenanceOpeningHostWallPolicy.IsDirectionAligned(elevenDegrees),
            "A wall outside the 10 degree tolerance must be rejected.");
        Assert(!MaintenanceOpeningHostWallPolicy.IsDirectionAligned(0.0),
            "A perpendicular wall must be rejected.");
        Assert(!MaintenanceOpeningHostWallPolicy.IsDirectionAligned(double.NaN),
            "Unknown wall direction must fail closed.");
    }

    private static void CandidateJsonContractIsHonest()
    {
        var result = new MaintenanceAnalysisResult
        {
            AnalysisId = "analysis-01",
            CandidateAuditEnabled = true,
            CandidateAuditComplete = false,
            CandidateAuditStrategy = "reportable_candidate_schemes",
            CandidateAuditScopeDefinition = "reportable_candidate_schemes",
            CandidateAuditScopeDescription = "400 mm representatives plus the original 80 mm selected point",
            CandidateAuditAllPathsEnumerated = false,
            CandidateAuditRoutePolicy = "one_deterministic_astar_path_per_retained_entry_target_profile",
            CandidateAuditSelectionPolicy = "original_80mm_hatch_search_and_wall_greedy_selection",
            CandidateAuditDisplayRankingPolicy = "selected_then_status_profile_entry_ladder_length_key"
        };
        MaintenanceCandidateEvaluation selected = Candidate(
            "W-A", MaintenanceCandidateStatus.Feasible, 3200, true);
        selected.LadderFloorMm = 1023.4;
        selected.LadderSupportSourceKeys.Add("floor-z");
        selected.LadderSupportSourceKeys.Add("floor-a");
        selected.Route.Add(new MaintenancePoint3(0, 0, 0));
        selected.Route.Add(new MaintenancePoint3(1000, 0, 0));
        MaintenanceCandidateEvaluation alternate = Candidate(
            "H-A", MaintenanceCandidateStatus.Unverified, 3600, false);
        alternate.EntryType = MaintenanceEntryType.CeilingHatch;
        alternate.OpeningWidthMm = 800.0;
        alternate.OpeningHeightMm = 800.0;
        alternate.Stage = MaintenanceCandidateStage.ServicePocket;
        alternate.ReasonCode = "service_side_inferred";
        result.CandidateEvaluations.Add(selected);
        result.CandidateEvaluations.Add(alternate);
        MaintenanceCandidateEvaluation otherRoomInvalidated = Candidate(
            "W-B", MaintenanceCandidateStatus.Rejected, 0, true);
        otherRoomInvalidated.GroupKey = "ROOM-02";
        otherRoomInvalidated.TargetKey = "AHU-02";
        otherRoomInvalidated.ReasonCode = "route_blocked";
        result.CandidateEvaluations.Add(otherRoomInvalidated);
        var entry = new MaintenanceCandidateEvaluation
        {
            CandidateKey = "W-A",
            GroupKey = "ROOM-01",
            TargetKey = "AHU-01",
            Scope = MaintenanceCandidateScope.Entry,
            Profile = MaintenanceAccessProfile.Full700,
            EntryType = MaintenanceEntryType.WallDoor,
            OpeningWidthMm = 600.0,
            OpeningHeightMm = 800.0,
            Status = MaintenanceCandidateStatus.Feasible,
            Stage = MaintenanceCandidateStage.Complete,
            ReasonCode = "entry_geometry_clear",
            Reason = "入口几何通过。"
        };
        result.CandidateEvaluations.Add(entry);
        MaintenanceCandidateAudit.FinalizeForReporting(result.CandidateEvaluations);
        result.CandidateSearchStats.Add(new MaintenanceCandidateSearchStats
        {
            GroupKey = "ROOM-01",
            TargetKey = "AHU-01",
            Profile = MaintenanceAccessProfile.Full700,
            EntryType = MaintenanceEntryType.CeilingHatch,
            RawSampleCount = 120,
            EligibleSampleCount = 80,
            DeduplicatedCount = 40,
            RetainedCount = 32,
            OmittedCount = 8,
            Truncated = true,
            RepresentativeSpacingMm = 400,
            AllPathsEnumerated = false,
            AlgorithmVersion = "hatch_spatial_buckets_400mm_nearest_to_target"
        });
        result.CandidateAuditFingerprint =
            MaintenanceCandidateAudit.ComputeFingerprint(result);

        JObject page = MaintenanceCandidateJson.BuildPage(
            result, "ROOM-01", "AHU-01", "Route", null, null,
            false, false, 20, 0);
        Assert((string)page["scopeDefinition"] == "reportable_candidate_schemes",
            "Candidate scope definition is missing.");
        Assert(Math.Abs((double)page["doorWidthMm"] - 600.0) < 1e-9 &&
               Math.Abs((double)page["doorHeightMm"] - 600.0) < 1e-9,
            "Candidate page omitted the configured 600x600 mm door opening.");
        Assert(!(bool)page["allPathsEnumerated"] && (bool)page["truncated"],
            "Contract must state that mathematical paths were not enumerated and coverage was truncated.");
        Assert(!(bool)page["requiresReselection"],
            "Another room's invalidated selection polluted the filtered room result.");
        Assert((bool)page["analysisRequiresReselection"] &&
               !(bool)page["filteredRequiresReselection"],
            "The page must distinguish whole-analysis selection health from the queried room/target.");
        Assert((int)page["total"] == 2 && !(bool)page["pageHasMore"],
            "Unexpected candidate pagination totals.");
        var candidates = (JArray)page["candidates"];
        Assert(candidates.Count(x => (bool)x["selected"]) == 1,
            "Exactly one selected candidate must be present.");
        Assert(candidates.All(x => x["routePointsMm"] == null),
            "Route points must remain opt-in to keep MCP pages bounded.");
        Assert(candidates.Any(x => (string)x["selectionStatus"] == "eligible_not_selected" &&
                                   !string.IsNullOrWhiteSpace((string)x["selectionReason"])),
            "Unselected route candidate lost its human-readable selection reason.");
        JObject selectedJson = (JObject)candidates.First(x => (bool)x["selected"]);
        Assert(Math.Abs((double)selectedJson["ladderFloorMm"] - 1023.4) < 1e-9,
            "Candidate JSON omitted the verified local ladder floor.");
        Assert(Math.Abs((double)selectedJson["openingWidthMm"] - 600.0) < 1e-9 &&
               Math.Abs((double)selectedJson["openingHeightMm"] - 600.0) < 1e-9,
            "Candidate JSON omitted the actual wall-door opening.");
        Assert(string.Join(",", selectedJson["ladderSupportSourceKeys"].Values<string>()) ==
               "floor-a,floor-z",
            "Candidate JSON did not expose deterministic ladder support source keys.");

        JObject entryPage = MaintenanceCandidateJson.BuildPage(
            result, "ROOM-01", "AHU-01", "Entry", null, null,
            false, false, 20, 0);
        Assert((string)entryPage["candidates"][0]["selectionStatus"] == "not_applicable",
            "A feasible entry row must not be mislabeled as rejected by route selection.");
    }

    private static void EntryAuditDowngradesRoute()
    {
        MaintenanceCandidateEvaluation route = Candidate(
            "H-01", MaintenanceCandidateStatus.Feasible, 2400, true);
        route.EntryType = MaintenanceEntryType.CeilingHatch;
        var entry = new MaintenanceCandidateEvaluation
        {
            CandidateKey = "H-01",
            GroupKey = "ROOM-01",
            TargetKey = "AHU-01",
            Scope = MaintenanceCandidateScope.Entry,
            Profile = MaintenanceAccessProfile.Full700,
            EntryType = MaintenanceEntryType.CeilingHatch,
            Status = MaintenanceCandidateStatus.Rejected,
            Stage = MaintenanceCandidateStage.Opening,
            ReasonCode = "hatch_opening_conflict",
            Reason = "天花检修口开口体与结构梁冲突。"
        };
        entry.Blockers.Add(new MaintenanceElementRef { ElementId = 99, Name = "结构梁" });
        var rows = new List<MaintenanceCandidateEvaluation> { route, entry };
        MaintenanceCandidateAudit.FinalizeForReporting(rows);

        Assert(route.IsSelected && route.Status == MaintenanceCandidateStatus.Rejected,
            "Legacy selection may remain traceable, but its scheme status must reflect failed entry evidence.");
        Assert(route.Stage == MaintenanceCandidateStage.Opening &&
               route.ReasonCode == "entry_audit_hatch_opening_conflict",
            "Route did not inherit the earlier entry failure stage and reason code.");
        Assert(route.SelectionReason.Contains("选择已失效") &&
               !route.SelectionReason.Contains("已通过"),
            "Invalidated selected route still claims that the access profile passed.");
        Assert(route.Blockers.Count == 1,
            "Route did not inherit the primary entry blocker evidence.");
    }

    private static void SharedHatchEntryFailureCrossesTargets()
    {
        MaintenanceCandidateEvaluation route = SharedHatchRoute(
            "H-SHARED",
            "AHU-02",
            MaintenanceCandidateStatus.Feasible,
            MaintenanceCandidateStage.Complete,
            2100.0);
        var entry = new MaintenanceCandidateEvaluation
        {
            CandidateKey = "H-SHARED",
            GroupKey = "ROOM-01",
            TargetKey = "AHU-01",
            Scope = MaintenanceCandidateScope.Entry,
            Profile = MaintenanceAccessProfile.Full700,
            EntryType = MaintenanceEntryType.CeilingHatch,
            Status = MaintenanceCandidateStatus.Rejected,
            Stage = MaintenanceCandidateStage.Opening,
            ReasonCode = "hatch_opening_conflict",
            Reason = "共用入口开口体冲突。"
        };
        var rows = new List<MaintenanceCandidateEvaluation> { route, entry };
        MaintenanceCandidateAudit.FinalizeForReporting(rows);
        Assert(route.Status == MaintenanceCandidateStatus.Rejected &&
               route.Stage == MaintenanceCandidateStage.Opening,
            "A shared route incorrectly stayed feasible after its physical hatch failed on the source row.");
    }

    private static void LocalRefrigerantBranchIsExempt()
    {
        MaintenancePipeExemptionDecision decision = MaintenancePipeExemptionPolicy.Evaluate(
            PipeInput(
                MaintenancePipeCategoryKind.PipeCurve,
                "AS_冷媒管",
                65.0,
                900.0,
                new MaintenanceBounds3Mm
                {
                    MinX = 950, MinY = 450, MinZ = 450,
                    MaxX = 1500, MaxY = 550, MaxZ = 550
                },
                new MaintenancePoint3(980, 500, 500),
                new MaintenancePoint3(1500, 500, 500)));
        Assert(decision.IsExempt && decision.ReasonCode == "target_local_short_branch",
            "A reliable 65 mm short branch with an endpoint at the equipment should be exempt.");
    }

    private static void LongRefrigerantMainIsRejected()
    {
        MaintenancePipeExemptionInput input = PipeInput(
            MaintenancePipeCategoryKind.PipeCurve,
            "AS_冷媒管",
            65.0,
            3500.0,
            new MaintenanceBounds3Mm
            {
                MinX = -1000, MinY = 450, MinZ = 450,
                MaxX = 2500, MaxY = 550, MaxZ = 550
            },
            new MaintenancePoint3(0, 500, 500),
            new MaintenancePoint3(2500, 500, 500));
        MaintenancePipeExemptionDecision decision = MaintenancePipeExemptionPolicy.Evaluate(input);
        Assert(!decision.IsExempt && decision.ReasonCode == "branch_too_long",
            "A system name must not exempt a long main crossing the group.");
    }

    private static void NearBoundsWithoutEndpointIsRejected()
    {
        MaintenancePipeExemptionInput input = PipeInput(
            MaintenancePipeCategoryKind.PipeAccessory,
            "AS_空调冷凝水管",
            32.0,
            200.0,
            new MaintenanceBounds3Mm
            {
                MinX = 950, MinY = 450, MinZ = 450,
                MaxX = 1100, MaxY = 550, MaxZ = 550
            },
            new MaintenancePoint3(1500, 500, 500));
        MaintenancePipeExemptionDecision decision = MaintenancePipeExemptionPolicy.Evaluate(input);
        Assert(!decision.IsExempt && decision.ReasonCode == "no_near_endpoint",
            "Bounding-box proximity alone must not establish a local branch.");
    }

    private static void AmbiguousPipeOwnershipIsRejected()
    {
        MaintenancePipeExemptionInput input = PipeInput(
            MaintenancePipeCategoryKind.PipeCurve,
            "AS_冷媒管",
            65.0,
            800.0,
            new MaintenanceBounds3Mm
            {
                MinX = 950, MinY = 450, MinZ = 450,
                MaxX = 1500, MaxY = 550, MaxZ = 550
            },
            new MaintenancePoint3(980, 500, 500),
            new MaintenancePoint3(1500, 500, 500));
        input.NearestOtherTargetDistanceMm = 150.0;
        MaintenancePipeExemptionDecision decision = MaintenancePipeExemptionPolicy.Evaluate(input);
        Assert(!decision.IsExempt && decision.ReasonCode == "ambiguous_target_ownership",
            "A pipe similarly close to another equipment must remain an obstacle.");
    }

    private static void ColdMediumWaterIsRejected()
    {
        MaintenancePipeExemptionInput input = PipeInput(
            MaintenancePipeCategoryKind.PipeCurve,
            "AS_冷媒水供水",
            65.0,
            800.0,
            new MaintenanceBounds3Mm
            {
                MinX = 950, MinY = 450, MinZ = 450,
                MaxX = 1500, MaxY = 550, MaxZ = 550
            },
            new MaintenancePoint3(980, 500, 500),
            new MaintenancePoint3(1500, 500, 500));
        MaintenancePipeExemptionDecision decision = MaintenancePipeExemptionPolicy.Evaluate(input);
        Assert(!decision.IsExempt && decision.ReasonCode == "system_not_exempt",
            "The broad token 冷媒 must not exempt a 冷媒水 system.");
    }

    private static void LocalCondensateFittingIsExempt()
    {
        MaintenancePipeExemptionInput input = PipeInput(
            MaintenancePipeCategoryKind.PipeFitting,
            "AS_空调冷凝水管",
            32.0,
            200.0,
            new MaintenanceBounds3Mm
            {
                MinX = 900, MinY = 450, MinZ = 450,
                MaxX = 1100, MaxY = 550, MaxZ = 550
            },
            new MaintenancePoint3(980, 500, 500),
            new MaintenancePoint3(1100, 500, 500));
        MaintenancePipeExemptionDecision decision = MaintenancePipeExemptionPolicy.Evaluate(input);
        Assert(decision.IsExempt && decision.SystemKind == "condensate",
            "A small connector-backed condensate fitting should follow the same local-branch policy.");
    }

    private static void UnsupportedAndUnreliableEvidenceIsRejected()
    {
        MaintenancePipeExemptionInput unsupported = PipeInput(
            MaintenancePipeCategoryKind.Other,
            "AS_冷媒管",
            65.0,
            500.0,
            new MaintenanceBounds3Mm
            {
                MinX = 950, MinY = 450, MinZ = 450,
                MaxX = 1100, MaxY = 550, MaxZ = 550
            },
            new MaintenancePoint3(980, 500, 500));
        Assert(!MaintenancePipeExemptionPolicy.Evaluate(unsupported).IsExempt,
            "Ducts, flex pipes, insulation and other categories must not use the pipe exemption.");

        MaintenancePipeExemptionInput unreliable = PipeInput(
            MaintenancePipeCategoryKind.PipeCurve,
            "AS_冷媒管",
            65.0,
            500.0,
            new MaintenanceBounds3Mm
            {
                MinX = 950, MinY = 450, MinZ = 450,
                MaxX = 1100, MaxY = 550, MaxZ = 550
            },
            new MaintenancePoint3(980, 500, 500));
        unreliable.SystemEvidenceReliable = false;
        Assert(MaintenancePipeExemptionPolicy.Evaluate(unreliable).ReasonCode == "unreliable_system_evidence",
            "A display name or missing parameter must fail closed.");
    }

    private static void CrossModelAndUnknownSizeAreRejected()
    {
        MaintenancePipeExemptionInput crossModel = PipeInput(
            MaintenancePipeCategoryKind.PipeCurve,
            "AS_冷媒管",
            65.0,
            500.0,
            new MaintenanceBounds3Mm
            {
                MinX = 950, MinY = 450, MinZ = 450,
                MaxX = 1100, MaxY = 550, MaxZ = 550
            },
            new MaintenancePoint3(980, 500, 500));
        crossModel.SameSourceModel = false;
        Assert(MaintenancePipeExemptionPolicy.Evaluate(crossModel).ReasonCode == "different_source_model",
            "A host/link or different-link spatial overlap must not prove branch ownership.");

        MaintenancePipeExemptionInput unknownSize = PipeInput(
            MaintenancePipeCategoryKind.PipeAccessory,
            "AS_空调冷凝水管",
            double.NaN,
            200.0,
            new MaintenanceBounds3Mm
            {
                MinX = 950, MinY = 450, MinZ = 450,
                MaxX = 1100, MaxY = 550, MaxZ = 550
            },
            new MaintenancePoint3(980, 500, 500));
        Assert(MaintenancePipeExemptionPolicy.Evaluate(unknownSize).ReasonCode == "diameter_out_of_range",
            "Missing connector or parameter size must remain an obstacle.");

        unknownSize.DiameterMm = 125.0;
        Assert(MaintenancePipeExemptionPolicy.Evaluate(unknownSize).ReasonCode == "diameter_out_of_range",
            "An oversized branch must remain an obstacle even when it is close to the equipment.");
    }

    private static void CandidateJsonExposesPipeEvidence()
    {
        var result = new MaintenanceAnalysisResult
        {
            CandidateAuditEnabled = true,
            CandidateAuditComplete = true,
            CandidateAuditScopeDefinition = "reportable_candidate_schemes"
        };
        result.ExemptPipeEvidence.Add(new MaintenancePipeExemptionEvidence
        {
            GroupKey = "5B",
            TargetKey = "LINK:1:2",
            Element = new MaintenanceElementRef
            {
                LinkInstanceId = 1,
                ElementId = 3,
                UniqueId = "pipe-3",
                Category = "管道",
                Name = "M-VRF 6"
            },
            CategoryKind = MaintenancePipeCategoryKind.PipeCurve.ToString(),
            SystemKind = "refrigerant",
            SystemTypeEvidence = "AS_冷媒管",
            SystemEvidenceSource = "BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM",
            ReasonCode = "target_local_short_branch",
            Reason = "target_local_pipe_branch_v1",
            DistanceMm = 25.0,
            LengthMm = 900.0,
            DiameterMm = 65.0
        });
        JObject page = MaintenanceCandidateJson.BuildPage(
            result,
            "5B",
            "LINK:1:2",
            null,
            null,
            null,
            false,
            false,
            10,
            0);
        JArray evidence = (JArray)page["exemptPipeEvidence"];
        Assert(evidence.Count == 1 &&
               (string)evidence[0]["key"] == "LINK:1:3" &&
               (long)evidence[0]["elementId"] == 3L &&
               (long)evidence[0]["linkInstanceId"] == 1L &&
               (string)evidence[0]["reasonCode"] == "target_local_short_branch",
            "The route audit page did not expose structured exemption evidence.");
    }

    private static MaintenancePipeExemptionInput PipeInput(
        MaintenancePipeCategoryKind category,
        string system,
        double diameterMm,
        double lengthMm,
        MaintenanceBounds3Mm bounds,
        params MaintenancePoint3[] endPoints)
    {
        var input = new MaintenancePipeExemptionInput
        {
            Category = category,
            SameSourceModel = true,
            SystemEvidenceReliable = true,
            SystemEvidence = system,
            DiameterMm = diameterMm,
            LengthMm = lengthMm,
            ElementBounds = bounds,
            TargetBounds = new MaintenanceBounds3Mm
            {
                MinX = 0, MinY = 0, MinZ = 0,
                MaxX = 1000, MaxY = 1000, MaxZ = 1000
            },
            NearestOtherTargetDistanceMm = double.PositiveInfinity
        };
        input.EndPoints.AddRange(endPoints ?? new MaintenancePoint3[0]);
        return input;
    }

    private static string PointKey(MaintenancePoint2 point)
    {
        return point.X + "," + point.Y;
    }

    private static MaintenanceCandidateEvaluation Candidate(
        string key,
        MaintenanceCandidateStatus status,
        double routeLengthMm,
        bool selected)
    {
        return new MaintenanceCandidateEvaluation
        {
            CandidateKey = key,
            GroupKey = "ROOM-01",
            TargetKey = "AHU-01",
            Scope = MaintenanceCandidateScope.Route,
            Profile = MaintenanceAccessProfile.Full700,
            EntryType = MaintenanceEntryType.WallDoor,
            OpeningWidthMm = MaintenanceAnalysisOptions.DefaultDoorWidthMm,
            OpeningHeightMm = MaintenanceAnalysisOptions.DefaultDoorHeightMm,
            LadderType = MaintenanceLadderType.AFrame,
            Status = status,
            Stage = status == MaintenanceCandidateStatus.Feasible
                ? MaintenanceCandidateStage.Complete
                : MaintenanceCandidateStage.Route,
            RouteLengthMm = routeLengthMm,
            IsSelected = selected
        };
    }

    private static MaintenanceCandidateEvaluation SharedHatchRoute(
        string candidateKey,
        string targetKey,
        MaintenanceCandidateStatus status,
        MaintenanceCandidateStage stage,
        double routeLengthMm)
    {
        return new MaintenanceCandidateEvaluation
        {
            CandidateKey = candidateKey,
            GroupKey = "ROOM-01",
            TargetKey = targetKey,
            Scope = MaintenanceCandidateScope.Route,
            Profile = MaintenanceAccessProfile.Full700,
            EntryType = MaintenanceEntryType.CeilingHatch,
            EntryCenter = new MaintenancePoint3(1000, 2000, 3000),
            OpeningWidthMm = MaintenanceSharedCeilingEntryPolicy.DefaultHatchSizeMm,
            OpeningHeightMm = MaintenanceSharedCeilingEntryPolicy.DefaultHatchSizeMm,
            Status = status,
            Stage = stage,
            RouteLengthMm = routeLengthMm
        };
    }

    private static void WorkbookSheetNamesRemainUnique()
    {
        string path = Path.Combine(Path.GetTempPath(), "openrevit-sheet-name-test-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            var sheets = new Dictionary<string, List<ElementData>>
            {
                { "A/B", new List<ElementData>() },
                { "A:B", new List<ElementData>() },
                { "abcdefghijklmnopqrstuvwxyz123456789-X", new List<ElementData>() },
                { "abcdefghijklmnopqrstuvwxyz123456789-Y", new List<ElementData>() }
            };
            ExcelHelper.Write(path, new Dictionary<string, int>(), sheets);

            using (var archive = ZipFile.OpenRead(path))
            using (var stream = archive.GetEntry("xl/workbook.xml").Open())
            {
                XDocument workbook = XDocument.Load(stream);
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                List<string> names = workbook.Descendants(ns + "sheet")
                    .Select(x => (string)x.Attribute("name"))
                    .ToList();
                Assert(names.Count == names.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    "Workbook contains duplicate sheet names.");
                Assert(names.All(x => x.Length <= Constants.MAX_SHEET_NAME_LENGTH),
                    "Workbook contains a sheet name longer than 31 characters.");
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void CsvFormulaInjectionIsNeutralized()
    {
        string[] dangerous = { "=1+1", "+cmd", "-2+3", "@SUM(A1:A2)", "\t=1" };
        foreach (string value in dangerous)
        {
            string escaped = CsvExportSafety.EscapeField(value);
            Assert(escaped.StartsWith("'", StringComparison.Ordinal),
                "Dangerous CSV value was not neutralized: " + value);
        }
        Assert(CsvExportSafety.EscapeField("normal") == "normal", "Normal CSV text changed unexpectedly.");
    }

    private static void WallAlternativeSelectionIsDeterministic()
    {
        var clearLimited = Alternative("B", MaintenanceWallAlternativeStatus.Available,
            MaintenanceAccessProfile.Limited600, 4000);
        var pendingFull = Alternative("A",
            MaintenanceWallAlternativeStatus.AvailablePendingReview,
            MaintenanceAccessProfile.Full700, 1000);
        var clearFullLong = Alternative("C", MaintenanceWallAlternativeStatus.Available,
            MaintenanceAccessProfile.Full700, 5000);
        var clearFullShort = Alternative("D", MaintenanceWallAlternativeStatus.Available,
            MaintenanceAccessProfile.Full700, 3000);
        var unavailable = Alternative("E",
            MaintenanceWallAlternativeStatus.UnavailableNoModelableWall,
            MaintenanceAccessProfile.Full700, 100);

        MaintenanceWallAlternativeResult first =
            MaintenanceWallAlternativePolicy.SelectPreferred(new[]
            {
                unavailable, pendingFull, clearFullLong, clearLimited, clearFullShort
            });
        MaintenanceWallAlternativeResult second =
            MaintenanceWallAlternativePolicy.SelectPreferred(new[]
            {
                clearFullShort, clearLimited, clearFullLong, pendingFull, unavailable
            });
        Assert(first == clearFullShort && second == clearFullShort,
            "Selection changed with input order or ignored clear/full/short ranking.");
        Assert(MaintenanceWallAlternativePolicy.SelectPreferred(new[] { unavailable }) == null,
            "An unavailable alternative must never be selected as modelable.");
    }

    private static void WallAlternativeRequiresEveryRole()
    {
        List<MaintenanceRenderItem> complete = CompleteWallAlternativeItems();
        Assert(MaintenanceWallAlternativePolicy.IsRenderGeometryComplete(complete),
            "The complete eight-role side-wall geometry was rejected.");
        MaintenanceComponentRole[] required =
        {
            MaintenanceComponentRole.WallDoor,
            MaintenanceComponentRole.AFrameLadder,
            MaintenanceComponentRole.EntryTurnZone,
            MaintenanceComponentRole.AccessRoute,
            MaintenanceComponentRole.HumanEnvelope,
            MaintenanceComponentRole.ServicePocket,
            MaintenanceComponentRole.TargetEquipment,
            MaintenanceComponentRole.VirtualBoundaryWall
        };
        foreach (MaintenanceComponentRole missing in required)
        {
            List<MaintenanceRenderItem> reduced = complete
                .Where(x => x.Role != missing &&
                    !(missing == MaintenanceComponentRole.AFrameLadder &&
                      x.Role == MaintenanceComponentRole.StraightLadder))
                .ToList();
            Assert(!MaintenanceWallAlternativePolicy.IsRenderGeometryComplete(reduced),
                "Missing required role was accepted: " + missing);
        }
    }

    private static void WallAlternativeFingerprintIsStable()
    {
        var first = Alternative("ALT-A", MaintenanceWallAlternativeStatus.Available,
            MaintenanceAccessProfile.Full700, 1000);
        first.GroupKey = "5B";
        first.TargetKey = "TARGET-A";
        first.DeviceNo = "01";
        first.SchemeNo = 2;
        first.RenderItems.AddRange(CompleteWallAlternativeItems());
        var second = Alternative("ALT-B", MaintenanceWallAlternativeStatus.Available,
            MaintenanceAccessProfile.Full700, 2000);
        second.GroupKey = "5B";
        second.TargetKey = "TARGET-B";
        second.DeviceNo = "02";
        second.SchemeNo = 1;
        second.RenderItems.AddRange(CompleteWallAlternativeItems()
            .AsEnumerable().Reverse());
        string left = MaintenanceWallAlternativePolicy.ComputeFingerprint(
            new[] { first, second });
        string right = MaintenanceWallAlternativePolicy.ComputeFingerprint(
            new[] { second, first });
        Assert(left == right, "Alternative fingerprint depends on input order.");
        first.SelectedEntry.OpeningWidthMm = 650.0;
        string changedOpening = MaintenanceWallAlternativePolicy.ComputeFingerprint(
            new[] { first, second });
        Assert(changedOpening != left,
            "Alternative fingerprint did not change with the selected door opening.");
    }

    private static void ManagedViewIdentityIsExact()
    {
        Assert(MaintenanceManagedViewPolicy.IsExactOwner(
            "owner", "identity", "owner", "identity"),
            "Exact owner and identity were not recognized.");
        Assert(!MaintenanceManagedViewPolicy.IsExactOwner(
            "owner", "identity", "other", "identity"),
            "A different owner was accepted.");
        Assert(!MaintenanceManagedViewPolicy.IsExactOwner(
            "owner", "identity", "owner", "other"),
            "A different identity was accepted.");
        Assert(MaintenanceManagedViewPolicy.IsDedicatedSchemeView(
                "JarviTools.MaintenanceHandReach.View.v1",
                "handreach|6A|Device02|Scheme01|TargetA"),
            "A HandReach device scheme view was not recognized.");
        Assert(MaintenanceManagedViewPolicy.IsDedicatedSchemeView(
                "JarviTools.MaintenanceWallAlternative.View.v1",
                "wall-alternative|6A|Device01|Scheme01|TargetB"),
            "A wall-alternative scheme view was not recognized.");
        Assert(!MaintenanceManagedViewPolicy.IsDedicatedSchemeView(
                "JarviTools.MaintenanceHandReach.View.v1",
                "handreach-overview|6A"),
            "A device overview was incorrectly classified as a single-scheme view.");
        Assert(!MaintenanceManagedViewPolicy.IsDedicatedSchemeView(
                "JarviTools.Maintenance.AiInternal.View.v1",
                "maintenance-ai|6A"),
            "An AI context view was incorrectly classified as a single-scheme view.");
        var occupied = new HashSet<string>(StringComparer.Ordinal)
        {
            "天花5B-设备01-方案01-侧墙备选",
            "天花5B-设备01-方案01-侧墙备选 [OpenRevit 01]"
        };
        string name = MaintenanceManagedViewPolicy.BuildAvailableName(
            "天花5B-设备01-方案01-侧墙备选", occupied);
        Assert(name.EndsWith("[OpenRevit 02]", StringComparison.Ordinal),
            "A user-owned same-name view was not preserved with a safe suffix.");
        Assert(MaintenanceManagedViewPolicy.ResolveViewFamilyTypeName(
                   MaintenanceManagedViewPurpose.FormalReachability) ==
               "三维-空间可达性分析",
            "Formal maintenance views were not assigned to the formal browser type.");
        Assert(MaintenanceManagedViewPolicy.ResolveViewFamilyTypeName(
                   MaintenanceManagedViewPurpose.AiInternalAnalysis) ==
               "三维-AI内部分析",
            "AI-only work views were not assigned to the internal browser type.");
        Assert(MaintenanceManagedViewPolicy.BuildAiAnalysisViewName("6F") ==
               "天花6F-维修可达",
            "The managed AI analysis view name is not stable.");
        Assert(MaintenanceManagedViewPolicy.BuildEquipmentOverviewViewName("6F") ==
               "天花6F-设备方案总览" &&
               MaintenanceManagedViewPolicy.BuildEquipmentOverviewViewIdentity("6F") ==
               "handreach-overview|6F",
            "The shared equipment overview view contract is not stable.");
        Assert(MaintenanceManagedViewPolicy.IsFormalMaintenanceApplicationId(
                   "JarviTools.MaintenanceReachability.v1") &&
               MaintenanceManagedViewPolicy.IsFormalMaintenanceApplicationId(
                   "JarviTools.MaintenanceHandReach.v1") &&
               MaintenanceManagedViewPolicy.IsFormalMaintenanceApplicationId(
                   "JarviTools.MaintenanceWallAlternative.v1") &&
               !MaintenanceManagedViewPolicy.IsFormalMaintenanceApplicationId(
                   "JarviTools.Unrelated.v1"),
            "Whole-floor formal visibility omitted or over-included an application owner.");
        Assert(MaintenanceManagedViewPolicy.BuildFloorOverviewViewName("6F") ==
               "楼层6F-整体可达",
            "An explicit floor group did not map to its whole-floor overview.");
        Assert(MaintenanceManagedViewPolicy.BuildFloorOverviewViewName("8A") ==
               "楼层8F-整体可达" &&
               MaintenanceManagedViewPolicy.BuildFloorOverviewViewName("5B") ==
               "楼层5F-整体可达",
            "Annotated ceiling suffixes did not map to the physical floor overview.");
        Assert(MaintenanceManagedViewPolicy.GroupBelongsToFloor(
                   "8A", "楼层8F-整体可达") &&
               !MaintenanceManagedViewPolicy.GroupBelongsToFloor(
                   "6F", "楼层8F-整体可达"),
            "Whole-floor visibility grouping is not exact.");
    }

    private static void StableDeviceIdentitySurvivesFrontInsertion()
    {
        var existing = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "OLD-A", "01" },
            { "OLD-B", "02" }
        };
        var requested = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "NEW-FRONT", "01" },
            { "OLD-A", "02" },
            { "OLD-B", "03" }
        };
        Dictionary<string, string> resolved =
            MaintenanceDeviceIdentityPolicy.ResolveDeviceNumbers(
                new[] { "NEW-FRONT", "OLD-A", "OLD-B" }, existing, requested);
        Assert(resolved["OLD-A"] == "01" && resolved["OLD-B"] == "02",
            "Existing stable targets were renumbered after a front insertion.");
        Assert(resolved["NEW-FRONT"] == "03",
            "The new target reused an existing target's number.");
    }

    private static void DeviceNumberingIsPerGroup()
    {
        string g5Old = MaintenanceDeviceIdentityPolicy.BuildBucketedTargetKey("5B", "OLD");
        string g8Old = MaintenanceDeviceIdentityPolicy.BuildBucketedTargetKey("8A", "OLD");
        string g5New = MaintenanceDeviceIdentityPolicy.BuildBucketedTargetKey("5B", "NEW");
        string g8New = MaintenanceDeviceIdentityPolicy.BuildBucketedTargetKey("8A", "NEW");
        var existing = new Dictionary<string, string>
        {
            { g5Old, "01" },
            { g8Old, "01" }
        };
        var requested = new Dictionary<string, string>
        {
            { g5New, "01" }, { g5Old, "02" },
            { g8New, "01" }, { g8Old, "02" }
        };
        Dictionary<string, string> resolved =
            MaintenanceDeviceIdentityPolicy.ResolveDeviceNumbers(
                new[] { g5New, g5Old, g8New, g8Old }, existing, requested);
        Assert(resolved[g5Old] == "01" && resolved[g8Old] == "01",
            "Existing devices in independent groups lost their local 01 number.");
        Assert(resolved[g5New] == "02" && resolved[g8New] == "02",
            "New device allocation leaked reservations across groups.");
    }

    private static void LegacyTargetIdentityRequiresExactPair()
    {
        Assert(!MaintenanceTargetIdentityPolicy.IsSameTarget(
            "5B", "", "设备A", "01", "5B", "", "", "01", true),
            "Device number alone was accepted as target identity.");
        Assert(!MaintenanceTargetIdentityPolicy.IsSameTarget(
            "5B", "", "设备A", "01", "5B", "", "设备A", "02", true),
            "Legacy name without the same device number was accepted.");
        Assert(MaintenanceTargetIdentityPolicy.IsSameTarget(
            "5B", "", "设备A", "01", "5B", "", "设备A", "01", true),
            "The explicitly allowed unique legacy group+name+device pair was rejected.");
        Assert(!MaintenanceTargetIdentityPolicy.IsSameTarget(
            "5B", "HASH-A", "设备A", "01", "5B", "HASH-B", "设备A", "01", true),
            "Different stable target hashes fell back to legacy fields.");
    }

    private static void NullBooleanIntersectionIsUnverified()
    {
        Assert(MaintenanceBooleanIntersectionPolicy.RequiresUnverified(false),
            "A null Boolean result must be classified as unverified.");
        Assert(!MaintenanceBooleanIntersectionPolicy.RequiresUnverified(true),
            "A returned Boolean result was incorrectly treated as missing.");
    }

    private static void IncompleteEvidenceFailsClosed()
    {
        var result = new MaintenanceAnalysisResult
        {
            EvidenceCollectionComplete = false
        };
        var target = new MaintenanceTargetResult
        {
            Decision = MaintenanceDecision.Pass,
            DecisionReason = "pass"
        };
        target.RenderItems.Add(new MaintenanceRenderItem
        {
            Role = MaintenanceComponentRole.ServicePocket,
            Decision = MaintenanceDecision.Pass
        });
        result.TargetResults.Add(target);
        result.CandidateEvaluations.Add(new MaintenanceCandidateEvaluation
        {
            Status = MaintenanceCandidateStatus.Feasible
        });
        var alternative = Alternative("ALT",
            MaintenanceWallAlternativeStatus.Available,
            MaintenanceAccessProfile.Full700,
            1000);
        alternative.CanVisualize = true;
        alternative.RenderItems.AddRange(CompleteWallAlternativeItems());
        result.WallAlternatives.Add(alternative);

        MaintenanceEvidenceCollectionPolicy.ApplyFailClosedGate(result);
        Assert(target.Decision == MaintenanceDecision.PendingReview,
            "Target Pass was not downgraded.");
        Assert(result.CandidateEvaluations[0].Status ==
               MaintenanceCandidateStatus.Unverified,
            "Candidate Feasible was not downgraded.");
        Assert(!alternative.CanVisualize &&
               alternative.Status == MaintenanceWallAlternativeStatus
                   .UnavailableEvidenceCollectionIncomplete &&
               alternative.RenderItems.Count == 0,
            "Wall alternative remained modelable with incomplete evidence.");
    }

    private static void ApprovalRenewalSerialIsStrict()
    {
        long reusable = MaintenanceApprovalSerialPolicy
            .ResolveReusableOwnVisualizationSerial(true, true, 42, 42);
        Assert(reusable == 42 &&
               MaintenanceApprovalSerialPolicy.IsAllowedOwnVisualizationSerial(42, reusable),
            "A just-completed owned visualization could not be re-approved.");
        Assert(MaintenanceApprovalSerialPolicy
                   .ResolveReusableOwnVisualizationSerial(true, true, 43, 42) < 0,
            "An external document change was accepted as owned visualization state.");
        Assert(!MaintenanceApprovalSerialPolicy
                   .IsAllowedOwnVisualizationSerial(43, reusable),
            "An approval survived a later external document serial.");
    }

    private static void DirectShapeIdentityIgnoresEvidenceFingerprint()
    {
        var item = new MaintenanceRenderItem
        {
            RenderKey = "5B|HUID:stable-target|route|scheme01",
            EvidenceFingerprint = "evidence-before"
        };
        string before = MaintenanceDirectShapeIdentityPolicy.BuildStableBasis(item);
        item.EvidenceFingerprint = "evidence-after-change-serial";
        string after = MaintenanceDirectShapeIdentityPolicy.BuildStableBasis(item);
        Assert(before == after,
            "Evidence fingerprint leaked into the stable DirectShape/ledger row identity.");
    }

    private static void SameFormalGeometryReuseIsFailClosed()
    {
        Assert(MaintenanceFormalReusePolicy.ShouldReuse(true, true, true),
            "A complete matching formal side-wall model was not reusable.");
        Assert(!MaintenanceFormalReusePolicy.ShouldReuse(true, true, false),
            "An incomplete formal role set was accepted for reuse.");
        Assert(MaintenanceFormalReusePolicy.MustRejectIncompleteFormal(true, true, false),
            "Incomplete current formal geometry did not trigger the duplicate-prevention gate.");
        Assert(!MaintenanceFormalReusePolicy.MustRejectIncompleteFormal(true, false, false),
            "The gate rejected independent modelling when no formal model exists.");
        Assert(MaintenanceFormalReusePolicy.MustRejectPotentialDuplicate(
                true, true, false, false),
            "A same-target formal model with different evidence could be duplicated.");
        Assert(MaintenanceFormalReusePolicy.MustRejectPotentialDuplicate(
                true, true, true, false),
            "An incomplete same-target formal model could be mixed with a second model.");
        Assert(!MaintenanceFormalReusePolicy.MustRejectPotentialDuplicate(
                true, true, true, true),
            "A complete same-evidence formal model was rejected instead of reused.");
        Assert(!MaintenanceFormalReusePolicy.MustRejectPotentialDuplicate(
                true, false, false, false),
            "Independent wall modelling was rejected when no same-target formal model exists.");
    }

    private static void FormalTargetMembershipIsExact()
    {
        string shortKey = "HUID:TARGET-1";
        string longerKey = "HUID:TARGET-10";
        List<string> stored = new List<string>
        {
            MaintenanceDirectShapeIdentityPolicy.ComputeTargetHash(longerKey)
        };
        Assert(MaintenanceDirectShapeIdentityPolicy.ContainsTargetHash(
                stored, longerKey),
            "Exact stable target membership was not found.");
        Assert(!MaintenanceDirectShapeIdentityPolicy.ContainsTargetHash(
                stored, shortKey),
            "A target name/key substring matched another device's formal geometry.");
    }

    private static void RouteEvidenceScopeIncludesWalls()
    {
        Assert(MaintenanceRouteEvidenceCoveragePolicy.ScopeDefinition
                .Contains("OST_Walls"),
            "The route evidence declaration omitted walls.");
        Assert(MaintenanceRouteEvidenceCoveragePolicy.ScopeDefinition
                .Contains("host_and_loaded_links"),
            "The route evidence declaration omitted host/link coverage.");
        Assert(MaintenanceRouteEvidenceCoveragePolicy.IsComplete(true, 0),
            "A completed wall pass without failures was rejected.");
        Assert(!MaintenanceRouteEvidenceCoveragePolicy.IsComplete(true, 1),
            "A wall collection failure did not make evidence incomplete.");
        Assert(!MaintenanceRouteEvidenceCoveragePolicy.IsComplete(false, 0),
            "A skipped wall pass was treated as complete evidence.");
    }

    private static void RoutePositiveLinkScopeIsExact()
    {
        var links = new[]
        {
            new MaintenanceLinkScopeEntry
            {
                LinkInstanceId = 10,
                LinkInstanceUniqueId = "uid-mep",
                InstanceName = "MEP",
                LoadedAtAnalysis = true
            },
            new MaintenanceLinkScopeEntry
            {
                LinkInstanceId = 20,
                LinkInstanceUniqueId = "uid-structure",
                InstanceName = "Structure",
                LoadedAtAnalysis = true
            },
            new MaintenanceLinkScopeEntry
            {
                LinkInstanceId = 30,
                LinkInstanceUniqueId = "uid-unloaded-other",
                InstanceName = "Other",
                LoadedAtAnalysis = false
            }
        };
        MaintenanceLinkScopeSnapshot all = MaintenanceLinkScopePolicy.Resolve(links, null);
        Assert(all.Includes(30, "uid-unloaded-other") && !all.Explicit,
            "Default route analysis must keep the current strict all-link gate.");

        MaintenanceLinkScopeSnapshot selected = MaintenanceLinkScopePolicy.Resolve(
            links,
            new long[] { 10, 20 });
        Assert(selected.Explicit && selected.RelevantLinks.Count == 2 &&
               selected.OutOfScopeLinks.Count == 1,
            "Positive route scope must structurally expose the excluded link.");
        Assert(selected.Includes(10, "uid-mep") &&
               !selected.Includes(30, "uid-unloaded-other"),
            "Only explicitly selected link identities may supply route candidates/failures.");
        Assert(!selected.Includes(10, "uid-unloaded-other"),
            "UniqueId mismatch must fail even if a numeric ElementId is reused.");

        var result = new MaintenanceAnalysisResult { LinkScope = selected };
        result.CandidateAuditComplete = true;
        result.CandidateAuditAllPathsEnumerated = false;
        string before = MaintenanceCandidateAudit.ComputeFingerprint(result);
        selected.RelevantLinks[0].LoadedAtAnalysis = false;
        string after = MaintenanceCandidateAudit.ComputeFingerprint(result);
        Assert(before != after,
            "Route result fingerprint must include relevant link scope and load state.");
    }

    private static void LadderSupportPointsMatchGeometry()
    {
        List<MaintenancePoint2> aFrame = MaintenanceLadderFloorPolicy.BuildSupportPoints(
            MaintenanceLadderType.AFrame,
            new MaintenancePoint2(1000, 2000),
            new MaintenancePoint2(1, 0),
            0,
            3000);
        Assert(aFrame.Count == 5, "A-frame support must include its centre and all four feet.");
        AssertPoint(aFrame[0], 1000, 2000, "A-frame centre support moved.");
        AssertPoint(aFrame[1], 1660, 1700, "A-frame front-left foot does not match the solid formula.");
        AssertPoint(aFrame[2], 1660, 2300, "A-frame front-right foot does not match the solid formula.");
        AssertPoint(aFrame[3], 340, 1700, "A-frame rear-left foot does not match the solid formula.");
        AssertPoint(aFrame[4], 340, 2300, "A-frame rear-right foot does not match the solid formula.");

        List<MaintenancePoint2> aFrameY = MaintenanceLadderFloorPolicy.BuildSupportPoints(
            MaintenanceLadderType.AFrame,
            new MaintenancePoint2(1000, 2000),
            new MaintenancePoint2(0, 1),
            0,
            3000);
        AssertPoint(aFrameY[1], 1300, 2660, "Y-axis A-frame front-left foot is rotated incorrectly.");
        AssertPoint(aFrameY[2], 700, 2660, "Y-axis A-frame front-right foot is rotated incorrectly.");
        AssertPoint(aFrameY[3], 1300, 1340, "Y-axis A-frame rear-left foot is rotated incorrectly.");
        AssertPoint(aFrameY[4], 700, 1340, "Y-axis A-frame rear-right foot is rotated incorrectly.");

        List<MaintenancePoint2> operation =
            MaintenanceLadderFloorPolicy.BuildOperationZoneSupportPoints(
                new MaintenancePoint2(1000, 2000),
                new MaintenancePoint2(0, 1),
                1200,
                2500);
        Assert(operation.Count == 5,
            "Operation zone support must include its centre and all four corners.");
        AssertPoint(operation[0], 1000, 2000, "Operation zone centre support moved.");
        AssertPoint(operation[1], 2250, 1400, "Operation zone corner rotation is incorrect.");
        AssertPoint(operation[2], 2250, 2600, "Operation zone corner rotation is incorrect.");
        AssertPoint(operation[3], -250, 2600, "Operation zone corner rotation is incorrect.");
        AssertPoint(operation[4], -250, 1400, "Operation zone corner rotation is incorrect.");

        List<MaintenancePoint2> straight = MaintenanceLadderFloorPolicy.BuildSupportPoints(
            MaintenanceLadderType.Straight,
            new MaintenancePoint2(1000, 2000),
            new MaintenancePoint2(0, 1),
            0,
            3000);
        Assert(straight.Count == 2, "Straight ladder must use both real bottom feet.");
        AssertPoint(straight[0], 1300, 1655, "Straight ladder left foot does not match the solid formula.");
        AssertPoint(straight[1], 700, 1655, "Straight ladder right foot does not match the solid formula.");
        Assert(straight.All(x => x.DistanceTo(new MaintenancePoint2(1000, 2000)) > 1.0),
            "Straight ladder incorrectly used planCenter as a support point.");
    }

    private static void LadderSupportToleranceIsFailClosed()
    {
        var boundary = new[]
        {
            Support(MaintenanceFloorSupportState.Clear, 1000.0, "floor-a"),
            Support(MaintenanceFloorSupportState.Clear, 1010.0, "floor-b")
        };
        MaintenanceLadderFloorDecision clear = MaintenanceLadderFloorPolicy.Evaluate(boundary, 2);
        Assert(clear.IsClear && Math.Abs(clear.FloorElevationMm - 1010.0) < 1e-9,
            "Exactly 10 mm support delta must pass at the highest verified support surface.");

        MaintenanceLadderFloorDecision uneven = MaintenanceLadderFloorPolicy.Evaluate(
            new[]
            {
                Support(MaintenanceFloorSupportState.Clear, 1000.0, "floor-a"),
                Support(MaintenanceFloorSupportState.Clear, 1010.001, "floor-b")
            },
            2);
        Assert(uneven.State == MaintenanceFloorSupportState.Missing &&
               uneven.ReasonCode == "ladder_floor_support_uneven",
            "A support delta above 10 mm must be rejected.");

        MaintenanceLadderFloorDecision missing = MaintenanceLadderFloorPolicy.Evaluate(
            new[] { Support(MaintenanceFloorSupportState.Clear, 1000.0, "floor-a") },
            2);
        Assert(missing.State == MaintenanceFloorSupportState.Missing &&
               missing.ReasonCode == "ladder_floor_support_missing",
            "A missing foot support must be rejected instead of accepted from the remaining foot.");
    }

    private static void UnverifiedLadderSupportStaysUnverified()
    {
        MaintenanceFloorSupportSample clear =
            Support(MaintenanceFloorSupportState.Clear, 1000.0, "floor-clear");
        MaintenanceFloorSupportSample unknown =
            Support(MaintenanceFloorSupportState.Unverified, double.NaN, "floor-unknown");
        unknown.Reason = "linked floor transform unavailable";
        MaintenanceLadderFloorDecision first = MaintenanceLadderFloorPolicy.Evaluate(
            new[] { clear, unknown },
            2);
        MaintenanceLadderFloorDecision second = MaintenanceLadderFloorPolicy.Evaluate(
            new[] { unknown, clear },
            2);
        Assert(first.State == MaintenanceFloorSupportState.Unverified &&
               first.ReasonCode == "ladder_floor_support_unverified",
            "Unknown support geometry was downgraded to a conflict or feasible result.");
        Assert(second.State == first.State &&
               (Math.Abs(second.FloorElevationMm - first.FloorElevationMm) < 1e-9 ||
                (double.IsNaN(second.FloorElevationMm) && double.IsNaN(first.FloorElevationMm))),
            "Support input order changed the fail-closed state.");
        Assert(first.SourceKeys.SequenceEqual(new[] { "floor-clear", "floor-unknown" }),
            "Support evidence source ordering is not deterministic.");
    }

    private static MaintenanceFloorSupportSample Support(
        MaintenanceFloorSupportState state,
        double elevationMm,
        string sourceKey)
    {
        return new MaintenanceFloorSupportSample
        {
            State = state,
            ElevationMm = elevationMm,
            SourceKey = sourceKey
        };
    }

    private static void AssertPoint(
        MaintenancePoint2 actual,
        double expectedX,
        double expectedY,
        string message)
    {
        AssertAlmost(expectedX, actual.X, 1e-8, message + " X");
        AssertAlmost(expectedY, actual.Y, 1e-8, message + " Y");
    }

    private static void ManualConclusionInheritanceIsFresh()
    {
        Assert(MaintenanceManualStatePolicy.ShouldInheritConclusion(
                "evidence-a", "EVIDENCE-A", "same reason", "same reason"),
            "The same evidence and decision reason did not preserve the manual conclusion.");
        Assert(!MaintenanceManualStatePolicy.ShouldInheritConclusion(
                "evidence-a", "evidence-b", "same reason", "same reason"),
            "A changed evidence snapshot preserved a stale manual conclusion.");
        Assert(!MaintenanceManualStatePolicy.ShouldInheritConclusion(
                "evidence-a", "evidence-a", "old reason", "new reason"),
            "A changed algorithm decision reason preserved a stale manual conclusion.");
        Assert(MaintenanceManualStatePolicy.ResolveProfessionalNote(
                "generated note", "human note") == "human note",
            "A professional note was not preserved independently of conclusion freshness.");
    }

    private static void LegacyManagedViewSchemesAreScoped()
    {
        List<int> migrated = MaintenanceLegacySchemeViewPolicy.ResolveManagedSchemes(
            2, new[] { 1, 1, 0, -1 }, true);
        Assert(migrated.SequenceEqual(new[] { 1, 2 }),
            "The current and migrated legacy schemes were not both scoped exactly once.");
        List<int> exactOnly = MaintenanceLegacySchemeViewPolicy.ResolveManagedSchemes(
            2, new[] { 1 }, false);
        Assert(exactOnly.SequenceEqual(new[] { 2 }),
            "An exact targeted operation unexpectedly broadened to legacy schemes.");
    }

    private static MaintenanceWallAlternativeResult Alternative(
        string key,
        MaintenanceWallAlternativeStatus status,
        MaintenanceAccessProfile profile,
        double routeLengthMm)
    {
        return new MaintenanceWallAlternativeResult
        {
            AlternativeKey = key,
            Status = status,
            Profile = profile,
            RouteLengthMm = routeLengthMm,
            SelectedEntry = new MaintenanceEntryCandidate
            {
                CandidateKey = key,
                OpeningWidthMm = MaintenanceAnalysisOptions.DefaultDoorWidthMm,
                OpeningHeightMm = MaintenanceAnalysisOptions.DefaultDoorHeightMm
            }
        };
    }

    private static List<MaintenanceRenderItem> CompleteWallAlternativeItems()
    {
        MaintenanceComponentRole[] roles =
        {
            MaintenanceComponentRole.WallDoor,
            MaintenanceComponentRole.AFrameLadder,
            MaintenanceComponentRole.EntryTurnZone,
            MaintenanceComponentRole.AccessRoute,
            MaintenanceComponentRole.HumanEnvelope,
            MaintenanceComponentRole.ServicePocket,
            MaintenanceComponentRole.TargetEquipment,
            MaintenanceComponentRole.VirtualBoundaryWall
        };
        var output = new List<MaintenanceRenderItem>();
        foreach (MaintenanceComponentRole role in roles)
        {
            var item = new MaintenanceRenderItem
            {
                RenderKey = role.ToString(),
                Role = role,
                GeometryType = MaintenanceRenderGeometryType.Box,
                Center = new MaintenancePoint3(100, 200, 300),
                Direction = new MaintenancePoint2(1, 0),
                WidthMm = 100,
                DepthMm = 100,
                HeightMm = 100
            };
            if (role == MaintenanceComponentRole.AFrameLadder ||
                role == MaintenanceComponentRole.AccessRoute ||
                role == MaintenanceComponentRole.HumanEnvelope)
            {
                item.GeometryType = MaintenanceRenderGeometryType.Polyline;
                item.Points.Add(new MaintenancePoint3(0, 0, 0));
                item.Points.Add(new MaintenancePoint3(100, 0, 100));
            }
            output.Add(item);
        }
        return output;
    }

    private static void AssertPathSafe(MaintenanceGrid grid, IList<GridCell> path)
    {
        for (int i = 1; i < path.Count; i++)
            Assert(MaintenancePathfinder.HasLineOfSight(grid, path[i - 1], path[i]),
                "Simplified segment crossed a blocked cell.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertAlmost(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException(message + " expected=" + expected + " actual=" + actual);
    }
}

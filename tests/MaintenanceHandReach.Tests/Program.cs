using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JarviTools.Commands.MaintenanceReachability;
using Newtonsoft.Json.Linq;

// HandReach 纯逻辑测试：不依赖 Revit。
// 数值用例取自 2026-08-18 真实模型原型结果（5B-handreach-results-20260818.json），
// 用于锁定"口边缘距离、水平/斜向距离、分级、区域合并"算法与原型一致。

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        Run("device01 nearest edge reproduces 8/18 baseline", Device01NearestEdge);
        Run("device02 nearest edge reproduces 8/18 baseline", Device02NearestEdge);
        Run("device03 nearest edge reproduces 8/18 baseline", Device03NearestEdge);
        Run("target inside hatch uses nearest of four edges", TargetInsideHatch);
        Run("oblique distance reproduces device01 250.0mm", Device01Oblique);
        Run("oblique distance reproduces device03 350.1mm", Device03Oblique);
        Run("distance grades cut at 300/400/500", DistanceGrades);
        Run("side-wall 500~600 review band requires explicit opt-in", SideWallDistanceReviewEligibility);
        Run("vertical grades cut at 300/500", VerticalGrades);
        Run("vertical candidate chain hard-rejects only over 500", VerticalCandidateEligibility);
        Run("ceiling personnel entry keeps the device at model height", CeilingPersonnelEntryKeepsModelHeight);
        Run("ceiling personnel entry uses nearest point on 450 envelope top", CeilingPersonnelEntryNearestTopPoint);
        Run("near-ceiling service face selects direct 450 hand reach", CeilingDirectReachBand);
        Run("ceiling overlap forces direct hand reach to orange review", CeilingDirectReachOverlapPolicy);
        Run("formal approval rejects incomplete discovery coverage", FormalApprovalCoveragePolicy);
        Run("unselected unknown candidates do not invalidate the selected scheme", UnselectedCandidateAuditIsolationPolicy);
        Run("new host and link identities use persistent UniqueIds", PersistentStableIdentity);
        Run("explicit positive link scope is identity-strict and auditable", ExplicitPositiveLinkScope);
        Run("pipe exemption approval signature tracks live system evidence", PipeExemptionFreshnessSignature);
        Run("four-neighbor merge keeps corner-only cells separate", MergeFourNeighborCorner);
        Run("eight-neighbor merge joins diagonals", MergeEightNeighborDiagonal);
        Run("merge counts and orders by size descending", MergeCountsAndOrder);
        Run("cell center math matches 40mm grid", CellCenterMath);
        Run("pack and unpack round-trip", PackRoundTrip);
        Run("formal contract keeps ceiling 450 and allows explicit side-wall 400", FixedContractValidation);
        Run("operation zone length axis follows ladder", OperationZoneAxes);
        Run("connectivity disagreement blocks formal agreement", ConnectivityAgreement);
        Run("verified ceiling candidate keeps connectivity disagreement as orange review", ConnectivityDisagreementReviewPolicy);
        Run("A-frame foot offsets cover all four concrete feet", AFrameFootOffsets);
        Run("450 hatch rejects concave ceiling notch", HatchRejectsConcaveNotch);
        Run("450 hatch rejects internal ceiling hole", HatchRejectsInternalHole);
        Run("450 hatch rejects thin slit crossing", HatchRejectsThinSlit);
        Run("450 hatch accepts normal single-face rectangle", HatchAcceptsNormalRectangle);
        Run("ceiling boundary generates four virtual side-wall segments without Revit walls", VirtualBoundarySingleRectangle);
        Run("adjacent ceilings do not generate a virtual wall on their shared edge", VirtualBoundarySkipsSharedEdge);
        Run("partial shared ceiling edge removes only the internal sub-segment", VirtualBoundarySplitsPartialSharedEdge);
        Run("duplicate ceiling footprints do not duplicate virtual side walls", VirtualBoundaryDeduplicatesCoincidentFaces);
        Run("side-wall local UV uses the true nearest opening edge", SideWallNearestEdgeLocalUv);
        Run("620 effective band fits 450 but rejects 800", EffectiveBand620Fits450Rejects800);
        Run("side-wall local UV rejects a wall-face notch", SideWallLocalUvRejectsWallNotch);
        Run("400/450 opening contract distinguishes ceiling entry from side-wall lean-in", OpeningContractExcludesHumanDoorAndTurnZone);
        Run("auto preference selects feasible side-wall before ceiling", OpeningPreferencePrioritizesSideWall);
        Run("hard-infeasible side-wall cannot outrank feasible ceiling", OpeningPreferenceSkipsHardInfeasibleSideWall);
        Run("opening-only preferences filter the other plane", OpeningOnlyPreferencesFilterOtherPlane);
        Run("same-plane opening order is distance then stable key", OpeningCandidateOrderIsStable);
        Run("ledger includes rejected targets and representative-only candidates", LedgerRejectedAndRepresentativeContract);
        Run("ledger manifest hashes match exact files", LedgerManifestHashesMatchFiles);
        Run("ledger manifest exposes incomplete collection coverage", LedgerManifestExposesCoverageFailure);
        Run("ledger repeated export preserves manual conclusion and note", LedgerPreservesManualFields);
        Run("ledger resets stale conclusion but preserves note", LedgerResetsStaleManualConclusion);
        Run("ledger blocks overwrite when manual target becomes orphan", LedgerBlocksManualOrphanLoss);
        Run("ledger unconfigured destination writes nothing", LedgerUnconfiguredWritesNothing);
        Run("legacy ledger is byte-archived before a clean v2 snapshot", LedgerArchivesLegacyBeforeCleanV2);
        Run("legacy manual note migrates but unproven conclusion stays archived", LedgerMigratesExplicitLegacyManualFields);
        Run("ambiguous legacy manual rows remain archive-only", LedgerArchivesAmbiguousLegacyManualRows);
        Run("legacy archive verification failure blocks replacement", LedgerArchiveFailureBlocksReplacement);

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

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertAlmost(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException(
                message + " expected=" + expected + " actual=" + actual);
    }

    private static void AssertThrows<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void Device01NearestEdge()
    {
        // 8/18: 口中心(-15400,-4070) 代理点(-15226.3,-3865.4) → 边缘(-15226.3,-3870) 水平4.6
        double edgeX, edgeY, horizontal;
        MaintenanceHandReachMath.NearestEdge(-15400.0, -4070.0, -15226.3, -3865.4, 200.0,
            out edgeX, out edgeY, out horizontal);
        AssertAlmost(-15226.3, edgeX, 0.05, "edgeX mismatch");
        AssertAlmost(-3870.0, edgeY, 0.05, "edgeY mismatch");
        AssertAlmost(4.6, horizontal, 0.05, "horizontal mismatch");
    }

    private static void Device02NearestEdge()
    {
        // 8/18: 口中心(-10520,-5580) 代理点(-10354,-5377.7) → 边缘(-10354,-5380) 水平2.3
        double edgeX, edgeY, horizontal;
        MaintenanceHandReachMath.NearestEdge(-10520.0, -5580.0, -10354.0, -5377.7, 200.0,
            out edgeX, out edgeY, out horizontal);
        AssertAlmost(-10354.0, edgeX, 0.05, "edgeX mismatch");
        AssertAlmost(-5380.0, edgeY, 0.05, "edgeY mismatch");
        AssertAlmost(2.3, horizontal, 0.05, "horizontal mismatch");
    }

    private static void Device03NearestEdge()
    {
        // 8/18: 口中心(-7940,-6140) 代理点(-7773.2,-5933.7) → 边缘(-7773.2,-5940) 水平6.3
        double edgeX, edgeY, horizontal;
        MaintenanceHandReachMath.NearestEdge(-7940.0, -6140.0, -7773.2, -5933.7, 200.0,
            out edgeX, out edgeY, out horizontal);
        AssertAlmost(-7773.2, edgeX, 0.05, "edgeX mismatch");
        AssertAlmost(-5940.0, edgeY, 0.05, "edgeY mismatch");
        AssertAlmost(6.3, horizontal, 0.05, "horizontal mismatch");
    }

    private static void TargetInsideHatch()
    {
        // 目标投影在口内：取四条边最近一条。中心(0,0) 目标(50,30)：qx=150 qy=170 → X侧边
        double edgeX, edgeY, horizontal;
        MaintenanceHandReachMath.NearestEdge(0.0, 0.0, 50.0, 30.0, 200.0,
            out edgeX, out edgeY, out horizontal);
        AssertAlmost(200.0, edgeX, 1e-9, "edgeX must be +X edge");
        AssertAlmost(30.0, edgeY, 1e-9, "edgeY must stay");
        AssertAlmost(150.0, horizontal, 1e-9, "horizontal must be 200-50");
    }

    private static void Device01Oblique()
    {
        // 边缘(-15226.3,-3870,22400) → 代理(-15226.3,-3865.4,22650)：斜向≈250.0
        double oblique = MaintenanceHandReachMath.ObliqueDistance(
            -15226.3, -3870.0, 22400.0, -15226.3, -3865.4, 22650.0);
        AssertAlmost(250.0, oblique, 0.05, "oblique mismatch");
    }

    private static void Device03Oblique()
    {
        // 边缘(-7773.2,-5940,22400) → 代理(-7773.2,-5933.7,22750)：斜向≈350.1
        double oblique = MaintenanceHandReachMath.ObliqueDistance(
            -7773.2, -5940.0, 22400.0, -7773.2, -5933.7, 22750.0);
        AssertAlmost(350.1, oblique, 0.05, "oblique mismatch");
    }

    private static void DistanceGrades()
    {
        Assert(MaintenanceHandReachMath.GradeDistance(300.0) == HandReachDistanceGrade.AWithin300, "300 must be A");
        Assert(MaintenanceHandReachMath.GradeDistance(300.1) == HandReachDistanceGrade.BWithin400, "300.1 must be B");
        Assert(MaintenanceHandReachMath.GradeDistance(400.0) == HandReachDistanceGrade.BWithin400, "400 must be B");
        Assert(MaintenanceHandReachMath.GradeDistance(400.1) == HandReachDistanceGrade.CWithin500, "400.1 must be C");
        Assert(MaintenanceHandReachMath.GradeDistance(500.0) == HandReachDistanceGrade.CWithin500, "500 must be C");
        Assert(MaintenanceHandReachMath.GradeDistance(500.1) == HandReachDistanceGrade.RejectedOver500, "500.1 must reject");
    }

    private static void SideWallDistanceReviewEligibility()
    {
        var normal = new HandReachOptions();
        Assert(MaintenanceHandReachMath.IsSideWallDistanceCandidateEligible(500.0, normal),
            "500mm must remain formally eligible.");
        Assert(!MaintenanceHandReachMath.IsSideWallDistanceCandidateEligible(500.1, normal),
            "Over 500mm must remain rejected without explicit review opt-in.");

        var review = new HandReachOptions
        {
            OpeningPreference = HandReachOpeningPreference.SideWallOnly,
            AllowSideWallDistanceOver500Review = true
        };
        MaintenanceHandReachMath.ValidateFixedContract(review);
        Assert(MaintenanceHandReachMath.IsSideWallDistanceCandidateEligible(580.6, review),
            "8D device02 must enter the orange review chain.");
        Assert(MaintenanceHandReachMath.IsSideWallDistanceCandidateEligible(589.8, review),
            "8B device02 must enter the orange review chain.");
        Assert(MaintenanceHandReachMath.IsSideWallDistanceCandidateEligible(600.0, review),
            "600mm must be the closed orange-review boundary.");
        Assert(!MaintenanceHandReachMath.IsSideWallDistanceCandidateEligible(600.1, review),
            "Over 600mm must remain rejected.");
        Assert(!MaintenanceHandReachMath.IsSideWallDistanceCandidateEligible(761.1, review),
            "8A device01 must remain outside the orange-review band.");

        review.OpeningPreference = HandReachOpeningPreference.CeilingOnly;
        AssertThrows<ArgumentException>(
            () => MaintenanceHandReachMath.ValidateFixedContract(review),
            "Distance review must not leak into ceiling or auto preference runs.");
    }

    private static void VerticalGrades()
    {
        Assert(MaintenanceHandReachMath.GradeVertical(300.0) == HandReachVerticalGrade.RecommendedWithin300, "300 recommended");
        Assert(MaintenanceHandReachMath.GradeVertical(300.1) == HandReachVerticalGrade.AttentionWithin500, "300.1 attention");
        Assert(MaintenanceHandReachMath.GradeVertical(500.0) == HandReachVerticalGrade.AttentionWithin500, "500 attention");
        Assert(MaintenanceHandReachMath.GradeVertical(500.1) == HandReachVerticalGrade.RejectedOver500, "500.1 reject");
    }

    private static void VerticalCandidateEligibility()
    {
        Assert(MaintenanceHandReachMath.IsVerticalCandidateEligible(500.0),
            "500.0 must remain eligible for the downstream candidate chain");
        Assert(!MaintenanceHandReachMath.IsVerticalCandidateEligible(500.1),
            "500.1 must be eliminated before region merging");
    }

    private static void CeilingPersonnelEntryKeepsModelHeight()
    {
        var options = new HandReachOptions
        {
            OpeningPreference = HandReachOpeningPreference.CeilingOnly
        };
        MaintenanceHandReachMath.ValidateFixedContract(options);
        double top = MaintenanceHandReachMath.ResolveCeilingPersonnelEntryTopMm(
            35900.0, 36875.0, options);
        AssertAlmost(36750.0, top, 1e-9,
            "A 975mm model gap must keep the device at 36875 and raise only the personnel envelope to 36750.");
        AssertAlmost(125.0, 36875.0 - top, 1e-9,
            "The final operation reach must use the configured 125mm gap.");
    }

    private static void CeilingPersonnelEntryNearestTopPoint()
    {
        double x;
        double y;
        double horizontal;
        MaintenanceHandReachMath.NearestPointInSquare(
            -6571.3, -16910.1,
            -6571.3, -16685.1,
            225.0,
            out x, out y, out horizontal);
        AssertAlmost(-6571.3, x, 1e-9,
            "The service point must retain its X coordinate on the 450 envelope edge.");
        AssertAlmost(-16685.1, y, 1e-9,
            "The service point must land on the 450 envelope edge.");
        AssertAlmost(0.0, horizontal, 1e-9,
            "A service projection on the envelope edge needs no horizontal final reach.");
    }

    private static void CeilingDirectReachBand()
    {
        const double ceilingTop = 35900.0;
        const double openingHeight = 120.0;
        var options = new HandReachOptions();
        AssertAlmost(600.0, options.CeilingDirectOperatorZoneLengthMm, 1e-9,
            "Direct ceiling reach must use a local 600mm operator zone length.");
        AssertAlmost(600.0, options.CeilingDirectOperatorZoneWidthMm, 1e-9,
            "Direct ceiling reach must use a local 600mm operator zone width.");
        Assert(MaintenanceHandReachMath.IsCeilingDirectReachMode(
                ceilingTop, 35875.0, openingHeight),
            "The 8D device01 service face overlapping the ceiling by 25mm must use direct hand reach.");
        Assert(MaintenanceHandReachMath.IsCeilingDirectReachMode(
                ceilingTop, ceilingTop + openingHeight, openingHeight),
            "The upper edge of the near-ceiling band must remain direct reach.");
        Assert(!MaintenanceHandReachMath.IsCeilingDirectReachMode(
                ceilingTop, ceilingTop + openingHeight + 0.1, openingHeight),
            "A higher device must continue through the personnel-entry branch.");
        Assert(!MaintenanceHandReachMath.IsCeilingDirectReachMode(
                ceilingTop, ceilingTop - openingHeight - 0.1, openingHeight),
            "A service face below the room-side opening plane must not be accepted as direct reach.");
        double startZ = MaintenanceHandReachMath.ResolveCeilingDirectReachStartZMm(
            ceilingTop, openingHeight);
        AssertAlmost(35780.0, startZ, 1e-9,
            "Direct reach must start from the conservative room-side opening plane.");
        AssertAlmost(95.0, 35875.0 - startZ, 1e-9,
            "The 8D direct vertical hand reach must be 95mm without moving the device.");
    }

    private static void CeilingDirectReachOverlapPolicy()
    {
        Assert(MaintenanceHandReachMath.RequiresCeilingDirectReachOverlapReview(-25.0),
            "A 25mm model overlap must be orange review.");
        Assert(!MaintenanceHandReachMath.RequiresCeilingDirectReachOverlapReview(0.0),
            "Touching without overlap must not trigger the overlap review rule.");
        Assert(!MaintenanceHandReachMath.RequiresCeilingDirectReachOverlapReview(25.0),
            "A service face above the ceiling must not be described as overlap.");
    }

    private static void HatchRejectsConcaveNotch()
    {
        var outer = new List<MaintenancePoint2>
        {
            new MaintenancePoint2(-500, -500), new MaintenancePoint2(500, -500),
            new MaintenancePoint2(500, 500), new MaintenancePoint2(40, 500),
            new MaintenancePoint2(40, 50), new MaintenancePoint2(-40, 50),
            new MaintenancePoint2(-40, 500), new MaintenancePoint2(-500, 500)
        };
        Assert(!MaintenanceHandReachMath.RectangleFullyContainedInFaceLoops(
                0, 0, 225, new List<List<MaintenancePoint2>> { outer }),
            "A concave notch cutting the 450 square must reject even when corners are inside.");
    }

    private static void HatchRejectsInternalHole()
    {
        var loops = new List<List<MaintenancePoint2>>
        {
            RectangleLoop(-500, -500, 500, 500),
            RectangleLoop(50, 50, 70, 70)
        };
        Assert(!MaintenanceHandReachMath.RectangleFullyContainedInFaceLoops(0, 0, 225, loops),
            "A small internal hole between the old 3x3 probes must reject.");
    }

    private static void HatchRejectsThinSlit()
    {
        var loops = new List<List<MaintenancePoint2>>
        {
            RectangleLoop(-500, -500, 500, 500),
            RectangleLoop(45, -300, 55, 300)
        };
        Assert(!MaintenanceHandReachMath.RectangleFullyContainedInFaceLoops(0, 0, 225, loops),
            "A 10mm slit crossing the square must reject even when old probes miss it.");
    }

    private static void HatchAcceptsNormalRectangle()
    {
        Assert(MaintenanceHandReachMath.RectangleFullyContainedInFaceLoops(
                0, 0, 225,
                new List<List<MaintenancePoint2>>
                {
                    RectangleLoop(-500, -500, 500, 500)
                }),
            "A 450 square fully inside one simple face must pass.");
    }

    private static void VirtualBoundarySingleRectangle()
    {
        List<HandReachVirtualBoundarySegment> segments =
            MaintenanceHandReachMath.BuildVirtualBoundarySegments(
                new List<List<List<MaintenancePoint2>>>
                {
                    new List<List<MaintenancePoint2>>
                    {
                        RectangleLoop(0.0, 0.0, 1000.0, 800.0)
                    }
                },
                20.0);
        Assert(segments.Count == 4,
            "one rectangular ceiling must create four boundary-derived side walls");
        foreach (HandReachVirtualBoundarySegment segment in segments)
        {
            MaintenancePoint2 midpoint = (segment.Start + segment.End) * 0.5;
            MaintenancePoint2 toCenter = new MaintenancePoint2(500.0, 400.0) - midpoint;
            double inwardDot = segment.Inward.X * toCenter.X +
                               segment.Inward.Y * toCenter.Y;
            Assert(inwardDot > 0.0,
                "every generated virtual wall normal must point into the ceiling");
        }
    }

    private static void VirtualBoundarySkipsSharedEdge()
    {
        List<HandReachVirtualBoundarySegment> segments =
            MaintenanceHandReachMath.BuildVirtualBoundarySegments(
                new List<List<List<MaintenancePoint2>>>
                {
                    new List<List<MaintenancePoint2>>
                    {
                        RectangleLoop(0.0, 0.0, 1000.0, 1000.0)
                    },
                    new List<List<MaintenancePoint2>>
                    {
                        RectangleLoop(1000.0, 0.0, 2000.0, 1000.0)
                    }
                },
                20.0);
        Assert(segments.Count == 6,
            "two adjacent rectangles must retain six outer segments, not two shared walls");
        Assert(!segments.Any(x =>
                Math.Abs(x.Start.X - 1000.0) <= 1e-6 &&
                Math.Abs(x.End.X - 1000.0) <= 1e-6),
            "the internal shared edge must never become a virtual side wall");
    }

    private static void VirtualBoundaryDeduplicatesCoincidentFaces()
    {
        List<MaintenancePoint2> first = RectangleLoop(0.0, 0.0, 1000.0, 800.0);
        List<MaintenancePoint2> second = RectangleLoop(0.0, 0.0, 1000.0, 800.0);
        List<HandReachVirtualBoundarySegment> segments =
            MaintenanceHandReachMath.BuildVirtualBoundarySegments(
                new List<List<List<MaintenancePoint2>>>
                {
                    new List<List<MaintenancePoint2>> { first },
                    new List<List<MaintenancePoint2>> { second }
                },
                20.0);
        Assert(segments.Count == 4,
            "coincident top faces must be de-duplicated into four stable wall segments");
    }

    private static void VirtualBoundarySplitsPartialSharedEdge()
    {
        List<HandReachVirtualBoundarySegment> segments =
            MaintenanceHandReachMath.BuildVirtualBoundarySegments(
                new List<List<List<MaintenancePoint2>>>
                {
                    new List<List<MaintenancePoint2>>
                    {
                        RectangleLoop(0.0, 0.0, 2000.0, 1000.0)
                    },
                    new List<List<MaintenancePoint2>>
                    {
                        RectangleLoop(500.0, 1000.0, 1500.0, 2000.0)
                    }
                },
                20.0);
        Assert(segments.Count == 8,
            "a partial T-junction must retain both exposed ends of the longer edge");
        Assert(!segments.Any(x =>
                Math.Abs(x.Start.Y - 1000.0) <= 1e-6 &&
                Math.Abs(x.End.Y - 1000.0) <= 1e-6 &&
                Math.Min(x.Start.X, x.End.X) < 1500.0 - 1e-6 &&
                Math.Max(x.Start.X, x.End.X) > 500.0 + 1e-6),
            "the shared 500..1500 sub-segment must not remain as a virtual wall");
        Assert(segments.Any(x =>
                   Math.Abs(x.Start.Y - 1000.0) <= 1e-6 &&
                   Math.Abs(x.End.Y - 1000.0) <= 1e-6 &&
                   Math.Abs(Math.Min(x.Start.X, x.End.X) - 0.0) <= 1e-6 &&
                   Math.Abs(Math.Max(x.Start.X, x.End.X) - 500.0) <= 1e-6) &&
               segments.Any(x =>
                   Math.Abs(x.Start.Y - 1000.0) <= 1e-6 &&
                   Math.Abs(x.End.Y - 1000.0) <= 1e-6 &&
                   Math.Abs(Math.Min(x.Start.X, x.End.X) - 1500.0) <= 1e-6 &&
                   Math.Abs(Math.Max(x.Start.X, x.End.X) - 2000.0) <= 1e-6),
            "both exposed sub-segments of the longer ceiling edge must remain available");
    }

    private static List<MaintenancePoint2> RectangleLoop(
        double minX, double minY, double maxX, double maxY)
    {
        return new List<MaintenancePoint2>
        {
            new MaintenancePoint2(minX, minY), new MaintenancePoint2(maxX, minY),
            new MaintenancePoint2(maxX, maxY), new MaintenancePoint2(minX, maxY)
        };
    }

    private static void SideWallNearestEdgeLocalUv()
    {
        double edgeU, edgeV, distance;
        MaintenanceHandReachOpeningPolicy.NearestSideWallOpeningEdgeLocalUv(
            0.0, 0.0, 350.0, 50.0, out edgeU, out edgeV, out distance);

        AssertAlmost(225.0, edgeU, 1e-9, "outside target must clamp to +U opening edge");
        AssertAlmost(50.0, edgeV, 1e-9, "V coordinate must remain on the nearest edge");
        AssertAlmost(125.0, distance, 1e-9, "distance must be measured from the wall opening edge");

        MaintenanceHandReachOpeningPolicy.NearestSideWallOpeningEdgeLocalUv(
            0.0, 0.0, 50.0, 30.0, out edgeU, out edgeV, out distance);
        AssertAlmost(225.0, edgeU, 1e-9, "inside target must choose the nearest +U edge");
        AssertAlmost(30.0, edgeV, 1e-9, "inside target V must remain unchanged");
        AssertAlmost(175.0, distance, 1e-9, "inside target distance must reach the nearest edge");

        MaintenanceHandReachOpeningPolicy.NearestSideWallOpeningEdgeLocalUv(
            0.0, 0.0, 350.0, 50.0, 400.0,
            out edgeU, out edgeV, out distance);
        AssertAlmost(200.0, edgeU, 1e-9, "400 side-wall opening must use a 200 mm half-size");
        AssertAlmost(150.0, distance, 1e-9, "400 side-wall distance must be measured from its own edge");
    }

    private static void EffectiveBand620Fits450Rejects800()
    {
        Assert(MaintenanceHandReachOpeningPolicy.EffectiveBandFitsOpening(620.0, 450.0),
            "620 mm net band must contain a 450 mm opening");
        Assert(!MaintenanceHandReachOpeningPolicy.EffectiveBandFitsOpening(620.0, 800.0),
            "620 mm net band must reject an 800 mm opening");
        Assert(!MaintenanceHandReachOpeningPolicy.EffectiveBandFitsOpening(449.9, 450.0),
            "449.9 mm must not be rounded into a 450 mm fit");
        Assert(MaintenanceHandReachOpeningPolicy.EffectiveBandFitsOpening(450.0, 450.0),
            "the exact 450 mm boundary must pass");
        Assert(MaintenanceHandReachOpeningPolicy.EffectiveBandFitsOpening(450.1, 450.0),
            "450.1 mm must pass");
    }

    private static void SideWallLocalUvRejectsWallNotch()
    {
        var outer = new List<MaintenancePoint2>
        {
            new MaintenancePoint2(-500, -500), new MaintenancePoint2(500, -500),
            new MaintenancePoint2(500, 500), new MaintenancePoint2(100, 500),
            new MaintenancePoint2(100, 100), new MaintenancePoint2(-100, 100),
            new MaintenancePoint2(-100, 500), new MaintenancePoint2(-500, 500)
        };

        Assert(!MaintenanceHandReachMath.RectangleFullyContainedInFaceLoops(
                0.0, 0.0, 225.0, new List<List<MaintenancePoint2>> { outer }),
            "a 450 square crossing a wall-face notch must not be feasible");
    }

    private static void OpeningContractExcludesHumanDoorAndTurnZone()
    {
        foreach (OpeningPlaneKind planeKind in new[]
        {
            OpeningPlaneKind.SideWallVertical,
            OpeningPlaneKind.CeilingHorizontal
        })
        {
            HandReachOpeningContract contract = MaintenanceHandReachOpeningPolicy.GetContract(planeKind);
            AssertAlmost(450.0, contract.WidthMm, 1e-9, "opening width must be 450");
            AssertAlmost(450.0, contract.HeightMm, 1e-9, "opening height must be 450");
            AssertAlmost(200.0, contract.CorridorDiameterMm, 1e-9, "HandReach corridor must be 200");
            Assert(contract.RequiresHumanPassage ==
                (planeKind == OpeningPlaneKind.CeilingHorizontal),
                "only the ceiling 450 opening requires personnel entry");
            Assert(contract.AllowsPartialBodyEntry,
                "both 450 opening types must allow partial body entry");
            Assert(!contract.RequiresHumanDoor600By600, "HandReach must not require a 600x600 crawl-through door");
            Assert(!contract.RequiresTurnZone900, "HandReach must not require a 900 turn zone");
            Assert(contract.RequiresOperatorAccessToOpeningFace,
                "the operator must still be able to reach the opening face");
        }

        HandReachOpeningContract reduced = MaintenanceHandReachOpeningPolicy.GetContract(
            OpeningPlaneKind.SideWallVertical, 400.0);
        AssertAlmost(400.0, reduced.WidthMm, 1e-9,
            "explicit reduced side-wall opening width must be 400");
        Assert(!reduced.RequiresHumanPassage,
            "400 side-wall opening must remain hand-reach only");
        AssertThrows<ArgumentException>(
            () => MaintenanceHandReachOpeningPolicy.GetContract(
                OpeningPlaneKind.CeilingHorizontal, 400.0),
            "400 ceiling opening must remain forbidden");
    }

    private static void OpeningPreferencePrioritizesSideWall()
    {
        List<HandReachOpeningCandidateRank> ordered = MaintenanceHandReachOpeningPolicy.OrderFeasibleCandidates(
            new[]
            {
                Candidate("ceiling-near", OpeningPlaneKind.CeilingHorizontal, true, 20.0),
                Candidate("wall-far", OpeningPlaneKind.SideWallVertical, true, 300.0)
            },
            OpeningPreference.AutoPreferSideWall);

        Assert(ordered.Count == 2, "both feasible candidates must remain available");
        Assert(ordered[0].StableKey == "wall-far", "Auto must prefer a feasible side-wall opening");
        Assert(ordered[1].StableKey == "ceiling-near", "ceiling must remain the second choice");
    }

    private static void OpeningPreferenceSkipsHardInfeasibleSideWall()
    {
        List<HandReachOpeningCandidateRank> ordered = MaintenanceHandReachOpeningPolicy.OrderFeasibleCandidates(
            new[]
            {
                Candidate("wall-conflict", OpeningPlaneKind.SideWallVertical, false, 10.0),
                Candidate("ceiling-clear", OpeningPlaneKind.CeilingHorizontal, true, 200.0)
            },
            OpeningPreference.AutoPreferSideWall);

        Assert(ordered.Count == 1, "hard-infeasible candidates must be filtered before preference ranking");
        Assert(ordered[0].StableKey == "ceiling-clear",
            "an infeasible side-wall candidate must not outrank a feasible ceiling candidate");
    }

    private static void OpeningOnlyPreferencesFilterOtherPlane()
    {
        HandReachOpeningCandidateRank[] candidates =
        {
            Candidate("wall", OpeningPlaneKind.SideWallVertical, true, 100.0),
            Candidate("ceiling", OpeningPlaneKind.CeilingHorizontal, true, 100.0)
        };

        List<HandReachOpeningCandidateRank> sideWallOnly =
            MaintenanceHandReachOpeningPolicy.OrderFeasibleCandidates(
                candidates, OpeningPreference.SideWallOnly);
        Assert(sideWallOnly.Count == 1 && sideWallOnly[0].StableKey == "wall",
            "SideWallOnly must exclude ceiling candidates");

        List<HandReachOpeningCandidateRank> ceilingOnly =
            MaintenanceHandReachOpeningPolicy.OrderFeasibleCandidates(
                candidates, OpeningPreference.CeilingOnly);
        Assert(ceilingOnly.Count == 1 && ceilingOnly[0].StableKey == "ceiling",
            "CeilingOnly must exclude side-wall candidates");
    }

    private static void OpeningCandidateOrderIsStable()
    {
        List<HandReachOpeningCandidateRank> ordered = MaintenanceHandReachOpeningPolicy.OrderFeasibleCandidates(
            new[]
            {
                Candidate("wall-b", OpeningPlaneKind.SideWallVertical, true, 100.0),
                Candidate("wall-far", OpeningPlaneKind.SideWallVertical, true, 200.0),
                Candidate("wall-a", OpeningPlaneKind.SideWallVertical, true, 100.0)
            },
            OpeningPreference.SideWallOnly);

        Assert(string.Join(",", ordered.Select(x => x.StableKey)) == "wall-a,wall-b,wall-far",
            "same-plane candidates must sort by edge distance, then ordinal stable key");

        List<HandReachOpeningCandidateRank> reversed = MaintenanceHandReachOpeningPolicy.OrderFeasibleCandidates(
            ordered.AsEnumerable().Reverse(), OpeningPreference.SideWallOnly);
        Assert(string.Join(",", reversed.Select(x => x.StableKey)) == "wall-a,wall-b,wall-far",
            "candidate order must not depend on input enumeration order");
    }

    private static HandReachOpeningCandidateRank Candidate(
        string stableKey,
        OpeningPlaneKind planeKind,
        bool isHardFeasible,
        double edgeDistanceMm)
    {
        return new HandReachOpeningCandidateRank
        {
            StableKey = stableKey,
            PlaneKind = planeKind,
            IsHardFeasible = isHardFeasible,
            EdgeDistanceMm = edgeDistanceMm
        };
    }

    private static void FormalApprovalCoveragePolicy()
    {
        Assert(MaintenanceHandReachMath.IsFormalSnapshotApprovable(
                true, new[] { true, true }),
            "complete discovery and selected-candidate audits must remain approvable");
        Assert(!MaintenanceHandReachMath.IsFormalSnapshotApprovable(
                false, new[] { true, true }),
            "incomplete discovery coverage must block approval");
        Assert(!MaintenanceHandReachMath.IsFormalSnapshotApprovable(
                true, new[] { true, false }),
            "an incomplete selected candidate must block approval");
        Assert(MaintenanceHandReachMath.IsFormalSnapshotApprovable(
                true, new[] { true }),
            "unselected alternatives must not block a verified selected candidate");
        Assert(!MaintenanceHandReachMath.IsFormalSnapshotApprovable(
                true, new bool[0]),
            "an empty target set must not unlock formal visualization");
    }

    private static void UnselectedCandidateAuditIsolationPolicy()
    {
        var target = new HandReachTargetResult();
        target.MarkCandidateSetAuditIncomplete();
        Assert(!target.CandidateAuditComplete,
            "an unknown unselected candidate must downgrade the candidate-set audit");
        Assert(target.SelectedCandidateAuditComplete,
            "an unknown unselected candidate must not invalidate an independently verified selected scheme");

        target.SelectedCandidateAuditComplete = false;
        target.MarkCandidateSetAuditIncomplete();
        Assert(!target.SelectedCandidateAuditComplete,
            "candidate-set downgrade must never restore an already incomplete selected scheme");
    }

    private static void PersistentStableIdentity()
    {
        Assert(MaintenanceStableIdentity.HostElementKey("host-guid") ==
               "HUID:host-guid", "Host identity must use element UniqueId.");
        Assert(MaintenanceStableIdentity.LinkedElementKey("link-guid", "element-guid") ==
               "LUID:link-guid:element-guid",
            "Linked identity must combine link-instance and linked-element UniqueIds.");
        var persistent = new MaintenanceElementRef
        {
            LinkInstanceId = 17,
            LinkInstanceUniqueId = "link-guid",
            ElementId = 23,
            UniqueId = "element-guid"
        };
        Assert(persistent.GetStableKey() == "LUID:link-guid:element-guid",
            "MaintenanceElementRef must prefer persistent linked identity.");
        var legacy = new MaintenanceElementRef
        {
            LinkInstanceId = 17,
            ElementId = 23
        };
        Assert(legacy.GetStableKey() == "LINK:17:23",
            "Legacy numeric identity remains read-only fallback for old data.");
    }

    private static void PipeExemptionFreshnessSignature()
    {
        var evidence = new MaintenancePipeExemptionEvidence
        {
            GroupKey = "8A",
            TargetKey = "LUID:link:device",
            Element = new MaintenanceElementRef
            {
                LinkInstanceId = 7,
                LinkInstanceUniqueId = "link",
                ElementId = 8,
                UniqueId = "pipe"
            },
            CategoryKind = MaintenancePipeCategoryKind.PipeCurve.ToString(),
            SystemKind = "refrigerant",
            SystemTypeEvidence = "冷媒管",
            SystemEvidenceSource = "Connector.MEPSystem.Type",
            ReasonCode = "target_local_short_branch",
            Reason = "directed test",
            DistanceMm = 100.0,
            LengthMm = 800.0,
            DiameterMm = 25.0
        };
        string stored = MaintenancePipeExemptionPolicy.BuildStoredEvidenceSignature(evidence);
        evidence.SystemTypeEvidence = "冷凝水";
        string changedStored = MaintenancePipeExemptionPolicy.BuildStoredEvidenceSignature(evidence);
        Assert(stored != changedStored,
            "Stored result fingerprint must include the accepted system evidence.");

        string live = MaintenancePipeExemptionPolicy.BuildLiveSystemEvidenceSignature(
            evidence, true, "冷媒管", "Connector.MEPSystem.Type");
        string renamed = MaintenancePipeExemptionPolicy.BuildLiveSystemEvidenceSignature(
            evidence, true, "冷凝水", "Connector.MEPSystem.Type");
        string sourceChanged = MaintenancePipeExemptionPolicy.BuildLiveSystemEvidenceSignature(
            evidence, true, "冷媒管", "BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM");
        string unreliable = MaintenancePipeExemptionPolicy.BuildLiveSystemEvidenceSignature(
            evidence, false, string.Empty, string.Empty);
        Assert(live != renamed && live != sourceChanged && live != unreliable,
            "Approval freshness must change when the live MEP system type, evidence source, or reliability changes.");
    }

    private static void ExplicitPositiveLinkScope()
    {
        var links = new[]
        {
            LinkScopeEntry(101, "uid-8f-mep", "8F MEP", true),
            LinkScopeEntry(102, "uid-8f-structure", "8F Structure", true),
            LinkScopeEntry(201, "uid-5f", "5F", false),
            LinkScopeEntry(301, "uid-point-cloud", "Point Cloud", false)
        };
        MaintenanceLinkScopeSnapshot strict = MaintenanceLinkScopePolicy.Resolve(links, null);
        Assert(!strict.Explicit && strict.RelevantLinks.Count == 4 &&
               strict.OutOfScopeLinks.Count == 0 && strict.Includes(999, "unknown"),
            "Omitting relevantLinkInstanceIds must preserve strict all-link scope.");

        MaintenanceLinkScopeSnapshot explicitScope = MaintenanceLinkScopePolicy.Resolve(
            links,
            new long[] { 102, 101, 101 });
        Assert(explicitScope.Explicit && explicitScope.RelevantLinks.Count == 2 &&
               explicitScope.OutOfScopeLinks.Count == 2,
            "Explicit scope must retain only the two confirmed links and list every exclusion.");
        Assert(explicitScope.Includes(null, string.Empty), "Host must always be in scope.");
        Assert(explicitScope.Includes(101, "uid-8f-mep"),
            "Selected link UniqueId must be included.");
        Assert(!explicitScope.Includes(101, "uid-5f"),
            "A nonempty mismatched UniqueId must not pass through numeric ElementId reuse.");
        Assert(explicitScope.Includes(101, string.Empty),
            "Numeric identity is allowed only when the source lacks UniqueId.");
        Assert(!explicitScope.Includes(201, "uid-5f"),
            "Out-of-scope unloaded links must not become collection failures or candidates.");

        string signature = MaintenanceLinkScopePolicy.BuildSignature(explicitScope);
        links[0].InstanceName = "Renamed display only";
        links[0].TypeName = "Renamed type display only";
        MaintenanceLinkScopeSnapshot renamed = MaintenanceLinkScopePolicy.Resolve(
            links,
            new long[] { 101, 102 });
        Assert(signature == MaintenanceLinkScopePolicy.BuildSignature(renamed),
            "Scope fingerprint must use persistent identity, not mutable display names.");
        links[0].LoadedAtAnalysis = false;
        MaintenanceLinkScopeSnapshot unloaded = MaintenanceLinkScopePolicy.Resolve(
            links,
            new long[] { 101, 102 });
        Assert(signature != MaintenanceLinkScopePolicy.BuildSignature(unloaded),
            "Relevant link load-state change must alter the scope fingerprint.");

        AssertThrows<ArgumentException>(
            () => MaintenanceLinkScopePolicy.Resolve(links, new long[] { 999 }),
            "Unknown positive-scope ids must fail closed.");
    }

    private static MaintenanceLinkScopeEntry LinkScopeEntry(
        long id, string uniqueId, string name, bool loaded)
    {
        return new MaintenanceLinkScopeEntry
        {
            LinkInstanceId = id,
            LinkInstanceUniqueId = uniqueId,
            InstanceName = name,
            TypeName = name + " type",
            LoadedAtAnalysis = loaded
        };
    }

    private static void MergeFourNeighborCorner()
    {
        var keys = new HashSet<long>
        {
            MaintenanceHandReachMath.Pack(0, 0),
            MaintenanceHandReachMath.Pack(1, 1)
        };
        List<List<long>> components = MaintenanceHandReachMath.MergeRegions(keys, 4, 4, false);
        Assert(components.Count == 2, "Corner-only contact must stay two regions in 4-neighbor.");
    }

    private static void MergeEightNeighborDiagonal()
    {
        var keys = new HashSet<long>
        {
            MaintenanceHandReachMath.Pack(0, 0),
            MaintenanceHandReachMath.Pack(1, 1)
        };
        List<List<long>> components = MaintenanceHandReachMath.MergeRegions(keys, 4, 4, true);
        Assert(components.Count == 1, "Diagonal contact must join in 8-neighbor.");
        Assert(components[0].Count == 2, "Joined component must hold both cells.");
    }

    private static void MergeCountsAndOrder()
    {
        var keys = new HashSet<long>();
        // 4 格连续块
        for (int x = 0; x < 4; x++) keys.Add(MaintenanceHandReachMath.Pack(x, 0));
        // 2 格分离块
        keys.Add(MaintenanceHandReachMath.Pack(10, 10));
        keys.Add(MaintenanceHandReachMath.Pack(10, 11));
        List<List<long>> components = MaintenanceHandReachMath.MergeRegions(keys, 20, 20, false);
        Assert(components.Count == 2, "Two separated blocks expected.");
        Assert(components[0].Count == 4 && components[1].Count == 2, "Order must be size descending.");
    }

    private static void CellCenterMath()
    {
        double x, y;
        MaintenanceHandReachMath.CellCenter(20, 20, -11160.0, -6180.0, 40.0, out x, out y);
        AssertAlmost(-10360.0, x, 1e-9, "x center mismatch");
        AssertAlmost(-5380.0, y, 1e-9, "y center mismatch");
    }

    private static void PackRoundTrip()
    {
        for (int ix = 0; ix < 41; ix++)
        {
            for (int iy = 0; iy < 41; iy++)
            {
                long key = MaintenanceHandReachMath.Pack(ix, iy);
                Assert(MaintenanceHandReachMath.UnpackIx(key) == ix, "ix round-trip failed");
                Assert(MaintenanceHandReachMath.UnpackIy(key) == iy, "iy round-trip failed");
            }
        }
    }

    private static void FixedContractValidation()
    {
        var defaults = new HandReachOptions();
        MaintenanceHandReachMath.ValidateFixedContract(defaults);
        Assert(defaults.OpeningPreference == HandReachOpeningPreference.SideWallOnly,
            "Default workflow must stop after side-wall HandReach so the person-door stage is not skipped.");

        var reducedSideWall = new HandReachOptions
        {
            HatchSizeMm = 400.0,
            OpeningPreference = HandReachOpeningPreference.SideWallOnly
        };
        MaintenanceHandReachMath.ValidateFixedContract(reducedSideWall);

        var wrongCeiling400 = new HandReachOptions
        {
            HatchSizeMm = 400.0,
            OpeningPreference = HandReachOpeningPreference.CeilingOnly
        };
        AssertThrows<ArgumentException>(
            () => MaintenanceHandReachMath.ValidateFixedContract(wrongCeiling400),
            "400 ceiling hatch must be rejected.");

        var unsupportedHatch = new HandReachOptions { HatchSizeMm = 350.0 };
        AssertThrows<ArgumentException>(
            () => MaintenanceHandReachMath.ValidateFixedContract(unsupportedHatch),
            "Unsupported side-wall hatch size must be rejected.");

        var wrongCorridor = new HandReachOptions { DefaultCorridorDiameterMm = 250.0 };
        AssertThrows<ArgumentException>(
            () => MaintenanceHandReachMath.ValidateFixedContract(wrongCorridor),
            "Non-200 default corridor must be rejected.");

        var missingDefaultTest = new HandReachOptions
        {
            CorridorTestDiametersMm = new[] { 250.0, 300.0, 350.0, 400.0 }
        };
        AssertThrows<ArgumentException>(
            () => MaintenanceHandReachMath.ValidateFixedContract(missingDefaultTest),
            "Corridor test grades must include 200mm.");
    }

    private static void OperationZoneAxes()
    {
        double lx, ly, wx, wy;
        MaintenanceHandReachMath.OperationZoneAxes(1.0, 0.0, out lx, out ly, out wx, out wy);
        AssertAlmost(1.0, lx, 1e-9, "X ladder length axis X");
        AssertAlmost(0.0, ly, 1e-9, "X ladder length axis Y");
        AssertAlmost(0.0, wx, 1e-9, "X ladder width axis X");
        AssertAlmost(1.0, wy, 1e-9, "X ladder width axis Y");

        MaintenanceHandReachMath.OperationZoneAxes(0.0, 2.0, out lx, out ly, out wx, out wy);
        AssertAlmost(0.0, lx, 1e-9, "Y ladder length axis X");
        AssertAlmost(1.0, ly, 1e-9, "Y ladder length axis Y");
        AssertAlmost(-1.0, wx, 1e-9, "Y ladder width axis X");
        AssertAlmost(0.0, wy, 1e-9, "Y ladder width axis Y");
    }

    private static void ConnectivityAgreement()
    {
        Assert(MaintenanceHandReachMath.ConnectivityAgrees(2, 2),
            "Equal 4/8 region counts must agree.");
        Assert(!MaintenanceHandReachMath.ConnectivityAgrees(2, 1),
            "4/8 region count disagreement must not be formal agreement.");
    }

    private static void ConnectivityDisagreementReviewPolicy()
    {
        Assert(MaintenanceHandReachMath.CanReviewConnectivityDisagreement(
                   false, true, true, true),
            "A fully verified ceiling personnel-entry candidate was not retained for orange review.");
        Assert(!MaintenanceHandReachMath.CanReviewConnectivityDisagreement(
                   false, true, true, false),
            "An incomplete selected candidate bypassed the connectivity fail-closed rule.");
        Assert(!MaintenanceHandReachMath.CanReviewConnectivityDisagreement(
                   false, false, true, true),
            "A side-wall candidate incorrectly received the ceiling-only connectivity exception.");
        Assert(!MaintenanceHandReachMath.CanReviewConnectivityDisagreement(
                   true, true, true, true),
            "An agreed connectivity result was incorrectly classified as a disagreement review case.");
    }

    private static void AFrameFootOffsets()
    {
        double[,] xFeet = MaintenanceHandReachMath.AFrameFootOffsets(3000.0, 1.0, 0.0);
        Assert(xFeet.GetLength(0) == 4 && xFeet.GetLength(1) == 2,
            "Exactly four XY foot offsets required.");
        AssertAlmost(660.0, xFeet[0, 0], 1e-9, "front-left X spread");
        AssertAlmost(-300.0, xFeet[0, 1], 1e-9, "front-left X width");
        AssertAlmost(-660.0, xFeet[3, 0], 1e-9, "rear-right X spread");
        AssertAlmost(300.0, xFeet[3, 1], 1e-9, "rear-right X width");

        double[,] yFeet = MaintenanceHandReachMath.AFrameFootOffsets(3000.0, 0.0, 1.0);
        AssertAlmost(300.0, yFeet[0, 0], 1e-9, "front-left Y width");
        AssertAlmost(660.0, yFeet[0, 1], 1e-9, "front-left Y spread");
        AssertAlmost(-300.0, yFeet[3, 0], 1e-9, "rear-right Y width");
        AssertAlmost(-660.0, yFeet[3, 1], 1e-9, "rear-right Y spread");
    }

    private static void LedgerRejectedAndRepresentativeContract()
    {
        string directory = CreateLedgerTestDirectory();
        try
        {
            MaintenanceHandReachLedgerExportResult export = MaintenanceHandReachLedgerService.Export(
                CreateLedgerResult(true),
                new MaintenanceLedgerDestination
                {
                    OutputDirectory = directory,
                    FilePrefix = "directed"
                });
            List<Dictionary<string, string>> summary = MaintenanceLedgerCsv.Parse(
                MaintenanceLedgerCsv.ReadAllTextShared(export.SummaryCsvPath));
            Assert(summary.Count == 2, "Every analyzed target, including rejected, needs a summary row.");
            Dictionary<string, string> rejected = summary.Single(x => x["目标键"] == "HOST:101");
            Assert(rejected["Revit维修结论"] == "rejected_no_feasible_hand_reach",
                "Rejected conclusion must survive in summary.");
            Assert(rejected["候选区域数"] == "0" && rejected["可行点数"] == "0",
                "Rejected target must be explicit, not synthesized as a candidate.");

            List<Dictionary<string, string>> candidates = MaintenanceLedgerCsv.Parse(
                MaintenanceLedgerCsv.ReadAllTextShared(export.CandidateCsvPath));
            Assert(candidates.Count == 1 && candidates[0]["区域编号"] == "1",
                "Candidate CSV must contain one representative per feasible merged region only.");

            JObject manifest = JObject.Parse(File.ReadAllText(export.ManifestJsonPath));
            JToken contract = manifest["candidateContract"];
            Assert(contract != null && !(bool)contract["allPathsEnumerated"],
                "Manifest must state allPathsEnumerated=false.");
            Assert(((string)contract["scope"]).Contains("representative"),
                "Manifest scope must say candidates are representatives.");
        }
        finally
        {
            DeleteLedgerTestDirectory(directory);
        }
    }

    private static void LedgerManifestHashesMatchFiles()
    {
        string directory = CreateLedgerTestDirectory();
        try
        {
            MaintenanceHandReachLedgerExportResult export = MaintenanceHandReachLedgerService.Export(
                CreateLedgerResult(true),
                new MaintenanceLedgerDestination
                {
                    OutputDirectory = directory,
                    FilePrefix = "hash"
                });
            JObject manifest = JObject.Parse(File.ReadAllText(export.ManifestJsonPath));
            foreach (JObject file in (JArray)manifest["files"])
            {
                string path = Path.Combine(directory, (string)file["name"]);
                string actual = MaintenanceLedgerCsv.Sha256Hex(File.ReadAllBytes(path));
                Assert(string.Equals(actual, (string)file["sha256"], StringComparison.Ordinal),
                    "Manifest SHA-256 must cover exact on-disk bytes for " + path);
            }
        }
        finally
        {
            DeleteLedgerTestDirectory(directory);
        }
    }

    private static void LedgerPreservesManualFields()
    {
        string directory = CreateLedgerTestDirectory();
        try
        {
            var destination = new MaintenanceLedgerDestination
            {
                OutputDirectory = directory,
                FilePrefix = "manual"
            };
            MaintenanceHandReachLedgerExportResult first =
                MaintenanceHandReachLedgerService.Export(CreateLedgerResult(true), destination);
            WriteManualValues(
                first.SummaryCsvPath,
                "HOST:101",
                "人工结论：保留",
                "专业备注：现场复核后填写");

            MaintenanceHandReachLedgerExportResult second =
                MaintenanceHandReachLedgerService.Export(CreateLedgerResult(true), destination);
            List<Dictionary<string, string>> rows = MaintenanceLedgerCsv.Parse(
                MaintenanceLedgerCsv.ReadAllTextShared(second.SummaryCsvPath));
            Dictionary<string, string> row = rows.Single(x => x["目标键"] == "HOST:101");
            Assert(row["台账人工确认"] == "人工结论：保留",
                "Repeated export must preserve manual conclusion.");
            Assert(row["台账人工备注"] == "专业备注：现场复核后填写",
                "Repeated export must preserve professional/manual note.");
            Assert(second.PreservedManualRowCount == 1,
                "Preserved manual row count must be explicit.");
        }
        finally
        {
            DeleteLedgerTestDirectory(directory);
        }
    }

    private static void LedgerBlocksManualOrphanLoss()
    {
        string directory = CreateLedgerTestDirectory();
        try
        {
            var destination = new MaintenanceLedgerDestination
            {
                OutputDirectory = directory,
                FilePrefix = "orphan"
            };
            MaintenanceHandReachLedgerExportResult first =
                MaintenanceHandReachLedgerService.Export(CreateLedgerResult(true), destination);
            WriteManualValues(first.SummaryCsvPath, "HOST:101", "人工保留", "目标暂时消失也不能丢");
            byte[] summaryBefore = File.ReadAllBytes(first.SummaryCsvPath);
            byte[] candidatesBefore = File.ReadAllBytes(first.CandidateCsvPath);
            byte[] manifestBefore = File.ReadAllBytes(first.ManifestJsonPath);

            bool blocked = false;
            try
            {
                MaintenanceHandReachLedgerService.Export(CreateLedgerResult(false), destination);
            }
            catch (InvalidDataException exception)
            {
                blocked = exception.Message.Contains("孤儿") && exception.Message.Contains("旧文件保持不变");
            }
            Assert(blocked, "Manual orphan must block snapshot replacement with an explicit reason.");
            Assert(summaryBefore.SequenceEqual(File.ReadAllBytes(first.SummaryCsvPath)),
                "Blocked orphan export must preserve summary bytes.");
            Assert(candidatesBefore.SequenceEqual(File.ReadAllBytes(first.CandidateCsvPath)),
                "Blocked orphan export must preserve candidate bytes.");
            Assert(manifestBefore.SequenceEqual(File.ReadAllBytes(first.ManifestJsonPath)),
                "Blocked orphan export must preserve manifest bytes.");
        }
        finally
        {
            DeleteLedgerTestDirectory(directory);
        }
    }

    private static void LedgerResetsStaleManualConclusion()
    {
        string directory = CreateLedgerTestDirectory();
        try
        {
            var destination = new MaintenanceLedgerDestination
            {
                OutputDirectory = directory,
                FilePrefix = "manual-stale"
            };
            MaintenanceHandReachLedgerExportResult first =
                MaintenanceHandReachLedgerService.Export(CreateLedgerResult(true), destination);
            WriteManualValues(first.SummaryCsvPath, "HOST:101",
                "人工结论：旧证据", "专业备注：必须保留");

            HandReachAnalysisResult changed = CreateLedgerResult(true);
            changed.ResultFingerprint = "changed-result-fingerprint";
            MaintenanceHandReachLedgerExportResult second =
                MaintenanceHandReachLedgerService.Export(changed, destination);
            Dictionary<string, string> row = MaintenanceLedgerCsv.Parse(
                    MaintenanceLedgerCsv.ReadAllTextShared(second.SummaryCsvPath))
                .Single(x => x["目标键"] == "HOST:101");
            Assert(row["台账人工确认"] == string.Empty &&
                   row["台账人工备注"] == "专业备注：必须保留",
                "Changed result fingerprint must reset the old conclusion but preserve its note.");
            Assert(second.ResetStaleManualConclusionCount == 1 &&
                   !string.IsNullOrWhiteSpace(second.ManualConclusionWarning),
                "Stale conclusion reset must be explicit in the export status.");
            JObject manifest = JObject.Parse(File.ReadAllText(second.ManifestJsonPath));
            Assert((int)manifest["manualDataPolicy"]["resetStaleManualConclusionCount"] == 1,
                "Manifest must disclose stale manual conclusion resets.");
        }
        finally
        {
            DeleteLedgerTestDirectory(directory);
        }
    }

    private static void LedgerUnconfiguredWritesNothing()
    {
        string directory = CreateLedgerTestDirectory();
        try
        {
            var destination = new MaintenanceLedgerDestination
            {
                OutputDirectory = string.Empty,
                FilePrefix = "must-not-write"
            };
            MaintenanceLedgerDestination normalized;
            string errorCode;
            string errorMessage;
            Assert(!destination.TryNormalize(
                    "maintenance-ledger",
                    out normalized,
                    out errorCode,
                    out errorMessage) &&
                   errorCode == "destination_not_configured" && normalized == null,
                "Unconfigured destination must resolve to an explicit no-write state.");
            bool rejected = false;
            try
            {
                MaintenanceHandReachLedgerService.Export(CreateLedgerResult(true), destination);
            }
            catch (InvalidOperationException exception)
            {
                rejected = exception.Message.Contains("未配置") && exception.Message.Contains("未写入任何文件");
            }
            Assert(rejected, "Exporter must report unconfigured destination before writing.");
            Assert(Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length == 0,
                "Unconfigured export must create no fallback files.");

            string missing = Path.Combine(directory, "missing");
            destination.OutputDirectory = missing;
            Assert(!destination.TryNormalize(
                    "maintenance-ledger",
                    out normalized,
                    out errorCode,
                    out errorMessage) &&
                   errorCode == "directory_missing" && !Directory.Exists(missing),
                "Missing configured directory must not be auto-created.");
        }
        finally
        {
            DeleteLedgerTestDirectory(directory);
        }
    }

    private static void LedgerManifestExposesCoverageFailure()
    {
        string directory = CreateLedgerTestDirectory();
        try
        {
            HandReachAnalysisResult result = CreateLedgerResult(true);
            result.LinkScope.Explicit = true;
            result.LinkScope.RelevantLinks.Add(new MaintenanceLinkScopeEntry
            {
                LinkInstanceId = 42,
                LinkInstanceUniqueId = "link-uid-42",
                InstanceName = "Relevant MEP",
                TypeName = "MEP Link",
                LoadedAtAnalysis = true
            });
            result.LinkScope.OutOfScopeLinks.Add(new MaintenanceLinkScopeEntry
            {
                LinkInstanceId = 43,
                LinkInstanceUniqueId = "link-uid-43",
                InstanceName = "Excluded Architecture",
                TypeName = "Architecture Link",
                LoadedAtAnalysis = true
            });
            result.CoverageComplete = false;
            result.CoverageFailures.Add(new HandReachCoverageFailure
            {
                Stage = "device_geometry",
                SourceKey = "L42:900",
                LinkInstanceId = 42,
                ElementId = 900,
                Category = "Mechanical Equipment",
                Mark = "AHU-X",
                Reason = "no verifiable solid"
            });
            MaintenanceHandReachLedgerExportResult exported =
                MaintenanceHandReachLedgerService.Export(
                    result,
                    new MaintenanceLedgerDestination
                    {
                        OutputDirectory = directory,
                        FilePrefix = "coverage"
                    });
            JObject manifest = JObject.Parse(File.ReadAllText(exported.ManifestJsonPath));
            Assert((bool)manifest["analysis"]["coverageComplete"] == false,
                "Manifest must explicitly mark incomplete evidence collection.");
            JToken linkScope = manifest["analysis"]["linkScope"];
            Assert((bool)linkScope["explicitScope"] &&
                   (int)linkScope["count"] == 2 &&
                   (int)linkScope["relevantLinkCount"] == 1 &&
                   (int)linkScope["outOfScopeLinkCount"] == 1 &&
                   (string)linkScope["relevantLinks"][0]["key"] ==
                       "LUID:link-uid-42:*" &&
                   (string)linkScope["outOfScopeLinks"][0]["key"] ==
                       "LUID:link-uid-43:*",
                "Manifest must expose the explicit relevant/out-of-scope link contract.");
            JToken failure = manifest["analysis"]["collectionFailures"].Single();
            Assert((string)failure["sourceKey"] == "L42:900" &&
                   (string)failure["reason"] == "no verifiable solid",
                "Manifest must retain failed device identity and reason.");
        }
        finally
        {
            DeleteLedgerTestDirectory(directory);
        }
    }

    private static void LedgerArchivesLegacyBeforeCleanV2()
    {
        string directory = CreateLedgerTestDirectory();
        try
        {
            const string prefix = "legacy-clean";
            string archiveRoot = Path.Combine(directory, "archive-root");
            string summaryPath = WriteLegacySummary(
                directory, prefix, false, string.Empty, string.Empty);
            byte[] legacyBytes = File.ReadAllBytes(summaryPath);

            string userBridge = Path.Combine(directory, prefix + ".user.csv");
            string codexBridge = Path.Combine(directory, prefix + ".codex.csv");
            string bridgeManifest = Path.Combine(directory, prefix + ".manifest.json");
            File.WriteAllBytes(userBridge, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(codexBridge, new byte[] { 4, 5, 6 });
            File.WriteAllBytes(bridgeManifest, new byte[] { 7, 8, 9 });

            MaintenanceHandReachLedgerExportResult exported =
                MaintenanceHandReachLedgerService.Export(
                    CreateLedgerResult(false),
                    new MaintenanceLedgerDestination
                    {
                        OutputDirectory = directory,
                        FilePrefix = prefix,
                        LegacyArchiveRoot = archiveRoot
                    });

            Assert(exported.LegacyMigrationStatus ==
                   "archived_legacy_started_clean",
                "Legacy file without explicit manual columns must start a clean v2 snapshot.");
            Assert(!string.IsNullOrWhiteSpace(exported.LegacyArchiveDirectory) &&
                   Directory.Exists(exported.LegacyArchiveDirectory),
                "Legacy archive directory was not returned or created.");
            string archivedSummary = Path.Combine(
                exported.LegacyArchiveDirectory,
                Path.GetFileName(summaryPath));
            byte[] archivedBytes = File.ReadAllBytes(archivedSummary);
            Assert(legacyBytes.SequenceEqual(archivedBytes),
                "Legacy summary was not archived as an exact byte copy.");
            Assert(MaintenanceLedgerCsv.Sha256Hex(legacyBytes) ==
                   MaintenanceLedgerCsv.Sha256Hex(archivedBytes),
                "Legacy archive SHA-256 did not match the source bytes.");
            Assert(Directory.GetFiles(
                    exported.LegacyArchiveDirectory,
                    "*HandReach台账旧版归档清单.json")
                    .Length == 1,
                "Hash-verifiable legacy archive manifest is missing.");

            List<Dictionary<string, string>> rows = MaintenanceLedgerCsv.Parse(
                MaintenanceLedgerCsv.ReadAllTextShared(exported.SummaryCsvPath));
            Dictionary<string, string> current = rows.Single(x =>
                x["目标键"] == "HOST:202");
            Assert(current["台账人工确认"] == string.Empty &&
                   current["台账人工备注"] == string.Empty,
                "Ambiguous legacy algorithm column ‘结论’ was incorrectly migrated as manual data.");
            Assert(File.ReadAllBytes(userBridge).SequenceEqual(new byte[] { 1, 2, 3 }) &&
                   File.ReadAllBytes(codexBridge).SequenceEqual(new byte[] { 4, 5, 6 }) &&
                   File.ReadAllBytes(bridgeManifest).SequenceEqual(new byte[] { 7, 8, 9 }),
                "HandReach migration modified the separate DirectShape bridge files.");

            JObject manifest = JObject.Parse(File.ReadAllText(exported.ManifestJsonPath));
            Assert((string)manifest["legacyMigration"]["status"] ==
                   "archived_legacy_started_clean" &&
                   !(bool)manifest["legacyMigration"]
                       ["ambiguousLegacyConclusionMigrated"],
                "New manifest did not disclose the clean legacy migration contract.");
        }
        finally
        {
            DeleteLedgerTestDirectory(directory);
        }
    }

    private static void LedgerMigratesExplicitLegacyManualFields()
    {
        string directory = CreateLedgerTestDirectory();
        try
        {
            const string prefix = "legacy-manual";
            string summaryPath = WriteLegacySummary(
                directory,
                prefix,
                true,
                "人工确认：现场通过",
                "人工备注：复核日期已记录");
            byte[] legacyBytes = File.ReadAllBytes(summaryPath);
            MaintenanceHandReachLedgerExportResult exported =
                MaintenanceHandReachLedgerService.Export(
                    CreateLedgerResult(false),
                    new MaintenanceLedgerDestination
                    {
                        OutputDirectory = directory,
                        FilePrefix = prefix,
                        LegacyArchiveRoot = Path.Combine(directory, "archive-root")
                    });

            Assert(exported.LegacyMigrationStatus ==
                   "archived_legacy_manual_migrated" &&
                   exported.LegacyMappedManualRowCount == 1,
                "Stable one-to-one legacy manual row was not migrated explicitly.");
            List<Dictionary<string, string>> rows = MaintenanceLedgerCsv.Parse(
                MaintenanceLedgerCsv.ReadAllTextShared(exported.SummaryCsvPath));
            Dictionary<string, string> current = rows.Single(x =>
                x["目标键"] == "HOST:202");
            Assert(current["台账人工确认"] == string.Empty &&
                   current["台账人工备注"] == "人工备注：复核日期已记录" &&
                   exported.ResetStaleManualConclusionCount == 1,
                "Legacy conclusion without evidence/result fingerprints must stay archive-only while its note is preserved.");
            Assert(File.ReadAllBytes(Path.Combine(
                    exported.LegacyArchiveDirectory,
                    Path.GetFileName(summaryPath))).SequenceEqual(legacyBytes),
                "Migrated legacy manual source was not preserved byte-for-byte.");
        }
        finally
        {
            DeleteLedgerTestDirectory(directory);
        }
    }

    private static void LedgerArchivesAmbiguousLegacyManualRows()
    {
        string directory = CreateLedgerTestDirectory();
        try
        {
            const string prefix = "legacy-ambiguous";
            string summaryPath = WriteLegacySummary(
                directory,
                prefix,
                true,
                "人工确认：不得丢失",
                "人工备注：身份待核对");
            byte[] legacyBytes = File.ReadAllBytes(summaryPath);
            HandReachAnalysisResult result = CreateLedgerResult(false);
            result.TargetResults.Add(new HandReachTargetResult
            {
                Target = new HandReachTargetInfo
                {
                    TargetKey = "HOST:303",
                    GroupKey = "5B",
                    DeviceNo = "02",
                    SchemeNo = 3,
                    EquipmentName = "Feasible AHU",
                    Mark = "AHU-F",
                    ElementId = 303,
                    CeilingTopMm = 3000.0,
                    ServiceFaceProxyX = 1300.0,
                    ServiceFaceProxyY = 2100.0,
                    ServiceFaceProxyZ = 3120.0
                },
                ConnectivityAgreed = true,
                CandidateAuditComplete = true,
                LadderStatus = HandReachLadderStatus.Validated,
                AttentionLevel = HandReachAttentionLevel.High,
                Conclusion = "feasible_hand_reach",
                ConclusionReason = "Duplicate identity test target."
            });

            MaintenanceHandReachLedgerExportResult exported =
                MaintenanceHandReachLedgerService.Export(
                    result,
                    new MaintenanceLedgerDestination
                    {
                        OutputDirectory = directory,
                        FilePrefix = prefix,
                        LegacyArchiveRoot = Path.Combine(directory, "archive-root")
                    });
            Assert(exported.LegacyMigrationStatus ==
                   "archived_legacy_ambiguous_started_clean" &&
                   exported.LegacyMappedManualRowCount == 0,
                "Ambiguous legacy identity was incorrectly auto-migrated.");
            List<Dictionary<string, string>> rows = MaintenanceLedgerCsv.Parse(
                MaintenanceLedgerCsv.ReadAllTextShared(exported.SummaryCsvPath));
            Assert(rows.All(x => string.IsNullOrWhiteSpace(x["台账人工确认"]) &&
                                 string.IsNullOrWhiteSpace(x["台账人工备注"])),
                "Ambiguous manual values leaked into an unproven current target.");
            Assert(File.ReadAllBytes(Path.Combine(
                    exported.LegacyArchiveDirectory,
                    Path.GetFileName(summaryPath))).SequenceEqual(legacyBytes),
                "Ambiguous legacy artificial data was not preserved in the archive.");
        }
        finally
        {
            DeleteLedgerTestDirectory(directory);
        }
    }

    private static void LedgerArchiveFailureBlocksReplacement()
    {
        string directory = CreateLedgerTestDirectory();
        try
        {
            const string prefix = "legacy-blocked";
            string summaryPath = WriteLegacySummary(
                directory, prefix, true, "人工保留", "不能覆盖");
            byte[] before = File.ReadAllBytes(summaryPath);
            string archiveRootFile = Path.Combine(directory, "archive-root-is-a-file");
            File.WriteAllText(archiveRootFile, "not a directory");

            bool blocked = false;
            try
            {
                MaintenanceHandReachLedgerService.Export(
                    CreateLedgerResult(false),
                    new MaintenanceLedgerDestination
                    {
                        OutputDirectory = directory,
                        FilePrefix = prefix,
                        LegacyArchiveRoot = archiveRootFile
                    });
            }
            catch (IOException)
            {
                blocked = true;
            }
            Assert(blocked,
                "A failed legacy archive did not block live snapshot replacement.");
            Assert(before.SequenceEqual(File.ReadAllBytes(summaryPath)),
                "Live legacy summary changed even though archive creation failed.");
            Assert(!File.Exists(Path.Combine(
                       directory, prefix + ".handreach.candidates.csv")) &&
                   !File.Exists(Path.Combine(
                       directory, prefix + ".handreach.manifest.json")),
                "New HandReach files were written before a verified legacy archive existed.");

            bool hashMismatchBlocked = false;
            try
            {
                MaintenanceHandReachLegacyMigrationService.EnsureArchiveHashMatches(
                    "0000", new byte[] { 1, 2, 3 });
            }
            catch (InvalidDataException)
            {
                hashMismatchBlocked = true;
            }
            Assert(hashMismatchBlocked,
                "Archive byte hash mismatch did not fail closed.");
        }
        finally
        {
            DeleteLedgerTestDirectory(directory);
        }
    }

    private static HandReachAnalysisResult CreateLedgerResult(bool includeRejectedTarget)
    {
        var result = new HandReachAnalysisResult
        {
            AnalysisId = "analysis-ledger-directed",
            CreatedAtUtc = new DateTime(2026, 8, 20, 1, 2, 3, DateTimeKind.Utc),
            GroupKey = "5B",
            ModelFingerprint = "model-fingerprint",
            EvidenceFingerprint = "evidence-fingerprint",
            ResultFingerprint = "result-fingerprint",
            Options = new HandReachOptions(),
            WindowLimitedSampling = true
        };
        result.EvidenceSources.Add(new MaintenanceElementRef
        {
            ElementId = 9001,
            UniqueId = "ceiling-9001",
            Category = "Ceilings",
            Name = "5B ceiling"
        });
        if (includeRejectedTarget)
        {
            result.TargetResults.Add(new HandReachTargetResult
            {
                Target = new HandReachTargetInfo
                {
                    TargetKey = "HOST:101",
                    GroupKey = "5B",
                    DeviceNo = "01",
                    SchemeNo = 1,
                    EquipmentName = "Rejected AHU",
                    Mark = "AHU-R",
                    ElementId = 101,
                    CeilingTopMm = 3000.0
                },
                ConnectivityAgreed = true,
                CandidateAuditComplete = true,
                LadderStatus = HandReachLadderStatus.Rejected,
                AttentionLevel = HandReachAttentionLevel.Rejected,
                Conclusion = "rejected_no_feasible_hand_reach",
                ConclusionReason = "No feasible 400/200 candidate."
            });
        }

        var sample = new HandReachSample
        {
            CenterX = 1000.0,
            CenterY = 2000.0,
            EdgeX = 1200.0,
            EdgeY = 2000.0,
            HorizontalMm = 180.0,
            ObliqueMm = 220.0,
            VerticalMm = 120.0,
            DistanceGrade = HandReachDistanceGrade.AWithin300,
            CorridorClear = new[] { true, true, false, false, false },
            LadderDirection = "X",
            LadderFloorMm = 0.0,
            OperationZoneClear = true
        };
        var feasible = new HandReachTargetResult
        {
            Target = new HandReachTargetInfo
            {
                TargetKey = "HOST:202",
                GroupKey = "5B",
                DeviceNo = "02",
                SchemeNo = 2,
                EquipmentName = "Feasible AHU",
                Mark = "AHU-F",
                ElementId = 202,
                CeilingTopMm = 3000.0,
                ServiceFaceProxyX = 1300.0,
                ServiceFaceProxyY = 2100.0,
                ServiceFaceProxyZ = 3120.0
            },
            ClearCount = 3,
            ConnectivityAgreed = true,
            CandidateAuditComplete = true,
            LadderStatus = HandReachLadderStatus.Validated,
            AttentionLevel = HandReachAttentionLevel.High,
            Conclusion = "feasible_hand_reach",
            ConclusionReason = "Representative region verified."
        };
        feasible.Regions.Add(new HandReachRegion
        {
            RegionNo = 1,
            PointCount = 3,
            MinX = 960.0,
            MinY = 1960.0,
            MaxX = 1040.0,
            MaxY = 2040.0,
            AreaM2 = 0.0048,
            Recommended = sample,
            RecommendedCorridorClear = (bool[])sample.CorridorClear.Clone(),
            MaxTestedClearDiameterMm = 250,
            RecommendedLadderDirection = "X",
            RecommendedOperationZoneClear = true,
            RecommendedVerticalGrade = HandReachVerticalGrade.RecommendedWithin300
        });
        result.TargetResults.Add(feasible);
        return result;
    }

    private static void WriteManualValues(
        string summaryPath,
        string targetKey,
        string conclusion,
        string note)
    {
        string csv = MaintenanceLedgerCsv.ReadAllTextShared(summaryPath);
        string headerLine = csv.Split(new[] { "\r\n" }, StringSplitOptions.None)[0]
            .TrimStart('\ufeff');
        string[] headers = headerLine.Split(',');
        List<Dictionary<string, string>> rows = MaintenanceLedgerCsv.Parse(csv);
        Dictionary<string, string> row = rows.Single(x => x["目标键"] == targetKey);
        row["台账人工确认"] = conclusion;
        row["台账人工备注"] = note;
        MaintenanceLedgerCsv.WriteAllTextAtomic(
            summaryPath,
            MaintenanceLedgerCsv.Serialize(
                headers,
                rows.Cast<IDictionary<string, string>>()));
    }

    private static string WriteLegacySummary(
        string directory,
        string prefix,
        bool includeExplicitManualColumns,
        string manualConclusion,
        string manualNote)
    {
        var headers = new List<string>
        {
            "设备编号",
            "设备",
            "检修面代理点Xmm",
            "检修面代理点Ymm",
            "检修面代理点Zmm",
            "结论"
        };
        if (includeExplicitManualColumns)
        {
            headers.Add("人工确认");
            headers.Add("人工备注");
        }
        var row = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["设备编号"] = "02",
            ["设备"] = "Feasible AHU | AHU-F",
            ["检修面代理点Xmm"] = "1300.0",
            ["检修面代理点Ymm"] = "2100.0",
            ["检修面代理点Zmm"] = "3120.0",
            ["结论"] = "旧算法结论：不可当作人工确认",
            ["人工确认"] = manualConclusion,
            ["人工备注"] = manualNote
        };
        string path = Path.Combine(
            directory,
            prefix + ".handreach.summary.csv");
        MaintenanceLedgerCsv.WriteAllTextAtomic(
            path,
            MaintenanceLedgerCsv.Serialize(
                headers,
                new[] { (IDictionary<string, string>)row }));
        return path;
    }

    private static string CreateLedgerTestDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "OpenRevit-HandReachLedgerTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteLedgerTestDirectory(string directory)
    {
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}

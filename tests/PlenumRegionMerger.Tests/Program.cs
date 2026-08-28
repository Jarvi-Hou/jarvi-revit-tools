using System;
using System.Collections.Generic;
using System.Linq;
using JarviTools.Commands.Plenum;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        Run("disconnected rectangles stay separate", Disconnected);
        Run("positive shared edge merges", SharedEdge);
        Run("corner-only contact stays separate", CornerOnly);
        Run("different world Z stays separate", DifferentZ);
        Run("different state or color band stays separate", DifferentState);
        Run("different planes stay separate", DifferentPlane);
        Run("L footprint remains concave", LShape);
        Run("interior hole remains a hole", Hole);
        Run("adaptive-grid T junction merges", AdaptiveTjunction);
        Run("source traceability survives merging", Traceability);
        Run("merged boundary conserves source area", AreaConservation);

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

    private static void Disconnected()
    {
        List<PlenumMergedRegion> regions = Merge(
            Cell(1, 0, 10, 0, 10),
            Cell(2, 20, 30, 0, 10));
        Assert(regions.Count == 2, "Disconnected cells were merged.");
    }

    private static void SharedEdge()
    {
        List<PlenumMergedRegion> regions = Merge(
            Cell(1, 0, 10, 0, 10),
            Cell(2, 10, 20, 0, 10));
        Assert(regions.Count == 1, "Shared-edge cells did not merge.");
        Assert(regions[0].Loops.Count == 1, "A simple rectangle needs one loop.");
        Assert(regions[0].Loops[0].Points.Count == 4, "Internal seam was not removed.");
    }

    private static void CornerOnly()
    {
        List<PlenumMergedRegion> regions = Merge(
            Cell(1, 0, 10, 0, 10),
            Cell(2, 10, 20, 10, 20));
        Assert(regions.Count == 2, "Corner-only contact must not connect components.");
    }

    private static void DifferentZ()
    {
        PlenumMergeCell upper = Cell(2, 10, 20, 0, 10);
        upper.BottomZ = 1;
        upper.TopZ = 1001;
        Assert(Merge(Cell(1, 0, 10, 0, 10), upper).Count == 2,
            "Different 0.001 mm Z values were merged.");
    }

    private static void DifferentState()
    {
        PlenumMergeCell other = Cell(2, 10, 20, 0, 10);
        other.StateKey = "Yellow";
        Assert(Merge(Cell(1, 0, 10, 0, 10), other).Count == 2,
            "Different visualization states were merged.");
    }

    private static void DifferentPlane()
    {
        PlenumMergeCell other = Cell(2, 10, 20, 0, 10);
        other.PlaneKey = "Plane-B";
        Assert(Merge(Cell(1, 0, 10, 0, 10), other).Count == 2,
            "Different planes were merged.");
    }

    private static void LShape()
    {
        PlenumMergedRegion region = Single(
            Cell(1, 0, 10, 0, 10),
            Cell(2, 10, 20, 0, 10),
            Cell(3, 0, 10, 10, 20));
        Assert(region.Loops.Count == 1, "L shape should have one outer loop.");
        Assert(region.Loops[0].Points.Count == 6, "L shape concavity was flattened.");
        AssertAlmost(300, region.BoundaryArea, "Wrong L-shape area.");
    }

    private static void Hole()
    {
        var cells = new List<PlenumMergeCell>();
        int id = 1;
        for (int v = 0; v < 3; v++)
            for (int u = 0; u < 3; u++)
                if (!(u == 1 && v == 1)) cells.Add(Cell(id++, u * 10, (u + 1) * 10, v * 10, (v + 1) * 10));

        PlenumMergedRegion region = Single(cells.ToArray());
        Assert(region.Loops.Count == 2, "The missing center cell must remain a real hole.");
        Assert(region.Loops.Count(x => x.IsHole) == 1, "Expected exactly one clockwise hole loop.");
        AssertAlmost(800, region.BoundaryArea, "Hole was filled or area was lost.");
    }

    private static void AdaptiveTjunction()
    {
        PlenumMergedRegion region = Single(
            Cell(1, 0, 20, 0, 20),
            Cell(2, 20, 30, 5, 10),
            Cell(3, 20, 30, 10, 15));
        Assert(region.TraceIds.Count == 3, "Coarse/fine T-junction cells did not join.");
        Assert(region.Loops.Count == 1, "T-junction union should have one outer loop.");
        AssertAlmost(500, region.BoundaryArea, "T-junction area is wrong.");
    }

    private static void Traceability()
    {
        PlenumMergedRegion region = Single(
            Cell(90, 0, 10, 0, 10),
            Cell(4, 10, 20, 0, 10),
            Cell(17, 20, 30, 0, 10));
        Assert(region.TraceIds.SequenceEqual(new[] { 4, 17, 90 }),
            "Trace IDs were lost or are not deterministic.");
    }

    private static void AreaConservation()
    {
        PlenumMergedRegion region = Single(
            Cell(1, 0, 40, 0, 10),
            Cell(2, 0, 10, 10, 30),
            Cell(3, 30, 40, 10, 30),
            Cell(4, 0, 40, 30, 40));
        AssertAlmost(1200, region.SourceArea, "Source area is wrong.");
        AssertAlmost(region.SourceArea, region.BoundaryArea, "Boundary does not conserve source area.");
        Assert(region.Loops.Any(x => x.IsHole), "Area fixture should retain its interior hole.");
    }

    private static List<PlenumMergedRegion> Merge(params PlenumMergeCell[] cells)
    {
        return PlenumRegionMerger.Merge(cells);
    }

    private static PlenumMergedRegion Single(params PlenumMergeCell[] cells)
    {
        List<PlenumMergedRegion> regions = Merge(cells);
        Assert(regions.Count == 1, "Expected one region, got " + regions.Count + ".");
        return regions[0];
    }

    private static PlenumMergeCell Cell(int id, long uMin, long uMax, long vMin, long vMax)
    {
        return new PlenumMergeCell
        {
            TraceId = id,
            PlaneKey = "Plane-A",
            StateKey = "Green",
            BottomZ = 0,
            TopZ = 1000,
            UMin = uMin,
            UMax = uMax,
            VMin = vMin,
            VMax = vMax
        };
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertAlmost(double expected, double actual, string message)
    {
        if (Math.Abs(expected - actual) > 1e-8)
            throw new InvalidOperationException(message + " expected=" + expected + " actual=" + actual);
    }
}

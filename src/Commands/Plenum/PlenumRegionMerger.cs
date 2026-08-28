using System;
using System.Collections.Generic;
using System.Linq;

namespace JarviTools.Commands.Plenum
{
    /// <summary>
    /// A quantized, axis-aligned UV rectangle used only by the visualization layer.
    /// Coordinates are deliberately integer-valued so that shared-edge and Z equality
    /// are deterministic. The caller currently uses 0.001 mm as one unit.
    /// </summary>
    internal sealed class PlenumMergeCell
    {
        public int TraceId;
        public string PlaneKey;
        public string StateKey;
        public long BottomZ;
        public long TopZ;
        public long UMin;
        public long UMax;
        public long VMin;
        public long VMax;
    }

    internal struct PlenumRegionPoint : IEquatable<PlenumRegionPoint>
    {
        public readonly long U;
        public readonly long V;

        public PlenumRegionPoint(long u, long v)
        {
            U = u;
            V = v;
        }

        public bool Equals(PlenumRegionPoint other)
        {
            return U == other.U && V == other.V;
        }

        public override bool Equals(object obj)
        {
            return obj is PlenumRegionPoint && Equals((PlenumRegionPoint)obj);
        }

        public override int GetHashCode()
        {
            unchecked { return (U.GetHashCode() * 397) ^ V.GetHashCode(); }
        }
    }

    internal sealed class PlenumRegionLoop
    {
        public List<PlenumRegionPoint> Points = new List<PlenumRegionPoint>();
        public double SignedArea;
        public bool IsHole { get { return SignedArea < 0.0; } }
    }

    internal sealed class PlenumMergedRegion
    {
        public string PlaneKey;
        public string StateKey;
        public long BottomZ;
        public long TopZ;
        public List<int> TraceIds = new List<int>();
        public List<PlenumRegionLoop> Loops = new List<PlenumRegionLoop>();
        public double SourceArea;
        public double BoundaryArea;
    }

    /// <summary>
    /// Pure geometry/topology routine. It neither reads nor mutates Revit state.
    /// Rectangles join only through a collinear edge interval of positive length.
    /// T-junctions therefore join while corner-only contact does not.
    /// </summary>
    internal static class PlenumRegionMerger
    {
        private sealed class MergeKey : IEquatable<MergeKey>
        {
            public string PlaneKey;
            public string StateKey;
            public long BottomZ;
            public long TopZ;

            public bool Equals(MergeKey other)
            {
                return other != null
                       && BottomZ == other.BottomZ
                       && TopZ == other.TopZ
                       && string.Equals(PlaneKey, other.PlaneKey, StringComparison.Ordinal)
                       && string.Equals(StateKey, other.StateKey, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as MergeKey);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = PlaneKey == null ? 0 : StringComparer.Ordinal.GetHashCode(PlaneKey);
                    hash = (hash * 397) ^ (StateKey == null ? 0 : StringComparer.Ordinal.GetHashCode(StateKey));
                    hash = (hash * 397) ^ BottomZ.GetHashCode();
                    return (hash * 397) ^ TopZ.GetHashCode();
                }
            }
        }

        private sealed class IndexedInterval
        {
            public int Index;
            public long Min;
            public long Max;
        }

        private sealed class BoundaryBucket
        {
            public readonly List<IndexedInterval> LowSide = new List<IndexedInterval>();
            public readonly List<IndexedInterval> HighSide = new List<IndexedInterval>();
        }

        private sealed class SignedInterval
        {
            public long Min;
            public long Max;
            public int Sign;
        }

        private sealed class DirectedSegment
        {
            public PlenumRegionPoint Start;
            public PlenumRegionPoint End;
        }

        private sealed class DisjointSet
        {
            private readonly int[] _parent;
            private readonly byte[] _rank;

            public DisjointSet(int count)
            {
                _parent = Enumerable.Range(0, count).ToArray();
                _rank = new byte[count];
            }

            public int Find(int value)
            {
                int root = value;
                while (_parent[root] != root) root = _parent[root];
                while (_parent[value] != value)
                {
                    int next = _parent[value];
                    _parent[value] = root;
                    value = next;
                }
                return root;
            }

            public void Union(int a, int b)
            {
                int rootA = Find(a);
                int rootB = Find(b);
                if (rootA == rootB) return;
                if (_rank[rootA] < _rank[rootB]) _parent[rootA] = rootB;
                else if (_rank[rootA] > _rank[rootB]) _parent[rootB] = rootA;
                else
                {
                    _parent[rootB] = rootA;
                    _rank[rootA]++;
                }
            }
        }

        public static List<PlenumMergedRegion> Merge(IEnumerable<PlenumMergeCell> source)
        {
            if (source == null) throw new ArgumentNullException("source");
            List<PlenumMergeCell> cells = source.ToList();
            Validate(cells);

            var regions = new List<PlenumMergedRegion>();
            foreach (IGrouping<MergeKey, PlenumMergeCell> grouping in cells
                         .GroupBy(CreateKey)
                         .OrderBy(x => x.Key.PlaneKey, StringComparer.Ordinal)
                         .ThenBy(x => x.Key.StateKey, StringComparer.Ordinal)
                         .ThenBy(x => x.Key.BottomZ)
                         .ThenBy(x => x.Key.TopZ))
            {
                List<PlenumMergeCell> group = grouping
                    .OrderBy(x => x.TraceId)
                    .ToList();
                var sets = new DisjointSet(group.Count);
                JoinSharedEdges(group, sets);

                foreach (List<PlenumMergeCell> component in Enumerable.Range(0, group.Count)
                             .GroupBy(sets.Find)
                             .Select(x => x.Select(index => group[index])
                                 .OrderBy(cell => cell.TraceId).ToList())
                             .OrderBy(x => x[0].TraceId))
                {
                    regions.Add(BuildRegion(grouping.Key, component));
                }
            }
            return regions;
        }

        private static MergeKey CreateKey(PlenumMergeCell cell)
        {
            return new MergeKey
            {
                PlaneKey = cell.PlaneKey,
                StateKey = cell.StateKey,
                BottomZ = cell.BottomZ,
                TopZ = cell.TopZ
            };
        }

        private static void Validate(IList<PlenumMergeCell> cells)
        {
            var traceIds = new HashSet<int>();
            foreach (PlenumMergeCell cell in cells)
            {
                if (cell == null) throw new ArgumentException("Merge cells cannot contain null.", "cells");
                if (string.IsNullOrWhiteSpace(cell.PlaneKey))
                    throw new ArgumentException("PlaneKey is required.", "cells");
                if (string.IsNullOrWhiteSpace(cell.StateKey))
                    throw new ArgumentException("StateKey is required.", "cells");
                if (cell.UMin >= cell.UMax || cell.VMin >= cell.VMax)
                    throw new ArgumentException("Every merge cell must have positive UV area.", "cells");
                if (cell.BottomZ >= cell.TopZ)
                    throw new ArgumentException("Every merge cell must have positive height.", "cells");
                if (!traceIds.Add(cell.TraceId))
                    throw new ArgumentException("TraceId must be unique: " + cell.TraceId, "cells");
            }
        }

        private static void JoinSharedEdges(IList<PlenumMergeCell> cells, DisjointSet sets)
        {
            var vertical = new Dictionary<long, BoundaryBucket>();
            var horizontal = new Dictionary<long, BoundaryBucket>();
            for (int i = 0; i < cells.Count; i++)
            {
                PlenumMergeCell cell = cells[i];
                GetBucket(vertical, cell.UMin).LowSide.Add(new IndexedInterval
                    { Index = i, Min = cell.VMin, Max = cell.VMax });
                GetBucket(vertical, cell.UMax).HighSide.Add(new IndexedInterval
                    { Index = i, Min = cell.VMin, Max = cell.VMax });
                GetBucket(horizontal, cell.VMin).LowSide.Add(new IndexedInterval
                    { Index = i, Min = cell.UMin, Max = cell.UMax });
                GetBucket(horizontal, cell.VMax).HighSide.Add(new IndexedInterval
                    { Index = i, Min = cell.UMin, Max = cell.UMax });
            }

            foreach (BoundaryBucket bucket in vertical.Values) MatchIntervals(bucket.LowSide, bucket.HighSide, sets);
            foreach (BoundaryBucket bucket in horizontal.Values) MatchIntervals(bucket.LowSide, bucket.HighSide, sets);
        }

        private static BoundaryBucket GetBucket(Dictionary<long, BoundaryBucket> source, long coordinate)
        {
            BoundaryBucket bucket;
            if (!source.TryGetValue(coordinate, out bucket))
            {
                bucket = new BoundaryBucket();
                source.Add(coordinate, bucket);
            }
            return bucket;
        }

        private static void MatchIntervals(List<IndexedInterval> low, List<IndexedInterval> high, DisjointSet sets)
        {
            low.Sort(CompareIntervals);
            high.Sort(CompareIntervals);
            int i = 0;
            int j = 0;
            while (i < low.Count && j < high.Count)
            {
                IndexedInterval a = low[i];
                IndexedInterval b = high[j];
                long overlapMin = Math.Max(a.Min, b.Min);
                long overlapMax = Math.Min(a.Max, b.Max);
                if (overlapMax > overlapMin) sets.Union(a.Index, b.Index);

                if (a.Max < b.Max) i++;
                else if (b.Max < a.Max) j++;
                else
                {
                    i++;
                    j++;
                }
            }
        }

        private static int CompareIntervals(IndexedInterval a, IndexedInterval b)
        {
            int value = a.Min.CompareTo(b.Min);
            if (value != 0) return value;
            value = a.Max.CompareTo(b.Max);
            return value != 0 ? value : a.Index.CompareTo(b.Index);
        }

        private static PlenumMergedRegion BuildRegion(MergeKey key, List<PlenumMergeCell> cells)
        {
            List<DirectedSegment> boundary = BuildBoundary(cells);
            List<PlenumRegionLoop> loops = TraceLoops(boundary)
                .OrderByDescending(x => Math.Abs(x.SignedArea))
                .ThenBy(x => x.Points.Min(p => p.U))
                .ThenBy(x => x.Points.Min(p => p.V))
                .ToList();

            double sourceArea = cells.Sum(x =>
                (double)(x.UMax - x.UMin) * (double)(x.VMax - x.VMin));
            double boundaryArea = loops.Sum(x => x.SignedArea);
            double tolerance = Math.Max(0.5, Math.Abs(sourceArea) * 1e-10);
            if (Math.Abs(sourceArea - boundaryArea) > tolerance)
            {
                throw new InvalidOperationException(
                    "Merged boundary area does not conserve source area. source=" + sourceArea
                    + " boundary=" + boundaryArea);
            }

            return new PlenumMergedRegion
            {
                PlaneKey = key.PlaneKey,
                StateKey = key.StateKey,
                BottomZ = key.BottomZ,
                TopZ = key.TopZ,
                TraceIds = cells.Select(x => x.TraceId).OrderBy(x => x).ToList(),
                Loops = loops,
                SourceArea = sourceArea,
                BoundaryArea = boundaryArea
            };
        }

        private static List<DirectedSegment> BuildBoundary(IEnumerable<PlenumMergeCell> cells)
        {
            var vertical = new Dictionary<long, List<SignedInterval>>();
            var horizontal = new Dictionary<long, List<SignedInterval>>();
            foreach (PlenumMergeCell cell in cells)
            {
                AddSigned(vertical, cell.UMin, cell.VMin, cell.VMax, -1); // left: down
                AddSigned(vertical, cell.UMax, cell.VMin, cell.VMax, 1);  // right: up
                AddSigned(horizontal, cell.VMin, cell.UMin, cell.UMax, 1);  // bottom: right
                AddSigned(horizontal, cell.VMax, cell.UMin, cell.UMax, -1); // top: left
            }

            var result = new List<DirectedSegment>();
            foreach (KeyValuePair<long, List<SignedInterval>> item in vertical)
            {
                foreach (SignedInterval interval in ReduceIntervals(item.Value))
                {
                    result.Add(interval.Sign > 0
                        ? Segment(item.Key, interval.Min, item.Key, interval.Max)
                        : Segment(item.Key, interval.Max, item.Key, interval.Min));
                }
            }
            foreach (KeyValuePair<long, List<SignedInterval>> item in horizontal)
            {
                foreach (SignedInterval interval in ReduceIntervals(item.Value))
                {
                    result.Add(interval.Sign > 0
                        ? Segment(interval.Min, item.Key, interval.Max, item.Key)
                        : Segment(interval.Max, item.Key, interval.Min, item.Key));
                }
            }
            return result;
        }

        private static DirectedSegment Segment(long u1, long v1, long u2, long v2)
        {
            return new DirectedSegment
            {
                Start = new PlenumRegionPoint(u1, v1),
                End = new PlenumRegionPoint(u2, v2)
            };
        }

        private static void AddSigned(Dictionary<long, List<SignedInterval>> source,
            long coordinate, long min, long max, int sign)
        {
            List<SignedInterval> intervals;
            if (!source.TryGetValue(coordinate, out intervals))
            {
                intervals = new List<SignedInterval>();
                source.Add(coordinate, intervals);
            }
            intervals.Add(new SignedInterval { Min = min, Max = max, Sign = sign });
        }

        private static List<SignedInterval> ReduceIntervals(IEnumerable<SignedInterval> source)
        {
            var events = new SortedDictionary<long, int>();
            foreach (SignedInterval interval in source)
            {
                AddEvent(events, interval.Min, interval.Sign);
                AddEvent(events, interval.Max, -interval.Sign);
            }

            var result = new List<SignedInterval>();
            int active = 0;
            bool hasPrevious = false;
            long previous = 0;
            foreach (KeyValuePair<long, int> item in events)
            {
                if (hasPrevious && item.Key > previous && active != 0)
                {
                    if (Math.Abs(active) != 1)
                        throw new InvalidOperationException("Overlapping merge rectangles are not supported.");
                    result.Add(new SignedInterval
                        { Min = previous, Max = item.Key, Sign = Math.Sign(active) });
                }
                active += item.Value;
                previous = item.Key;
                hasPrevious = true;
            }
            if (active != 0) throw new InvalidOperationException("Unbalanced boundary intervals.");
            return result;
        }

        private static void AddEvent(IDictionary<long, int> events, long coordinate, int delta)
        {
            int value;
            events.TryGetValue(coordinate, out value);
            events[coordinate] = value + delta;
        }

        private static List<PlenumRegionLoop> TraceLoops(IList<DirectedSegment> segments)
        {
            var outgoing = new Dictionary<PlenumRegionPoint, List<int>>();
            for (int i = 0; i < segments.Count; i++)
            {
                List<int> list;
                if (!outgoing.TryGetValue(segments[i].Start, out list))
                {
                    list = new List<int>();
                    outgoing.Add(segments[i].Start, list);
                }
                list.Add(i);
            }

            var used = new bool[segments.Count];
            var loops = new List<PlenumRegionLoop>();
            while (true)
            {
                int first = FindFirstUnused(segments, used);
                if (first < 0) break;
                DirectedSegment initial = segments[first];
                var points = new List<PlenumRegionPoint> { initial.Start };
                used[first] = true;
                PlenumRegionPoint current = initial.End;
                int incomingDirection = Direction(initial);
                int guard = 0;
                while (!current.Equals(initial.Start))
                {
                    points.Add(current);
                    List<int> candidates;
                    if (!outgoing.TryGetValue(current, out candidates))
                        throw new InvalidOperationException("Merged boundary is open.");
                    int next = ChooseNext(segments, used, candidates, incomingDirection);
                    if (next < 0) throw new InvalidOperationException("Merged boundary cannot be traced.");
                    used[next] = true;
                    incomingDirection = Direction(segments[next]);
                    current = segments[next].End;
                    if (++guard > segments.Count)
                        throw new InvalidOperationException("Merged boundary trace exceeded its safety limit.");
                }

                SimplifyCollinear(points);
                if (points.Count < 4)
                    throw new InvalidOperationException("Merged boundary loop has fewer than four corners.");
                double signedArea = SignedArea(points);
                if (Math.Abs(signedArea) < 0.5)
                    throw new InvalidOperationException("Merged boundary loop has zero area.");
                loops.Add(new PlenumRegionLoop { Points = points, SignedArea = signedArea });
            }
            return loops;
        }

        private static int FindFirstUnused(IList<DirectedSegment> segments, IList<bool> used)
        {
            int best = -1;
            for (int i = 0; i < segments.Count; i++)
            {
                if (used[i]) continue;
                if (best < 0 || CompareSegments(segments[i], segments[best]) < 0) best = i;
            }
            return best;
        }

        private static int CompareSegments(DirectedSegment a, DirectedSegment b)
        {
            int value = a.Start.U.CompareTo(b.Start.U);
            if (value != 0) return value;
            value = a.Start.V.CompareTo(b.Start.V);
            if (value != 0) return value;
            value = a.End.U.CompareTo(b.End.U);
            return value != 0 ? value : a.End.V.CompareTo(b.End.V);
        }

        private static int ChooseNext(IList<DirectedSegment> segments, IList<bool> used,
            IEnumerable<int> candidates, int incomingDirection)
        {
            int best = -1;
            int bestRank = int.MaxValue;
            foreach (int candidate in candidates)
            {
                if (used[candidate]) continue;
                int turn = (Direction(segments[candidate]) - incomingDirection + 4) % 4;
                int rank = turn == 3 ? 0 : (turn == 0 ? 1 : (turn == 1 ? 2 : 3));
                if (rank < bestRank || (rank == bestRank
                                        && (best < 0 || CompareSegments(segments[candidate], segments[best]) < 0)))
                {
                    best = candidate;
                    bestRank = rank;
                }
            }
            return best;
        }

        // East=0, North=1, West=2, South=3.
        private static int Direction(DirectedSegment segment)
        {
            if (segment.End.U > segment.Start.U) return 0;
            if (segment.End.V > segment.Start.V) return 1;
            if (segment.End.U < segment.Start.U) return 2;
            return 3;
        }

        private static void SimplifyCollinear(List<PlenumRegionPoint> points)
        {
            bool changed;
            do
            {
                changed = false;
                for (int i = 0; i < points.Count && points.Count > 4; i++)
                {
                    PlenumRegionPoint previous = points[(i - 1 + points.Count) % points.Count];
                    PlenumRegionPoint current = points[i];
                    PlenumRegionPoint next = points[(i + 1) % points.Count];
                    if ((previous.U == current.U && current.U == next.U)
                        || (previous.V == current.V && current.V == next.V))
                    {
                        points.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            } while (changed);
        }

        private static double SignedArea(IList<PlenumRegionPoint> points)
        {
            decimal twiceArea = 0m;
            for (int i = 0; i < points.Count; i++)
            {
                PlenumRegionPoint a = points[i];
                PlenumRegionPoint b = points[(i + 1) % points.Count];
                twiceArea += (decimal)a.U * b.V - (decimal)b.U * a.V;
            }
            return (double)(twiceArea / 2m);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace JarviTools.Commands.Plenum
{
    internal sealed class PlenumOccupancyRange
    {
        public PlenumOccupancyRange(double startHeightFt, double endHeightFt)
        {
            StartHeightFt = startHeightFt;
            EndHeightFt = endHeightFt;
        }

        public double StartHeightFt { get; private set; }
        public double EndHeightFt { get; private set; }
    }

    internal sealed class PlenumProfileSignature
    {
        public bool IsUnknown;
        public List<PlenumOccupancyRange> MepOccupiedRanges = new List<PlenumOccupancyRange>();
    }

    internal static class PlenumProfileClassifier
    {
        public static bool ProfilesDiffer(
            IList<PlenumProfileSignature> profiles,
            double conservativeBoundaryHeightFt,
            double toleranceFt)
        {
            if (profiles == null || profiles.Count <= 1) return false;

            bool hasUnknown = profiles.Any(x => x == null || x.IsUnknown);
            bool hasKnown = profiles.Any(x => x != null && !x.IsUnknown);
            if (hasUnknown && hasKnown) return true;
            if (!hasKnown) return false;

            if (double.IsNaN(conservativeBoundaryHeightFt)
                || double.IsInfinity(conservativeBoundaryHeightFt)
                || conservativeBoundaryHeightFt < 0.0)
                return true;

            double safeTolerance = Math.Max(0.0, toleranceFt);
            List<List<PlenumOccupancyRange>> comparable = profiles
                .Where(x => x != null && !x.IsUnknown)
                .Select(x => NormalizeRanges(
                    x.MepOccupiedRanges,
                    conservativeBoundaryHeightFt,
                    safeTolerance))
                .ToList();

            for (int baselineIndex = 0; baselineIndex < comparable.Count; baselineIndex++)
            {
                List<PlenumOccupancyRange> baseline = comparable[baselineIndex];
                for (int candidateIndex = baselineIndex + 1;
                     candidateIndex < comparable.Count;
                     candidateIndex++)
                {
                    List<PlenumOccupancyRange> candidate = comparable[candidateIndex];
                    if (candidate.Count != baseline.Count) return true;
                    for (int rangeIndex = 0; rangeIndex < baseline.Count; rangeIndex++)
                    {
                        if (Math.Abs(candidate[rangeIndex].StartHeightFt
                                     - baseline[rangeIndex].StartHeightFt) > safeTolerance
                            || Math.Abs(candidate[rangeIndex].EndHeightFt
                                        - baseline[rangeIndex].EndHeightFt) > safeTolerance)
                            return true;
                    }
                }
            }
            return false;
        }

        internal static List<PlenumOccupancyRange> NormalizeRanges(
            IEnumerable<PlenumOccupancyRange> ranges,
            double clipEndHeightFt,
            double toleranceFt)
        {
            double safeClipEnd = Math.Max(0.0, clipEndHeightFt);
            double safeTolerance = Math.Max(0.0, toleranceFt);
            var clipped = (ranges ?? Enumerable.Empty<PlenumOccupancyRange>())
                .Where(x => x != null)
                .Select(x => new PlenumOccupancyRange(
                    Math.Max(0.0, x.StartHeightFt),
                    Math.Min(safeClipEnd, x.EndHeightFt)))
                .Where(x => x.EndHeightFt - x.StartHeightFt > 1e-9)
                .OrderBy(x => x.StartHeightFt)
                .ThenBy(x => x.EndHeightFt)
                .ToList();

            var merged = new List<PlenumOccupancyRange>();
            foreach (PlenumOccupancyRange range in clipped)
            {
                PlenumOccupancyRange current = merged.LastOrDefault();
                if (current == null || range.StartHeightFt > current.EndHeightFt + safeTolerance)
                {
                    merged.Add(range);
                    continue;
                }

                merged[merged.Count - 1] = new PlenumOccupancyRange(
                    current.StartHeightFt,
                    Math.Max(current.EndHeightFt, range.EndHeightFt));
            }
            return merged;
        }
    }
}

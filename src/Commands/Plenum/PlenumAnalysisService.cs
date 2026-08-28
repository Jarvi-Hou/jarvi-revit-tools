using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JarviTools.Commands.MaintenanceReachability;

namespace JarviTools.Commands.Plenum
{
    internal static class PlenumAnalysisService
    {
        private const double MmPerFoot = 304.8;
        private const double IntersectionToleranceFt = 1.0 / MmPerFoot;
        private const double FullCoverageThreshold = 0.999;

        internal sealed class Bounds3
        {
            public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

            public bool ContainsVertical(double x, double y, double zMin, double zMax, double pad)
            {
                return x >= MinX - pad && x <= MaxX + pad
                    && y >= MinY - pad && y <= MaxY + pad
                    && MaxZ >= zMin - pad && MinZ <= zMax + pad;
            }
        }

        internal sealed class Candidate
        {
            public Element Element;
            public Transform ToHost;
            public Transform FromHost;
            public Bounds3 WorldBounds;
            public List<Solid> Solids = new List<Solid>();
            public List<Bounds3> WorldSolidBounds = new List<Bounds3>();
            public int MeshCount;
            public string GeometryError;
            public PlenumState State;
            public PlenumSourceRef Source;
            public string SourceKey;
            public BuiltInCategory Category;
        }

        internal sealed class CandidateCollectionFailure
        {
            public string SourceKey;
            public PlenumSourceRef Source;
            public BuiltInCategory Category;
            public string Reason;
        }

        private sealed class FeatureSeed
        {
            public UV Uv;
            public XYZ Point;
            public string SourceKey;
        }

        private sealed class FeatureRegion
        {
            public double UMin, UMax, VMin, VMax;
            public string SourceKey;
            public bool RefineInterior;
            public bool RequireHit;
            public List<UV> Polygon = new List<UV>();

            public bool Overlaps(double uMin, double uMax, double vMin, double vMax)
            {
                UV representative;
                return TryGetRepresentative(uMin, uMax, vMin, vMax, out representative);
            }

            public bool CrossesCellBoundary(double uMin, double uMax, double vMin, double vMax)
            {
                UV representative;
                if (!TryGetRepresentative(uMin, uMax, vMin, vMax, out representative)) return false;
                if (RefineInterior) return true;
                bool cellFullyInsideProjection = CellCorners(uMin, uMax, vMin, vMax)
                    .All(PointInsideOrBoundary);
                return !cellFullyInsideProjection;
            }

            public bool TryGetRepresentative(double uMin, double uMax, double vMin, double vMax,
                out UV representative)
            {
                representative = null;
                const double tolerance = 1e-8;
                if (Polygon == null || Polygon.Count < 3
                    || UMax < uMin - tolerance || UMin > uMax + tolerance
                    || VMax < vMin - tolerance || VMin > vMax + tolerance)
                    return false;

                List<UV> corners = CellCorners(uMin, uMax, vMin, vMax);
                var points = new List<UV>();
                points.AddRange(corners.Where(PointInsideOrBoundary));
                points.AddRange(Polygon.Where(p => PointInRect(p, uMin, uMax, vMin, vMax)));

                for (int i = 0; i < Polygon.Count; i++)
                {
                    UV a = Polygon[i];
                    UV b = Polygon[(i + 1) % Polygon.Count];
                    for (int j = 0; j < corners.Count; j++)
                    {
                        UV c = corners[j];
                        UV d = corners[(j + 1) % corners.Count];
                        UV intersection;
                        if (TrySegmentIntersection(a, b, c, d, out intersection)) points.Add(intersection);
                    }
                }

                if (points.Count == 0) return false;
                representative = new UV(points.Average(p => p.U), points.Average(p => p.V));
                return true;
            }

            private bool PointInsideOrBoundary(UV point)
            {
                const double tolerance = 1e-8;
                bool positive = false;
                bool negative = false;
                for (int i = 0; i < Polygon.Count; i++)
                {
                    UV a = Polygon[i];
                    UV b = Polygon[(i + 1) % Polygon.Count];
                    double cross = Cross(a, b, point);
                    if (cross > tolerance) positive = true;
                    else if (cross < -tolerance) negative = true;
                    if (positive && negative) return false;
                }
                return true;
            }

            private static List<UV> CellCorners(double uMin, double uMax, double vMin, double vMax)
            {
                return new List<UV>
                {
                    new UV(uMin, vMin), new UV(uMax, vMin),
                    new UV(uMax, vMax), new UV(uMin, vMax)
                };
            }

            private static bool PointInRect(UV p, double uMin, double uMax, double vMin, double vMax)
            {
                const double tolerance = 1e-8;
                return p.U >= uMin - tolerance && p.U <= uMax + tolerance
                       && p.V >= vMin - tolerance && p.V <= vMax + tolerance;
            }

            private static double Cross(UV a, UV b, UV p)
            {
                return (b.U - a.U) * (p.V - a.V) - (b.V - a.V) * (p.U - a.U);
            }

            private static bool TrySegmentIntersection(UV a, UV b, UV c, UV d, out UV point)
            {
                point = null;
                double rU = b.U - a.U;
                double rV = b.V - a.V;
                double sU = d.U - c.U;
                double sV = d.V - c.V;
                double denominator = rU * sV - rV * sU;
                if (Math.Abs(denominator) < 1e-12) return false;
                double cmaU = c.U - a.U;
                double cmaV = c.V - a.V;
                double t = (cmaU * sV - cmaV * sU) / denominator;
                double u = (cmaU * rV - cmaV * rU) / denominator;
                const double tolerance = 1e-8;
                if (t < -tolerance || t > 1.0 + tolerance || u < -tolerance || u > 1.0 + tolerance)
                    return false;
                point = new UV(a.U + t * rU, a.V + t * rV);
                return true;
            }
        }

        private sealed class CellDraft
        {
            public int Depth;
            public double UMin, UMax, VMin, VMax;
            public double Coverage;
            public double AreaFt2;
            public bool IsFullFootprintInsideCeiling;
            public XYZ Center;
            public XYZ P00, P10, P11, P01;
            public List<XYZ> FaceProbePoints = new List<XYZ>();
            public List<FeatureSeed> Seeds = new List<FeatureSeed>();
            public List<FeatureRegion> Regions = new List<FeatureRegion>();
        }

        private sealed class Hit
        {
            public double ZMin;
            public double ZMax;
            public PlenumState State;
            public PlenumSourceRef Source;
            public string SourceKey;
        }

        private sealed class BoundarySegment
        {
            public UV Start;
            public UV End;
        }

        private sealed class UvTriangle
        {
            public UV A;
            public UV B;
            public UV C;
            public double UMin;
            public double UMax;
            public double VMin;
            public double VMax;
        }

        private sealed class MergedHit
        {
            public double ZMin;
            public double ZMax;
            public List<PlenumSourceRef> Sources = new List<PlenumSourceRef>();
        }

        private enum FreeEnvelopeValidationState
        {
            Clear,
            Conflict,
            Unverified
        }

        private sealed class ProbeOutcome
        {
            public XYZ Point;
            public bool Unknown;
            public string Warning;
            public double ConnectedHeightFt;
            public double StructureHeightFt;
            public PlenumSourceRef FirstBlocker;
            public string FirstBlockerKey;
            public Hit StructureHit;
            public List<Hit> MepHits = new List<Hit>();
            public HashSet<string> HitKeys = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> UnknownCandidateKeys = new HashSet<string>(StringComparer.Ordinal);
        }

        public static Element ResolveCeiling(UIDocument uidoc, long? explicitElementId)
        {
            if (uidoc == null || uidoc.Document == null)
                throw new InvalidOperationException("Revit 没有活动文档。");

            Document doc = uidoc.Document;
            if (explicitElementId.HasValue)
            {
                Element explicitElement = doc.GetElement(new ElementId(explicitElementId.Value));
                if (!IsCeiling(explicitElement))
                    throw new InvalidOperationException("指定 ElementId 不是当前文档中的吊顶：" + explicitElementId.Value);
                return explicitElement;
            }

            var selected = uidoc.Selection.GetElementIds()
                .Select(doc.GetElement)
                .Where(IsCeiling)
                .ToList();
            if (selected.Count == 1) return selected[0];
            if (selected.Count > 1)
                throw new InvalidOperationException("当前选择包含多块吊顶，请只保留一块。");

            View view = doc.ActiveView;
            var visible = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_Ceilings)
                .WhereElementIsNotElementType()
                .ToElements()
                .Where(IsCeiling)
                .ToList();
            if (visible.Count == 1) return visible[0];

            throw new InvalidOperationException(
                visible.Count == 0
                    ? "当前视图中没有可见吊顶。请预选一块吊顶，或用三维剖面框只保留目标吊顶。"
                    : "当前视图中有 " + visible.Count + " 块吊顶。请预选一块，或收紧三维剖面框使目标唯一。");
        }

        public static PlenumAnalysisResult Analyze(UIApplication uiapp, Element ceiling, PlenumAnalysisConfig config)
        {
            if (uiapp == null || uiapp.ActiveUIDocument == null)
                throw new InvalidOperationException("Revit 没有活动文档。");
            if (!IsCeiling(ceiling))
                throw new ArgumentException("分析对象必须是宿主装饰模型中的吊顶。", "ceiling");

            config = config ?? new PlenumAnalysisConfig();
            config.Validate();

            Stopwatch stopwatch = Stopwatch.StartNew();
            Document doc = uiapp.ActiveUIDocument.Document;
            View activeView = doc.ActiveView;
            PlanarFace topFace = FindHorizontalTopFace(ceiling);
            if (topFace == null)
                throw new InvalidOperationException("未找到可信的水平吊顶顶面；V1 暂不支持斜面或曲面吊顶。");

            double topZ = topFace.Origin.Z;
            double searchTop = ResolveSearchTop(activeView, topZ, config.SearchHeightMm / MmPerFoot);
            if (searchTop - topZ < 100.0 / MmPerFoot)
                throw new InvalidOperationException("剖面框上界距离吊顶过近，无法形成有效搜索空间。");

            BoundingBoxXYZ ceilingBounds = ceiling.get_BoundingBox(activeView) ?? ceiling.get_BoundingBox(null);
            if (ceilingBounds == null)
                throw new InvalidOperationException("吊顶没有可用包围盒。");
            Bounds3 roi = new Bounds3
            {
                MinX = ceilingBounds.Min.X,
                MinY = ceilingBounds.Min.Y,
                MinZ = topZ,
                MaxX = ceilingBounds.Max.X,
                MaxY = ceilingBounds.Max.Y,
                MaxZ = searchTop
            };

            var result = new PlenumAnalysisResult
            {
                AnalysisId = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = DateTime.UtcNow.ToString("o"),
                DocumentTitle = doc.Title,
                DocumentPath = doc.PathName,
                ViewId = activeView.Id.Value,
                ViewName = activeView.Name,
                CeilingId = ceiling.Id.Value,
                CeilingUniqueId = ceiling.UniqueId,
                CeilingName = ceiling.Name,
                CeilingTopZFt = topZ,
                SearchTopZFt = searchTop,
                CeilingAreaFt2 = topFace.Area,
                Config = config
            };

            List<Candidate> candidates = CollectCandidates(doc, roi, result);
            result.CandidateCount = candidates.Count;
            result.MepCandidateCount = candidates.Count(c => c.State == PlenumState.MepOccupied);
            result.StructureCandidateCount = candidates.Count(c => c.State == PlenumState.Structure);
            result.UnsupportedCandidateCount = candidates.Count(c => c.Solids.Count == 0
                                                                      || c.MeshCount > 0
                                                                      || !string.IsNullOrEmpty(c.GeometryError));

            List<FeatureSeed> seeds = BuildFeatureSeeds(candidates, topFace, config);
            List<FeatureRegion> regions = BuildFeatureRegions(candidates, topFace);
            List<BoundarySegment> ceilingBoundary = BuildFaceBoundarySegments(topFace);
            double faceAreaScale;
            double meshAreaFt2;
            List<UvTriangle> ceilingTriangles = BuildFaceTriangles(
                topFace, out faceAreaScale, out meshAreaFt2);
            if (ceilingTriangles.Count == 0)
                throw new InvalidOperationException("吊顶顶面无法生成可信的三角网格，已停止分析。");
            double meshRelativeError = Math.Abs(meshAreaFt2 - topFace.Area) / topFace.Area;
            double meshErrorLimit = HasOnlyStraightEdges(topFace) ? 0.001 : 0.005;
            if (meshRelativeError > meshErrorLimit)
            {
                throw new InvalidOperationException(
                    "吊顶顶面三角网格面积与 Revit 精确面积偏差 " +
                    (meshRelativeError * 100.0).ToString("0.###") +
                    "% ，超过允许值 " + (meshErrorLimit * 100.0).ToString("0.###") +
                    "%；为避免复杂边界被错误采样，已停止分析。");
            }
            result.CeilingMeshAreaFt2 = meshAreaFt2;
            result.CeilingMeshRelativeError = meshRelativeError;
            result.FeatureSeedCount = seeds.Count;

            BoundingBoxUV uvBounds = topFace.GetBoundingBox();
            var drafts = new List<CellDraft>();
            BuildCells(topFace, uvBounds.Min.U, uvBounds.Max.U, uvBounds.Min.V, uvBounds.Max.V,
                0, seeds, regions, ceilingBoundary, ceilingTriangles, faceAreaScale, config, drafts);

            var coveredKeys = new HashSet<string>(StringComparer.Ordinal);
            var candidateByKey = candidates.ToDictionary(c => c.SourceKey, StringComparer.Ordinal);
            int nextCellId = 1;
            foreach (CellDraft draft in drafts)
            {
                var probePoints = new List<Tuple<XYZ, bool>> { Tuple.Create(draft.Center, false) };
                foreach (XYZ point in draft.FaceProbePoints)
                {
                    if (!probePoints.Any(p => HorizontalDistanceFt(p.Item1, point) * MmPerFoot < 5.0))
                        probePoints.Add(Tuple.Create(point, false));
                }
                foreach (FeatureSeed seed in draft.Seeds)
                {
                    if (!probePoints.Any(p => HorizontalDistanceFt(p.Item1, seed.Point) * MmPerFoot < 5.0))
                        probePoints.Add(Tuple.Create(seed.Point, true));
                }
                foreach (FeatureRegion region in draft.Regions)
                {
                    UV uv;
                    if (!region.TryGetRepresentative(
                            draft.UMin, draft.UMax, draft.VMin, draft.VMax, out uv)) continue;
                    if (!topFace.IsInside(uv)) continue;
                    XYZ point = topFace.Evaluate(uv);
                    if (!probePoints.Any(p => HorizontalDistanceFt(p.Item1, point) * MmPerFoot < 5.0))
                        probePoints.Add(Tuple.Create(point, true));
                }

                var outcomes = new List<Tuple<ProbeOutcome, bool>>();
                foreach (var probe in probePoints)
                {
                    ProbeOutcome outcome = AnalyzeProbe(probe.Item1, topZ, searchTop, candidates, coveredKeys);
                    outcomes.Add(Tuple.Create(outcome, probe.Item2));
                    if (probe.Item2) result.FeatureProbeCount++;
                    else result.UniformProbeCount++;
                }

                var localHitKeys = new HashSet<string>(outcomes.SelectMany(x => x.Item1.HitKeys), StringComparer.Ordinal);
                List<string> requiredKeys = draft.Regions
                    .Where(r => r.RequireHit)
                    .Select(r => r.SourceKey).Distinct().ToList();
                List<string> missingKeys = requiredKeys.Where(k => !localHitKeys.Contains(k)).ToList();
                List<string> geometryUnverifiedKeys = outcomes
                    .SelectMany(x => x.Item1.UnknownCandidateKeys)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToList();
                List<string> unverifiedKeys = missingKeys
                    .Concat(geometryUnverifiedKeys)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToList();
                bool unknown = outcomes.Any(x => x.Item1.Unknown) || missingKeys.Count > 0;
                var known = outcomes.Where(x => !x.Item1.Unknown).Select(x => x.Item1).ToList();
                ProbeOutcome worst = known.OrderBy(x => x.ConnectedHeightFt).FirstOrDefault();
                double minStructure = known.Count == 0 ? double.NaN : known.Min(x => x.StructureHeightFt);
                var warningParts = outcomes.Select(x => x.Item1.Warning)
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
                if (missingKeys.Count > 0)
                {
                    string examples = string.Join(", ", missingKeys.Take(3).Select(k =>
                        candidateByKey.ContainsKey(k)
                            ? candidateByKey[k].Source.Category + " " + candidateByKey[k].Source.ElementId
                            : k));
                    warningParts.Add("单元投影内有 " + missingKeys.Count +
                                     " 个候选构件未被本单元探针证实：" + examples);
                }
                string warning = string.Join("; ", warningParts);

                var profiles = new List<PlenumProbeProfile>();
                foreach (Tuple<ProbeOutcome, bool> item in outcomes)
                {
                    ProbeOutcome outcome = item.Item1;
                    List<PlenumVerticalInterval> profileIntervals = outcome.Unknown
                        ? UnknownIntervals(searchTop - topZ)
                        : BuildCellIntervals(
                            topZ,
                            searchTop,
                            outcome.StructureHeightFt,
                            outcome.MepHits,
                            new List<Hit> { outcome.StructureHit });
                    profiles.Add(new PlenumProbeProfile
                    {
                        Point = outcome.Point,
                        IsFeatureProbe = item.Item2,
                        IsUnknown = outcome.Unknown,
                        ConnectedFreeHeightFt = outcome.ConnectedHeightFt,
                        StructureBoundaryHeightFt = outcome.Unknown ? double.NaN : outcome.StructureHeightFt,
                        FirstBlocker = outcome.FirstBlocker,
                        Warning = outcome.Warning,
                        VerticalIntervals = profileIntervals,
                        ObservedEvidenceIntervals = BuildObservedEvidenceIntervals(
                            topZ, searchTop, outcome.MepHits, outcome.StructureHit),
                        UnknownCandidateKeys = outcome.UnknownCandidateKeys.ToList()
                    });
                }

                List<PlenumVerticalInterval> intervals = unknown || worst == null
                    ? UnknownIntervals(searchTop - topZ)
                    : BuildCellIntervals(
                        topZ,
                        searchTop,
                        minStructure,
                        known.SelectMany(x => x.MepHits).ToList(),
                        known.Select(x => x.StructureHit).Where(x => x != null).ToList());

                bool isMixed = ProfilesDiffer(profiles, minStructure);
                bool envelopeUnverified = false;
                var freeEnvelopeConflictKeys = new List<string>();
                var freeEnvelopeUnverifiedKeys = new List<string>();
                if (!unknown && worst != null && !isMixed
                    && draft.Coverage >= FullCoverageThreshold)
                {
                    string conflictKey;
                    string conflictReason;
                    FreeEnvelopeValidationState envelopeState = ValidateFreeEnvelope(
                        draft,
                        intervals,
                        candidates,
                        out conflictKey,
                        out conflictReason);
                    if (envelopeState == FreeEnvelopeValidationState.Conflict)
                    {
                        isMixed = true;
                        freeEnvelopeConflictKeys.Add(conflictKey);
                        warningParts.Add("Exact free-envelope validation: "
                                         + conflictReason + " (" + conflictKey + ")");
                        warning = string.Join("; ", warningParts.Distinct());
                    }
                    else if (envelopeState == FreeEnvelopeValidationState.Unverified)
                    {
                        envelopeUnverified = true;
                        freeEnvelopeUnverifiedKeys.Add(conflictKey);
                        if (!unverifiedKeys.Contains(conflictKey)) unverifiedKeys.Add(conflictKey);
                        warningParts.Add("Exact free-envelope validation unverified: "
                                         + conflictReason + " (" + conflictKey + ")");
                        warning = string.Join("; ", warningParts.Distinct());
                    }
                }

                result.Cells.Add(new PlenumCellResult
                {
                    CellId = nextCellId++,
                    Depth = draft.Depth,
                    UMin = draft.UMin,
                    UMax = draft.UMax,
                    VMin = draft.VMin,
                    VMax = draft.VMax,
                    CoverageFraction = draft.Coverage,
                    AreaFt2 = draft.AreaFt2,
                    ResolutionMm = Math.Max(draft.UMax - draft.UMin, draft.VMax - draft.VMin) * MmPerFoot,
                    Center = draft.Center,
                    P00 = draft.P00,
                    P10 = draft.P10,
                    P11 = draft.P11,
                    P01 = draft.P01,
                    IsFullFootprintInsideCeiling = draft.IsFullFootprintInsideCeiling,
                    IsUnknown = unknown || worst == null || envelopeUnverified,
                    IsMixed = isMixed,
                    ConnectedFreeHeightFt = unknown || worst == null || envelopeUnverified
                        ? 0.0
                        : worst.ConnectedHeightFt,
                    StructureBoundaryHeightFt = unknown || worst == null || envelopeUnverified
                        ? double.NaN
                        : minStructure,
                    ProbeCount = probePoints.Count,
                    FeatureProbeCount = probePoints.Count(x => x.Item2),
                    FirstBlocker = unknown || worst == null || envelopeUnverified
                        ? null
                        : worst.FirstBlocker,
                    Warning = warning,
                    VerticalIntervals = envelopeUnverified
                        ? UnknownIntervals(searchTop - topZ)
                        : intervals,
                    ProbeProfiles = profiles,
                    ProjectionMissCandidateKeys = missingKeys
                        .OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    GeometryUnverifiedCandidateKeys = geometryUnverifiedKeys,
                    UnverifiedCandidateKeys = unverifiedKeys,
                    FreeEnvelopeConflictCandidateKeys = freeEnvelopeConflictKeys,
                    FreeEnvelopeUnverifiedCandidateKeys = freeEnvelopeUnverifiedKeys
                });
            }

            result.CoveredCandidateCount = coveredKeys.Count;
            var projectedKeys = new HashSet<string>(
                drafts.SelectMany(d => d.Regions).Select(r => r.SourceKey), StringComparer.Ordinal);
            projectedKeys.UnionWith(coveredKeys);
            result.ProjectedCandidateCount = projectedKeys.Count;
            int uncovered = projectedKeys.Count(k => !coveredKeys.Contains(k));
            if (uncovered > 0)
                result.Warnings.Add(uncovered + " 个与吊顶投影相交的候选构件未被探针命中；相关单元已标为 Unknown。");
            if (result.UnsupportedCandidateCount > 0)
                result.Warnings.Add(result.UnsupportedCandidateCount + " 个候选构件的几何为空、部分 Mesh 或提取不完整；其投影覆盖位置不并入 Free。");
            result.Warnings.Add("本次类别范围为楼板/屋面/梁/结构柱与常规风管、水管、桥架、线管、喷淋、机电设备等；未纳入类别不得解读为已证明空闲。");
            if (activeView is View3D && ((View3D)activeView).IsSectionBoxActive)
                result.Warnings.Add("搜索上界采用当前三维视图剖面框；修改剖面框后应重新分析。");

            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return result;
        }

        private static bool IsCeiling(Element element)
        {
            return element != null && element.Category != null
                && element.Category.Id.Value == (long)BuiltInCategory.OST_Ceilings;
        }

        internal static PlanarFace FindHorizontalTopFace(Element ceiling)
        {
            GeometryExtraction extraction = ExtractGeometry(ceiling);
            var upwardFaces = new List<PlanarFace>();
            foreach (Solid solid in extraction.Solids)
            {
                foreach (Face face in solid.Faces)
                {
                    PlanarFace planar = face as PlanarFace;
                    if (planar == null || planar.FaceNormal.Z < 0.999999) continue;
                    upwardFaces.Add(planar);
                }
            }
            if (upwardFaces.Count == 0) return null;

            double highestZ = upwardFaces.Max(x => x.Origin.Z);
            bool hasOtherElevation = upwardFaces.Any(
                x => Math.Abs(x.Origin.Z - highestZ) > IntersectionToleranceFt);
            List<PlanarFace> highestFaces = upwardFaces
                .Where(x => Math.Abs(x.Origin.Z - highestZ) <= IntersectionToleranceFt)
                .ToList();
            if (hasOtherElevation || highestFaces.Count != 1)
            {
                throw new InvalidOperationException(
                    "检测到同一吊顶包含多个水平顶面或不同顶面标高。V1 为防止静默漏算已停止；请拆分吊顶后逐块分析。");
            }
            return highestFaces[0];
        }

        private static double ResolveSearchTop(View view, double topZ, double configuredHeightFt)
        {
            View3D view3 = view as View3D;
            if (view3 != null && view3.IsSectionBoxActive)
            {
                BoundingBoxXYZ box = view3.GetSectionBox();
                double maxZ = double.MinValue;
                for (int i = 0; i < 8; i++)
                {
                    double x = (i & 1) == 0 ? box.Min.X : box.Max.X;
                    double y = (i & 2) == 0 ? box.Min.Y : box.Max.Y;
                    double z = (i & 4) == 0 ? box.Min.Z : box.Max.Z;
                    maxZ = Math.Max(maxZ, box.Transform.OfPoint(new XYZ(x, y, z)).Z);
                }
                if (maxZ > topZ + 100.0 / MmPerFoot) return maxZ;
            }
            return topZ + configuredHeightFt;
        }

        internal static List<Candidate> CollectCandidates(Document hostDoc, Bounds3 hostRoi, PlenumAnalysisResult result)
        {
            return CollectCandidates(hostDoc, hostRoi, result, null);
        }

        internal static List<Candidate> CollectCandidates(
            Document hostDoc,
            Bounds3 hostRoi,
            PlenumAnalysisResult result,
            MaintenanceLinkScopeSnapshot linkScope)
        {
            var candidates = new List<Candidate>();
            List<BuiltInCategory> structureCategories = StructureCategories();
            List<BuiltInCategory> mepCategories = MepCategories();

            AddCandidates(hostDoc, null, Transform.Identity, hostRoi, structureCategories,
                PlenumState.Structure, candidates, result.CandidateCollectionFailures);
            AddCandidates(hostDoc, null, Transform.Identity, hostRoi, mepCategories,
                PlenumState.MepOccupied, candidates, result.CandidateCollectionFailures);

            foreach (RevitLinkInstance link in new FilteredElementCollector(hostDoc)
                         .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                string linkUniqueId;
                try { linkUniqueId = link.UniqueId ?? string.Empty; }
                catch { linkUniqueId = string.Empty; }
                if (linkScope != null &&
                    !linkScope.Includes(link.Id.Value, linkUniqueId))
                    continue;
                string linkProbeReason;
                bool linkMayOverlap = LinkMayOverlapRoi(link, hostRoi, out linkProbeReason);
                Document linkDoc;
                try { linkDoc = link.GetLinkDocument(); }
                catch (Exception ex)
                {
                    if (linkMayOverlap)
                        result.CandidateCollectionFailures.Add(CreateLinkCollectionFailure(
                            hostDoc,
                            link,
                            "link document unavailable: " + ex.GetType().Name));
                    continue;
                }
                if (linkDoc == null)
                {
                    if (linkMayOverlap)
                        result.CandidateCollectionFailures.Add(CreateLinkCollectionFailure(
                            hostDoc, link,
                            string.IsNullOrWhiteSpace(linkProbeReason)
                                ? "link document is unloaded"
                                : "link document is unloaded; " + linkProbeReason));
                    continue;
                }
                result.LoadedLinks.Add(linkDoc.Title + " [instance " + link.Id.Value + "]");
                Transform toHost;
                try { toHost = link.GetTotalTransform(); }
                catch (Exception ex)
                {
                    if (linkMayOverlap)
                        result.CandidateCollectionFailures.Add(CreateLinkCollectionFailure(
                            hostDoc, link, "link transform unavailable: " + ex.GetType().Name));
                    continue;
                }
                AddCandidates(linkDoc, link, toHost, hostRoi, structureCategories,
                    PlenumState.Structure, candidates, result.CandidateCollectionFailures);
                AddCandidates(linkDoc, link, toHost, hostRoi, mepCategories,
                    PlenumState.MepOccupied, candidates, result.CandidateCollectionFailures);
            }

            return candidates
                .GroupBy(c => c.SourceKey)
                .Select(g => g.First())
                .ToList();
        }

        private static void AddCandidates(Document sourceDoc, RevitLinkInstance link, Transform toHost,
            Bounds3 hostRoi, List<BuiltInCategory> categories, PlenumState state,
            List<Candidate> target, ICollection<CandidateCollectionFailure> failures)
        {
            Transform fromHost = toHost.Inverse;
            Outline sourceOutline = TransformOutline(hostRoi, fromHost);
            var categoryFilter = new ElementMulticategoryFilter(categories);
            IList<Element> elements;
            using (var collector = new FilteredElementCollector(sourceDoc))
            {
                elements = collector.WherePasses(categoryFilter)
                    .WhereElementIsNotElementType()
                    .WherePasses(new BoundingBoxIntersectsFilter(sourceOutline))
                    .ToElements();
            }

            foreach (Element element in elements)
            {
                if (IsOwnedAnalysisDirectShape(element)) continue;
                BuiltInCategory category = element.Category == null
                    ? BuiltInCategory.INVALID
                    : (BuiltInCategory)(int)element.Category.Id.Value;
                var source = new PlenumSourceRef
                {
                    SourceType = link == null ? "Host" : "RevitLink",
                    DocumentTitle = sourceDoc.Title,
                    LinkInstanceId = link == null ? (long?)null : link.Id.Value,
                    LinkInstanceUniqueId = link == null ? string.Empty : link.UniqueId,
                    ElementId = element.Id.Value,
                    UniqueId = element.UniqueId,
                    Category = element.Category == null ? "?" : element.Category.Name,
                    Name = element.Name,
                    BlockerKind = state == PlenumState.Structure ? "Structure" : "MEP"
                };
                string key = link == null
                    ? MaintenanceStableIdentity.HostElementKey(element.UniqueId)
                    : MaintenanceStableIdentity.LinkedElementKey(
                        link.UniqueId, element.UniqueId);
                BoundingBoxXYZ bb;
                try { bb = element.get_BoundingBox(null); }
                catch (Exception ex)
                {
                    failures.Add(new CandidateCollectionFailure
                    {
                        SourceKey = key,
                        Source = source,
                        Category = category,
                        Reason = "bounding box unavailable: " + ex.GetType().Name
                    });
                    continue;
                }
                if (bb == null)
                {
                    failures.Add(new CandidateCollectionFailure
                    {
                        SourceKey = key,
                        Source = source,
                        Category = category,
                        Reason = "bounding box is null after ROI filter"
                    });
                    continue;
                }
                Bounds3 worldBounds = WorldBounds(bb, toHost);
                if (!BoundsOverlap(worldBounds, hostRoi)) continue;
                // ROI 在吊顶顶面以上是开区间；仅在下界/上界相切的楼板不属于夹层阻挡。
                if (worldBounds.MaxZ <= hostRoi.MinZ + IntersectionToleranceFt
                    || worldBounds.MinZ >= hostRoi.MaxZ - IntersectionToleranceFt)
                    continue;

                GeometryExtraction extraction = ExtractGeometry(element);
                var worldSolidBounds = new List<Bounds3>();
                foreach (Solid solid in extraction.Solids)
                {
                    try { worldSolidBounds.Add(WorldBounds(solid.GetBoundingBox(), toHost)); }
                    catch { worldSolidBounds.Add(null); }
                }
                target.Add(new Candidate
                {
                    Element = element,
                    ToHost = toHost,
                    FromHost = fromHost,
                    WorldBounds = worldBounds,
                    Solids = extraction.Solids,
                    WorldSolidBounds = worldSolidBounds,
                    MeshCount = extraction.MeshCount,
                    GeometryError = extraction.Error,
                    State = state,
                    Source = source,
                    SourceKey = key,
                    Category = category
                });
            }
        }

        private static bool LinkMayOverlapRoi(
            RevitLinkInstance link,
            Bounds3 hostRoi,
            out string probeReason)
        {
            probeReason = string.Empty;
            try
            {
                BoundingBoxXYZ box = link.get_BoundingBox(null);
                if (box == null)
                {
                    probeReason = "link instance bounding box is null, overlap treated as unknown";
                    return true;
                }
                return BoundsOverlap(WorldBounds(box, Transform.Identity), hostRoi);
            }
            catch (Exception ex)
            {
                probeReason = "link instance bounds unavailable: " + ex.GetType().Name +
                              ", overlap treated as unknown";
                return true;
            }
        }

        private static bool IsOwnedAnalysisDirectShape(Element element)
        {
            DirectShape shape = element as DirectShape;
            if (shape == null) return false;
            string applicationId;
            try { applicationId = shape.ApplicationId ?? string.Empty; }
            catch { return true; }
            return applicationId.StartsWith("JarviTools.", StringComparison.Ordinal);
        }

        internal static IList<string> CandidateCoverageLimitations()
        {
            return new[]
            {
                "净空候选按结构、MEP和常见实体障碍类别白名单收集；不代表扫描了模型中的全部类别。",
                "已覆盖普通柱、结构柱、通用模型、橱柜、楼板、屋面、结构梁及主要机电类别；维修路线与HandReach另行强制补充宿主和已加载链接墙体。",
                "ApplicationId 以 JarviTools. 开头的 DirectShape 属于分析展示产物，已显式排除，避免把旧结果反当障碍。"
            };
        }

        private static CandidateCollectionFailure CreateLinkCollectionFailure(
            Document hostDoc,
            RevitLinkInstance link,
            string reason)
        {
            string name;
            string uniqueId;
            try { name = link.Name ?? string.Empty; }
            catch { name = string.Empty; }
            try { uniqueId = link.UniqueId ?? string.Empty; }
            catch { uniqueId = string.Empty; }
            long linkId = link.Id.Value;
            string linkStableKey = string.IsNullOrWhiteSpace(uniqueId)
                ? "LINK:" + linkId + ":*"
                : "LUID:" + uniqueId + ":*";
            return new CandidateCollectionFailure
            {
                SourceKey = linkStableKey,
                Category = BuiltInCategory.INVALID,
                Reason = reason ?? string.Empty,
                Source = new PlenumSourceRef
                {
                    SourceType = "RevitLink",
                    DocumentTitle = hostDoc.Title,
                    LinkInstanceId = linkId,
                    LinkInstanceUniqueId = uniqueId,
                    ElementId = linkId,
                    UniqueId = uniqueId,
                    Category = "RevitLinkInstance",
                    Name = name,
                    BlockerKind = "CollectionCoverage"
                }
            };
        }

        private static List<FeatureSeed> BuildFeatureSeeds(List<Candidate> candidates, PlanarFace topFace,
            PlenumAnalysisConfig config)
        {
            var seeds = new List<FeatureSeed>();
            double spacingFt = config.FeatureSpacingMm / MmPerFoot;
            foreach (Candidate candidate in candidates.Where(IsFeatureCritical))
            {
                bool flatFloorOrRoof = IsFloorOrRoof(candidate)
                                       && !RequiresInteriorRefinement(candidate);
                if (flatFloorOrRoof)
                {
                    AddStructuralBoundarySeeds(candidate, topFace, spacingFt, seeds);
                    continue;
                }

                foreach (Solid solid in candidate.Solids.OrderByDescending(s => s.Volume).Take(5))
                {
                    try { AddFeatureSeed(candidate.ToHost.OfPoint(solid.ComputeCentroid()), candidate.SourceKey, topFace, seeds); }
                    catch { }
                }

                LocationCurve location = candidate.Element.Location as LocationCurve;
                if (location != null && location.Curve != null)
                {
                    double length;
                    try { length = location.Curve.Length; }
                    catch { length = 0.0; }
                    int count = Math.Max(1, Math.Min(1000, (int)Math.Ceiling(length / spacingFt)));
                    for (int i = 0; i <= count; i++)
                    {
                        double parameter = count == 0 ? 0.5 : (double)i / count;
                        try
                        {
                            XYZ point = location.Curve.Evaluate(parameter, true);
                            AddFeatureSeed(candidate.ToHost.OfPoint(point), candidate.SourceKey, topFace, seeds);
                        }
                        catch { }
                    }
                }

                XYZ center = new XYZ(
                    (candidate.WorldBounds.MinX + candidate.WorldBounds.MaxX) * 0.5,
                    (candidate.WorldBounds.MinY + candidate.WorldBounds.MaxY) * 0.5,
                    (candidate.WorldBounds.MinZ + candidate.WorldBounds.MaxZ) * 0.5);
                AddFeatureSeed(center, candidate.SourceKey, topFace, seeds);
            }
            return seeds;
        }

        private static List<FeatureRegion> BuildFeatureRegions(List<Candidate> candidates, PlanarFace topFace)
        {
            var regions = new List<FeatureRegion>();
            foreach (Candidate candidate in candidates.Where(IsFeatureCritical))
            {
                bool added = false;
                bool refineInterior = RequiresInteriorRefinement(candidate);
                // 水平恒高楼板/屋面使用真实底面边界种子，避免凸包把洞口填满。
                if (IsFloorOrRoof(candidate) && !refineInterior) continue;
                foreach (Solid solid in candidate.Solids)
                {
                    var projected = new List<UV>();
                    foreach (Face face in solid.Faces)
                    {
                        Mesh mesh;
                        try { mesh = face.Triangulate(0.5); }
                        catch { continue; }
                        if (mesh == null) continue;
                        foreach (XYZ vertex in mesh.Vertices)
                        {
                            XYZ world = candidate.ToHost.OfPoint(vertex);
                            projected.Add(WorldToUv(
                                new XYZ(world.X, world.Y, topFace.Origin.Z), topFace));
                        }
                    }
                    List<UV> hull = ConvexHull(projected);
                    if (hull.Count < 3) continue;
                    regions.Add(CreateFeatureRegion(
                        hull, candidate.SourceKey, refineInterior, true));
                    added = true;
                }

                if (added) continue;
                var fallback = new List<UV>
                {
                    WorldToUv(new XYZ(candidate.WorldBounds.MinX, candidate.WorldBounds.MinY, topFace.Origin.Z), topFace),
                    WorldToUv(new XYZ(candidate.WorldBounds.MaxX, candidate.WorldBounds.MinY, topFace.Origin.Z), topFace),
                    WorldToUv(new XYZ(candidate.WorldBounds.MaxX, candidate.WorldBounds.MaxY, topFace.Origin.Z), topFace),
                    WorldToUv(new XYZ(candidate.WorldBounds.MinX, candidate.WorldBounds.MaxY, topFace.Origin.Z), topFace)
                };
                regions.Add(CreateFeatureRegion(
                    ConvexHull(fallback), candidate.SourceKey, true, true));
            }
            return regions;
        }

        private static FeatureRegion CreateFeatureRegion(
            List<UV> polygon, string sourceKey, bool refineInterior, bool requireHit)
        {
            return new FeatureRegion
            {
                UMin = polygon.Min(p => p.U),
                UMax = polygon.Max(p => p.U),
                VMin = polygon.Min(p => p.V),
                VMax = polygon.Max(p => p.V),
                SourceKey = sourceKey,
                RefineInterior = refineInterior,
                RequireHit = requireHit,
                Polygon = polygon
            };
        }

        private static List<UV> ConvexHull(IEnumerable<UV> source)
        {
            const double tolerance = 1e-8;
            var sorted = source.OrderBy(p => p.U).ThenBy(p => p.V).ToList();
            var points = new List<UV>();
            foreach (UV point in sorted)
            {
                if (points.Count == 0
                    || Math.Abs(points[points.Count - 1].U - point.U) > tolerance
                    || Math.Abs(points[points.Count - 1].V - point.V) > tolerance)
                    points.Add(point);
            }
            if (points.Count <= 2) return points;

            var lower = new List<UV>();
            foreach (UV point in points)
            {
                while (lower.Count >= 2
                       && HullCross(lower[lower.Count - 2], lower[lower.Count - 1], point) <= tolerance)
                    lower.RemoveAt(lower.Count - 1);
                lower.Add(point);
            }
            var upper = new List<UV>();
            for (int i = points.Count - 1; i >= 0; i--)
            {
                UV point = points[i];
                while (upper.Count >= 2
                       && HullCross(upper[upper.Count - 2], upper[upper.Count - 1], point) <= tolerance)
                    upper.RemoveAt(upper.Count - 1);
                upper.Add(point);
            }
            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        private static double HullCross(UV a, UV b, UV c)
        {
            return (b.U - a.U) * (c.V - a.V) - (b.V - a.V) * (c.U - a.U);
        }

        private static bool IsFeatureCritical(Candidate candidate)
        {
            return candidate.State == PlenumState.MepOccupied
                   || candidate.State == PlenumState.Structure;
        }

        private static bool IsFloorOrRoof(Candidate candidate)
        {
            return candidate != null
                   && (candidate.Category == BuiltInCategory.OST_Floors
                       || candidate.Category == BuiltInCategory.OST_Roofs);
        }

        private static void AddStructuralBoundarySeeds(
            Candidate candidate,
            PlanarFace topFace,
            double spacingFt,
            List<FeatureSeed> seeds)
        {
            foreach (Solid solid in candidate.Solids)
            {
                foreach (Face face in solid.Faces)
                {
                    PlanarFace planar = face as PlanarFace;
                    if (planar == null) continue;
                    XYZ worldNormal;
                    try { worldNormal = candidate.ToHost.OfVector(planar.FaceNormal).Normalize(); }
                    catch { continue; }
                    if (worldNormal.Z > -0.999999) continue;

                    foreach (EdgeArray edgeLoop in planar.EdgeLoops)
                    {
                        foreach (Edge edge in edgeLoop)
                        {
                            Curve curve;
                            double length;
                            try
                            {
                                curve = edge.AsCurve();
                                length = curve.Length;
                            }
                            catch { continue; }
                            int count = Math.Max(1, Math.Min(2000,
                                (int)Math.Ceiling(length / Math.Max(spacingFt, 1e-6))));
                            for (int i = 0; i <= count; i++)
                            {
                                try
                                {
                                    XYZ sourcePoint = curve.Evaluate((double)i / count, true);
                                    AddFeatureSeed(
                                        candidate.ToHost.OfPoint(sourcePoint),
                                        candidate.SourceKey,
                                        topFace,
                                        seeds);
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
        }

        private static bool RequiresInteriorRefinement(Candidate candidate)
        {
            if (candidate == null || candidate.State != PlenumState.Structure) return false;
            if (candidate.Solids.Count == 0) return true;

            var undersideElevations = new List<double>();
            foreach (Solid solid in candidate.Solids)
            {
                bool foundHorizontalUnderside = false;
                foreach (Face face in solid.Faces)
                {
                    PlanarFace planar = face as PlanarFace;
                    if (planar == null)
                    {
                        try
                        {
                            BoundingBoxUV uvBounds = face.GetBoundingBox();
                            UV center = new UV(
                                (uvBounds.Min.U + uvBounds.Max.U) * 0.5,
                                (uvBounds.Min.V + uvBounds.Max.V) * 0.5);
                            XYZ sampledNormal = candidate.ToHost
                                .OfVector(face.ComputeNormal(center)).Normalize();
                            if (sampledNormal.Z < -0.001) return true;
                        }
                        catch { return true; }
                        continue;
                    }
                    XYZ worldNormal;
                    try { worldNormal = candidate.ToHost.OfVector(planar.FaceNormal).Normalize(); }
                    catch { return true; }
                    if (worldNormal.Z < -0.001 && worldNormal.Z > -0.999999) return true;
                    if (worldNormal.Z > -0.999999) continue;

                    foundHorizontalUnderside = true;
                    undersideElevations.Add(candidate.ToHost.OfPoint(planar.Origin).Z);
                }
                if (!foundHorizontalUnderside) return true;
            }

            if (undersideElevations.Count == 0) return true;
            double min = undersideElevations.Min();
            double max = undersideElevations.Max();
            return (max - min) * MmPerFoot > 1.0;
        }

        private static void AddFeatureSeed(XYZ worldPoint, string sourceKey, PlanarFace topFace,
            List<FeatureSeed> seeds)
        {
            UV uv;
            XYZ point;
            if (!TryProjectToTopFace(worldPoint, topFace, out uv, out point)) return;
            if (seeds.Any(s => HorizontalDistanceFt(s.Point, point) * MmPerFoot < 10.0)) return;
            seeds.Add(new FeatureSeed { Uv = uv, Point = point, SourceKey = sourceKey });
        }

        private static bool TryProjectToTopFace(XYZ worldPoint, PlanarFace topFace, out UV uv, out XYZ point)
        {
            XYZ onPlane = new XYZ(worldPoint.X, worldPoint.Y, topFace.Origin.Z);
            uv = WorldToUv(onPlane, topFace);
            if (!topFace.IsInside(uv))
            {
                point = XYZ.Zero;
                return false;
            }
            point = topFace.Evaluate(uv);
            return true;
        }

        private static UV WorldToUv(XYZ worldPoint, PlanarFace topFace)
        {
            XYZ delta = worldPoint - topFace.Origin;
            return new UV(delta.DotProduct(topFace.XVector), delta.DotProduct(topFace.YVector));
        }

        private static void BuildCells(PlanarFace face, double uMin, double uMax, double vMin, double vMax,
            int depth, List<FeatureSeed> seeds, List<FeatureRegion> regions,
            List<BoundarySegment> ceilingBoundary, List<UvTriangle> ceilingTriangles,
            double faceAreaScale, PlenumAnalysisConfig config, List<CellDraft> leaves)
        {
            List<UvTriangle> cellTriangles = ceilingTriangles
                .Where(x => TriangleOverlapsCell(x, uMin, uMax, vMin, vMax)).ToList();
            if (cellTriangles.Count == 0) return;
            UV representative;
            List<UV> pieceRepresentatives;
            double intersectionArea = IntersectFaceTriangles(
                cellTriangles, uMin, uMax, vMin, vMax,
                out representative, out pieceRepresentatives);
            double cellArea = (uMax - uMin) * (vMax - vMin);
            if (intersectionArea <= 1e-12 || cellArea <= 1e-12 || representative == null) return;
            double rawCoverage = intersectionArea / cellArea;
            if (rawCoverage < -1e-8 || rawCoverage > 1.0 + 1e-6)
                throw new InvalidOperationException(
                    "吊顶网格与分析单元的交叠面积超出数值允许范围，已停止分析。");
            double coverage = Math.Max(0.0, Math.Min(1.0, rawCoverage));
            UV centerUv = new UV((uMin + uMax) * 0.5, (vMin + vMax) * 0.5);
            if (coverage >= FullCoverageThreshold && face.IsInside(centerUv))
                representative = centerUv;
            List<UV> validRepresentatives = pieceRepresentatives
                .Where(face.IsInside).ToList();
            if (!face.IsInside(representative) || validRepresentatives.Count != pieceRepresentatives.Count)
                throw new InvalidOperationException(
                    "吊顶网格生成的采样代表点落在真实顶面之外，已停止分析。");

            double sizeMm = Math.Max(uMax - uMin, vMax - vMin) * MmPerFoot;
            bool refineBase = sizeMm > config.BaseCellMm;
            bool refineFeature = sizeMm > config.FeatureCellMm
                                 && (seeds.Count > 0
                                     || regions.Any(r => r.CrossesCellBoundary(
                                         uMin, uMax, vMin, vMax)));
            if ((refineBase || refineFeature) && depth < config.MaxDepth)
            {
                double um = (uMin + uMax) * 0.5;
                double vm = (vMin + vMax) * 0.5;
                var q00Seeds = seeds.Where(s => s.Uv.U < um && s.Uv.V < vm).ToList();
                var q10Seeds = seeds.Where(s => s.Uv.U >= um && s.Uv.V < vm).ToList();
                var q11Seeds = seeds.Where(s => s.Uv.U >= um && s.Uv.V >= vm).ToList();
                var q01Seeds = seeds.Where(s => s.Uv.U < um && s.Uv.V >= vm).ToList();
                BuildCells(face, uMin, um, vMin, vm, depth + 1, q00Seeds,
                    regions.Where(r => r.Overlaps(uMin, um, vMin, vm)).ToList(),
                    ceilingBoundary, cellTriangles, faceAreaScale, config, leaves);
                BuildCells(face, um, uMax, vMin, vm, depth + 1, q10Seeds,
                    regions.Where(r => r.Overlaps(um, uMax, vMin, vm)).ToList(),
                    ceilingBoundary, cellTriangles, faceAreaScale, config, leaves);
                BuildCells(face, um, uMax, vm, vMax, depth + 1, q11Seeds,
                    regions.Where(r => r.Overlaps(um, uMax, vm, vMax)).ToList(),
                    ceilingBoundary, cellTriangles, faceAreaScale, config, leaves);
                BuildCells(face, uMin, um, vm, vMax, depth + 1, q01Seeds,
                    regions.Where(r => r.Overlaps(uMin, um, vm, vMax)).ToList(),
                    ceilingBoundary, cellTriangles, faceAreaScale, config, leaves);
                return;
            }

            if (leaves.Count >= config.MaxCells)
                throw new InvalidOperationException(
                    "分析单元超过上限 " + config.MaxCells +
                    "。已提前终止，请增大基础/特征单元尺寸后重试。");
            leaves.Add(new CellDraft
            {
                Depth = depth,
                UMin = uMin,
                UMax = uMax,
                VMin = vMin,
                VMax = vMax,
                Coverage = coverage,
                AreaFt2 = intersectionArea * faceAreaScale,
                IsFullFootprintInsideCeiling = coverage >= FullCoverageThreshold
                    && !ceilingBoundary.Any(segment => SegmentCrossesCellInterior(
                        segment, uMin, uMax, vMin, vMax)),
                Center = face.Evaluate(representative),
                P00 = face.Evaluate(new UV(uMin, vMin)),
                P10 = face.Evaluate(new UV(uMax, vMin)),
                P11 = face.Evaluate(new UV(uMax, vMax)),
                P01 = face.Evaluate(new UV(uMin, vMax)),
                FaceProbePoints = coverage >= FullCoverageThreshold
                    ? new List<XYZ>()
                    : validRepresentatives.Select(face.Evaluate).ToList(),
                Seeds = seeds,
                Regions = regions
            });
        }

        private static List<UvTriangle> BuildFaceTriangles(
            PlanarFace face, out double areaScale, out double meshAreaFt2)
        {
            var triangles = new List<UvTriangle>();
            areaScale = face.XVector.CrossProduct(face.YVector).GetLength();
            meshAreaFt2 = 0.0;
            if (areaScale <= 1e-12) return triangles;
            Mesh mesh;
            try { mesh = face.Triangulate(1.0); }
            catch { return triangles; }
            if (mesh == null) return triangles;

            for (int i = 0; i < mesh.NumTriangles; i++)
            {
                MeshTriangle source = mesh.get_Triangle(i);
                UV a = WorldToUv(source.get_Vertex(0), face);
                UV b = WorldToUv(source.get_Vertex(1), face);
                UV c = WorldToUv(source.get_Vertex(2), face);
                double twiceArea = Math.Abs(
                    a.U * (b.V - c.V) + b.U * (c.V - a.V) + c.U * (a.V - b.V));
                if (twiceArea <= 1e-12) continue;
                meshAreaFt2 += twiceArea * 0.5 * areaScale;
                triangles.Add(new UvTriangle
                {
                    A = a,
                    B = b,
                    C = c,
                    UMin = Math.Min(a.U, Math.Min(b.U, c.U)),
                    UMax = Math.Max(a.U, Math.Max(b.U, c.U)),
                    VMin = Math.Min(a.V, Math.Min(b.V, c.V)),
                    VMax = Math.Max(a.V, Math.Max(b.V, c.V))
                });
            }
            return triangles;
        }

        private static bool HasOnlyStraightEdges(PlanarFace face)
        {
            foreach (EdgeArray loop in face.EdgeLoops)
            {
                foreach (Edge edge in loop)
                {
                    Curve curve;
                    try { curve = edge.AsCurve(); }
                    catch { return false; }
                    if (!(curve is Line)) return false;
                }
            }
            return true;
        }

        private static bool TriangleOverlapsCell(UvTriangle triangle,
            double uMin, double uMax, double vMin, double vMax)
        {
            const double tolerance = 1e-12;
            return triangle.UMax >= uMin - tolerance && triangle.UMin <= uMax + tolerance
                   && triangle.VMax >= vMin - tolerance && triangle.VMin <= vMax + tolerance;
        }

        private static double IntersectFaceTriangles(List<UvTriangle> triangles,
            double uMin, double uMax, double vMin, double vMax,
            out UV representative, out List<UV> pieceRepresentatives)
        {
            representative = null;
            pieceRepresentatives = new List<UV>();
            double totalArea = 0.0;
            double largestPieceArea = 0.0;
            const double tolerance = 1e-12;
            foreach (UvTriangle triangle in triangles)
            {
                if (triangle.UMax < uMin - tolerance || triangle.UMin > uMax + tolerance
                    || triangle.VMax < vMin - tolerance || triangle.VMin > vMax + tolerance)
                    continue;

                List<UV> clipped = ClipTriangleToRectangle(
                    triangle, uMin, uMax, vMin, vMax);
                if (clipped.Count < 3) continue;
                double area = PolygonArea(clipped);
                if (area <= tolerance) continue;
                UV pieceRepresentative = PolygonCentroid(clipped);
                pieceRepresentatives.Add(pieceRepresentative);
                totalArea += area;
                if (area > largestPieceArea)
                {
                    largestPieceArea = area;
                    representative = pieceRepresentative;
                }
            }
            return totalArea;
        }

        private static List<UV> ClipTriangleToRectangle(UvTriangle triangle,
            double uMin, double uMax, double vMin, double vMax)
        {
            var polygon = new List<UV> { triangle.A, triangle.B, triangle.C };
            polygon = ClipPolygonToAxis(polygon, true, uMin, true);
            polygon = ClipPolygonToAxis(polygon, true, uMax, false);
            polygon = ClipPolygonToAxis(polygon, false, vMin, true);
            polygon = ClipPolygonToAxis(polygon, false, vMax, false);
            return polygon;
        }

        private static List<UV> ClipPolygonToAxis(List<UV> input,
            bool useU, double boundary, bool keepGreater)
        {
            var output = new List<UV>();
            if (input == null || input.Count == 0) return output;
            UV previous = input[input.Count - 1];
            bool previousInside = IsInsideClipBoundary(previous, useU, boundary, keepGreater);
            foreach (UV current in input)
            {
                bool currentInside = IsInsideClipBoundary(current, useU, boundary, keepGreater);
                if (currentInside != previousInside)
                    output.Add(IntersectClipBoundary(previous, current, useU, boundary));
                if (currentInside) output.Add(current);
                previous = current;
                previousInside = currentInside;
            }
            return output;
        }

        private static bool IsInsideClipBoundary(UV point,
            bool useU, double boundary, bool keepGreater)
        {
            double value = useU ? point.U : point.V;
            const double tolerance = 1e-10;
            return keepGreater ? value >= boundary - tolerance : value <= boundary + tolerance;
        }

        private static UV IntersectClipBoundary(UV a, UV b, bool useU, double boundary)
        {
            double aValue = useU ? a.U : a.V;
            double bValue = useU ? b.U : b.V;
            double denominator = bValue - aValue;
            if (Math.Abs(denominator) <= 1e-15) return a;
            double t = Math.Max(0.0, Math.Min(1.0, (boundary - aValue) / denominator));
            return new UV(a.U + (b.U - a.U) * t, a.V + (b.V - a.V) * t);
        }

        private static double PolygonArea(List<UV> polygon)
        {
            double twiceArea = 0.0;
            for (int i = 0; i < polygon.Count; i++)
            {
                UV a = polygon[i];
                UV b = polygon[(i + 1) % polygon.Count];
                twiceArea += a.U * b.V - b.U * a.V;
            }
            return Math.Abs(twiceArea) * 0.5;
        }

        private static UV PolygonCentroid(List<UV> polygon)
        {
            double twiceArea = 0.0;
            double uSum = 0.0;
            double vSum = 0.0;
            for (int i = 0; i < polygon.Count; i++)
            {
                UV a = polygon[i];
                UV b = polygon[(i + 1) % polygon.Count];
                double cross = a.U * b.V - b.U * a.V;
                twiceArea += cross;
                uSum += (a.U + b.U) * cross;
                vSum += (a.V + b.V) * cross;
            }
            if (Math.Abs(twiceArea) <= 1e-15)
                return new UV(polygon.Average(x => x.U), polygon.Average(x => x.V));
            return new UV(uSum / (3.0 * twiceArea), vSum / (3.0 * twiceArea));
        }

        private static List<BoundarySegment> BuildFaceBoundarySegments(PlanarFace face)
        {
            var segments = new List<BoundarySegment>();
            foreach (EdgeArray loop in face.EdgeLoops)
            {
                foreach (Edge edge in loop)
                {
                    IList<XYZ> points;
                    try { points = edge.Tessellate(); }
                    catch { continue; }
                    for (int i = 1; i < points.Count; i++)
                    {
                        segments.Add(new BoundarySegment
                        {
                            Start = WorldToUv(points[i - 1], face),
                            End = WorldToUv(points[i], face)
                        });
                    }
                }
            }
            return segments;
        }

        private static bool SegmentCrossesCellInterior(
            BoundarySegment segment, double uMin, double uMax, double vMin, double vMax)
        {
            // 缩进 0.1 mm：边界恰好与单元边重合不算穿过；进入单元内部才排除候选体块。
            double inset = 0.1 / MmPerFoot;
            double innerUMin = uMin + inset;
            double innerUMax = uMax - inset;
            double innerVMin = vMin + inset;
            double innerVMax = vMax - inset;
            if (innerUMin >= innerUMax || innerVMin >= innerVMax) return true;

            UV a = segment.Start;
            UV b = segment.End;
            if (PointInClosedRect(a, innerUMin, innerUMax, innerVMin, innerVMax)
                || PointInClosedRect(b, innerUMin, innerUMax, innerVMin, innerVMax))
                return true;

            var corners = new[]
            {
                new UV(innerUMin, innerVMin), new UV(innerUMax, innerVMin),
                new UV(innerUMax, innerVMax), new UV(innerUMin, innerVMax)
            };
            for (int i = 0; i < corners.Length; i++)
            {
                UV ignored;
                if (TrySegmentIntersection(a, b, corners[i], corners[(i + 1) % corners.Length], out ignored))
                    return true;
            }
            return false;
        }

        private static bool PointInClosedRect(
            UV point, double uMin, double uMax, double vMin, double vMax)
        {
            return point.U >= uMin && point.U <= uMax
                   && point.V >= vMin && point.V <= vMax;
        }

        private static bool TrySegmentIntersection(UV a, UV b, UV c, UV d, out UV point)
        {
            point = null;
            double rU = b.U - a.U;
            double rV = b.V - a.V;
            double sU = d.U - c.U;
            double sV = d.V - c.V;
            double denominator = rU * sV - rV * sU;
            if (Math.Abs(denominator) < 1e-12) return false;
            double cmaU = c.U - a.U;
            double cmaV = c.V - a.V;
            double t = (cmaU * sV - cmaV * sU) / denominator;
            double u = (cmaU * rV - cmaV * rU) / denominator;
            const double tolerance = 1e-8;
            if (t < -tolerance || t > 1.0 + tolerance || u < -tolerance || u > 1.0 + tolerance)
                return false;
            point = new UV(a.U + t * rU, a.V + t * rV);
            return true;
        }

        private static ProbeOutcome AnalyzeProbe(XYZ point, double topZ, double searchTop,
            List<Candidate> candidates, HashSet<string> coveredKeys)
        {
            XYZ start = new XYZ(point.X, point.Y, topZ + IntersectionToleranceFt);
            XYZ end = new XYZ(point.X, point.Y, searchTop);
            var hits = new List<Hit>();
            var warnings = new List<string>();
            var hitKeys = new HashSet<string>(StringComparer.Ordinal);
            var unknownCandidateKeys = new HashSet<string>(StringComparer.Ordinal);
            bool unsupportedAtProbe = false;

            foreach (Candidate candidate in candidates)
            {
                if (!candidate.WorldBounds.ContainsVertical(point.X, point.Y, start.Z, end.Z, IntersectionToleranceFt))
                    continue;
                if (candidate.Solids.Count == 0)
                {
                    unsupportedAtProbe = true;
                    unknownCandidateKeys.Add(candidate.SourceKey);
                    warnings.Add(candidate.Source.Category + " " + candidate.Source.ElementId + " 无可求交 Solid");
                    continue;
                }
                if (candidate.MeshCount > 0 || !string.IsNullOrEmpty(candidate.GeometryError))
                {
                    unsupportedAtProbe = true;
                    unknownCandidateKeys.Add(candidate.SourceKey);
                    warnings.Add(candidate.Source.Category + " " + candidate.Source.ElementId +
                                 " 几何不完整: " + (candidate.GeometryError ?? ("Mesh " + candidate.MeshCount)));
                }

                Line sourceLine = Line.CreateBound(candidate.FromHost.OfPoint(start), candidate.FromHost.OfPoint(end));
                bool candidateHit = false;
                foreach (Solid solid in candidate.Solids)
                {
                    try
                    {
                        using (var options = new SolidCurveIntersectionOptions
                        {
                            ResultType = SolidCurveIntersectionMode.CurveSegmentsInside
                        })
                        using (SolidCurveIntersection intersection = solid.IntersectWithCurve(sourceLine, options))
                        {
                            for (int i = 0; i < intersection.SegmentCount; i++)
                            {
                                Curve segment = intersection.GetCurveSegment(i);
                                XYZ a = candidate.ToHost.OfPoint(segment.GetEndPoint(0));
                                XYZ b = candidate.ToHost.OfPoint(segment.GetEndPoint(1));
                                double zMin = Math.Max(topZ, Math.Min(a.Z, b.Z));
                                double zMax = Math.Min(searchTop, Math.Max(a.Z, b.Z));
                                if (zMax - zMin <= 1e-7) continue;
                                hits.Add(new Hit
                                {
                                    ZMin = zMin,
                                    ZMax = zMax,
                                    State = candidate.State,
                                    Source = candidate.Source,
                                    SourceKey = candidate.SourceKey
                                });
                                candidateHit = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        unsupportedAtProbe = true;
                        unknownCandidateKeys.Add(candidate.SourceKey);
                        warnings.Add(candidate.Source.Category + " " + candidate.Source.ElementId + " 求交失败: " + ex.GetType().Name);
                    }
                }
                if (candidateHit)
                {
                    coveredKeys.Add(candidate.SourceKey);
                    hitKeys.Add(candidate.SourceKey);
                }
            }

            Hit structure = hits
                .Where(h => h.State == PlenumState.Structure
                            && h.ZMax > topZ + IntersectionToleranceFt)
                .OrderBy(h => h.ZMin).FirstOrDefault();
            double mepUpperZ = structure == null ? searchTop : structure.ZMin;
            List<Hit> mepHits = hits.Where(h => h.State == PlenumState.MepOccupied
                                                && h.ZMax > topZ + IntersectionToleranceFt
                                                && h.ZMin < mepUpperZ - IntersectionToleranceFt)
                .OrderBy(h => h.ZMin).ToList();
            Hit observedBlocker = mepHits.FirstOrDefault();
            if (observedBlocker == null) observedBlocker = structure;

            if (unsupportedAtProbe)
            {
                return new ProbeOutcome
                {
                    Point = point,
                    Unknown = true,
                    StructureHeightFt = structure == null
                        ? double.NaN
                        : Math.Max(0.0, structure.ZMin - topZ),
                    FirstBlocker = observedBlocker == null ? null : observedBlocker.Source,
                    FirstBlockerKey = observedBlocker == null ? null : observedBlocker.SourceKey,
                    StructureHit = structure,
                    MepHits = mepHits,
                    Warning = string.Join("; ", warnings.Distinct()),
                    HitKeys = hitKeys,
                    UnknownCandidateKeys = unknownCandidateKeys
                };
            }

            if (structure == null)
            {
                return new ProbeOutcome
                {
                    Point = point,
                    Unknown = true,
                    StructureHeightFt = double.NaN,
                    FirstBlocker = observedBlocker == null ? null : observedBlocker.Source,
                    FirstBlockerKey = observedBlocker == null ? null : observedBlocker.SourceKey,
                    MepHits = mepHits,
                    Warning = "未找到结构上界",
                    HitKeys = hitKeys,
                    UnknownCandidateKeys = unknownCandidateKeys
                };
            }

            Hit mep = mepHits.FirstOrDefault();
            Hit blocker = mep != null && mep.ZMin < structure.ZMin ? mep : structure;
            return new ProbeOutcome
            {
                Point = point,
                Unknown = false,
                ConnectedHeightFt = Math.Max(0.0, blocker.ZMin - topZ),
                StructureHeightFt = Math.Max(0.0, structure.ZMin - topZ),
                FirstBlocker = blocker.Source,
                FirstBlockerKey = blocker.SourceKey,
                StructureHit = structure,
                Warning = string.Join("; ", warnings.Distinct()),
                MepHits = mepHits,
                HitKeys = hitKeys,
                UnknownCandidateKeys = unknownCandidateKeys
            };
        }

        private static List<PlenumVerticalInterval> BuildCellIntervals(
            double topZ,
            double searchTop,
            double structureHeightFt,
            List<Hit> mepHits,
            List<Hit> structureHits)
        {
            double boundaryZ = topZ + Math.Max(0.0, structureHeightFt);
            var clipped = mepHits
                .Where(h => h.ZMax > topZ + 1e-9 && h.ZMin < boundaryZ - 1e-9)
                .OrderBy(h => h.ZMin)
                .ToList();
            var merged = new List<MergedHit>();
            foreach (Hit hit in clipped)
            {
                double zMin = Math.Max(topZ, hit.ZMin);
                double zMax = Math.Min(boundaryZ, hit.ZMax);
                if (zMax - zMin <= 1e-9) continue;
                MergedHit current = merged.LastOrDefault();
                if (current != null && zMin <= current.ZMax + IntersectionToleranceFt)
                {
                    current.ZMax = Math.Max(current.ZMax, zMax);
                    if (!current.Sources.Any(s => SameSource(s, hit.Source))) current.Sources.Add(hit.Source);
                }
                else
                {
                    merged.Add(new MergedHit
                    {
                        ZMin = zMin,
                        ZMax = zMax,
                        Sources = new List<PlenumSourceRef> { hit.Source }
                    });
                }
            }

            var intervals = new List<PlenumVerticalInterval>();
            double cursor = topZ;
            foreach (MergedHit hit in merged)
            {
                if (hit.ZMin > cursor + 1e-9)
                {
                    intervals.Add(new PlenumVerticalInterval
                    {
                        State = "Free",
                        StartHeightFt = cursor - topZ,
                        EndHeightFt = hit.ZMin - topZ
                    });
                }
                intervals.Add(new PlenumVerticalInterval
                {
                    State = "MepOccupied",
                    StartHeightFt = Math.Max(0.0, hit.ZMin - topZ),
                    EndHeightFt = Math.Max(0.0, hit.ZMax - topZ),
                    Sources = hit.Sources
                });
                cursor = Math.Max(cursor, hit.ZMax);
            }
            if (boundaryZ > cursor + 1e-9)
            {
                intervals.Add(new PlenumVerticalInterval
                {
                    State = "Free",
                    StartHeightFt = cursor - topZ,
                    EndHeightFt = boundaryZ - topZ
                });
            }

            List<Hit> boundaryStructures = (structureHits ?? new List<Hit>())
                .Where(h => h != null && h.ZMin <= boundaryZ + IntersectionToleranceFt
                            && h.ZMax > boundaryZ + 1e-9)
                .ToList();
            if (boundaryStructures.Count > 0)
            {
                double structureEndZ = Math.Min(
                    searchTop,
                    boundaryStructures.Max(h => h.ZMax));
                if (structureEndZ > boundaryZ + 1e-9)
                {
                    var sources = new List<PlenumSourceRef>();
                    foreach (Hit hit in boundaryStructures)
                    {
                        if (!sources.Any(s => SameSource(s, hit.Source))) sources.Add(hit.Source);
                    }
                    intervals.Add(new PlenumVerticalInterval
                    {
                        State = "Structure",
                        StartHeightFt = boundaryZ - topZ,
                        EndHeightFt = structureEndZ - topZ,
                        Sources = sources
                    });
                }
            }
            return intervals;
        }

        private static FreeEnvelopeValidationState ValidateFreeEnvelope(
            CellDraft draft,
            List<PlenumVerticalInterval> intervals,
            List<Candidate> candidates,
            out string conflictKey,
            out string conflictReason)
        {
            conflictKey = null;
            conflictReason = null;
            if (draft == null || intervals == null || candidates == null)
                return FreeEnvelopeValidationState.Unverified;

            foreach (PlenumVerticalInterval interval in intervals.Where(x =>
                         string.Equals(x.State, "Free", StringComparison.Ordinal)
                         && x.ThicknessFt > 1e-9))
            {
                double heightFt = Math.Min(interval.ThicknessFt, 10000.0 / MmPerFoot);
                if (heightFt < 1.0 / MmPerFoot) heightFt = 1.0 / MmPerFoot;
                double startHeightFt = Math.Max(0.0, interval.StartHeightFt);
                double startZ = draft.P00.Z + startHeightFt;
                var envelopeBounds = new Bounds3
                {
                    MinX = new[] { draft.P00.X, draft.P10.X, draft.P11.X, draft.P01.X }.Min(),
                    MinY = new[] { draft.P00.Y, draft.P10.Y, draft.P11.Y, draft.P01.Y }.Min(),
                    MinZ = startZ,
                    MaxX = new[] { draft.P00.X, draft.P10.X, draft.P11.X, draft.P01.X }.Max(),
                    MaxY = new[] { draft.P00.Y, draft.P10.Y, draft.P11.Y, draft.P01.Y }.Max(),
                    MaxZ = startZ + heightFt
                };

                Solid hostEnvelope = null;
                foreach (Candidate candidate in candidates.Where(x =>
                             BoundsOverlap(x.WorldBounds, envelopeBounds)))
                {
                    conflictKey = candidate.SourceKey;
                    if (candidate.Solids.Count == 0
                        || candidate.MeshCount > 0
                        || !string.IsNullOrEmpty(candidate.GeometryError)
                        || candidate.WorldSolidBounds.Count != candidate.Solids.Count
                        || candidate.WorldSolidBounds.Any(x => x == null))
                    {
                        conflictReason = "candidate geometry is not fully verifiable";
                        return FreeEnvelopeValidationState.Unverified;
                    }

                    if (hostEnvelope == null)
                    {
                        hostEnvelope = CreateCellPrism(draft, startHeightFt, heightFt);
                        if (hostEnvelope == null)
                        {
                            conflictReason = "free prism creation failed";
                            return FreeEnvelopeValidationState.Unverified;
                        }
                    }

                    Solid sourceEnvelope;
                    try
                    {
                        sourceEnvelope = candidate.FromHost.IsIdentity
                            ? hostEnvelope
                            : SolidUtils.CreateTransformed(hostEnvelope, candidate.FromHost);
                    }
                    catch
                    {
                        conflictReason = "free prism transform failed";
                        return FreeEnvelopeValidationState.Unverified;
                    }

                    for (int solidIndex = 0; solidIndex < candidate.Solids.Count; solidIndex++)
                    {
                        Bounds3 solidBounds = candidate.WorldSolidBounds[solidIndex];
                        if (solidBounds == null || !BoundsOverlap(solidBounds, envelopeBounds)) continue;
                        try
                        {
                            Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                                sourceEnvelope,
                                candidate.Solids[solidIndex],
                                BooleanOperationsType.Intersect);
                            if (intersection != null && intersection.Volume > 1e-9)
                            {
                                conflictReason = "blocker intersects proposed free prism";
                                return FreeEnvelopeValidationState.Conflict;
                            }
                        }
                        catch
                        {
                            conflictReason = "exact blocker/free-prism intersection failed";
                            return FreeEnvelopeValidationState.Unverified;
                        }
                    }
                }
            }

            conflictKey = null;
            conflictReason = null;
            return FreeEnvelopeValidationState.Clear;
        }

        private static Solid CreateCellPrism(
            CellDraft draft,
            double startHeightFt,
            double requestedHeightFt)
        {
            return PlenumCellSolidFactory.Create(
                draft.P00,
                draft.P10,
                draft.P11,
                draft.P01,
                startHeightFt,
                requestedHeightFt);
        }

        private static List<PlenumVerticalInterval> UnknownIntervals(double searchHeightFt)
        {
            return new List<PlenumVerticalInterval>
            {
                new PlenumVerticalInterval
                {
                    State = "Unknown",
                    StartHeightFt = 0.0,
                    EndHeightFt = Math.Max(0.0, searchHeightFt)
                }
            };
        }

        private static List<PlenumVerticalInterval> BuildObservedEvidenceIntervals(
            double topZ,
            double searchTop,
            List<Hit> mepHits,
            Hit structureHit)
        {
            var sourceHits = new List<Hit>();
            if (mepHits != null) sourceHits.AddRange(mepHits);
            if (structureHit != null) sourceHits.Add(structureHit);
            var result = new List<PlenumVerticalInterval>();
            foreach (Hit hit in sourceHits.OrderBy(h => h.ZMin).ThenBy(h => h.State))
            {
                double start = Math.Max(topZ, hit.ZMin) - topZ;
                double end = Math.Min(searchTop, hit.ZMax) - topZ;
                if (end - start <= 1e-9) continue;
                string state = hit.State == PlenumState.Structure
                    ? "Structure"
                    : "MepOccupied";
                PlenumVerticalInterval current = result.LastOrDefault();
                if (current != null
                    && string.Equals(current.State, state, StringComparison.Ordinal)
                    && start <= current.EndHeightFt + IntersectionToleranceFt)
                {
                    current.EndHeightFt = Math.Max(current.EndHeightFt, end);
                    if (!current.Sources.Any(s => SameSource(s, hit.Source)))
                        current.Sources.Add(hit.Source);
                }
                else
                {
                    result.Add(new PlenumVerticalInterval
                    {
                        State = state,
                        StartHeightFt = Math.Max(0.0, start),
                        EndHeightFt = Math.Max(0.0, end),
                        Sources = new List<PlenumSourceRef> { hit.Source }
                    });
                }
            }
            return result;
        }

        private static bool ProfilesDiffer(
            List<PlenumProbeProfile> profiles,
            double conservativeStructureBoundaryHeightFt)
        {
            const double toleranceFt = 20.0 / MmPerFoot;
            var signatures = (profiles ?? new List<PlenumProbeProfile>())
                .Select(profile => new PlenumProfileSignature
                {
                    IsUnknown = profile == null || profile.IsUnknown,
                    MepOccupiedRanges = profile == null
                        ? new List<PlenumOccupancyRange>()
                        : profile.VerticalIntervals
                            .Where(interval => string.Equals(
                                interval.State,
                                "MepOccupied",
                                StringComparison.Ordinal))
                            .Select(interval => new PlenumOccupancyRange(
                                interval.StartHeightFt,
                                interval.EndHeightFt))
                            .ToList()
                })
                .ToList();

            // The cell envelope already stops at the lowest observed structure boundary
            // and unions every observed MEP hit below it. Structure variation above that
            // conservative boundary must not turn an otherwise known cell into Mixed.
            return PlenumProfileClassifier.ProfilesDiffer(
                signatures,
                conservativeStructureBoundaryHeightFt,
                toleranceFt);
        }

        private static bool SameSource(PlenumSourceRef a, PlenumSourceRef b)
        {
            return a != null && b != null
                   && a.ElementId == b.ElementId
                   && a.LinkInstanceId == b.LinkInstanceId
                   && string.Equals(a.DocumentTitle, b.DocumentTitle, StringComparison.Ordinal);
        }

        private sealed class GeometryExtraction
        {
            public readonly List<Solid> Solids = new List<Solid>();
            public int MeshCount;
            public string Error;
        }

        private static GeometryExtraction ExtractGeometry(Element element)
        {
            var result = new GeometryExtraction();
            var options = new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine
            };
            GeometryElement geometry;
            try { geometry = element.get_Geometry(options); }
            catch (Exception ex)
            {
                result.Error = ex.GetType().Name + ": " + ex.Message;
                return result;
            }
            if (geometry == null)
            {
                result.Error = "GeometryElement is null";
                return result;
            }

            var stack = new Stack<GeometryObject>();
            foreach (GeometryObject item in geometry) stack.Push(item);
            while (stack.Count > 0)
            {
                GeometryObject item = stack.Pop();
                Solid solid = item as Solid;
                if (solid != null)
                {
                    if (solid.Volume > 1e-9 && solid.Faces.Size > 0) result.Solids.Add(solid);
                    continue;
                }
                GeometryInstance instance = item as GeometryInstance;
                if (instance != null)
                {
                    GeometryElement instanceGeometry;
                    try { instanceGeometry = instance.GetInstanceGeometry(); }
                    catch (Exception ex)
                    {
                        result.Error = ex.GetType().Name + ": " + ex.Message;
                        continue;
                    }
                    foreach (GeometryObject child in instanceGeometry) stack.Push(child);
                    continue;
                }
                Mesh mesh = item as Mesh;
                if (mesh != null) result.MeshCount++;
            }
            if (result.Solids.Count == 0 && string.IsNullOrEmpty(result.Error))
                result.Error = result.MeshCount > 0 ? "Mesh-only geometry" : "No solid geometry";
            return result;
        }

        private static Bounds3 WorldBounds(BoundingBoxXYZ bb, Transform toHost)
        {
            var bounds = new Bounds3
            {
                MinX = double.MaxValue,
                MinY = double.MaxValue,
                MinZ = double.MaxValue,
                MaxX = double.MinValue,
                MaxY = double.MinValue,
                MaxZ = double.MinValue
            };
            for (int i = 0; i < 8; i++)
            {
                double x = (i & 1) == 0 ? bb.Min.X : bb.Max.X;
                double y = (i & 2) == 0 ? bb.Min.Y : bb.Max.Y;
                double z = (i & 4) == 0 ? bb.Min.Z : bb.Max.Z;
                XYZ sourcePoint = bb.Transform.OfPoint(new XYZ(x, y, z));
                XYZ p = toHost.OfPoint(sourcePoint);
                bounds.MinX = Math.Min(bounds.MinX, p.X);
                bounds.MinY = Math.Min(bounds.MinY, p.Y);
                bounds.MinZ = Math.Min(bounds.MinZ, p.Z);
                bounds.MaxX = Math.Max(bounds.MaxX, p.X);
                bounds.MaxY = Math.Max(bounds.MaxY, p.Y);
                bounds.MaxZ = Math.Max(bounds.MaxZ, p.Z);
            }
            return bounds;
        }

        private static Outline TransformOutline(Bounds3 hostBounds, Transform fromHost)
        {
            var points = new List<XYZ>();
            for (int i = 0; i < 8; i++)
            {
                double x = (i & 1) == 0 ? hostBounds.MinX : hostBounds.MaxX;
                double y = (i & 2) == 0 ? hostBounds.MinY : hostBounds.MaxY;
                double z = (i & 4) == 0 ? hostBounds.MinZ : hostBounds.MaxZ;
                points.Add(fromHost.OfPoint(new XYZ(x, y, z)));
            }
            return new Outline(
                new XYZ(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)),
                new XYZ(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z)));
        }

        private static bool BoundsOverlap(Bounds3 a, Bounds3 b)
        {
            return a.MaxX >= b.MinX && a.MinX <= b.MaxX
                && a.MaxY >= b.MinY && a.MinY <= b.MaxY
                && a.MaxZ >= b.MinZ && a.MinZ <= b.MaxZ;
        }

        private static double HorizontalDistanceFt(XYZ a, XYZ b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static List<BuiltInCategory> StructureCategories()
        {
            return new List<BuiltInCategory>
            {
                BuiltInCategory.OST_Floors,
                BuiltInCategory.OST_Roofs,
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_Columns
            };
        }

        private static List<BuiltInCategory> MepCategories()
        {
            return new List<BuiltInCategory>
            {
                BuiltInCategory.OST_DuctCurves,
                BuiltInCategory.OST_FlexDuctCurves,
                BuiltInCategory.OST_DuctFitting,
                BuiltInCategory.OST_DuctAccessory,
                BuiltInCategory.OST_DuctTerminal,
                BuiltInCategory.OST_DuctInsulations,
                BuiltInCategory.OST_DuctLinings,
                BuiltInCategory.OST_PipeCurves,
                BuiltInCategory.OST_FlexPipeCurves,
                BuiltInCategory.OST_PipeFitting,
                BuiltInCategory.OST_PipeAccessory,
                BuiltInCategory.OST_PipeInsulations,
                BuiltInCategory.OST_CableTray,
                BuiltInCategory.OST_CableTrayFitting,
                BuiltInCategory.OST_Conduit,
                BuiltInCategory.OST_ConduitFitting,
                BuiltInCategory.OST_MechanicalEquipment,
                BuiltInCategory.OST_ElectricalEquipment,
                BuiltInCategory.OST_ElectricalFixtures,
                BuiltInCategory.OST_LightingFixtures,
                BuiltInCategory.OST_PlumbingFixtures,
                BuiltInCategory.OST_Sprinklers,
                BuiltInCategory.OST_SpecialityEquipment,
                BuiltInCategory.OST_GenericModel,
                BuiltInCategory.OST_Casework,
                BuiltInCategory.OST_FabricationDuctwork,
                BuiltInCategory.OST_FabricationPipework,
                BuiltInCategory.OST_FabricationContainment,
                BuiltInCategory.OST_FabricationHangers,
                BuiltInCategory.OST_FabricationDuctworkInsulation,
                BuiltInCategory.OST_FabricationPipeworkInsulation,
                BuiltInCategory.OST_FabricationDuctworkLining,
                BuiltInCategory.OST_FabricationDuctworkStiffeners
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using JarviTools.Commands.Plenum;

namespace JarviTools.Commands.MaintenanceReachability
{
    /// <summary>
    /// Route and HandReach share this explicit wall-obstacle pass.  Walls remain
    /// outside the general plenum product's category contract, but they are mandatory
    /// evidence for maintenance-route feasibility.
    /// </summary>
    internal static class MaintenanceWallObstacleCollector
    {
        private const double Epsilon = 1e-9;

        private sealed class GeometryExtraction
        {
            public readonly List<Solid> Solids = new List<Solid>();
            public int MeshCount;
            public string Error = string.Empty;
        }

        internal static List<PlenumAnalysisService.Candidate> Collect(
            Document hostDocument,
            PlenumAnalysisService.Bounds3 hostRoi,
            ICollection<PlenumAnalysisService.CandidateCollectionFailure> failures)
        {
            return Collect(hostDocument, hostRoi, failures, null);
        }

        internal static List<PlenumAnalysisService.Candidate> Collect(
            Document hostDocument,
            PlenumAnalysisService.Bounds3 hostRoi,
            ICollection<PlenumAnalysisService.CandidateCollectionFailure> failures,
            MaintenanceLinkScopeSnapshot linkScope)
        {
            if (hostDocument == null) throw new ArgumentNullException("hostDocument");
            if (hostRoi == null) throw new ArgumentNullException("hostRoi");
            if (failures == null) throw new ArgumentNullException("failures");

            var output = new List<PlenumAnalysisService.Candidate>();
            AddCandidates(
                hostDocument,
                null,
                Transform.Identity,
                hostRoi,
                output,
                failures);

            IEnumerable<RevitLinkInstance> links;
            try
            {
                links = new FilteredElementCollector(hostDocument)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .ToList();
            }
            catch (Exception exception)
            {
                failures.Add(CreateScanFailure(
                    hostDocument,
                    null,
                    "wall link-instance collection unavailable: " +
                    exception.GetType().Name));
                return output;
            }

            foreach (RevitLinkInstance link in links)
            {
                string linkUniqueId = TryGet(() => link.UniqueId);
                if (linkScope != null &&
                    !linkScope.Includes(link.Id.Value, linkUniqueId))
                    continue;
                string overlapReason;
                bool mayOverlap = LinkMayOverlapRoi(link, hostRoi, out overlapReason);
                Document linkDocument;
                try { linkDocument = link.GetLinkDocument(); }
                catch (Exception exception)
                {
                    if (mayOverlap)
                        failures.Add(CreateScanFailure(
                            hostDocument,
                            link,
                            "wall link document unavailable: " +
                            exception.GetType().Name));
                    continue;
                }
                if (linkDocument == null)
                {
                    if (mayOverlap)
                        failures.Add(CreateScanFailure(
                            hostDocument,
                            link,
                            string.IsNullOrWhiteSpace(overlapReason)
                                ? "wall link document is unloaded"
                                : "wall link document is unloaded; " + overlapReason));
                    continue;
                }

                Transform toHost;
                try { toHost = link.GetTotalTransform(); }
                catch (Exception exception)
                {
                    if (mayOverlap)
                        failures.Add(CreateScanFailure(
                            hostDocument,
                            link,
                            "wall link transform unavailable: " +
                            exception.GetType().Name));
                    continue;
                }
                if (toHost == null)
                {
                    if (mayOverlap)
                        failures.Add(CreateScanFailure(
                            hostDocument,
                            link,
                            "wall link transform is null"));
                    continue;
                }
                AddCandidates(
                    linkDocument,
                    link,
                    toHost,
                    hostRoi,
                    output,
                    failures);
            }

            return output
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.SourceKey))
                .GroupBy(x => x.SourceKey, StringComparer.Ordinal)
                .Select(x => x.First())
                .ToList();
        }

        private static void AddCandidates(
            Document sourceDocument,
            RevitLinkInstance link,
            Transform toHost,
            PlenumAnalysisService.Bounds3 hostRoi,
            ICollection<PlenumAnalysisService.Candidate> output,
            ICollection<PlenumAnalysisService.CandidateCollectionFailure> failures)
        {
            if (sourceDocument == null || toHost == null || hostRoi == null) return;
            Transform fromHost;
            PlenumAnalysisService.Bounds3 sourceRoi;
            try
            {
                fromHost = toHost.Inverse;
                sourceRoi = TransformBounds(hostRoi, fromHost);
            }
            catch (Exception exception)
            {
                failures.Add(CreateScanFailure(
                    sourceDocument,
                    link,
                    "wall ROI transform unavailable: " + exception.GetType().Name));
                return;
            }
            if (sourceRoi == null)
            {
                failures.Add(CreateScanFailure(
                    sourceDocument,
                    link,
                    "wall ROI transform returned no bounds"));
                return;
            }

            IList<Element> elements;
            try
            {
                var outline = new Outline(
                    new XYZ(sourceRoi.MinX, sourceRoi.MinY, sourceRoi.MinZ),
                    new XYZ(sourceRoi.MaxX, sourceRoi.MaxY, sourceRoi.MaxZ));
                elements = new FilteredElementCollector(sourceDocument)
                    .OfCategory(BuiltInCategory.OST_Walls)
                    .WhereElementIsNotElementType()
                    .WherePasses(new BoundingBoxIntersectsFilter(outline))
                    .ToElements();
            }
            catch (Exception exception)
            {
                failures.Add(CreateScanFailure(
                    sourceDocument,
                    link,
                    "wall element collection unavailable: " +
                    exception.GetType().Name));
                return;
            }

            foreach (Element element in elements)
            {
                PlenumSourceRef source = CreateSource(sourceDocument, link, element);
                string key = BuildSourceKey(link, element, source);
                if (!key.StartsWith("HUID:", StringComparison.Ordinal) &&
                    !key.StartsWith("LUID:", StringComparison.Ordinal))
                    failures.Add(CreateFailure(
                        key,
                        source,
                        "wall persistent UniqueId unavailable; numeric identity is legacy-only"));

                BoundingBoxXYZ boundingBox;
                try { boundingBox = element.get_BoundingBox(null); }
                catch (Exception exception)
                {
                    failures.Add(CreateFailure(
                        key,
                        source,
                        "wall bounding box unavailable: " +
                        exception.GetType().Name));
                    continue;
                }
                if (boundingBox == null)
                {
                    failures.Add(CreateFailure(
                        key,
                        source,
                        "wall bounding box is null after ROI filter"));
                    continue;
                }

                PlenumAnalysisService.Bounds3 worldBounds;
                try { worldBounds = WorldBounds(boundingBox, toHost); }
                catch (Exception exception)
                {
                    failures.Add(CreateFailure(
                        key,
                        source,
                        "wall bounding box transform unavailable: " +
                        exception.GetType().Name));
                    continue;
                }
                if (worldBounds == null)
                {
                    failures.Add(CreateFailure(
                        key,
                        source,
                        "wall bounding box transform returned no bounds"));
                    continue;
                }
                if (!BoundsOverlap(worldBounds, hostRoi)) continue;

                GeometryExtraction extraction = ExtractGeometry(element);
                var worldSolidBounds = new List<PlenumAnalysisService.Bounds3>();
                foreach (Solid solid in extraction.Solids)
                {
                    try
                    {
                        worldSolidBounds.Add(WorldBounds(solid.GetBoundingBox(), toHost));
                    }
                    catch (Exception exception)
                    {
                        worldSolidBounds.Add(null);
                        if (string.IsNullOrWhiteSpace(extraction.Error))
                            extraction.Error = "wall solid bounds unavailable: " +
                                               exception.GetType().Name;
                    }
                }
                if (!string.IsNullOrWhiteSpace(extraction.Error))
                    failures.Add(CreateFailure(key, source, extraction.Error));

                output.Add(new PlenumAnalysisService.Candidate
                {
                    Element = element,
                    ToHost = toHost,
                    FromHost = fromHost,
                    WorldBounds = worldBounds,
                    Solids = extraction.Solids,
                    WorldSolidBounds = worldSolidBounds,
                    MeshCount = extraction.MeshCount,
                    GeometryError = extraction.Error,
                    State = PlenumState.Structure,
                    Source = source,
                    SourceKey = key,
                    Category = BuiltInCategory.OST_Walls
                });
            }
        }

        private static GeometryExtraction ExtractGeometry(Element element)
        {
            var extraction = new GeometryExtraction();
            try
            {
                GeometryElement geometry = element.get_Geometry(new Options
                {
                    DetailLevel = ViewDetailLevel.Fine,
                    IncludeNonVisibleObjects = false,
                    ComputeReferences = false
                });
                CollectGeometry(geometry, Transform.Identity, extraction);
            }
            catch (Exception exception)
            {
                extraction.Error = "wall geometry extraction failed: " +
                                   exception.GetType().Name;
            }
            if (extraction.Solids.Count == 0 && string.IsNullOrWhiteSpace(extraction.Error))
                extraction.Error = extraction.MeshCount > 0
                    ? "wall geometry is mesh-only"
                    : "wall has no solid geometry";
            return extraction;
        }

        private static void CollectGeometry(
            GeometryElement geometry,
            Transform transform,
            GeometryExtraction extraction)
        {
            if (geometry == null) return;
            foreach (GeometryObject geometryObject in geometry)
            {
                Solid solid = geometryObject as Solid;
                if (solid != null)
                {
                    try
                    {
                        if (solid.Volume <= Epsilon) continue;
                        extraction.Solids.Add(transform == null || transform.IsIdentity
                            ? solid
                            : SolidUtils.CreateTransformed(solid, transform));
                    }
                    catch (Exception exception)
                    {
                        extraction.Error = "wall solid transform failed: " +
                                           exception.GetType().Name;
                    }
                    continue;
                }
                Mesh mesh = geometryObject as Mesh;
                if (mesh != null)
                {
                    extraction.MeshCount++;
                    continue;
                }
                GeometryInstance instance = geometryObject as GeometryInstance;
                if (instance == null) continue;
                try
                {
                    CollectGeometry(
                        instance.GetSymbolGeometry(),
                        (transform ?? Transform.Identity).Multiply(instance.Transform),
                        extraction);
                }
                catch (Exception exception)
                {
                    extraction.Error = "wall instance geometry failed: " +
                                       exception.GetType().Name;
                }
            }
        }

        private static bool LinkMayOverlapRoi(
            RevitLinkInstance link,
            PlenumAnalysisService.Bounds3 hostRoi,
            out string reason)
        {
            reason = string.Empty;
            try
            {
                BoundingBoxXYZ boundingBox = link.get_BoundingBox(null);
                if (boundingBox == null)
                {
                    reason = "link instance bounds are null; overlap is unknown";
                    return true;
                }
                return BoundsOverlap(
                    WorldBounds(boundingBox, Transform.Identity),
                    hostRoi);
            }
            catch (Exception exception)
            {
                reason = "link instance bounds unavailable: " +
                         exception.GetType().Name + "; overlap is unknown";
                return true;
            }
        }

        private static PlenumSourceRef CreateSource(
            Document sourceDocument,
            RevitLinkInstance link,
            Element element)
        {
            return new PlenumSourceRef
            {
                SourceType = link == null ? "Host" : "RevitLink",
                DocumentTitle = TryGet(() => sourceDocument.Title),
                LinkInstanceId = link == null ? (long?)null : link.Id.Value,
                LinkInstanceUniqueId = link == null
                    ? string.Empty
                    : TryGet(() => link.UniqueId),
                ElementId = element == null ? 0L : element.Id.Value,
                UniqueId = element == null
                    ? string.Empty
                    : TryGet(() => element.UniqueId),
                Category = element == null || element.Category == null
                    ? "Walls"
                    : TryGet(() => element.Category.Name),
                Name = element == null ? string.Empty : TryGet(() => element.Name),
                BlockerKind = "Structure"
            };
        }

        private static string BuildSourceKey(
            RevitLinkInstance link,
            Element element,
            PlenumSourceRef source)
        {
            if (link == null)
                return string.IsNullOrWhiteSpace(source.UniqueId)
                    ? "HOST:" + source.ElementId
                    : MaintenanceStableIdentity.HostElementKey(source.UniqueId);
            return string.IsNullOrWhiteSpace(source.LinkInstanceUniqueId) ||
                   string.IsNullOrWhiteSpace(source.UniqueId)
                ? "LINK:" + link.Id.Value + ":" + source.ElementId
                : MaintenanceStableIdentity.LinkedElementKey(
                    source.LinkInstanceUniqueId,
                    source.UniqueId);
        }

        private static PlenumAnalysisService.CandidateCollectionFailure CreateFailure(
            string sourceKey,
            PlenumSourceRef source,
            string reason)
        {
            return new PlenumAnalysisService.CandidateCollectionFailure
            {
                SourceKey = sourceKey ?? string.Empty,
                Source = source,
                Category = BuiltInCategory.OST_Walls,
                Reason = reason ?? string.Empty
            };
        }

        private static PlenumAnalysisService.CandidateCollectionFailure CreateScanFailure(
            Document sourceDocument,
            RevitLinkInstance link,
            string reason)
        {
            string linkUniqueId = link == null
                ? string.Empty
                : TryGet(() => link.UniqueId);
            string sourceKey = link == null
                ? "HUID:*"
                : (string.IsNullOrWhiteSpace(linkUniqueId)
                    ? "LINK:" + link.Id.Value + ":*"
                    : "LUID:" + linkUniqueId + ":*");
            return CreateFailure(
                sourceKey,
                new PlenumSourceRef
                {
                    SourceType = link == null ? "Host" : "RevitLink",
                    DocumentTitle = TryGet(() => sourceDocument.Title),
                    LinkInstanceId = link == null ? (long?)null : link.Id.Value,
                    LinkInstanceUniqueId = linkUniqueId,
                    ElementId = link == null ? 0L : link.Id.Value,
                    UniqueId = linkUniqueId,
                    Category = "Walls",
                    Name = link == null ? "OST_Walls" : TryGet(() => link.Name),
                    BlockerKind = "CollectionCoverage"
                },
                reason);
        }

        private static PlenumAnalysisService.Bounds3 WorldBounds(
            BoundingBoxXYZ boundingBox,
            Transform toHost)
        {
            if (boundingBox == null || toHost == null) return null;
            Transform transform = toHost.Multiply(
                boundingBox.Transform ?? Transform.Identity);
            return BoundsFromPoints(Enumerable.Range(0, 8).Select(i =>
                transform.OfPoint(new XYZ(
                    (i & 1) == 0 ? boundingBox.Min.X : boundingBox.Max.X,
                    (i & 2) == 0 ? boundingBox.Min.Y : boundingBox.Max.Y,
                    (i & 4) == 0 ? boundingBox.Min.Z : boundingBox.Max.Z)))
                .ToList());
        }

        private static PlenumAnalysisService.Bounds3 TransformBounds(
            PlenumAnalysisService.Bounds3 bounds,
            Transform transform)
        {
            if (bounds == null || transform == null) return null;
            return BoundsFromPoints(Enumerable.Range(0, 8).Select(i =>
                transform.OfPoint(new XYZ(
                    (i & 1) == 0 ? bounds.MinX : bounds.MaxX,
                    (i & 2) == 0 ? bounds.MinY : bounds.MaxY,
                    (i & 4) == 0 ? bounds.MinZ : bounds.MaxZ)))
                .ToList());
        }

        private static PlenumAnalysisService.Bounds3 BoundsFromPoints(
            IList<XYZ> points)
        {
            if (points == null || points.Count == 0) return null;
            return new PlenumAnalysisService.Bounds3
            {
                MinX = points.Min(x => x.X),
                MinY = points.Min(x => x.Y),
                MinZ = points.Min(x => x.Z),
                MaxX = points.Max(x => x.X),
                MaxY = points.Max(x => x.Y),
                MaxZ = points.Max(x => x.Z)
            };
        }

        private static bool BoundsOverlap(
            PlenumAnalysisService.Bounds3 left,
            PlenumAnalysisService.Bounds3 right)
        {
            return left != null && right != null &&
                   left.MaxX >= right.MinX && left.MinX <= right.MaxX &&
                   left.MaxY >= right.MinY && left.MinY <= right.MaxY &&
                   left.MaxZ >= right.MinZ && left.MinZ <= right.MaxZ;
        }

        private static string TryGet(Func<string> getter)
        {
            try { return getter() ?? string.Empty; }
            catch { return string.Empty; }
        }
    }
}

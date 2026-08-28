using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using JarviTools.Commands.Plenum;

namespace JarviTools.Commands.MaintenanceReachability
{
    internal enum MaintenanceCollisionState
    {
        Clear,
        Conflict,
        Unverified
    }

    internal sealed class MaintenanceCollisionResult
    {
        public MaintenanceCollisionState State;
        public string BlockerKey;
        public string Reason;

        public bool IsClear
        {
            get { return State == MaintenanceCollisionState.Clear; }
        }
    }

    /// <summary>
    /// Creates the simple solids used by the maintenance-reachability result and
    /// validates proposed clearance bodies against host/link obstacle geometry.
    /// All coordinates and dimensions use Revit internal units (feet).
    /// </summary>
    internal static class MaintenanceGeometryService
    {
        private const double Epsilon = 1e-9;
        private const double MmPerFoot = 304.8;

        /// <summary>
        /// Creates a world-XY oriented box. <paramref name="center"/> is the plan
        /// centre and center.Z is the bottom elevation; xDirection controls its
        /// length axis.
        /// </summary>
        public static Solid MakeBox(
            XYZ center,
            double lengthFt,
            double widthFt,
            double heightFt,
            XYZ xDirection)
        {
            if (center == null) throw new ArgumentNullException("center");
            EnsurePositive(lengthFt, "lengthFt");
            EnsurePositive(widthFt, "widthFt");
            EnsurePositive(heightFt, "heightFt");

            XYZ x = HorizontalUnit(xDirection, XYZ.BasisX);
            XYZ y = XYZ.BasisZ.CrossProduct(x).Normalize();
            XYZ baseCenter = center;

            XYZ p0 = baseCenter - x.Multiply(lengthFt * 0.5) - y.Multiply(widthFt * 0.5);
            XYZ p1 = baseCenter + x.Multiply(lengthFt * 0.5) - y.Multiply(widthFt * 0.5);
            XYZ p2 = baseCenter + x.Multiply(lengthFt * 0.5) + y.Multiply(widthFt * 0.5);
            XYZ p3 = baseCenter - x.Multiply(lengthFt * 0.5) + y.Multiply(widthFt * 0.5);

            return ExtrudePolygon(new[] { p0, p1, p2, p3 }, XYZ.BasisZ, heightFt);
        }

        /// <summary>Creates an axis-aligned box from two opposite world corners.</summary>
        public static Solid MakeBox(XYZ corner0, XYZ corner1)
        {
            if (corner0 == null) throw new ArgumentNullException("corner0");
            if (corner1 == null) throw new ArgumentNullException("corner1");

            double minX = Math.Min(corner0.X, corner1.X);
            double minY = Math.Min(corner0.Y, corner1.Y);
            double minZ = Math.Min(corner0.Z, corner1.Z);
            double maxX = Math.Max(corner0.X, corner1.X);
            double maxY = Math.Max(corner0.Y, corner1.Y);
            double maxZ = Math.Max(corner0.Z, corner1.Z);
            return MakeBox(
                new XYZ((minX + maxX) * 0.5, (minY + maxY) * 0.5, minZ),
                maxX - minX,
                maxY - minY,
                maxZ - minZ,
                XYZ.BasisX);
        }

        /// <summary>
        /// Creates a horizontal capsule (rectangle plus two semicircular ends).
        /// Start/end form the plan centreline and their average Z is the vertical
        /// centre of the returned solid.
        /// </summary>
        public static Solid MakeHorizontalCapsule(
            XYZ start,
            XYZ end,
            double radiusFt,
            double heightFt)
        {
            if (start == null) throw new ArgumentNullException("start");
            if (end == null) throw new ArgumentNullException("end");
            EnsurePositive(radiusFt, "radiusFt");
            EnsurePositive(heightFt, "heightFt");

            double centerZ = (start.Z + end.Z) * 0.5;
            XYZ a = new XYZ(start.X, start.Y, centerZ - heightFt * 0.5);
            XYZ b = new XYZ(end.X, end.Y, centerZ - heightFt * 0.5);
            XYZ delta = b - a;
            XYZ horizontal = new XYZ(delta.X, delta.Y, 0.0);
            if (horizontal.GetLength() <= Epsilon)
                return MakeVerticalCylinder(new XYZ(a.X, a.Y, centerZ), radiusFt, heightFt);

            XYZ direction = horizontal.Normalize();
            XYZ normal = XYZ.BasisZ.CrossProduct(direction).Normalize();
            XYZ aLeft = a + normal.Multiply(radiusFt);
            XYZ bLeft = b + normal.Multiply(radiusFt);
            XYZ bRight = b - normal.Multiply(radiusFt);
            XYZ aRight = a - normal.Multiply(radiusFt);

            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(aLeft, bLeft));
            loop.Append(Arc.Create(bLeft, bRight, b + direction.Multiply(radiusFt)));
            loop.Append(Line.CreateBound(bRight, aRight));
            loop.Append(Arc.Create(aRight, aLeft, a - direction.Multiply(radiusFt)));
            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { loop },
                XYZ.BasisZ,
                heightFt);
        }

        /// <summary>
        /// Creates a rectangular beam between arbitrary 3D endpoints. Width and
        /// depth describe its cross-section perpendicular to the beam axis.
        /// </summary>
        public static Solid MakeBeam(
            XYZ start,
            XYZ end,
            double widthFt,
            double depthFt)
        {
            if (start == null) throw new ArgumentNullException("start");
            if (end == null) throw new ArgumentNullException("end");
            EnsurePositive(widthFt, "widthFt");
            EnsurePositive(depthFt, "depthFt");

            XYZ axis = end - start;
            double length = axis.GetLength();
            if (length <= Epsilon) return null;
            XYZ direction = axis.Normalize();
            XYZ u;
            XYZ v;
            PerpendicularFrame(direction, out u, out v);

            XYZ p0 = start - u.Multiply(widthFt * 0.5) - v.Multiply(depthFt * 0.5);
            XYZ p1 = start + u.Multiply(widthFt * 0.5) - v.Multiply(depthFt * 0.5);
            XYZ p2 = start + u.Multiply(widthFt * 0.5) + v.Multiply(depthFt * 0.5);
            XYZ p3 = start - u.Multiply(widthFt * 0.5) + v.Multiply(depthFt * 0.5);
            return ExtrudePolygon(new[] { p0, p1, p2, p3 }, direction, length);
        }

        /// <summary>Creates a set of overlapping capsule solids for a route polyline.</summary>
        public static List<Solid> BuildRoute(
            IList<XYZ> points,
            double radiusFt,
            double heightFt)
        {
            var result = new List<Solid>();
            if (points == null || points.Count == 0) return result;

            if (points.Count == 1)
            {
                Solid pointBody = MakeVerticalCylinder(points[0], radiusFt, heightFt);
                if (pointBody != null) result.Add(pointBody);
                return result;
            }

            for (int i = 1; i < points.Count; i++)
            {
                Solid segment = MakeHorizontalCapsule(points[i - 1], points[i], radiusFt, heightFt);
                if (segment != null) result.Add(segment);
            }
            return result;
        }

        /// <summary>
        /// Creates the three members of a configurable wall access-door frame.
        /// bottomCenter is the centre of the clear opening at its bottom edge.
        /// </summary>
        public static List<Solid> BuildDoorFrame(
            XYZ bottomCenter,
            XYZ wallTangent,
            double doorWidthFt,
            double frameDepthFt,
            double doorHeightFt,
            double frameThicknessFt)
        {
            if (bottomCenter == null) throw new ArgumentNullException("bottomCenter");
            EnsurePositive(doorWidthFt, "doorWidthFt");
            EnsurePositive(frameDepthFt, "frameDepthFt");
            EnsurePositive(doorHeightFt, "doorHeightFt");
            EnsurePositive(frameThicknessFt, "frameThicknessFt");

            XYZ tangent = HorizontalUnit(wallTangent, XYZ.BasisX);
            double jambOffset = (doorWidthFt + frameThicknessFt) * 0.5;
            var result = new List<Solid>();
            Solid left = MakeBox(
                bottomCenter - tangent.Multiply(jambOffset),
                frameThicknessFt,
                frameDepthFt,
                doorHeightFt,
                tangent);
            Solid right = MakeBox(
                bottomCenter + tangent.Multiply(jambOffset),
                frameThicknessFt,
                frameDepthFt,
                doorHeightFt,
                tangent);
            Solid head = MakeBox(
                bottomCenter + XYZ.BasisZ.Multiply(doorHeightFt),
                doorWidthFt + frameThicknessFt * 2.0,
                frameDepthFt,
                frameThicknessFt,
                tangent);
            if (left != null) result.Add(left);
            if (right != null) result.Add(right);
            if (head != null) result.Add(head);
            return result;
        }

        /// <summary>Creates a thin access-door leaf inside a frame opening.</summary>
        public static Solid BuildDoorLeaf(
            XYZ bottomCenter,
            XYZ wallTangent,
            double doorWidthFt,
            double leafDepthFt,
            double doorHeightFt)
        {
            if (bottomCenter == null) throw new ArgumentNullException("bottomCenter");
            return MakeBox(
                bottomCenter,
                doorWidthFt,
                leafDepthFt,
                doorHeightFt,
                wallTangent);
        }

        /// <summary>
        /// Conservative 90-degree outward swing envelope for one hinged door leaf.
        /// The caller supplies the inward direction as seen from outside the room;
        /// Left/Right therefore remain stable even when a boundary loop reverses.
        /// </summary>
        public static Solid BuildOutwardDoorSwingEnvelope(
            XYZ bottomCenter,
            XYZ inwardDirection,
            double doorWidthFt,
            double leafThicknessFt,
            double doorHeightFt,
            double outboardOffsetFt,
            MaintenanceDoorHingeSide hingeSide)
        {
            if (bottomCenter == null) throw new ArgumentNullException("bottomCenter");
            EnsurePositive(doorWidthFt, "doorWidthFt");
            EnsurePositive(leafThicknessFt, "leafThicknessFt");
            EnsurePositive(doorHeightFt, "doorHeightFt");
            EnsurePositive(outboardOffsetFt, "outboardOffsetFt");
            if (hingeSide != MaintenanceDoorHingeSide.Left &&
                hingeSide != MaintenanceDoorHingeSide.Right)
                throw new ArgumentOutOfRangeException("hingeSide");

            XYZ inward = HorizontalUnit(inwardDirection, XYZ.BasisY);
            XYZ outward = -inward;
            XYZ viewerRight = inward.CrossProduct(XYZ.BasisZ).Normalize();
            bool leftHinge = hingeSide == MaintenanceDoorHingeSide.Left;
            XYZ hinge = bottomCenter +
                        viewerRight.Multiply((leftHinge ? -1.0 : 1.0) * doorWidthFt * 0.5) +
                        outward.Multiply(outboardOffsetFt);
            XYZ closedDirection = leftHinge ? viewerRight : -viewerRight;

            double halfThickness = leafThicknessFt * 0.5;
            double radius = doorWidthFt + halfThickness;
            double anglePadding = Math.Atan2(halfThickness, doorWidthFt);
            const int segmentCount = 18;
            var points = new List<XYZ> { hinge };
            for (int index = 0; index <= segmentCount; index++)
            {
                double angle = -anglePadding +
                               (Math.PI * 0.5 + anglePadding * 2.0) * index / segmentCount;
                XYZ radial = closedDirection.Multiply(Math.Cos(angle)) +
                             outward.Multiply(Math.Sin(angle));
                points.Add(hinge + radial.Multiply(radius));
            }
            return ExtrudePolygon(points, XYZ.BasisZ, doorHeightFt);
        }

        public static Solid BuildOutwardOpenDoorLeaf(
            XYZ bottomCenter,
            XYZ inwardDirection,
            double doorWidthFt,
            double leafThicknessFt,
            double doorHeightFt,
            double outboardOffsetFt,
            MaintenanceDoorHingeSide hingeSide)
        {
            if (bottomCenter == null) throw new ArgumentNullException("bottomCenter");
            if (hingeSide != MaintenanceDoorHingeSide.Left &&
                hingeSide != MaintenanceDoorHingeSide.Right)
                throw new ArgumentOutOfRangeException("hingeSide");
            XYZ inward = HorizontalUnit(inwardDirection, XYZ.BasisY);
            XYZ outward = -inward;
            XYZ viewerRight = inward.CrossProduct(XYZ.BasisZ).Normalize();
            bool leftHinge = hingeSide == MaintenanceDoorHingeSide.Left;
            XYZ hinge = bottomCenter +
                        viewerRight.Multiply((leftHinge ? -1.0 : 1.0) * doorWidthFt * 0.5) +
                        outward.Multiply(outboardOffsetFt);
            XYZ leafCenter = hinge + outward.Multiply(doorWidthFt * 0.5);
            return BuildDoorLeaf(
                leafCenter,
                outward,
                doorWidthFt,
                leafThicknessFt,
                doorHeightFt);
        }

        public static MaintenanceCollisionResult ValidateGeneratedBodies(
            IEnumerable<Solid> first,
            IEnumerable<Solid> second,
            string conflictReason)
        {
            List<Solid> left = first == null
                ? new List<Solid>()
                : first.Where(x => x != null && x.Volume > Epsilon).ToList();
            List<Solid> right = second == null
                ? new List<Solid>()
                : second.Where(x => x != null && x.Volume > Epsilon).ToList();
            if (left.Count == 0 || right.Count == 0)
                return Result(MaintenanceCollisionState.Unverified, null,
                    "generated comparison geometry is empty");
            foreach (Solid a in left)
            foreach (Solid b in right)
            {
                try
                {
                    Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                        a, b, BooleanOperationsType.Intersect);
                    if (intersection == null)
                        return Result(MaintenanceCollisionState.Unverified, null,
                            "generated geometry intersection returned null");
                    if (intersection.Volume > Epsilon)
                        return Result(MaintenanceCollisionState.Conflict, null,
                            conflictReason ?? "generated geometry conflicts");
                }
                catch
                {
                    return Result(MaintenanceCollisionState.Unverified, null,
                        "generated geometry intersection failed");
                }
            }
            return Result(MaintenanceCollisionState.Clear, null, null);
        }

        /// <summary>
        /// Creates a practical A-frame ladder. The two feet spread along
        /// alongDirection; the climbing and rear planes both receive rungs.
        /// </summary>
        public static List<Solid> BuildAFrameLadder(
            XYZ planCenter,
            XYZ alongDirection,
            double floorZ,
            double topZ)
        {
            var result = new List<Solid>();
            if (planCenter == null || topZ <= floorZ + Epsilon) return result;

            XYZ along = HorizontalUnit(alongDirection, XYZ.BasisX);
            XYZ across = XYZ.BasisZ.CrossProduct(along).Normalize();
            double height = topZ - floorZ;
            double halfSpread = Clamp(height * 0.22, Mm(450.0), Mm(700.0));
            double halfWidth = Mm(300.0);
            double railRadius = Mm(18.0);
            double rungRadius = Mm(13.0);
            XYZ apexCenter = new XYZ(planCenter.X, planCenter.Y, topZ);
            XYZ frontCenter = new XYZ(planCenter.X, planCenter.Y, floorZ) + along.Multiply(halfSpread);
            XYZ rearCenter = new XYZ(planCenter.X, planCenter.Y, floorZ) - along.Multiply(halfSpread);

            XYZ frontLeft = frontCenter - across.Multiply(halfWidth);
            XYZ frontRight = frontCenter + across.Multiply(halfWidth);
            XYZ rearLeft = rearCenter - across.Multiply(halfWidth);
            XYZ rearRight = rearCenter + across.Multiply(halfWidth);
            XYZ apexLeft = apexCenter - across.Multiply(halfWidth);
            XYZ apexRight = apexCenter + across.Multiply(halfWidth);

            AddIfNotNull(result, MakeRod(frontLeft, apexLeft, railRadius));
            AddIfNotNull(result, MakeRod(frontRight, apexRight, railRadius));
            AddIfNotNull(result, MakeRod(rearLeft, apexLeft, railRadius));
            AddIfNotNull(result, MakeRod(rearRight, apexRight, railRadius));
            AddIfNotNull(result, MakeRod(apexLeft, apexRight, railRadius));

            int rungCount = Math.Max(2, (int)Math.Floor(height / Mm(280.0)));
            for (int i = 1; i <= rungCount; i++)
            {
                double t = i / (double)(rungCount + 1);
                AddIfNotNull(result, MakeRod(Lerp(frontLeft, apexLeft, t), Lerp(frontRight, apexRight, t), rungRadius));
                AddIfNotNull(result, MakeRod(Lerp(rearLeft, apexLeft, t), Lerp(rearRight, apexRight, t), rungRadius));
            }
            return result;
        }

        /// <summary>
        /// Creates a leaned straight ladder centred in plan. alongDirection points
        /// from its bottom toward its top.
        /// </summary>
        public static List<Solid> BuildStraightLadder(
            XYZ planCenter,
            XYZ alongDirection,
            double floorZ,
            double topZ)
        {
            var result = new List<Solid>();
            if (planCenter == null || topZ <= floorZ + Epsilon) return result;

            XYZ along = HorizontalUnit(alongDirection, XYZ.BasisX);
            XYZ across = XYZ.BasisZ.CrossProduct(along).Normalize();
            double height = topZ - floorZ;
            double totalRun = Clamp(height * 0.23, Mm(450.0), Mm(900.0));
            double halfWidth = Mm(300.0);
            double railRadius = Mm(18.0);
            double rungRadius = Mm(13.0);
            XYZ bottomCenter = new XYZ(planCenter.X, planCenter.Y, floorZ) - along.Multiply(totalRun * 0.5);
            XYZ topCenter = new XYZ(planCenter.X, planCenter.Y, topZ) + along.Multiply(totalRun * 0.5);
            XYZ bottomLeft = bottomCenter - across.Multiply(halfWidth);
            XYZ bottomRight = bottomCenter + across.Multiply(halfWidth);
            XYZ topLeft = topCenter - across.Multiply(halfWidth);
            XYZ topRight = topCenter + across.Multiply(halfWidth);

            AddIfNotNull(result, MakeRod(bottomLeft, topLeft, railRadius));
            AddIfNotNull(result, MakeRod(bottomRight, topRight, railRadius));
            int rungCount = Math.Max(2, (int)Math.Floor(height / Mm(280.0)));
            for (int i = 1; i <= rungCount; i++)
            {
                double t = i / (double)(rungCount + 1);
                AddIfNotNull(result, MakeRod(Lerp(bottomLeft, topLeft, t), Lerp(bottomRight, topRight, t), rungRadius));
            }
            return result;
        }

        /// <summary>Returns world bounds for a solid by triangulating its faces.</summary>
        public static PlenumAnalysisService.Bounds3 SolidBounds(Solid solid)
        {
            if (solid == null) return null;
            var points = new List<XYZ>();
            foreach (Face face in solid.Faces)
            {
                try
                {
                    Mesh mesh = face.Triangulate();
                    if (mesh == null) continue;
                    foreach (XYZ vertex in mesh.Vertices) points.Add(vertex);
                }
                catch
                {
                    // Edge tessellation below remains a useful fallback.
                }
            }
            if (points.Count == 0)
            {
                foreach (Edge edge in solid.Edges)
                {
                    try { points.AddRange(edge.Tessellate()); }
                    catch { }
                }
            }
            return PointsBounds(points);
        }

        public static PlenumAnalysisService.Bounds3 SolidBounds(IEnumerable<Solid> solids)
        {
            if (solids == null) return null;
            PlenumAnalysisService.Bounds3 result = null;
            foreach (Solid solid in solids)
                result = UnionBounds(result, SolidBounds(solid));
            return result;
        }

        /// <summary>
        /// Exact collision validation. Body solids are expressed in host/world
        /// coordinates; each body is transformed into the candidate source model
        /// before Revit's Boolean intersection is evaluated.
        /// </summary>
        public static MaintenanceCollisionResult Validate(
            IEnumerable<Solid> bodySolids,
            IList<PlenumAnalysisService.Candidate> candidates,
            ISet<string> ignoredSourceKeys)
        {
            List<Solid> bodies = bodySolids == null
                ? new List<Solid>()
                : bodySolids.Where(x => x != null && x.Volume > Epsilon).ToList();
            if (bodies.Count == 0)
                return Result(MaintenanceCollisionState.Unverified, null, "proposed body geometry is empty");
            if (candidates == null)
                return Result(MaintenanceCollisionState.Unverified, null, "obstacle candidates are unavailable");

            var bodyBounds = new List<PlenumAnalysisService.Bounds3>();
            foreach (Solid body in bodies)
            {
                PlenumAnalysisService.Bounds3 bounds = SolidBounds(body);
                if (bounds == null)
                    return Result(MaintenanceCollisionState.Unverified, null, "proposed body bounds are unavailable");
                bodyBounds.Add(bounds);
            }
            PlenumAnalysisService.Bounds3 aggregateBounds = SolidBounds(bodies);
            if (aggregateBounds == null)
                return Result(MaintenanceCollisionState.Unverified, null, "proposed body bounds are unavailable");

            foreach (PlenumAnalysisService.Candidate candidate in candidates)
            {
                if (candidate == null) continue;
                if (ignoredSourceKeys != null
                    && !string.IsNullOrEmpty(candidate.SourceKey)
                    && ignoredSourceKeys.Contains(candidate.SourceKey))
                    continue;

                // CollectCandidates already limits the overall ROI. A missing
                // candidate bound cannot be safely dismissed.
                if (candidate.WorldBounds == null)
                    return Result(MaintenanceCollisionState.Unverified, candidate, "candidate world bounds are unavailable");
                if (!BoundsOverlap(candidate.WorldBounds, aggregateBounds)) continue;

                if (candidate.Solids == null
                    || candidate.Solids.Count == 0
                    || candidate.MeshCount > 0
                    || !string.IsNullOrEmpty(candidate.GeometryError)
                    || candidate.WorldSolidBounds == null
                    || candidate.WorldSolidBounds.Count != candidate.Solids.Count
                    || candidate.WorldSolidBounds.Any(x => x == null))
                {
                    return Result(MaintenanceCollisionState.Unverified, candidate,
                        "overlapping candidate geometry is not fully verifiable");
                }

                for (int bodyIndex = 0; bodyIndex < bodies.Count; bodyIndex++)
                {
                    if (!BoundsOverlap(candidate.WorldBounds, bodyBounds[bodyIndex])) continue;
                    Solid sourceBody;
                    try
                    {
                        Transform fromHost = candidate.FromHost ?? Transform.Identity;
                        sourceBody = fromHost.IsIdentity
                            ? bodies[bodyIndex]
                            : SolidUtils.CreateTransformed(bodies[bodyIndex], fromHost);
                    }
                    catch
                    {
                        return Result(MaintenanceCollisionState.Unverified, candidate,
                            "proposed body could not be transformed into the candidate model");
                    }

                    for (int solidIndex = 0; solidIndex < candidate.Solids.Count; solidIndex++)
                    {
                        if (!BoundsOverlap(candidate.WorldSolidBounds[solidIndex], bodyBounds[bodyIndex])) continue;
                        Solid obstacle = candidate.Solids[solidIndex];
                        if (obstacle == null)
                            return Result(MaintenanceCollisionState.Unverified, candidate,
                                "candidate contains an empty solid");
                        try
                        {
                            Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                                sourceBody,
                                obstacle,
                                BooleanOperationsType.Intersect);
                            if (MaintenanceBooleanIntersectionPolicy.RequiresUnverified(
                                intersection != null))
                                return Result(MaintenanceCollisionState.Unverified, candidate,
                                    "exact candidate/body intersection returned null");
                            if (intersection.Volume > Epsilon)
                                return Result(MaintenanceCollisionState.Conflict, candidate,
                                    "candidate intersects proposed maintenance body");
                        }
                        catch
                        {
                            return Result(MaintenanceCollisionState.Unverified, candidate,
                                "exact candidate/body intersection failed");
                        }
                    }
                }
            }

            return Result(MaintenanceCollisionState.Clear, null, null);
        }

        public static MaintenanceCollisionResult Validate(
            Solid body,
            IList<PlenumAnalysisService.Candidate> candidates,
            ISet<string> ignoredSourceKeys)
        {
            return Validate(body == null ? null : new[] { body }, candidates, ignoredSourceKeys);
        }

        public static bool BoundsOverlap(
            PlenumAnalysisService.Bounds3 a,
            PlenumAnalysisService.Bounds3 b)
        {
            return a != null && b != null
                && a.MaxX >= b.MinX && a.MinX <= b.MaxX
                && a.MaxY >= b.MinY && a.MinY <= b.MaxY
                && a.MaxZ >= b.MinZ && a.MinZ <= b.MaxZ;
        }

        private static Solid MakeVerticalCylinder(XYZ center, double radiusFt, double heightFt)
        {
            XYZ baseCenter = new XYZ(center.X, center.Y, center.Z - heightFt * 0.5);
            XYZ left = baseCenter - XYZ.BasisX.Multiply(radiusFt);
            XYZ right = baseCenter + XYZ.BasisX.Multiply(radiusFt);
            var loop = new CurveLoop();
            loop.Append(Arc.Create(left, right, baseCenter + XYZ.BasisY.Multiply(radiusFt)));
            loop.Append(Arc.Create(right, left, baseCenter - XYZ.BasisY.Multiply(radiusFt)));
            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { loop },
                XYZ.BasisZ,
                heightFt);
        }

        private static Solid MakeRod(XYZ start, XYZ end, double radiusFt)
        {
            XYZ axis = end - start;
            double length = axis.GetLength();
            if (length <= Epsilon) return null;
            XYZ direction = axis.Normalize();
            XYZ u;
            XYZ v;
            PerpendicularFrame(direction, out u, out v);
            XYZ p0 = start + u.Multiply(radiusFt);
            XYZ p1 = start - u.Multiply(radiusFt);
            var loop = new CurveLoop();
            loop.Append(Arc.Create(p0, p1, start + v.Multiply(radiusFt)));
            loop.Append(Arc.Create(p1, p0, start - v.Multiply(radiusFt)));
            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { loop },
                direction,
                length);
        }

        private static Solid ExtrudePolygon(IList<XYZ> points, XYZ direction, double distance)
        {
            var loop = new CurveLoop();
            for (int i = 0; i < points.Count; i++)
                loop.Append(Line.CreateBound(points[i], points[(i + 1) % points.Count]));
            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { loop },
                direction,
                distance);
        }

        private static void PerpendicularFrame(XYZ direction, out XYZ u, out XYZ v)
        {
            XYZ reference = Math.Abs(direction.DotProduct(XYZ.BasisZ)) > 0.9
                ? XYZ.BasisX
                : XYZ.BasisZ;
            u = direction.CrossProduct(reference).Normalize();
            v = direction.CrossProduct(u).Normalize();
        }

        private static XYZ HorizontalUnit(XYZ value, XYZ fallback)
        {
            XYZ horizontal = value == null ? null : new XYZ(value.X, value.Y, 0.0);
            if (horizontal == null || horizontal.GetLength() <= Epsilon)
                horizontal = new XYZ(fallback.X, fallback.Y, 0.0);
            if (horizontal.GetLength() <= Epsilon) horizontal = XYZ.BasisX;
            return horizontal.Normalize();
        }

        private static PlenumAnalysisService.Bounds3 PointsBounds(IList<XYZ> points)
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

        private static PlenumAnalysisService.Bounds3 UnionBounds(
            PlenumAnalysisService.Bounds3 a,
            PlenumAnalysisService.Bounds3 b)
        {
            if (a == null) return b;
            if (b == null) return a;
            return new PlenumAnalysisService.Bounds3
            {
                MinX = Math.Min(a.MinX, b.MinX),
                MinY = Math.Min(a.MinY, b.MinY),
                MinZ = Math.Min(a.MinZ, b.MinZ),
                MaxX = Math.Max(a.MaxX, b.MaxX),
                MaxY = Math.Max(a.MaxY, b.MaxY),
                MaxZ = Math.Max(a.MaxZ, b.MaxZ)
            };
        }

        private static MaintenanceCollisionResult Result(
            MaintenanceCollisionState state,
            PlenumAnalysisService.Candidate candidate,
            string reason)
        {
            return new MaintenanceCollisionResult
            {
                State = state,
                BlockerKey = candidate == null ? null : candidate.SourceKey,
                Reason = reason
            };
        }

        private static XYZ Lerp(XYZ a, XYZ b, double t)
        {
            return a + (b - a).Multiply(t);
        }

        private static void AddIfNotNull(ICollection<Solid> target, Solid solid)
        {
            if (solid != null) target.Add(solid);
        }

        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static double Mm(double value)
        {
            return value / MmPerFoot;
        }

        private static void EnsurePositive(double value, string parameterName)
        {
            if (value <= Epsilon) throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

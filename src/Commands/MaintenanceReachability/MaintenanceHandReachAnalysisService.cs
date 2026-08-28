using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using JarviTools.Commands.Plenum;

namespace JarviTools.Commands.MaintenanceReachability
{
    /// <summary>
    /// 正式分析服务：450×450侧墙探身伸手口、显式400×400侧墙缩小备选 /
    /// 450×450天花直接伸手或人员钻入口 +
    /// 最后圆形操作伸手段 + 人字梯 + 40mm 网格区域合并。独立于
    /// MaintenanceAnalysisService（侧墙600×600爬入式检修门路线）；设备保持模型原高度，
    /// 侧墙路线以天花真实顶面边界生成100mm厚虚拟侧墙，不要求项目预建实体墙。
    /// </summary>
    internal static class MaintenanceHandReachAnalysisService
    {
        private const double MmPerFoot = 304.8;
        private const double Epsilon = 1e-9;

        private sealed class GroupInput
        {
            public string Key;
            public readonly List<Element> Ceilings = new List<Element>();
        }

        private sealed class ObstacleWork
        {
            public readonly List<Solid> Solids = new List<Solid>();
            public readonly List<PlenumAnalysisService.Bounds3> SolidBounds = new List<PlenumAnalysisService.Bounds3>();
            public string Key;
            public string UniqueId;
            public string Category;
            public string Name;
            public string SystemType;
            public string ExemptionReason;
            public bool IsExempt;
            public bool IsFloor;
            public bool IsRoof;
            public bool IsWall;
            public bool IsGeometryUnverified;
            public string GeometryUnverifiedReason;
            public PlenumAnalysisService.Bounds3 FallbackBounds;
        }

        private sealed class FloorSupportResult
        {
            public MaintenanceCollisionState State;
            public double FloorMm;
            public ObstacleWork Work;
            public int SolidIndex = -1;
            public string Reason;
        }

        private sealed class CeilingFaceFootprint
        {
            public PlanarFace Face;
            public string SurfaceKey = string.Empty;
            public readonly List<List<MaintenancePoint2>> BoundaryLoops =
                new List<List<MaintenancePoint2>>();
        }

        private sealed class SideWallSurface
        {
            public ObstacleWork Owner;
            public int SolidIndex;
            public PlanarFace InnerFace;
            public PlanarFace OuterFace;
            public XYZ InnerOrigin;
            public XYZ OuterOrigin;
            public XYZ Tangent;
            public XYZ NormalTowardTarget;
            public double ThicknessFt;
            public bool IsVirtualBoundary;
            public int BoundaryLoopIndex = -1;
            public int BoundarySegmentIndex = -1;
            public MaintenancePoint2 BoundaryStart;
            public MaintenancePoint2 BoundaryEnd;
            public double WallBottomMm;
            public double WallTopMm;
            public string SurfaceKey = string.Empty;
            public readonly List<List<MaintenancePoint2>> InnerLoops =
                new List<List<MaintenancePoint2>>();
            public readonly List<List<MaintenancePoint2>> OuterLoops =
                new List<List<MaintenancePoint2>>();
        }

        private sealed class SideWallAnalysisOutcome
        {
            public int RawSampleCount;
            public int FaceFitCount;
            public int DistanceOkCount;
            public int OpeningFailCount;
            public int CorridorFailCount;
            public int LadderFailCount;
            public int ClearCount;
            public int Regions4Count;
            public int Regions8Count;
            public bool ConnectivityAgreed = true;
            public bool CandidateAuditComplete = true;
            public bool AnyFloorSupport;
            public int UnverifiedGeometryCount;
            public readonly List<HandReachRegion> Regions = new List<HandReachRegion>();
            public readonly Dictionary<string, int> BlockerCounts =
                new Dictionary<string, int>(StringComparer.Ordinal);
        }

        public static HandReachAnalysisResult Analyze(
            Document doc,
            ICollection<ElementId> selectedCeilingIds)
        {
            return Analyze(doc, selectedCeilingIds, new HandReachOptions(), null);
        }

        public static HandReachAnalysisResult Analyze(
            Document doc,
            ICollection<ElementId> selectedCeilingIds,
            HandReachOptions options,
            IList<HandReachDeviceInput> deviceRefs)
        {
            if (doc == null) throw new ArgumentNullException("doc");
            if (selectedCeilingIds == null || selectedCeilingIds.Count == 0)
                throw new InvalidOperationException("请先选择至少一块天花板。");

            options = options == null ? new HandReachOptions() : options.Clone();
            List<GroupInput> groups = ResolveGroups(
                doc,
                selectedCeilingIds,
                options.StrictCeilingSelection);
            if (groups.Count == 0)
                throw new InvalidOperationException("选中图元中没有可分析的天花板。");

            MaintenanceHandReachMath.ValidateFixedContract(options);
            if (options.SideWallOperatorZoneDepthMm <= 0.0 ||
                options.SideWallOperatorZoneWidthMm <= 0.0)
                throw new ArgumentException(
                    "侧墙局部人体站位包络的深度和宽度必须为正数。", "options");
            if (options.CeilingDirectOperatorZoneLengthMm <= 0.0 ||
                options.CeilingDirectOperatorZoneWidthMm <= 0.0)
                throw new ArgumentException(
                    "天花直接伸手的局部人体站位包络尺寸必须为正数。", "options");
            var result = new HandReachAnalysisResult
            {
                Options = options,
                ModelFingerprint = MaintenanceLedgerSyncService.GetModelFingerprint(doc)
            };
            result.LinkScope = MaintenanceLinkScopeService.Resolve(
                doc,
                options.RelevantLinkInstanceIds);
            if (deviceRefs != null && result.LinkScope.Explicit)
            {
                List<long> outOfScopeDeviceLinks = deviceRefs
                    .Where(x => x != null && !result.LinkScope.Includes(
                        x.LinkInstanceId,
                        string.Empty))
                    .Select(x => x.LinkInstanceId)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();
                if (outOfScopeDeviceLinks.Count > 0)
                    throw new ArgumentException(
                        "deviceRefs contains links outside relevantLinkInstanceIds: " +
                        string.Join(",", outOfScopeDeviceLinks));
            }
            result.CoverageLimitations.AddRange(
                PlenumAnalysisService.CandidateCoverageLimitations());
            MaintenanceLinkScopeService.AddScopeLimitation(
                result.CoverageLimitations,
                result.LinkScope);
            AddEvidenceSources(
                result,
                MaintenanceLinkScopeService.RelevantLinkEvidenceSources(
                    doc,
                    result.LinkScope));
            result.Warnings.Add(
                "净空候选使用显式类别白名单；未声明为全模型所有类别扫描。详见 coverageLimitations。 ");
            if (result.LinkScope.Explicit && result.CoverageLimitations.Count > 0)
                result.Warnings.Add(result.CoverageLimitations.Last());

            var groupKeys = new List<string>();
            foreach (GroupInput group in groups)
            {
                AnalyzeGroup(doc, group, result, options, deviceRefs);
                groupKeys.Add(group.Key);
            }
            result.GroupKey = string.Join("+", groupKeys);
            if (!result.CoverageComplete)
            {
                foreach (HandReachTargetResult target in result.TargetResults)
                {
                    target.CandidateAuditComplete = false;
                    target.SelectedCandidateAuditComplete = false;
                    if (target.AttentionLevel != HandReachAttentionLevel.Rejected)
                    {
                        target.AttentionLevel = HandReachAttentionLevel.OrangeReview;
                        target.Conclusion = "conditional_feasible_hand_reach_coverage_incomplete";
                        target.ConclusionReason =
                            "设备发现或障碍证据收集不完整，禁止正式通过或写入，需补齐模型证据后重分析。";
                    }
                }
            }
            if (result.TargetResults.Count == 0)
                result.Warnings.Add("选定天花分组内没有找到可分析的机械设备。");
            result.ResultFingerprint = ComputeFingerprint(result);
            // 兼容旧调用方；Tools 入口会用当前 Revit VersionGuid/链接变换重算实时证据哈希。
            result.EvidenceFingerprint = result.ResultFingerprint;
            return result;
        }

        // ---------------------------------------------------------------- group

        private static List<GroupInput> ResolveGroups(
            Document doc,
            ICollection<ElementId> selectedCeilingIds,
            bool strictCeilingSelection)
        {
            List<Element> selected = selectedCeilingIds
                .Select(doc.GetElement)
                .Where(IsCeiling)
                .ToList();
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
                    .OrderBy(x => x.Id.Value)
                    .ToList();
                input.Ceilings.Clear();
                input.Ceilings.AddRange(distinct);
                output.Add(input);
            }
            return output.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
        }

        // ---------------------------------------------------------------- group analysis

        private static void AnalyzeGroup(
            Document doc,
            GroupInput group,
            HandReachAnalysisResult result,
            HandReachOptions options,
            IList<HandReachDeviceInput> deviceRefs)
        {
            var faces = new List<PlanarFace>();
            foreach (Element ceiling in group.Ceilings)
                faces.AddRange(FindHighestHorizontalFaces(ceiling));
            if (faces.Count == 0)
                throw new InvalidOperationException("天花分组“" + group.Key + "”找不到水平顶面。");

            double topFt = faces.Max(x => x.Origin.Z);
            if (faces.Any(x => Math.Abs(x.Origin.Z - topFt) * MmPerFoot > 10.0))
                throw new InvalidOperationException(
                    "天花分组“" + group.Key + "”内顶面标高差超过 10 mm，请分组后再分析。");
            double topZMm = topFt * MmPerFoot;
            result.CeilingTopMm = topZMm;

            // 分组 XY 范围（用于 ROI 与楼面判断）
            var faceVertices = new List<XYZ>();
            bool faceTriangulationFailed = false;
            foreach (PlanarFace face in faces)
            {
                try
                {
                    Mesh mesh = face.Triangulate(0.5);
                    foreach (XYZ vertex in mesh.Vertices) faceVertices.Add(vertex);
                }
                catch
                {
                    faceTriangulationFailed = true;
                }
            }
            if (faceTriangulationFailed)
                throw new InvalidOperationException(
                    "天花分组“" + group.Key + "”有顶面无法三角化，已保守停止，未给出绿灯。");
            if (faceVertices.Count == 0)
                throw new InvalidOperationException("天花分组“" + group.Key + "”无法三角化顶面。");
            List<CeilingFaceFootprint> faceFootprints =
                BuildCeilingFaceFootprints(faces, group.Key, options.HatchSizeMm);
            double gMinX = faceVertices.Min(x => x.X) * MmPerFoot;
            double gMinY = faceVertices.Min(x => x.Y) * MmPerFoot;
            double gMaxX = faceVertices.Max(x => x.X) * MmPerFoot;
            double gMaxY = faceVertices.Max(x => x.Y) * MmPerFoot;

            // 自动设备发现只看天花组的实际 XY 包络与合理 Z 带，不向外扩 3m。
            var discoveryRoi = new PlenumAnalysisService.Bounds3
            {
                MinX = gMinX / MmPerFoot,
                MinY = gMinY / MmPerFoot,
                MinZ = topFt - 1000.0 / MmPerFoot,
                MaxX = gMaxX / MmPerFoot,
                MaxY = gMaxY / MmPerFoot,
                MaxZ = topFt + 2000.0 / MmPerFoot
            };
            var discoveryProbe = new PlenumAnalysisResult();
            List<PlenumAnalysisService.Candidate> discoveryCandidates =
                PlenumAnalysisService.CollectCandidates(
                    doc,
                    discoveryRoi,
                    discoveryProbe,
                    result.LinkScope);
            discoveryCandidates = discoveryCandidates
                .Where(x => CandidateIsInLinkScope(x, result.LinkScope))
                .ToList();
            RegisterCollectionFailures(
                result, "device_discovery_collection", discoveryProbe.CandidateCollectionFailures);

            // 障碍/楼板需要覆盖候选口与梯具操作区，但此集合绝不用于自动设备发现。
            const double obstaclePadMm = 2200.0;
            var obstacleRoi = new PlenumAnalysisService.Bounds3
            {
                MinX = (gMinX - obstaclePadMm) / MmPerFoot,
                MinY = (gMinY - obstaclePadMm) / MmPerFoot,
                MinZ = topFt - 5000.0 / MmPerFoot,
                MaxX = (gMaxX + obstaclePadMm) / MmPerFoot,
                MaxY = (gMaxY + obstaclePadMm) / MmPerFoot,
                MaxZ = topFt + 2000.0 / MmPerFoot
            };
            var obstacleProbe = new PlenumAnalysisResult();
            List<PlenumAnalysisService.Candidate> candidates =
                PlenumAnalysisService.CollectCandidates(
                    doc,
                    obstacleRoi,
                    obstacleProbe,
                    result.LinkScope);
            candidates.AddRange(MaintenanceWallObstacleCollector.Collect(
                doc,
                obstacleRoi,
                obstacleProbe.CandidateCollectionFailures,
                result.LinkScope));
            candidates = candidates
                .Where(x => CandidateIsInLinkScope(x, result.LinkScope))
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.SourceKey))
                .GroupBy(x => x.SourceKey, StringComparer.Ordinal)
                .Select(x => x.First())
                .ToList();
            RegisterCollectionFailures(
                result, "obstacle_collection", obstacleProbe.CandidateCollectionFailures);
            if (candidates.Count == 0)
                throw new InvalidOperationException("天花分组“" + group.Key + "”周边障碍几何不可读，不能给出正式结论。");

            foreach (Element ceiling in group.Ceilings)
            {
                result.CeilingSources.Add(ToElementRef(doc, ceiling));
                AddEvidenceSource(result, ToElementRef(doc, ceiling));
            }

            AddEvidenceSources(
                result,
                discoveryCandidates.Concat(candidates).Select(ToElementRef));

            List<DeviceWork> resolved = ResolveDevices(
                doc, discoveryCandidates, deviceRefs, result);
            var devices = new List<DeviceWork>();
            foreach (DeviceWork device in resolved)
            {
                bool projectionUnverified;
                if (DeviceBelongsToCeilingProjection(
                    device, faces, topFt,
                    topFt - 1000.0 / MmPerFoot,
                    topFt + 2000.0 / MmPerFoot,
                    out projectionUnverified))
                {
                    if (projectionUnverified)
                    {
                        device.GeometryUnverified = true;
                        result.Warnings.Add("设备 " + device.Info.GetDisplayName() +
                                            " 的天花投影有部分面无法验证，结果不得正式通过。");
                    }
                    devices.Add(device);
                    AddEvidenceSource(result, ToElementRef(device));
                }
                else if (projectionUnverified)
                {
                    RecordCoverageFailure(
                        result,
                        "device_projection",
                        DeviceSourceKey(device),
                        device.Info.LinkInstanceId > 0 ? (long?)device.Info.LinkInstanceId : null,
                        device.LinkInstanceUniqueId,
                        device.Info.ElementId,
                        device.Element == null || device.Element.Category == null
                            ? string.Empty
                            : device.Element.Category.Name,
                        device.Info.Mark,
                        "设备与天花组投影关系无法验证，已保守排除");
                    AddEvidenceSource(result, ToElementRef(device));
                }
            }
            int deviceIndex = 0;
            foreach (DeviceWork device in devices)
            {
                deviceIndex++;
                device.Info.DeviceNo = deviceIndex.ToString("00");
                device.Info.GroupKey = group.Key;
                device.Info.CeilingTopMm = topZMm;
                AnalyzeTarget(doc, faces, faceFootprints, topZMm, candidates,
                    device, devices, result, options);
            }
        }

        private sealed class DeviceWork
        {
            public HandReachTargetInfo Info = new HandReachTargetInfo();
            public Element Element;
            public Transform ToHost;
            public XYZ HostLocationPoint;
            public string LinkInstanceUniqueId = string.Empty;
            public bool GeometryUnverified;
            public readonly List<Solid> HostSolids = new List<Solid>();
            public readonly List<XYZ> HostVertices = new List<XYZ>();
        }

        private static List<DeviceWork> ResolveDevices(
            Document doc,
            IList<PlenumAnalysisService.Candidate> candidates,
            IList<HandReachDeviceInput> deviceRefs,
            HandReachAnalysisResult result)
        {
            var output = new List<DeviceWork>();

            if (deviceRefs != null && deviceRefs.Count > 0)
            {
                foreach (HandReachDeviceInput input in deviceRefs)
                {
                    RevitLinkInstance link = doc.GetElement(new ElementId(input.LinkInstanceId)) as RevitLinkInstance;
                    if (link == null)
                        throw new InvalidOperationException("找不到链接实例 " + input.LinkInstanceId + "。");
                    Document linkDoc = link.GetLinkDocument();
                    if (linkDoc == null)
                        throw new InvalidOperationException("链接 " + input.LinkInstanceId + " 未加载。");
                    Element element = linkDoc.GetElement(new ElementId(input.ElementId));
                    if (element == null)
                        throw new InvalidOperationException("链接 " + input.LinkInstanceId + " 内找不到图元 " + input.ElementId + "。");
                    DeviceWork work = BuildDeviceWork(element, link.GetTotalTransform());
                    if (work != null)
                    {
                        work.Info.LinkInstanceId = input.LinkInstanceId;
                        work.LinkInstanceUniqueId = Safe(link.UniqueId);
                        work.Info.TargetKey = MaintenanceStableIdentity.LinkedElementKey(
                            work.LinkInstanceUniqueId, element.UniqueId);
                        output.Add(work);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "指定设备 " + input.LinkInstanceId + ":" + input.ElementId +
                            " 的实体几何不可验证，已停止分析。");
                    }
                }
                return output;
            }

            foreach (PlenumAnalysisService.Candidate candidate in candidates)
            {
                if (candidate == null || candidate.Element == null) continue;
                if (candidate.Category != BuiltInCategory.OST_MechanicalEquipment) continue;
                if (candidate.ToHost == null)
                    throw new InvalidOperationException(
                        "自动发现设备 " + candidate.Element.Id.Value +
                        " 缺少宿主变换，已停止分析，未按宿主坐标放行。");
                DeviceWork work;
                try { work = BuildDeviceWork(candidate.Element, candidate.ToHost); }
                catch (Exception ex)
                {
                    RecordDeviceDiscoveryFailure(
                        result, candidate,
                        "设备几何构建异常：" + ex.GetType().Name);
                    continue;
                }
                if (work == null)
                {
                    RecordDeviceDiscoveryFailure(
                        result, candidate,
                        "机械设备无可验证 Solid 或可三角化实体顶点");
                    continue;
                }
                if (candidate.Source != null && candidate.Source.LinkInstanceId.HasValue)
                {
                    work.Info.LinkInstanceId = candidate.Source.LinkInstanceId.Value;
                    work.LinkInstanceUniqueId = Safe(candidate.Source.LinkInstanceUniqueId);
                }
                work.Info.TargetKey = candidate.SourceKey;
                output.Add(work);
            }
            return output.OrderBy(x => x.Info.LinkInstanceId)
                         .ThenBy(x => x.Info.ElementId)
                         .ToList();
        }

        private static DeviceWork BuildDeviceWork(Element element, Transform toHost)
        {
            if (element == null) return null;
            if (toHost == null)
                throw new InvalidOperationException("设备宿主变换为空，无法验证几何。");
            var work = new DeviceWork
            {
                Element = element,
                ToHost = toHost
            };
            work.Info.ElementId = element.Id.Value;
            work.Info.EquipmentName = ResolveEquipmentName(element);
            work.Info.Mark = ReadMark(element);
            LocationPoint locationPoint = element.Location as LocationPoint;
            if (locationPoint != null)
                work.HostLocationPoint = toHost.OfPoint(locationPoint.Point);
            work.Info.TargetKey = MaintenanceStableIdentity.HostElementKey(element.UniqueId);

            CollectSolids(element.get_Geometry(new Options
            {
                DetailLevel = ViewDetailLevel.Fine,
                IncludeNonVisibleObjects = false,
                ComputeReferences = false
            }), work.ToHost, work.HostSolids);
            if (work.HostSolids.Count == 0)
                return null; // 无几何的设备无法分析，诚实跳过
            foreach (Solid solid in work.HostSolids)
            {
                foreach (Face face in solid.Faces)
                {
                    try
                    {
                        Mesh mesh = face.Triangulate(0.5);
                        foreach (XYZ vertex in mesh.Vertices)
                            work.HostVertices.Add(vertex);
                    }
                    catch
                    {
                        work.GeometryUnverified = true;
                    }
                }
            }
            if (work.HostVertices.Count == 0) return null;
            return work;
        }

        private static bool DeviceBelongsToCeilingProjection(
            DeviceWork device,
            IList<PlanarFace> ceilingFaces,
            double ceilingTopFt,
            double zMinFt,
            double zMaxFt,
            out bool projectionUnverified)
        {
            projectionUnverified = false;
            if (device == null || device.HostVertices.Count == 0) return false;
            double deviceMinZ = device.HostVertices.Min(x => x.Z);
            double deviceMaxZ = device.HostVertices.Max(x => x.Z);
            if (deviceMaxZ < zMinFt || deviceMinZ > zMaxFt) return false;

            double minX = device.HostVertices.Min(x => x.X);
            double minY = device.HostVertices.Min(x => x.Y);
            double maxX = device.HostVertices.Max(x => x.X);
            double maxY = device.HostVertices.Max(x => x.Y);
            XYZ membershipPoint = device.HostLocationPoint ?? new XYZ(
                (minX + maxX) * 0.5,
                (minY + maxY) * 0.5,
                (deviceMinZ + deviceMaxZ) * 0.5);
            bool pointUnverified;
            bool belongs = PointInsideCeilingProjection(
                new XYZ(membershipPoint.X, membershipPoint.Y, ceilingTopFt),
                ceilingFaces,
                out pointUnverified);
            projectionUnverified = pointUnverified;
            return belongs;
        }

        private static bool PointInsideCeilingProjection(
            XYZ point,
            IEnumerable<PlanarFace> ceilingFaces,
            out bool projectionUnverified)
        {
            projectionUnverified = false;
            foreach (PlanarFace face in ceilingFaces)
            {
                try
                {
                    IntersectionResult projection = face.Project(point);
                    if (projection != null &&
                        projection.Distance <= 2.0 / MmPerFoot &&
                        face.IsInside(projection.UVPoint))
                        return true;
                }
                catch
                {
                    projectionUnverified = true;
                }
            }
            return false;
        }

        private static List<CeilingFaceFootprint> BuildCeilingFaceFootprints(
            IEnumerable<PlanarFace> faces,
            string groupKey,
            double hatchSizeMm)
        {
            string openingLabel = FormatSquareOpening(hatchSizeMm);
            var output = new List<CeilingFaceFootprint>();
            foreach (PlanarFace face in faces)
            {
                var footprint = new CeilingFaceFootprint { Face = face };
                try
                {
                    foreach (EdgeArray edgeLoop in face.EdgeLoops)
                    {
                        var loop = new List<MaintenancePoint2>();
                        foreach (Edge edge in edgeLoop)
                        {
                            Curve curve = edge.AsCurveFollowingFace(face);
                            IList<XYZ> points = curve.Tessellate();
                            for (int i = 0; i < points.Count; i++)
                            {
                                var point = new MaintenancePoint2(
                                    points[i].X * MmPerFoot,
                                    points[i].Y * MmPerFoot);
                                if (loop.Count == 0 ||
                                    Math.Abs(loop[loop.Count - 1].X - point.X) > 1e-6 ||
                                    Math.Abs(loop[loop.Count - 1].Y - point.Y) > 1e-6)
                                    loop.Add(point);
                            }
                        }
                        if (loop.Count > 1 &&
                            Math.Abs(loop[0].X - loop[loop.Count - 1].X) <= 1e-6 &&
                            Math.Abs(loop[0].Y - loop[loop.Count - 1].Y) <= 1e-6)
                            loop.RemoveAt(loop.Count - 1);
                        if (loop.Count < 3)
                            throw new InvalidOperationException("face boundary loop has fewer than three points");
                        footprint.BoundaryLoops.Add(loop);
                    }
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "天花分组“" + groupKey + "”的真实顶面边界不可完整读取；" +
                        openingLabel + "口无法做完整包含验证，已保守停止。", ex);
                }
                if (footprint.BoundaryLoops.Count == 0)
                    throw new InvalidOperationException(
                        "天花分组“" + groupKey + "”的真实顶面没有可读边界；" +
                        openingLabel + "口无法做完整包含验证，已保守停止。");
                IEnumerable<MaintenancePoint2> boundaryPoints =
                    footprint.BoundaryLoops.SelectMany(x => x);
                footprint.SurfaceKey = BuildPlanarSurfaceKey(
                    "CEILING:" + groupKey,
                    face.Origin,
                    face.FaceNormal,
                    0.0) +
                    "|X=" + (long)Math.Round(boundaryPoints.Min(x => x.X) * 10.0) +
                    "," + (long)Math.Round(boundaryPoints.Max(x => x.X) * 10.0) +
                    "|Y=" + (long)Math.Round(boundaryPoints.Min(x => x.Y) * 10.0) +
                    "," + (long)Math.Round(boundaryPoints.Max(x => x.Y) * 10.0) +
                    "|L=" + footprint.BoundaryLoops.Count;
                output.Add(footprint);
            }
            return output;
        }

        private static bool HatchFullyContainedInSingleFace(
            IEnumerable<CeilingFaceFootprint> footprints,
            double centerXmm,
            double centerYmm,
            double halfSizeMm,
            double topZMm,
            out CeilingFaceFootprint containingFootprint,
            out bool unverified)
        {
            containingFootprint = null;
            unverified = false;
            foreach (CeilingFaceFootprint footprint in footprints)
            {
                if (!MaintenanceHandReachMath.RectangleFullyContainedInFaceLoops(
                    centerXmm, centerYmm, halfSizeMm, footprint.BoundaryLoops))
                    continue;
                try
                {
                    bool allCornersInside = true;
                    foreach (double dx in new[] { -halfSizeMm, halfSizeMm })
                    foreach (double dy in new[] { -halfSizeMm, halfSizeMm })
                    {
                        XYZ point = new XYZ(
                            (centerXmm + dx) / MmPerFoot,
                            (centerYmm + dy) / MmPerFoot,
                            topZMm / MmPerFoot);
                        IntersectionResult projection = footprint.Face.Project(point);
                        if (projection == null ||
                            projection.Distance > 2.0 / MmPerFoot ||
                            !footprint.Face.IsInside(projection.UVPoint))
                        {
                            allCornersInside = false;
                            break;
                        }
                    }
                    if (allCornersInside)
                    {
                        containingFootprint = footprint;
                        return true;
                    }
                }
                catch
                {
                    unverified = true;
                }
            }
            return false;
        }

        private static void AnalyzeTarget(
            Document doc,
            List<PlanarFace> ceilingFaces,
            List<CeilingFaceFootprint> ceilingFootprints,
            double topZMm,
            List<PlenumAnalysisService.Candidate> candidates,
            DeviceWork device,
            IList<DeviceWork> groupDevices,
            HandReachAnalysisResult result,
            HandReachOptions options)
        {
            var targetResult = new HandReachTargetResult { Target = device.Info };
            result.TargetResults.Add(targetResult);
            if (!result.CoverageComplete)
            {
                targetResult.CandidateAuditComplete = false;
                targetResult.SelectedCandidateAuditComplete = false;
            }
            if (device.GeometryUnverified)
            {
                targetResult.CandidateAuditComplete = false;
                targetResult.SelectedCandidateAuditComplete = false;
                result.Warnings.Add("设备 " + device.Info.GetDisplayName() +
                                    " 的部分实体面无法读取，结果不得正式通过。");
            }

            // 送风方向 / 检修侧方向（与原型一致）
            XYZ supply;
            bool supplyInferred;
            ResolveSupplyDirection(device, out supply, out supplyInferred);
            if (supplyInferred)
            {
                targetResult.CandidateAuditComplete = false;
                targetResult.SelectedCandidateAuditComplete = false;
                result.Warnings.Add("设备 " + device.Info.GetDisplayName() +
                                    " 缺少可验证送风连接器方向，已使用实体长边推断，结果需橙色会审。");
            }
            XYZ service = XYZ.BasisZ.CrossProduct(supply);
            if (service.GetLength() <= Epsilon) service = XYZ.BasisY;
            service = service.Normalize();
            device.Info.SupplyDirectionX = supply.X;
            device.Info.SupplyDirectionY = supply.Y;
            device.Info.ServiceDirectionX = service.X;
            device.Info.ServiceDirectionY = service.Y;

            // 检修面最近代理点（缺厂家操作点；原型公式：服务侧最远 + 送风向中点 + 最低点）
            double serviceMax = device.HostVertices.Max(x => x.DotProduct(service));
            double supplyMin = device.HostVertices.Min(x => x.DotProduct(supply));
            double supplyMax = device.HostVertices.Max(x => x.DotProduct(supply));
            double zMin = device.HostVertices.Min(x => x.Z);
            double zMax = device.HostVertices.Max(x => x.Z);
            double alongMid = (supplyMin + supplyMax) * 0.5;
            XYZ proxy = service.Multiply(serviceMax) +
                        supply.Multiply(alongMid) +
                        XYZ.BasisZ.Multiply(zMin);
            device.Info.ServiceFaceProxyX = proxy.X * MmPerFoot;
            device.Info.ServiceFaceProxyY = proxy.Y * MmPerFoot;
            device.Info.ServiceFaceProxyZ = proxy.Z * MmPerFoot;
            device.Info.OperationPointStatus = HandReachOperationPointStatus.Missing;
            device.Info.OperationPointNote = "厂家未提资操作点；使用检修面最近代理点。";

            double proxyX = device.Info.ServiceFaceProxyX;
            double proxyY = device.Info.ServiceFaceProxyY;
            double modelProxyZ = device.Info.ServiceFaceProxyZ;
            double modelVerticalMm = modelProxyZ - topZMm;
            OpeningPreference openingPreference =
                (OpeningPreference)(int)options.OpeningPreference;
            double verticalMm = modelVerticalMm;
            double proxyZ = modelProxyZ;
            HandReachVerticalGrade verticalGrade =
                MaintenanceHandReachMath.GradeVertical(Math.Abs(verticalMm));
            targetResult.ModelVerticalDifferenceMm = modelVerticalMm;
            targetResult.AnalysisVerticalDifferenceMm = verticalMm;
            targetResult.AnalysisServiceFaceProxyZ = proxyZ;
            targetResult.ModelDeviceMinX = device.HostVertices.Min(x => x.X) * MmPerFoot;
            targetResult.ModelDeviceMinY = device.HostVertices.Min(x => x.Y) * MmPerFoot;
            targetResult.ModelDeviceMinZ = zMin * MmPerFoot;
            targetResult.ModelDeviceMaxX = device.HostVertices.Max(x => x.X) * MmPerFoot;
            targetResult.ModelDeviceMaxY = device.HostVertices.Max(x => x.Y) * MmPerFoot;
            targetResult.ModelDeviceMaxZ = zMax * MmPerFoot;

            double ladderTopMm = topZMm + options.LadderTopAboveCeilingMm;
            targetResult.LadderStatus = HandReachLadderStatus.NotValidatedMissingFloor;

            // 障碍物收集：设备周边窗口内的候选实体；冷媒管/冷凝水管只豁免且保留证据。
            // 架梯楼面在每个候选点正下方按真实楼板面单独解析，不能用设备窗口 bbox 顶替代。
            double winHalf = 2200.0;
            var obstacles = new List<ObstacleWork>();
            var exempts = new List<ObstacleWork>();
            CollectObstacles(candidates, device, proxyX, proxyY,
                topZMm - 5000.0,
                Math.Max(Math.Max(modelProxyZ, proxyZ) + 1500.0,
                    topZMm + 2000.0), winHalf,
                groupDevices,
                result,
                obstacles, exempts);
            if (obstacles.Any(x => x.IsGeometryUnverified) ||
                exempts.Any(x => x.IsGeometryUnverified))
            {
                // 未验证障碍仅使候选集合审计降级。每个候选仍会在 CheckCollision 中
                // 按 fallback bounds 单独 fail-closed；能进入 clear 集合的最佳候选
                // 已经独立避开相关未知项，不应被其他淘汰候选连带封锁。
                targetResult.MarkCandidateSetAuditIncomplete();
            }
            targetResult.ObstacleSolidCount = obstacles.Sum(x => x.Solids.Count);
            targetResult.ExemptSolidCount = exempts.Sum(x => x.Solids.Count);
            foreach (ObstacleWork exempt in exempts)
            {
                targetResult.ExemptEvidence.Add(new HandReachObstacle
                {
                    Key = exempt.Key,
                    UniqueId = exempt.UniqueId,
                    Category = exempt.Category,
                    Name = exempt.Name,
                    SystemType = exempt.SystemType,
                    Relation = exempt.ExemptionReason
                });
            }

            bool allowSideWall = MaintenanceHandReachOpeningPolicy.IsPlaneAllowed(
                OpeningPlaneKind.SideWallVertical,
                openingPreference);
            if (allowSideWall)
            {
                targetResult.SideWallAttempted = true;
                SideWallAnalysisOutcome wallOutcome = AnalyzeSideWallOpenings(
                    proxy,
                    topZMm,
                    ceilingFootprints,
                    obstacles,
                    exempts,
                    options);
                targetResult.SideWallRawSampleCount = wallOutcome.RawSampleCount;
                targetResult.SideWallFaceFitCount = wallOutcome.FaceFitCount;
                targetResult.SideWallDistanceOkCount = wallOutcome.DistanceOkCount;
                targetResult.SideWallOpeningFailCount = wallOutcome.OpeningFailCount;
                targetResult.SideWallCorridorFailCount = wallOutcome.CorridorFailCount;
                targetResult.SideWallLadderFailCount = wallOutcome.LadderFailCount;
                targetResult.SideWallClearCount = wallOutcome.ClearCount;
                AddBlockerEvidence(targetResult, obstacles, wallOutcome.BlockerCounts);

                bool wallHardFeasible = wallOutcome.ClearCount > 0 &&
                    wallOutcome.CandidateAuditComplete &&
                    wallOutcome.ConnectivityAgreed;
                bool selectWall = openingPreference == OpeningPreference.SideWallOnly ||
                    openingPreference == OpeningPreference.AutoPreferSideWall &&
                    wallHardFeasible;
                if (openingPreference == OpeningPreference.AutoPreferSideWall &&
                    !wallHardFeasible)
                {
                    result.Warnings.Add(
                        "设备 " + device.Info.GetDisplayName() +
                        " 的侧墙" + FormatSquareOpening(options.HatchSizeMm) +
                        "方案未达到自动选择硬门槛" +
                        "（clear=" + wallOutcome.ClearCount +
                        "，candidateAuditComplete=" + wallOutcome.CandidateAuditComplete +
                        "，connectivityAgreed=" + wallOutcome.ConnectivityAgreed +
                        "）；已按 fail-closed 回退天花450×450方案，由设备与天花的实际关系决定直接伸手或人员钻入。");
                }
                if (selectWall)
                {
                    ApplySideWallOutcome(targetResult, wallOutcome, options);
                    if (wallOutcome.UnverifiedGeometryCount > 0)
                    {
                        result.Warnings.Add("设备 " + device.Info.GetDisplayName() + " 有 " +
                            wallOutcome.UnverifiedGeometryCount +
                            " 个侧墙候选因面边界、布尔或楼面未知被保守淘汰，未作为净空通过。");
                    }
                    HandReachVerticalGrade wallVerticalGrade = targetResult.Regions.Count == 0
                        ? HandReachVerticalGrade.RecommendedWithin300
                        : MaintenanceHandReachMath.GradeVertical(
                            targetResult.Regions[0].Recommended.VerticalMm);
                    bool sideWallDistanceOver500Review =
                        targetResult.Regions.Count > 0 &&
                        targetResult.Regions[0].Recommended.ObliqueMm >
                            options.MaxDistanceMm + 1e-6;
                    if (sideWallDistanceOver500Review)
                    {
                        result.Warnings.Add(
                            "设备 " + device.Info.GetDisplayName() +
                            " 的侧墙伸手实际距离为 " +
                            targetResult.Regions[0].Recommended.ObliqueMm.ToString("F1") +
                            "mm，已按显式500~600mm橙色待复核规则保留；" +
                            "不属于500mm正式通过范围。");
                    }
                    FinalizeConclusion(
                        targetResult,
                        wallVerticalGrade,
                        result.Warnings,
                        options.HatchSizeMm,
                        sideWallDistanceOver500Review);
                    return;
                }
            }
            targetResult.SelectedOpeningPlane =
                HandReachOpeningPlaneKind.CeilingHorizontal;
            bool ceilingDirectReach =
                MaintenanceHandReachMath.IsCeilingDirectReachMode(
                    topZMm, proxyZ, options.OpeningHeightMm);
            if (!ceilingDirectReach && proxyZ < topZMm)
            {
                targetResult.CandidateAuditComplete = false;
                targetResult.SelectedCandidateAuditComplete = false;
                targetResult.AttentionLevel = HandReachAttentionLevel.Rejected;
                targetResult.Conclusion =
                    "rejected_service_face_below_ceiling_direct_reach_band";
                targetResult.ConclusionReason =
                    "设备检修面低于天花室内侧检修口起算面，既不属于天花直接伸手，也不能建立向上的人员钻入包络。";
                result.Warnings.Add(
                    "设备 " + device.Info.GetDisplayName() +
                    " 的检修面低于天花直接伸手有效带，已停止该天花方案，未再抛出运行异常。");
                return;
            }
            targetResult.CeilingDirectReachApplied = ceilingDirectReach;
            targetResult.CeilingPersonnelEntryApplied = !ceilingDirectReach;
            double ceilingReachStartZ = ceilingDirectReach
                ? MaintenanceHandReachMath.ResolveCeilingDirectReachStartZMm(
                    topZMm, options.OpeningHeightMm)
                : MaintenanceHandReachMath.ResolveCeilingPersonnelEntryTopMm(
                    topZMm, proxyZ, options);
            verticalMm = ceilingDirectReach
                ? Math.Abs(proxyZ - ceilingReachStartZ)
                : modelVerticalMm;
            targetResult.AnalysisVerticalDifferenceMm = verticalMm;
            verticalGrade = ceilingDirectReach
                ? MaintenanceHandReachMath.GradeVertical(verticalMm)
                : HandReachVerticalGrade.PersonnelEntryNotDistanceLimited;

            // 网格采样：代理点周边 1600×1600、40mm 步长（41×41=1681，与原型一致）
            double half = options.HatchSizeMm * 0.5;
            double span = (options.GridPointsPerAxis - 1) * options.GridSpacingMm * 0.5;
            double startX = proxyX - span;
            double startY = proxyY - span;
            int n = options.GridPointsPerAxis;
            var clear = new Dictionary<long, HandReachSample>();
            int unverifiedGeometryCount = 0;

            Func<XYZ, bool> onCeiling = p =>
            {
                foreach (PlanarFace face in ceilingFaces)
                {
                    try
                    {
                        IntersectionResult ir = face.Project(p);
                        if (ir != null && ir.Distance < 2.0 / MmPerFoot && face.IsInside(ir.UVPoint))
                            return true;
                    }
                    catch
                    {
                        unverifiedGeometryCount++;
                    }
                }
                return false;
            };
            Func<double, double, CeilingFaceFootprint> containingCeilingFace = (x, y) =>
            {
                // 3×3 仅作快速前筛；最终必须由同一个真实顶面的完整边界包含。
                foreach (double dx in new[] { -half, 0.0, half })
                foreach (double dy in new[] { -half, 0.0, half })
                    if (!onCeiling(new XYZ((x + dx) / MmPerFoot, (y + dy) / MmPerFoot, topZMm / MmPerFoot)))
                        return null;
                CeilingFaceFootprint footprint;
                bool footprintUnverified;
                bool contained = HatchFullyContainedInSingleFace(
                    ceilingFootprints, x, y, half, topZMm,
                    out footprint, out footprintUnverified);
                if (footprintUnverified) unverifiedGeometryCount++;
                return contained ? footprint : null;
            };

            var blockerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            bool anyFloorSupport = false;

            Func<Solid, IList<FloorSupportResult>, MaintenanceCollisionResult> hitTest =
                (body, supports) => CheckCollision(body, obstacles, supports);

            for (int ix = 0; ix < n; ix++)
            {
                for (int iy = 0; iy < n; iy++)
                {
                    targetResult.RawSampleCount++;
                    double cx;
                    double cy;
                    MaintenanceHandReachMath.CellCenter(ix, iy, startX, startY, options.GridSpacingMm, out cx, out cy);
                    CeilingFaceFootprint ceilingSurface = containingCeilingFace(cx, cy);
                    if (ceilingSurface == null) continue;
                    targetResult.HatchInsideCount++;
                    double edgeX;
                    double edgeY;
                    double horizontalMm;
                    MaintenanceHandReachMath.NearestEdge(cx, cy, proxyX, proxyY, half, out edgeX, out edgeY, out horizontalMm);
                    double reachStartX;
                    double reachStartY;
                    double finalHorizontalMm;
                    MaintenanceHandReachMath.NearestPointInSquare(
                        cx, cy, proxyX, proxyY, half,
                        out reachStartX, out reachStartY,
                        out finalHorizontalMm);
                    double obliqueMm = MaintenanceHandReachMath.ObliqueDistance(
                        reachStartX, reachStartY, ceilingReachStartZ,
                        proxyX, proxyY, proxyZ);
                    if (obliqueMm > options.MaxDistanceMm + 1e-6) continue;
                    targetResult.DistanceOkCount++;
                    HandReachDistanceGrade grade = MaintenanceHandReachMath.GradeDistance(obliqueMm);

                    // 直接伸手时，检修口从天花室内侧贯穿到顶面；
                    // 人员钻入时保持既有天花顶面以上的入口净空检查。
                    double openingBottomZ = ceilingDirectReach
                        ? topZMm - options.OpeningHeightMm
                        : topZMm;
                    Solid opening = MaintenanceGeometryService.MakeBox(
                        new XYZ(cx / MmPerFoot, cy / MmPerFoot, openingBottomZ / MmPerFoot),
                        options.HatchSizeMm / MmPerFoot,
                        options.HatchSizeMm / MmPerFoot,
                        options.OpeningHeightMm / MmPerFoot,
                        XYZ.BasisX);
                    MaintenanceCollisionResult openingHit = hitTest(opening, null);
                    if (!openingHit.IsClear)
                    {
                        targetResult.OpeningFailCount++;
                        if (openingHit.State == MaintenanceCollisionState.Unverified)
                            unverifiedGeometryCount++;
                        RecordBlocker(blockerCounts, openingHit.BlockerKey);
                        continue;
                    }

                    // 仅高位设备建立人员钻入包络；贴近天花的设备从洞口室内侧直接伸手。
                    if (!ceilingDirectReach)
                    {
                        Solid personnelEnvelope = MaintenanceGeometryService.MakeBox(
                            new XYZ(cx / MmPerFoot, cy / MmPerFoot,
                                (topZMm + options.OpeningHeightMm) / MmPerFoot),
                            options.HatchSizeMm / MmPerFoot,
                            options.HatchSizeMm / MmPerFoot,
                            Math.Max(1.0,
                                ceilingReachStartZ - topZMm - options.OpeningHeightMm) / MmPerFoot,
                            XYZ.BasisX);
                        MaintenanceCollisionResult personnelHit =
                            hitTest(personnelEnvelope, null);
                        if (!personnelHit.IsClear)
                        {
                            targetResult.CorridorFailCount++;
                            if (personnelHit.State == MaintenanceCollisionState.Unverified)
                                unverifiedGeometryCount++;
                            RecordBlocker(blockerCounts, personnelHit.BlockerKey);
                            continue;
                        }
                    }

                    XYZ startPt = new XYZ(
                        reachStartX / MmPerFoot,
                        reachStartY / MmPerFoot,
                        ceilingReachStartZ / MmPerFoot);
                    XYZ endPt = new XYZ(proxyX / MmPerFoot, proxyY / MmPerFoot, proxyZ / MmPerFoot);

                    Solid defaultCylinder = MakeCylinder(
                        startPt, endPt, options.DefaultCorridorDiameterMm * 0.5 / MmPerFoot);
                    MaintenanceCollisionResult corridorHit = hitTest(defaultCylinder, null);
                    if (!corridorHit.IsClear)
                    {
                        targetResult.CorridorFailCount++;
                        if (corridorHit.State == MaintenanceCollisionState.Unverified)
                            unverifiedGeometryCount++;
                        RecordBlocker(blockerCounts, corridorHit.BlockerKey);
                        continue;
                    }

                    int exemptHitCount;
                    string exemptUnverifiedReason;
                    if (!TryCountExemptHits(
                        defaultCylinder, exempts, out exemptHitCount, out exemptUnverifiedReason))
                    {
                        targetResult.CorridorFailCount++;
                        unverifiedGeometryCount++;
                        RecordBlocker(blockerCounts, exemptUnverifiedReason);
                        continue;
                    }

                    FloorSupportResult floorSupport = ResolveFloorSupport(
                        obstacles, cx, cy, topZMm);
                    if (floorSupport.State != MaintenanceCollisionState.Clear)
                    {
                        targetResult.LadderFailCount++;
                        if (floorSupport.State == MaintenanceCollisionState.Unverified)
                            unverifiedGeometryCount++;
                        RecordBlocker(blockerCounts,
                            floorSupport.Work == null ? floorSupport.Reason : floorSupport.Work.Key);
                        continue;
                    }
                    anyFloorSupport = true;
                    double ladderFloorMm = floorSupport.FloorMm;

                    // 逐档测试通道直径（200 默认已过，继续测更大档）
                    var sample = new HandReachSample
                    {
                        OpeningPlane = HandReachOpeningPlaneKind.CeilingHorizontal,
                        SurfaceKey = ceilingSurface.SurfaceKey,
                        BoundaryLoopIndex = -1,
                        BoundarySegmentIndex = -1,
                        BoundarySampleIndex = -1,
                        Ix = ix,
                        Iy = iy,
                        CenterX = cx,
                        CenterY = cy,
                        CenterZ = topZMm,
                        EdgeX = edgeX,
                        EdgeY = edgeY,
                        EdgeZ = topZMm,
                        OpeningTangentX = 1.0,
                        OpeningTangentY = 0.0,
                        OpeningInwardX = 0.0,
                        OpeningInwardY = 0.0,
                        OpeningDepthMm = options.OpeningHeightMm,
                        ChannelStartX = startPt.X * MmPerFoot,
                        ChannelStartY = startPt.Y * MmPerFoot,
                        ChannelStartZ = startPt.Z * MmPerFoot,
                        PersonnelEntryTopZ = ceilingDirectReach ? 0.0 : ceilingReachStartZ,
                        HorizontalMm = finalHorizontalMm,
                        ObliqueMm = obliqueMm,
                        VerticalMm = verticalMm,
                        DistanceGrade = grade,
                        ExemptIntersectCount = exemptHitCount,
                        BlockerKey = string.Empty,
                        LadderCenterX = cx,
                        LadderCenterY = cy,
                        LadderFloorMm = ladderFloorMm
                    };
                    sample.CorridorClear = new bool[options.CorridorTestDiametersMm.Length];
                    for (int d = 0; d < options.CorridorTestDiametersMm.Length; d++)
                    {
                        double diameter = options.CorridorTestDiametersMm[d];
                        if (diameter <= options.DefaultCorridorDiameterMm + 1e-6)
                        {
                            sample.CorridorClear[d] = true;
                            continue;
                        }
                        Solid test = MakeCylinder(startPt, endPt, diameter * 0.5 / MmPerFoot);
                        MaintenanceCollisionResult testHit = hitTest(test, null);
                        sample.CorridorClear[d] = testHit.IsClear;
                        if (testHit.State == MaintenanceCollisionState.Unverified)
                            unverifiedGeometryCount++;
                    }

                    // 人字梯 + 下方操作区（X 优先，再 Y）
                    foreach (XYZ dir in new[] { XYZ.BasisX, XYZ.BasisY })
                    {
                        List<Solid> ladders = MaintenanceGeometryService.BuildAFrameLadder(
                            new XYZ(cx / MmPerFoot, cy / MmPerFoot, 0.0),
                            dir,
                            ladderFloorMm / MmPerFoot,
                            ladderTopMm / MmPerFoot);
                        double lengthX, lengthY, widthX, widthY;
                        MaintenanceHandReachMath.OperationZoneAxes(
                            dir.X, dir.Y,
                            out lengthX, out lengthY, out widthX, out widthY);
                        // 直接伸手只验证梯上600×600局部站位；高位人员钻入仍保留
                        // 既有1200×2500完整楼面操作区。两者都继续验证真实人字梯和四脚支撑。
                        double operationZoneLengthMm = ceilingDirectReach
                            ? options.CeilingDirectOperatorZoneLengthMm
                            : options.OperationZoneLengthMm;
                        double operationZoneWidthMm = ceilingDirectReach
                            ? options.CeilingDirectOperatorZoneWidthMm
                            : options.OperationZoneWidthMm;
                        Solid zone = MaintenanceGeometryService.MakeBox(
                            new XYZ(cx / MmPerFoot, cy / MmPerFoot, ladderFloorMm / MmPerFoot),
                            operationZoneLengthMm / MmPerFoot,
                            operationZoneWidthMm / MmPerFoot,
                            (ladderTopMm - ladderFloorMm) / MmPerFoot,
                            new XYZ(lengthX, lengthY, 0.0));
                        double[,] footOffsets = MaintenanceHandReachMath.AFrameFootOffsets(
                            ladderTopMm - ladderFloorMm, dir.X, dir.Y);
                        var footSupports = new List<FloorSupportResult> { floorSupport };
                        bool feetSupported = true;
                        for (int foot = 0; foot < footOffsets.GetLength(0); foot++)
                        {
                            FloorSupportResult footSupport = ResolveFloorSupport(
                                obstacles,
                                cx + footOffsets[foot, 0],
                                cy + footOffsets[foot, 1],
                                topZMm);
                            if (footSupport.State == MaintenanceCollisionState.Unverified)
                                unverifiedGeometryCount++;
                            if (footSupport.State != MaintenanceCollisionState.Clear ||
                                Math.Abs(footSupport.FloorMm - ladderFloorMm) > 10.0)
                            {
                                feetSupported = false;
                                RecordBlocker(blockerCounts,
                                    footSupport.Work == null ? footSupport.Reason : footSupport.Work.Key);
                                break;
                            }
                            footSupports.Add(footSupport);
                        }
                        if (!feetSupported) continue;

                        bool ladderClear = true;
                        foreach (Solid ladder in ladders)
                        {
                            MaintenanceCollisionResult ladderHit = hitTest(ladder, footSupports);
                            if (ladderHit.State == MaintenanceCollisionState.Unverified)
                                unverifiedGeometryCount++;
                            if (!ladderHit.IsClear)
                            {
                                ladderClear = false;
                                RecordBlocker(blockerCounts, ladderHit.BlockerKey);
                                break;
                            }
                        }
                        MaintenanceCollisionResult zoneHit = hitTest(zone, footSupports);
                        if (zoneHit.State == MaintenanceCollisionState.Unverified)
                            unverifiedGeometryCount++;
                        if (!zoneHit.IsClear)
                            RecordBlocker(blockerCounts, zoneHit.BlockerKey);
                        if (ladderClear && zoneHit.IsClear)
                        {
                            sample.LadderDirection = dir == XYZ.BasisX ? "X" : "Y";
                            sample.LadderAlongX = dir.X;
                            sample.LadderAlongY = dir.Y;
                            sample.OperationZoneClear = true;
                            break;
                        }
                    }
                    if (string.IsNullOrEmpty(sample.LadderDirection))
                    {
                        targetResult.LadderFailCount++;
                        continue;
                    }

                    clear[MaintenanceHandReachMath.Pack(ix, iy)] = sample;
                }
            }

            targetResult.ClearCount = clear.Count;
            if (unverifiedGeometryCount > 0)
            {
                targetResult.MarkCandidateSetAuditIncomplete();
                result.Warnings.Add("设备 " + device.Info.GetDisplayName() + " 有 " +
                                    unverifiedGeometryCount +
                                    " 个未选择候选因几何/变换/布尔未知被保守判为未验证，未作为净空通过；" +
                                    "已独立完成全链验证的最佳候选不受连带影响。");
            }

            // 区域合并：四/八邻接双算法，区域数须一致
            var components4 = new List<List<long>>();
            var components8 = new List<List<long>>();
            foreach (IGrouping<string, KeyValuePair<long, HandReachSample>> surfaceGroup in
                clear.GroupBy(x => x.Value.SurfaceKey, StringComparer.Ordinal))
            {
                var surfaceKeys = new HashSet<long>(surfaceGroup.Select(x => x.Key));
                components4.AddRange(MaintenanceHandReachMath.MergeRegions(
                    surfaceKeys, n, n, false));
                components8.AddRange(MaintenanceHandReachMath.MergeRegions(
                    surfaceKeys, n, n, true));
            }
            targetResult.Regions4Count = components4.Count;
            targetResult.Regions8Count = components8.Count;
            targetResult.ConnectivityAgreed = MaintenanceHandReachMath.ConnectivityAgrees(
                components4.Count, components8.Count);
            if (!targetResult.ConnectivityAgreed)
            {
                targetResult.CandidateAuditComplete = false;
                result.Warnings.Add("设备 " + device.Info.GetDisplayName() +
                                    " 的40mm区域四邻接/八邻接数量不一致；禁止正式绿灯，" +
                                    "仅当推荐候选自身全链证据完整时允许橙色待复核。");
            }

            int regionNo = 0;
            foreach (List<long> component in components4)
            {
                regionNo++;
                List<HandReachSample> rows = component.Select(k => clear[k]).ToList();
                HandReachSample recommended = rows
                    .OrderBy(x => x.ObliqueMm)
                    .ThenByDescending(x => ClearCount(x))
                    .ThenBy(x => x.CenterX)
                    .ThenBy(x => x.CenterY)
                    .First();
                var region = new HandReachRegion
                {
                    OpeningPlane = HandReachOpeningPlaneKind.CeilingHorizontal,
                    SurfaceKey = recommended.SurfaceKey,
                    RegionNo = regionNo,
                    PointCount = rows.Count,
                    MinX = rows.Min(x => x.CenterX),
                    MaxX = rows.Max(x => x.CenterX),
                    MinY = rows.Min(x => x.CenterY),
                    MaxY = rows.Max(x => x.CenterY),
                    MinZ = rows.Min(x => x.CenterZ),
                    MaxZ = rows.Max(x => x.CenterZ),
                    AreaM2 = rows.Count * options.GridSpacingMm * options.GridSpacingMm / 1000000.0,
                    Recommended = recommended,
                    RecommendedCorridorClear = (bool[])recommended.CorridorClear.Clone(),
                    RecommendedExemptIntersectCount = recommended.ExemptIntersectCount,
                    RecommendedLadderDirection = recommended.LadderDirection,
                    RecommendedOperationZoneClear = recommended.OperationZoneClear,
                    RecommendedVerticalGrade = verticalGrade,
                    MaxTestedClearDiameterMm = MaxClearDiameter(recommended, options),
                    RecommendedBlockerKey = recommended.BlockerKey ?? string.Empty
                };
                targetResult.Regions.Add(region);
            }

            OrderOpeningRegions(targetResult.Regions, OpeningPreference.CeilingOnly);
            for (int i = 0; i < targetResult.Regions.Count; i++)
                targetResult.Regions[i].RegionNo = i + 1;
            targetResult.HasSelectedOpening = targetResult.Regions.Count > 0;

            if (targetResult.Regions.Count > 0)
            {
                targetResult.LadderStatus = HandReachLadderStatus.Validated;
                targetResult.LadderFloorMm = targetResult.Regions[0].Recommended.LadderFloorMm;
                targetResult.LadderTopMm = ladderTopMm;
            }
            else if (anyFloorSupport)
            {
                targetResult.LadderStatus = HandReachLadderStatus.Rejected;
            }
            else if (targetResult.LadderFailCount > 0)
            {
                result.Warnings.Add("天花分组内设备 " + device.Info.GetDisplayName() +
                                    " 的候选点正下方未找到可验证楼板实体支撑，梯具未通过。");
            }

            // 结论与关注级（规则：缺操作点至少高关注；B/C 距离或垂直>300 → 橙色会审）
            AddBlockerEvidence(targetResult, obstacles, blockerCounts);
            FinalizeConclusion(
                targetResult,
                verticalGrade,
                result.Warnings,
                options.HatchSizeMm);
        }

        private static SideWallAnalysisOutcome AnalyzeSideWallOpenings(
            XYZ proxy,
            double ceilingTopMm,
            List<CeilingFaceFootprint> ceilingFootprints,
            List<ObstacleWork> obstacles,
            List<ObstacleWork> exempts,
            HandReachOptions options)
        {
            var outcome = new SideWallAnalysisOutcome();
            double sideWallDistanceLimitMm =
                MaintenanceHandReachMath.ResolveSideWallDistanceLimitMm(options);
            int surfaceUnverifiedCount;
            List<SideWallSurface> surfaces = BuildSideWallSurfaces(
                ceilingFootprints, obstacles, proxy, ceilingTopMm,
                sideWallDistanceLimitMm,
                out surfaceUnverifiedCount);
            outcome.UnverifiedGeometryCount += surfaceUnverifiedCount;
            if (surfaceUnverifiedCount > 0) outcome.CandidateAuditComplete = false;

            double half = options.HatchSizeMm * 0.5;
            double span = (options.GridPointsPerAxis - 1) * options.GridSpacingMm * 0.5;
            int n = options.GridPointsPerAxis;
            foreach (SideWallSurface surface in surfaces
                .OrderBy(x => x.SurfaceKey, StringComparer.Ordinal))
            {
                double targetU = proxy.DotProduct(surface.Tangent) * MmPerFoot;
                double targetV = proxy.Z * MmPerFoot;
                double startU = targetU - span;
                double startV = targetV - span;
                var clear = new Dictionary<long, HandReachSample>();

                for (int ix = 0; ix < n; ix++)
                for (int iy = 0; iy < n; iy++)
                {
                    outcome.RawSampleCount++;
                    double centerU;
                    double centerV;
                    MaintenanceHandReachMath.CellCenter(
                        ix, iy, startU, startV, options.GridSpacingMm,
                        out centerU, out centerV);
                    if (centerV - half < ceilingTopMm - 1e-6) continue;
                    if (!MaintenanceHandReachMath.RectangleFullyContainedInFaceLoops(
                            centerU, centerV, half, surface.InnerLoops) ||
                        !MaintenanceHandReachMath.RectangleFullyContainedInFaceLoops(
                            centerU, centerV, half, surface.OuterLoops))
                        continue;
                    bool faceContainmentUnverified;
                    if (!SideWallSquareInsideBothFaces(
                        surface, centerU, centerV, half,
                        out faceContainmentUnverified))
                    {
                        if (faceContainmentUnverified)
                        {
                            outcome.UnverifiedGeometryCount++;
                            outcome.CandidateAuditComplete = false;
                        }
                        continue;
                    }
                    outcome.FaceFitCount++;

                    double edgeU;
                    double edgeV;
                    double localEdgeDistance;
                    MaintenanceHandReachOpeningPolicy.NearestSideWallOpeningEdgeLocalUv(
                        centerU, centerV, targetU, targetV,
                        options.HatchSizeMm,
                        out edgeU, out edgeV, out localEdgeDistance);
                    XYZ centerPoint = PointOnVerticalFace(
                        surface.InnerOrigin, surface.Tangent, centerU, centerV);
                    XYZ edgePoint = PointOnVerticalFace(
                        surface.InnerOrigin, surface.Tangent, edgeU, edgeV);
                    double obliqueMm = edgePoint.DistanceTo(proxy) * MmPerFoot;
                    if (!MaintenanceHandReachMath.IsSideWallDistanceCandidateEligible(
                        obliqueMm, options)) continue;
                    outcome.DistanceOkCount++;

                    Solid opening;
                    try
                    {
                        opening = MakeSideWallOpeningSolid(
                            surface, centerPoint, options.HatchSizeMm);
                    }
                    catch
                    {
                        opening = null;
                    }
                    if (opening == null)
                    {
                        outcome.OpeningFailCount++;
                        outcome.UnverifiedGeometryCount++;
                        outcome.CandidateAuditComplete = false;
                        RecordBlocker(outcome.BlockerCounts,
                            "UNVERIFIED:side-wall-opening-solid");
                        continue;
                    }
                    MaintenanceCollisionResult openingHit = CheckCollision(
                        opening, obstacles, null, surface.Owner.Key);
                    if (!openingHit.IsClear)
                    {
                        outcome.OpeningFailCount++;
                        if (openingHit.State == MaintenanceCollisionState.Unverified)
                        {
                            outcome.UnverifiedGeometryCount++;
                            outcome.CandidateAuditComplete = false;
                        }
                        RecordBlocker(outcome.BlockerCounts, openingHit.BlockerKey);
                        continue;
                    }

                    XYZ inward = surface.Tangent.Multiply((centerU - edgeU) / MmPerFoot) +
                                  XYZ.BasisZ.Multiply((centerV - edgeV) / MmPerFoot);
                    if (inward.GetLength() <= Epsilon) inward = surface.Tangent;
                    else inward = inward.Normalize();
                    XYZ channelStart = edgePoint +
                        inward.Multiply(options.ChannelInwardOffsetMm / MmPerFoot) +
                        surface.NormalTowardTarget.Multiply(
                            options.ChannelCeilingLiftMm / MmPerFoot);
                    Solid defaultCylinder = MakeCylinder(
                        channelStart,
                        proxy,
                        options.DefaultCorridorDiameterMm * 0.5 / MmPerFoot);
                    if (defaultCylinder == null)
                    {
                        outcome.CorridorFailCount++;
                        outcome.UnverifiedGeometryCount++;
                        outcome.CandidateAuditComplete = false;
                        RecordBlocker(outcome.BlockerCounts,
                            "UNVERIFIED:side-wall-channel-solid");
                        continue;
                    }
                    MaintenanceCollisionResult corridorHit = CheckCollision(
                        defaultCylinder, obstacles, null, surface.Owner.Key);
                    if (!corridorHit.IsClear)
                    {
                        outcome.CorridorFailCount++;
                        if (corridorHit.State == MaintenanceCollisionState.Unverified)
                        {
                            outcome.UnverifiedGeometryCount++;
                            outcome.CandidateAuditComplete = false;
                        }
                        RecordBlocker(outcome.BlockerCounts, corridorHit.BlockerKey);
                        continue;
                    }
                    int exemptHitCount;
                    string exemptUnverifiedReason;
                    if (!TryCountExemptHits(
                        defaultCylinder, exempts,
                        out exemptHitCount, out exemptUnverifiedReason))
                    {
                        outcome.CorridorFailCount++;
                        outcome.UnverifiedGeometryCount++;
                        outcome.CandidateAuditComplete = false;
                        RecordBlocker(outcome.BlockerCounts, exemptUnverifiedReason);
                        continue;
                    }

                    XYZ outward = surface.NormalTowardTarget.Negate();
                    XYZ outsideFacePoint = centerPoint +
                        outward.Multiply(surface.ThicknessFt);
                    double ladderTopMm = centerV + half +
                        options.LadderTopAboveCeilingMm;
                    // 先在最大半展开宽度之外探测楼面，再按实测梯高收敛到
                    // halfSpread + 100mm；这样内侧梯脚不会穿进 owner wall。
                    double ladderStandOffMm = 800.0;
                    XYZ ladderPlan = outsideFacePoint +
                        outward.Multiply(ladderStandOffMm / MmPerFoot);
                    double ladderXmm = ladderPlan.X * MmPerFoot;
                    double ladderYmm = ladderPlan.Y * MmPerFoot;
                    FloorSupportResult floorSupport = ResolveFloorSupport(
                        obstacles, ladderXmm, ladderYmm, ceilingTopMm);
                    if (!AcceptSideWallFloorSupport(floorSupport, outcome))
                        continue;
                    double ladderFloorMm = floorSupport.FloorMm;
                    if (ladderTopMm <= ladderFloorMm + 100.0)
                    {
                        outcome.LadderFailCount++;
                        RecordBlocker(outcome.BlockerCounts,
                            "side-wall ladder height is not positive");
                        continue;
                    }
                    double ladderHeightMm = ladderTopMm - ladderFloorMm;
                    double halfSpreadMm = Math.Max(
                        450.0, Math.Min(700.0, ladderHeightMm * 0.22));
                    ladderStandOffMm = halfSpreadMm + 100.0;
                    ladderPlan = outsideFacePoint +
                        outward.Multiply(ladderStandOffMm / MmPerFoot);
                    ladderXmm = ladderPlan.X * MmPerFoot;
                    ladderYmm = ladderPlan.Y * MmPerFoot;
                    floorSupport = ResolveFloorSupport(
                        obstacles, ladderXmm, ladderYmm, ceilingTopMm);
                    if (!AcceptSideWallFloorSupport(floorSupport, outcome))
                        continue;
                    if (Math.Abs(floorSupport.FloorMm - ladderFloorMm) > 10.0)
                    {
                        outcome.LadderFailCount++;
                        RecordBlocker(outcome.BlockerCounts,
                            "side-wall ladder center floor changes over 10mm");
                        continue;
                    }
                    ladderFloorMm = floorSupport.FloorMm;

                    var sample = new HandReachSample
                    {
                        OpeningPlane = HandReachOpeningPlaneKind.SideWallVertical,
                        SurfaceKey = surface.SurfaceKey,
                        BoundaryLoopIndex = surface.BoundaryLoopIndex,
                        BoundarySegmentIndex = surface.BoundarySegmentIndex,
                        BoundarySampleIndex = -1,
                        Ix = ix,
                        Iy = iy,
                        CenterX = centerPoint.X * MmPerFoot,
                        CenterY = centerPoint.Y * MmPerFoot,
                        CenterZ = centerV,
                        EdgeX = edgePoint.X * MmPerFoot,
                        EdgeY = edgePoint.Y * MmPerFoot,
                        EdgeZ = edgeV,
                        OpeningTangentX = surface.Tangent.X,
                        OpeningTangentY = surface.Tangent.Y,
                        OpeningInwardX = surface.NormalTowardTarget.X,
                        OpeningInwardY = surface.NormalTowardTarget.Y,
                        OpeningDepthMm = surface.ThicknessFt * MmPerFoot,
                        UsesVirtualBoundaryWall = surface.IsVirtualBoundary,
                        BoundaryStartX = surface.BoundaryStart.X,
                        BoundaryStartY = surface.BoundaryStart.Y,
                        BoundaryEndX = surface.BoundaryEnd.X,
                        BoundaryEndY = surface.BoundaryEnd.Y,
                        VirtualWallBottomMm = surface.WallBottomMm,
                        VirtualWallTopMm = surface.WallTopMm,
                        ChannelStartX = channelStart.X * MmPerFoot,
                        ChannelStartY = channelStart.Y * MmPerFoot,
                        ChannelStartZ = channelStart.Z * MmPerFoot,
                        HorizontalMm = Math.Sqrt(
                            Math.Pow((proxy.X - edgePoint.X) * MmPerFoot, 2.0) +
                            Math.Pow((proxy.Y - edgePoint.Y) * MmPerFoot, 2.0)),
                        ObliqueMm = obliqueMm,
                        VerticalMm = Math.Abs(targetV - edgeV),
                        DistanceGrade = MaintenanceHandReachMath.GradeDistance(obliqueMm),
                        ExemptIntersectCount = exemptHitCount,
                        BlockerKey = string.Empty,
                        LadderDirection = "WALL_OUTWARD",
                        LadderCenterX = ladderXmm,
                        LadderCenterY = ladderYmm,
                        LadderAlongX = outward.X,
                        LadderAlongY = outward.Y,
                        LadderFloorMm = ladderFloorMm
                    };
                    sample.CorridorClear = new bool[options.CorridorTestDiametersMm.Length];
                    for (int d = 0; d < options.CorridorTestDiametersMm.Length; d++)
                    {
                        double diameter = options.CorridorTestDiametersMm[d];
                        if (diameter <= options.DefaultCorridorDiameterMm + 1e-6)
                        {
                            sample.CorridorClear[d] = true;
                            continue;
                        }
                        Solid test = MakeCylinder(
                            channelStart, proxy, diameter * 0.5 / MmPerFoot);
                        MaintenanceCollisionResult testHit = test == null
                            ? CollisionResult(
                                MaintenanceCollisionState.Unverified,
                                "UNVERIFIED:side-wall-channel-solid",
                                "channel solid unavailable")
                            : CheckCollision(test, obstacles, null, surface.Owner.Key);
                        sample.CorridorClear[d] = testHit.IsClear;
                        if (testHit.State == MaintenanceCollisionState.Unverified)
                        {
                            outcome.UnverifiedGeometryCount++;
                            outcome.CandidateAuditComplete = false;
                        }
                    }

                    List<Solid> ladders = MaintenanceGeometryService.BuildAFrameLadder(
                        new XYZ(ladderPlan.X, ladderPlan.Y, 0.0),
                        outward,
                        ladderFloorMm / MmPerFoot,
                        ladderTopMm / MmPerFoot);
                    double[,] footOffsets = MaintenanceHandReachMath.AFrameFootOffsets(
                        ladderTopMm - ladderFloorMm,
                        outward.X,
                        outward.Y);
                    var footSupports = new List<FloorSupportResult> { floorSupport };
                    bool feetSupported = true;
                    for (int foot = 0; foot < footOffsets.GetLength(0); foot++)
                    {
                        FloorSupportResult support = ResolveFloorSupport(
                            obstacles,
                            ladderXmm + footOffsets[foot, 0],
                            ladderYmm + footOffsets[foot, 1],
                            ceilingTopMm);
                        if (support.State == MaintenanceCollisionState.Unverified)
                        {
                            outcome.UnverifiedGeometryCount++;
                            outcome.CandidateAuditComplete = false;
                        }
                        if (support.State != MaintenanceCollisionState.Clear ||
                            Math.Abs(support.FloorMm - ladderFloorMm) > 10.0)
                        {
                            feetSupported = false;
                            RecordBlocker(outcome.BlockerCounts,
                                support.Work == null ? support.Reason : support.Work.Key);
                            break;
                        }
                        footSupports.Add(support);
                    }
                    if (!feetSupported)
                    {
                        outcome.LadderFailCount++;
                        continue;
                    }

                    // 侧墙口只验证梯上局部人体站位，不复用天花口1200×2500楼面操作区。
                    // 包络以实际梯具平面中心为中心，深度轴沿墙外法向、宽度轴沿墙切向。
                    XYZ zoneCenter = ladderPlan;
                    Solid operationZone = MaintenanceGeometryService.MakeBox(
                        new XYZ(zoneCenter.X, zoneCenter.Y, ladderFloorMm / MmPerFoot),
                        options.SideWallOperatorZoneDepthMm / MmPerFoot,
                        options.SideWallOperatorZoneWidthMm / MmPerFoot,
                        (ladderTopMm - ladderFloorMm) / MmPerFoot,
                        outward);
                    bool ladderClear = ladders.Count > 0;
                    foreach (Solid ladder in ladders)
                    {
                        MaintenanceCollisionResult ladderHit = CheckCollision(
                            ladder, obstacles, footSupports);
                        if (ladderHit.State == MaintenanceCollisionState.Unverified)
                        {
                            outcome.UnverifiedGeometryCount++;
                            outcome.CandidateAuditComplete = false;
                        }
                        if (!ladderHit.IsClear)
                        {
                            ladderClear = false;
                            RecordBlocker(outcome.BlockerCounts, ladderHit.BlockerKey);
                            break;
                        }
                    }
                    MaintenanceCollisionResult zoneHit = CheckCollision(
                        operationZone, obstacles, footSupports);
                    if (zoneHit.State == MaintenanceCollisionState.Unverified)
                    {
                        outcome.UnverifiedGeometryCount++;
                        outcome.CandidateAuditComplete = false;
                    }
                    if (!zoneHit.IsClear)
                        RecordBlocker(outcome.BlockerCounts, zoneHit.BlockerKey);
                    if (!ladderClear || !zoneHit.IsClear)
                    {
                        outcome.LadderFailCount++;
                        continue;
                    }
                    sample.OperationZoneClear = true;
                    clear[MaintenanceHandReachMath.Pack(ix, iy)] = sample;
                }

                AddSideWallRegions(surface, clear, n, options, outcome);
            }

            outcome.ClearCount = outcome.Regions.Sum(x => x.PointCount);
            OrderOpeningRegions(outcome.Regions, OpeningPreference.SideWallOnly);
            for (int i = 0; i < outcome.Regions.Count; i++)
                outcome.Regions[i].RegionNo = i + 1;
            return outcome;
        }

        private static void AddSideWallRegions(
            SideWallSurface surface,
            Dictionary<long, HandReachSample> clear,
            int n,
            HandReachOptions options,
            SideWallAnalysisOutcome outcome)
        {
            List<List<long>> components4 = MaintenanceHandReachMath.MergeRegions(
                new HashSet<long>(clear.Keys), n, n, false);
            List<List<long>> components8 = MaintenanceHandReachMath.MergeRegions(
                new HashSet<long>(clear.Keys), n, n, true);
            outcome.Regions4Count += components4.Count;
            outcome.Regions8Count += components8.Count;
            if (!MaintenanceHandReachMath.ConnectivityAgrees(
                components4.Count, components8.Count))
            {
                outcome.ConnectivityAgreed = false;
                outcome.CandidateAuditComplete = false;
            }

            foreach (List<long> component in components4)
            {
                List<HandReachSample> rows = component.Select(x => clear[x]).ToList();
                var byKey = rows.ToDictionary(
                    x => SideWallSampleStableKey(x),
                    x => x,
                    StringComparer.Ordinal);
                List<HandReachOpeningCandidateRank> ranked =
                    MaintenanceHandReachOpeningPolicy.OrderFeasibleCandidates(
                        rows.Select(x => new HandReachOpeningCandidateRank
                        {
                            StableKey = SideWallSampleStableKey(x),
                            PlaneKind = OpeningPlaneKind.SideWallVertical,
                            IsHardFeasible = true,
                            EdgeDistanceMm = x.ObliqueMm
                        }),
                        OpeningPreference.SideWallOnly);
                HandReachSample recommended = byKey[ranked[0].StableKey];
                outcome.Regions.Add(new HandReachRegion
                {
                    OpeningPlane = HandReachOpeningPlaneKind.SideWallVertical,
                    SurfaceKey = surface.SurfaceKey,
                    PointCount = rows.Count,
                    MinX = rows.Min(x => x.CenterX),
                    MaxX = rows.Max(x => x.CenterX),
                    MinY = rows.Min(x => x.CenterY),
                    MaxY = rows.Max(x => x.CenterY),
                    MinZ = rows.Min(x => x.CenterZ),
                    MaxZ = rows.Max(x => x.CenterZ),
                    AreaM2 = rows.Count * options.GridSpacingMm *
                        options.GridSpacingMm / 1000000.0,
                    Recommended = recommended,
                    RecommendedCorridorClear =
                        (bool[])recommended.CorridorClear.Clone(),
                    RecommendedExemptIntersectCount =
                        recommended.ExemptIntersectCount,
                    RecommendedLadderDirection = recommended.LadderDirection,
                    RecommendedOperationZoneClear =
                        recommended.OperationZoneClear,
                    RecommendedVerticalGrade =
                        MaintenanceHandReachMath.GradeVertical(recommended.VerticalMm),
                    MaxTestedClearDiameterMm = MaxClearDiameter(recommended, options),
                    RecommendedBlockerKey = recommended.BlockerKey ?? string.Empty
                });
            }
        }

        private static bool AcceptSideWallFloorSupport(
            FloorSupportResult support,
            SideWallAnalysisOutcome outcome)
        {
            if (support != null && support.State == MaintenanceCollisionState.Clear)
            {
                outcome.AnyFloorSupport = true;
                return true;
            }
            outcome.LadderFailCount++;
            if (support == null || support.State == MaintenanceCollisionState.Unverified)
            {
                outcome.UnverifiedGeometryCount++;
                outcome.CandidateAuditComplete = false;
            }
            RecordBlocker(outcome.BlockerCounts,
                support == null
                    ? "UNVERIFIED:side-wall-floor-support"
                    : support.Work == null
                        ? support.Reason
                        : support.Work.Key);
            return false;
        }

        private static void OrderOpeningRegions(
            List<HandReachRegion> regions,
            OpeningPreference preference)
        {
            if (regions == null || regions.Count < 2) return;
            var byKey = regions.ToDictionary(
                x => SideWallRegionStableKey(x),
                x => x,
                StringComparer.Ordinal);
            List<HandReachOpeningCandidateRank> ranked =
                MaintenanceHandReachOpeningPolicy.OrderFeasibleCandidates(
                    regions.Select(x => new HandReachOpeningCandidateRank
                    {
                        StableKey = SideWallRegionStableKey(x),
                        PlaneKind = x.OpeningPlane == HandReachOpeningPlaneKind.SideWallVertical
                            ? OpeningPlaneKind.SideWallVertical
                            : OpeningPlaneKind.CeilingHorizontal,
                        IsHardFeasible = true,
                        EdgeDistanceMm = x.Recommended.ObliqueMm
                    }),
                    preference);
            regions.Clear();
            regions.AddRange(ranked.Select(x => byKey[x.StableKey]));
        }

        private static string SideWallSampleStableKey(HandReachSample sample)
        {
            return sample.SurfaceKey + "|" + sample.Ix + "|" + sample.Iy;
        }

        private static string SideWallRegionStableKey(HandReachRegion region)
        {
            return SideWallSampleStableKey(region.Recommended) + "|" +
                   region.PointCount;
        }

        private static void ApplySideWallOutcome(
            HandReachTargetResult target,
            SideWallAnalysisOutcome outcome,
            HandReachOptions options)
        {
            target.SelectedOpeningPlane = HandReachOpeningPlaneKind.SideWallVertical;
            target.RawSampleCount = outcome.RawSampleCount;
            target.HatchInsideCount = outcome.FaceFitCount;
            target.VerticalFailCount = 0;
            target.DistanceOkCount = outcome.DistanceOkCount;
            target.OpeningFailCount = outcome.OpeningFailCount;
            target.CorridorFailCount = outcome.CorridorFailCount;
            target.LadderFailCount = outcome.LadderFailCount;
            target.ClearCount = outcome.ClearCount;
            target.Regions4Count = outcome.Regions4Count;
            target.Regions8Count = outcome.Regions8Count;
            target.ConnectivityAgreed = outcome.ConnectivityAgreed;
            target.CandidateAuditComplete =
                target.CandidateAuditComplete && outcome.CandidateAuditComplete;
            target.Regions.AddRange(outcome.Regions);
            target.HasSelectedOpening = target.Regions.Count > 0;
            target.SelectedCandidateAuditComplete =
                target.SelectedCandidateAuditComplete &&
                target.HasSelectedOpening &&
                outcome.ConnectivityAgreed;
            if (target.Regions.Count > 0)
            {
                HandReachSample recommended = target.Regions[0].Recommended;
                target.LadderStatus = HandReachLadderStatus.Validated;
                target.LadderFloorMm = recommended.LadderFloorMm;
                target.LadderTopMm = recommended.CenterZ +
                    options.HatchSizeMm * 0.5 +
                    options.LadderTopAboveCeilingMm;
            }
            else if (outcome.AnyFloorSupport)
            {
                target.LadderStatus = HandReachLadderStatus.Rejected;
            }
            else if (outcome.LadderFailCount > 0)
            {
                target.LadderStatus = HandReachLadderStatus.NotValidatedMissingFloor;
            }
        }

        private static void AddBlockerEvidence(
            HandReachTargetResult target,
            IEnumerable<ObstacleWork> obstacles,
            IDictionary<string, int> blockerCounts)
        {
            if (target == null || obstacles == null || blockerCounts == null) return;
            foreach (KeyValuePair<string, int> pair in blockerCounts
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .Take(8))
            {
                ObstacleWork obstacle = obstacles.FirstOrDefault(
                    x => string.Equals(x.Key, pair.Key, StringComparison.Ordinal));
                if (obstacle == null) continue;
                HandReachObstacle existing = target.RealObstacles.FirstOrDefault(
                    x => string.Equals(x.Key, obstacle.Key, StringComparison.Ordinal));
                if (existing != null)
                {
                    existing.Relation = (existing.Relation ?? string.Empty) +
                        "；阻碍候选网格点计数=" + pair.Value;
                    continue;
                }
                target.RealObstacles.Add(new HandReachObstacle
                {
                    Key = obstacle.Key,
                    UniqueId = obstacle.UniqueId,
                    Category = obstacle.Category,
                    Name = obstacle.Name,
                    SystemType = obstacle.SystemType,
                    Relation = "阻碍候选网格点计数=" + pair.Value
                });
            }
        }

        private static List<SideWallSurface> BuildSideWallSurfaces(
            IEnumerable<CeilingFaceFootprint> ceilingFootprints,
            IEnumerable<ObstacleWork> obstacles,
            XYZ proxy,
            double ceilingTopMm,
            double maxDistanceMm,
            out int unverifiedCount)
        {
            unverifiedCount = 0;
            var output = new List<SideWallSurface>();
            List<CeilingFaceFootprint> footprints = (ceilingFootprints ??
                    Enumerable.Empty<CeilingFaceFootprint>())
                .Where(x => x != null && x.BoundaryLoops.Count > 0)
                .ToList();
            if (footprints.Count == 0)
            {
                unverifiedCount++;
                return output;
            }

            double wallTopMm;
            if (!TryResolveVirtualWallTop(obstacles, ceilingTopMm, out wallTopMm))
            {
                unverifiedCount++;
                return output;
            }

            List<List<List<MaintenancePoint2>>> footprintLoops = footprints
                .Select(x => x.BoundaryLoops.Select(loop => loop.ToList()).ToList())
                .ToList();
            List<HandReachVirtualBoundarySegment> segments =
                MaintenanceHandReachMath.BuildVirtualBoundarySegments(
                    footprintLoops, 20.0);
            if (segments.Count == 0)
            {
                unverifiedCount++;
                return output;
            }
            const double virtualWallThicknessMm = 100.0;
            foreach (HandReachVirtualBoundarySegment segment in segments)
            {
                XYZ tangent = new XYZ(segment.Tangent.X, segment.Tangent.Y, 0.0);
                XYZ toward = new XYZ(segment.Inward.X, segment.Inward.Y, 0.0);
                XYZ innerOrigin = new XYZ(
                    segment.Start.X / MmPerFoot,
                    segment.Start.Y / MmPerFoot,
                    ceilingTopMm / MmPerFoot);
                XYZ proxyPlan = new XYZ(proxy.X, proxy.Y, innerOrigin.Z);
                double signedInwardMm =
                    (proxyPlan - innerOrigin).DotProduct(toward) * MmPerFoot;
                if (signedInwardMm < -10.0 ||
                    signedInwardMm > maxDistanceMm + 1e-6)
                    continue;
                double nearestPlanDistanceMm = DistanceToBoundarySegmentMm(
                    proxy.X * MmPerFoot,
                    proxy.Y * MmPerFoot,
                    segment.Start,
                    segment.End);
                if (nearestPlanDistanceMm > maxDistanceMm + 1e-6) continue;

                var owner = new ObstacleWork
                {
                    Key = "VIRTUAL_CEILING_BOUNDARY:" + segment.StableKey,
                    UniqueId = segment.StableKey,
                    Category = "虚拟边界墙",
                    Name = "由天花边界生成的虚拟侧墙",
                    IsWall = true
                };
                var surface = new SideWallSurface
                {
                    Owner = owner,
                    SolidIndex = -1,
                    InnerFace = null,
                    OuterFace = null,
                    InnerOrigin = innerOrigin,
                    OuterOrigin = innerOrigin - toward.Multiply(
                        virtualWallThicknessMm / MmPerFoot),
                    Tangent = tangent,
                    NormalTowardTarget = toward,
                    ThicknessFt = virtualWallThicknessMm / MmPerFoot,
                    IsVirtualBoundary = true,
                    BoundaryLoopIndex = segment.LoopIndex,
                    BoundarySegmentIndex = segment.SegmentIndex,
                    BoundaryStart = segment.Start,
                    BoundaryEnd = segment.End,
                    WallBottomMm = ceilingTopMm,
                    WallTopMm = wallTopMm
                };
                double startU = innerOrigin.DotProduct(tangent) * MmPerFoot;
                double endU = new XYZ(
                    segment.End.X / MmPerFoot,
                    segment.End.Y / MmPerFoot,
                    innerOrigin.Z).DotProduct(tangent) * MmPerFoot;
                double minU = Math.Min(startU, endU);
                double maxU = Math.Max(startU, endU);
                var faceLoop = new List<MaintenancePoint2>
                {
                    new MaintenancePoint2(minU, ceilingTopMm),
                    new MaintenancePoint2(maxU, ceilingTopMm),
                    new MaintenancePoint2(maxU, wallTopMm),
                    new MaintenancePoint2(minU, wallTopMm)
                };
                surface.InnerLoops.Add(faceLoop);
                surface.OuterLoops.Add(faceLoop.ToList());
                surface.SurfaceKey = BuildSideWallSurfaceKey(surface);
                output.Add(surface);
            }
            return output
                .GroupBy(x => x.SurfaceKey, StringComparer.Ordinal)
                .Select(x => x.First())
                .OrderBy(x => x.SurfaceKey, StringComparer.Ordinal)
                .ToList();
        }

        private static bool TryResolveVirtualWallTop(
            IEnumerable<ObstacleWork> obstacles,
            double ceilingTopMm,
            out double wallTopMm)
        {
            wallTopMm = double.NaN;
            List<double> verifiedLevels = (obstacles ?? Enumerable.Empty<ObstacleWork>())
                .Where(x => x != null && !x.IsGeometryUnverified &&
                            (x.IsFloor || x.IsRoof))
                .SelectMany(x => x.SolidBounds)
                .Where(x => x != null)
                .Select(x => x.MinZ * MmPerFoot)
                .Where(x => x > ceilingTopMm + 100.0)
                .OrderBy(x => x)
                .ToList();
            if (verifiedLevels.Count == 0) return false;
            wallTopMm = verifiedLevels[0];
            return wallTopMm > ceilingTopMm + 1.0;
        }

        private static double DistanceToBoundarySegmentMm(
            double x,
            double y,
            MaintenancePoint2 start,
            MaintenancePoint2 end)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= Epsilon)
                return new MaintenancePoint2(x, y).DistanceTo(start);
            double t = ((x - start.X) * dx + (y - start.Y) * dy) /
                       lengthSquared;
            t = Math.Max(0.0, Math.Min(1.0, t));
            double nearestX = start.X + dx * t;
            double nearestY = start.Y + dy * t;
            double px = x - nearestX;
            double py = y - nearestY;
            return Math.Sqrt(px * px + py * py);
        }

        private static bool TryBuildVerticalFaceLoops(
            PlanarFace face,
            XYZ tangent,
            ICollection<List<MaintenancePoint2>> output)
        {
            if (face == null || tangent == null || output == null) return false;
            try
            {
                foreach (EdgeArray edgeLoop in face.EdgeLoops)
                {
                    var loop = new List<MaintenancePoint2>();
                    foreach (Edge edge in edgeLoop)
                    {
                        Curve curve = edge.AsCurveFollowingFace(face);
                        IList<XYZ> points = curve.Tessellate();
                        foreach (XYZ point in points)
                        {
                            var local = new MaintenancePoint2(
                                point.DotProduct(tangent) * MmPerFoot,
                                point.Z * MmPerFoot);
                            if (loop.Count == 0 ||
                                Math.Abs(loop[loop.Count - 1].X - local.X) > 1e-6 ||
                                Math.Abs(loop[loop.Count - 1].Y - local.Y) > 1e-6)
                                loop.Add(local);
                        }
                    }
                    if (loop.Count > 1 &&
                        Math.Abs(loop[0].X - loop[loop.Count - 1].X) <= 1e-6 &&
                        Math.Abs(loop[0].Y - loop[loop.Count - 1].Y) <= 1e-6)
                        loop.RemoveAt(loop.Count - 1);
                    if (loop.Count < 3) return false;
                    output.Add(loop);
                }
                return output.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool SideWallSquareInsideBothFaces(
            SideWallSurface surface,
            double centerU,
            double centerV,
            double halfSizeMm,
            out bool unverified)
        {
            unverified = false;
            // 虚拟侧墙的两侧矩形边界已经由天花边界段和结构底标高精确构造；
            // 不存在可再投影的 Revit PlanarFace，前置 loops 完整包含即为面内通过。
            if (surface != null && surface.IsVirtualBoundary) return true;
            try
            {
                foreach (double du in new[] { -halfSizeMm, halfSizeMm })
                foreach (double dv in new[] { -halfSizeMm, halfSizeMm })
                {
                    XYZ innerPoint = PointOnVerticalFace(
                        surface.InnerOrigin,
                        surface.Tangent,
                        centerU + du,
                        centerV + dv);
                    XYZ outerPoint = PointOnVerticalFace(
                        surface.OuterOrigin,
                        surface.Tangent,
                        centerU + du,
                        centerV + dv);
                    if (!PointIsOnFace(surface.InnerFace, innerPoint) ||
                        !PointIsOnFace(surface.OuterFace, outerPoint))
                        return false;
                }
                return true;
            }
            catch
            {
                unverified = true;
                return false;
            }
        }

        private static bool PointIsOnFace(Face face, XYZ point)
        {
            IntersectionResult projection = face.Project(point);
            return projection != null && projection.XYZPoint != null &&
                   projection.Distance <= 2.0 / MmPerFoot &&
                   face.IsInside(projection.UVPoint);
        }

        private static XYZ PointOnVerticalFace(
            XYZ faceOrigin,
            XYZ tangent,
            double uMm,
            double zMm)
        {
            double originU = faceOrigin.DotProduct(tangent);
            return faceOrigin +
                   tangent.Multiply(uMm / MmPerFoot - originU) +
                   XYZ.BasisZ.Multiply(zMm / MmPerFoot - faceOrigin.Z);
        }

        private static Solid MakeSideWallOpeningSolid(
            SideWallSurface surface,
            XYZ innerCenter,
            double openingSizeMm)
        {
            if (surface == null || innerCenter == null || openingSizeMm <= 0.0)
                return null;
            const double marginMm = 2.0;
            double halfFt = openingSizeMm * 0.5 / MmPerFoot;
            XYZ outward = surface.NormalTowardTarget.Negate();
            XYZ outerCenter = innerCenter + outward.Multiply(surface.ThicknessFt);
            XYZ baseCenter = outerCenter + outward.Multiply(marginMm / MmPerFoot);
            XYZ p0 = baseCenter - surface.Tangent.Multiply(halfFt) -
                     XYZ.BasisZ.Multiply(halfFt);
            XYZ p1 = baseCenter + surface.Tangent.Multiply(halfFt) -
                     XYZ.BasisZ.Multiply(halfFt);
            XYZ p2 = baseCenter + surface.Tangent.Multiply(halfFt) +
                     XYZ.BasisZ.Multiply(halfFt);
            XYZ p3 = baseCenter - surface.Tangent.Multiply(halfFt) +
                     XYZ.BasisZ.Multiply(halfFt);
            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(p0, p1));
            loop.Append(Line.CreateBound(p1, p2));
            loop.Append(Line.CreateBound(p2, p3));
            loop.Append(Line.CreateBound(p3, p0));
            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { loop },
                surface.NormalTowardTarget,
                surface.ThicknessFt + marginMm * 2.0 / MmPerFoot);
        }

        private static string BuildPlanarSurfaceKey(
            string prefix,
            XYZ origin,
            XYZ normal,
            double thicknessFt)
        {
            XYZ canonical = normal == null || normal.GetLength() <= Epsilon
                ? XYZ.BasisZ
                : normal.Normalize();
            if (canonical.X < -1e-9 ||
                Math.Abs(canonical.X) <= 1e-9 && canonical.Y < -1e-9 ||
                Math.Abs(canonical.X) <= 1e-9 &&
                Math.Abs(canonical.Y) <= 1e-9 && canonical.Z < 0.0)
                canonical = canonical.Negate();
            long nx = (long)Math.Round(canonical.X * 1000000.0);
            long ny = (long)Math.Round(canonical.Y * 1000000.0);
            long nz = (long)Math.Round(canonical.Z * 1000000.0);
            long plane = (long)Math.Round(
                origin.DotProduct(canonical) * MmPerFoot * 10.0);
            long thickness = (long)Math.Round(thicknessFt * MmPerFoot * 10.0);
            return (prefix ?? string.Empty) + "|N=" + nx + "," + ny + "," + nz +
                   "|P=" + plane + "|T=" + thickness;
        }

        private static string BuildSideWallSurfaceKey(SideWallSurface surface)
        {
            XYZ canonical = surface.NormalTowardTarget;
            if (canonical.X < -1e-9 ||
                Math.Abs(canonical.X) <= 1e-9 && canonical.Y < 0.0)
                canonical = canonical.Negate();
            double p1 = surface.InnerOrigin.DotProduct(canonical);
            double p2 = surface.OuterOrigin.DotProduct(canonical);
            XYZ canonicalTangent = XYZ.BasisZ.CrossProduct(canonical).Normalize();
            bool reverseU = surface.Tangent.DotProduct(canonicalTangent) < 0.0;
            IEnumerable<double> canonicalU = surface.InnerLoops
                .SelectMany(x => x)
                .Select(x => reverseU ? -x.X : x.X);
            double minU = canonicalU.Min();
            double maxU = canonicalU.Max();
            double minZ = surface.InnerLoops.SelectMany(x => x).Min(x => x.Y);
            double maxZ = surface.InnerLoops.SelectMany(x => x).Max(x => x.Y);
            XYZ lowerPlaneOrigin = p1 <= p2
                ? surface.InnerOrigin
                : surface.OuterOrigin;
            return BuildPlanarSurfaceKey(
                       "WALL:" + surface.Owner.Key,
                       lowerPlaneOrigin,
                       canonical,
                       Math.Abs(p2 - p1)) +
                   "|P2=" + (long)Math.Round(Math.Max(p1, p2) * MmPerFoot * 10.0) +
                   "|U=" + (long)Math.Round(minU * 10.0) + "," +
                              (long)Math.Round(maxU * 10.0) +
                   "|Z=" + (long)Math.Round(minZ * 10.0) + "," +
                              (long)Math.Round(maxZ * 10.0) +
                   "|L=" + surface.InnerLoops.Count + "," + surface.OuterLoops.Count;
        }

        private static void FinalizeConclusion(
            HandReachTargetResult target,
            HandReachVerticalGrade verticalGrade,
            List<string> warnings,
            double hatchSizeMm,
            bool sideWallDistanceOver500Review = false)
        {
            string openingLabel = FormatSquareOpening(hatchSizeMm);
            bool ceilingDirectReach = target.SelectedOpeningPlane ==
                HandReachOpeningPlaneKind.CeilingHorizontal &&
                target.CeilingDirectReachApplied;
            bool ceilingPersonnelEntry = target.SelectedOpeningPlane ==
                HandReachOpeningPlaneKind.CeilingHorizontal &&
                target.CeilingPersonnelEntryApplied;
            bool ceilingValidatedMode = ceilingDirectReach || ceilingPersonnelEntry;
            if (!ceilingValidatedMode &&
                verticalGrade == HandReachVerticalGrade.RejectedOver500)
            {
                target.SelectedCandidateAuditComplete = false;
                target.AttentionLevel = HandReachAttentionLevel.Rejected;
                target.Conclusion = "rejected_vertical_over_500";
                target.ConclusionReason =
                    "检修面代理点相对天花的垂直高差超过500mm硬上限；所有候选已在垂直规则阶段淘汰。";
                return;
            }
            if (target.Regions.Count == 0)
            {
                target.SelectedCandidateAuditComplete = false;
                target.AttentionLevel = HandReachAttentionLevel.Rejected;
                target.Conclusion = ceilingDirectReach
                    ? "rejected_no_feasible_ceiling_direct_hand_reach"
                    : (ceilingPersonnelEntry
                        ? "rejected_no_feasible_ceiling_personnel_entry"
                        : "rejected_no_feasible_hand_reach");
                target.ConclusionReason = ceilingDirectReach
                    ? "窗口内无同时满足" + openingLabel + "天花口、洞口下方直接伸手通道与梯具的可行点；建议调整检修口。"
                    : (ceilingPersonnelEntry
                        ? "窗口内无同时满足" + openingLabel + "口、人员钻入包络、最后操作伸手段与梯具的可行点；建议调整检修口。"
                        : "窗口内无同时满足" + openingLabel + "口、200通道与梯具的可行点；建议淘汰或调整检修口。");
                return;
            }

            if (!target.ConnectivityAgreed)
            {
                if (MaintenanceHandReachMath.CanReviewConnectivityDisagreement(
                    target.ConnectivityAgreed,
                    ceilingValidatedMode,
                    target.HasSelectedOpening,
                    target.SelectedCandidateAuditComplete))
                {
                    target.AttentionLevel = HandReachAttentionLevel.OrangeReview;
                    target.Conclusion =
                        "review_only_ceiling_connectivity_disagreement";
                    target.ConclusionReason = ceilingDirectReach
                        ? "推荐候选自身的" + openingLabel + "天花口、洞口下方直接伸手通道、梯具/操作位和碰撞证据均已完整验证；" +
                          "仅40mm网格的四邻接与八邻接区域分组不一致，因此保留为橙色待人工目视复核，不能作为正式绿灯。"
                        : "推荐候选自身的" + openingLabel + "口、人员钻入包络、最后操作伸手段、梯具/操作位和碰撞证据均已完整验证；" +
                          "仅40mm网格的四邻接与八邻接区域分组不一致，因此保留为橙色待人工目视复核，不能作为正式绿灯。";
                    return;
                }

                target.SelectedCandidateAuditComplete = false;
                target.AttentionLevel = HandReachAttentionLevel.Rejected;
                target.Conclusion = "rejected_connectivity_disagreement";
                target.ConclusionReason =
                    "40mm区域四邻接与八邻接结果不一致，且推荐候选自身证据不满足橙色复核例外。";
                return;
            }

            HandReachRegion best = target.Regions[0];
            if (!target.CandidateAuditComplete)
            {
                target.AttentionLevel = HandReachAttentionLevel.OrangeReview;
                target.Conclusion = ceilingDirectReach
                    ? "conditional_feasible_ceiling_direct_hand_reach_audit_incomplete"
                    : (ceilingPersonnelEntry
                        ? "conditional_feasible_ceiling_personnel_entry_audit_incomplete"
                        : "conditional_feasible_hand_reach_audit_incomplete");
                target.ConclusionReason = ceilingDirectReach
                    ? "最佳候选自身的" + openingLabel + "天花口、洞口下方直接伸手通道、梯具/操作位和连通性已完整验证；其他未选择候选仍有未知项，并且模型存在天花交叠可能，按橙色待复核方案保留。"
                    : (ceilingPersonnelEntry
                        ? "最佳候选自身的" + openingLabel + "口、人员钻入包络、最后操作伸手段、梯具/操作位和连通性已完整验证；其他未选择候选仍有未知项，按橙色待复核方案保留。"
                        : "最佳候选自身的" + openingLabel + "口、200通道、梯具/操作位和连通性已完整验证，可作为橙色待复核方案写入；其他未选择候选仍有未知项，不能宣称全候选审计完整。");
                return;
            }
            bool conditional = verticalGrade == HandReachVerticalGrade.AttentionWithin500 ||
                               best.Recommended.DistanceGrade == HandReachDistanceGrade.BWithin400 ||
                               best.Recommended.DistanceGrade == HandReachDistanceGrade.CWithin500;
            if (target.LadderStatus == HandReachLadderStatus.NotValidatedMissingFloor)
            {
                target.AttentionLevel = HandReachAttentionLevel.High;
                target.Conclusion = "conditional_feasible_hand_reach_ladder_unverified";
                target.ConclusionReason = openingLabel + "口与检修通道成立，但未找到可架梯楼面，梯具未验证。";
                warnings.Add("设备 " + target.Target.GetDisplayName() + " 梯具未验证。");
                return;
            }
            bool reducedSideWall400 = target.SelectedOpeningPlane ==
                HandReachOpeningPlaneKind.SideWallVertical &&
                Math.Abs(hatchSizeMm -
                    MaintenanceHandReachOpeningPolicy.ReducedSideWallOpeningSizeMm) <= 1e-6;
            if (reducedSideWall400)
            {
                target.AttentionLevel = HandReachAttentionLevel.OrangeReview;
                target.Conclusion = sideWallDistanceOver500Review
                    ? "review_only_side_wall_400_distance_500_to_600"
                    : "review_only_side_wall_400";
                target.ConclusionReason =
                    "400×400侧墙缩小口、200通道与梯具已完成几何检查；该尺寸仅作为明确指定的现场复核备选，" +
                    (sideWallDistanceOver500Review
                        ? "且实际伸手距离超过500mm但不超过600mm，"
                        : string.Empty) +
                    "不自动替代450×450正式侧墙口。";
                return;
            }
            if (sideWallDistanceOver500Review)
            {
                target.AttentionLevel = HandReachAttentionLevel.OrangeReview;
                target.Conclusion =
                    "review_only_side_wall_distance_500_to_600";
                target.ConclusionReason =
                    openingLabel + "侧墙口、200通道与梯具已完成几何检查；" +
                    "实际伸手距离超过500mm但不超过600mm，仅按人员略向洞内探身的橙色待复核方案保留，" +
                    "不属于500mm正式通过范围。";
                return;
            }
            if (ceilingDirectReach &&
                MaintenanceHandReachMath.RequiresCeilingDirectReachOverlapReview(
                    target.ModelVerticalDifferenceMm))
            {
                target.AttentionLevel = HandReachAttentionLevel.OrangeReview;
                target.Conclusion =
                    "review_only_ceiling_direct_hand_reach_model_overlap";
                target.ConclusionReason =
                    openingLabel + "天花口、洞口下方直接伸手通道与梯具已完成几何检查；" +
                    "当前设备检修面与天花模型交叠" +
                    Math.Abs(target.ModelVerticalDifferenceMm).ToString("F1") +
                    "mm，方案按橙色保留，由现场目视确认模型安装关系后采用。";
                return;
            }
            if (ceilingPersonnelEntry)
            {
                target.AttentionLevel = HandReachAttentionLevel.High;
                target.Conclusion = "feasible_ceiling_personnel_entry";
                target.ConclusionReason =
                    "设备保持模型原高度；" + openingLabel + "天花口、人员钻入包络、最后操作伸手段与梯具均通过。" +
                    "天花到设备的整体高差不再按洞口直接伸手距离淘汰。";
                return;
            }
            if (ceilingDirectReach)
            {
                target.AttentionLevel = conditional
                    ? HandReachAttentionLevel.OrangeReview
                    : HandReachAttentionLevel.High;
                target.Conclusion = conditional
                    ? "conditional_feasible_ceiling_direct_hand_reach"
                    : "feasible_ceiling_direct_hand_reach";
                target.ConclusionReason = conditional
                    ? openingLabel + "天花口、洞口下方直接伸手通道与梯具成立；距离超出推荐档，需橙色目视复核。"
                    : openingLabel + "天花口、洞口下方直接伸手通道与梯具均通过；不生成人员钻入包络。";
                return;
            }
            if (conditional)
            {
                target.AttentionLevel = HandReachAttentionLevel.OrangeReview;
                target.Conclusion = "conditional_feasible_hand_reach";
                target.ConclusionReason = openingLabel + "口与200通道成立；距离或垂直高差超出推荐档，需橙色重点会审。";
                return;
            }
            target.AttentionLevel = HandReachAttentionLevel.High;
            target.Conclusion = "feasible_hand_reach";
            target.ConclusionReason = openingLabel + "口、200通道与梯具全部通过；因操作点未提资，关注等级保持高。";
        }

        private static string FormatSquareOpening(double sizeMm)
        {
            string size = Math.Round(sizeMm, 1).ToString("0.#");
            return size + "×" + size;
        }

        // ---------------------------------------------------------------- geometry helpers

        private static void ResolveSupplyDirection(DeviceWork device, out XYZ supply, out bool inferred)
        {
            inferred = false;
            FamilyInstance instance = device.Element as FamilyInstance;
            if (instance != null && instance.MEPModel != null &&
                instance.MEPModel.ConnectorManager != null)
            {
                foreach (Connector connector in instance.MEPModel.ConnectorManager.Connectors)
                {
                    try
                    {
                        if (connector.Domain != Domain.DomainHvac ||
                            connector.DuctSystemType != DuctSystemType.SupplyAir) continue;
                        XYZ direction = device.ToHost.OfVector(connector.CoordinateSystem.BasisZ);
                        direction = new XYZ(direction.X, direction.Y, 0.0);
                        if (direction.GetLength() > 1e-8)
                        {
                            supply = direction.Normalize();
                            return;
                        }
                    }
                    catch { }
                }
            }
            inferred = true;
            double dx = device.HostVertices.Max(x => x.X) - device.HostVertices.Min(x => x.X);
            double dy = device.HostVertices.Max(x => x.Y) - device.HostVertices.Min(x => x.Y);
            supply = dx >= dy ? XYZ.BasisX : XYZ.BasisY;
        }

        private static void CollectObstacles(
            List<PlenumAnalysisService.Candidate> candidates,
            DeviceWork device,
            double proxyX,
            double proxyY,
            double zMinMm,
            double zMaxMm,
            double windowHalfMm,
            IList<DeviceWork> groupDevices,
            HandReachAnalysisResult result,
            List<ObstacleWork> obstacles,
            List<ObstacleWork> exempts)
        {
            var catIds = new HashSet<BuiltInCategory>
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
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_Columns,
                BuiltInCategory.OST_Walls,
                BuiltInCategory.OST_GenericModel,
                BuiltInCategory.OST_Casework,
                BuiltInCategory.OST_Floors,
                BuiltInCategory.OST_Roofs,
                BuiltInCategory.OST_FabricationDuctwork,
                BuiltInCategory.OST_FabricationPipework,
                BuiltInCategory.OST_FabricationContainment,
                BuiltInCategory.OST_FabricationHangers,
                BuiltInCategory.OST_FabricationDuctworkInsulation,
                BuiltInCategory.OST_FabricationPipeworkInsulation,
                BuiltInCategory.OST_FabricationDuctworkLining,
                BuiltInCategory.OST_FabricationDuctworkStiffeners
            };
            var window = new PlenumAnalysisService.Bounds3
            {
                MinX = (proxyX - windowHalfMm) / MmPerFoot,
                MinY = (proxyY - windowHalfMm) / MmPerFoot,
                MinZ = zMinMm / MmPerFoot,
                MaxX = (proxyX + windowHalfMm) / MmPerFoot,
                MaxY = (proxyY + windowHalfMm) / MmPerFoot,
                MaxZ = zMaxMm / MmPerFoot
            };
            foreach (PlenumAnalysisService.Candidate candidate in candidates)
            {
                if (candidate == null || candidate.Element == null) continue;
                // 跳过设备自身：按链接实例+元素ID比对（不同托管包装下 ReferenceEquals 不可靠）
                long candidateLinkId = candidate.Source == null || !candidate.Source.LinkInstanceId.HasValue
                    ? 0L
                    : candidate.Source.LinkInstanceId.Value;
                if (candidateLinkId == device.Info.LinkInstanceId &&
                    candidate.Element.Id.Value == device.Info.ElementId) continue;
                if (!catIds.Contains(candidate.Category)) continue;
                if (candidate.WorldBounds != null &&
                    !BoundsOverlap(candidate.WorldBounds, window)) continue;

                Element element = candidate.Element;
                string systemType;
                string systemEvidenceSource;
                MaintenancePipeExemptionDecision exemption = EvaluatePipeExemption(
                    candidate, device, groupDevices,
                    out systemType, out systemEvidenceSource);
                bool isExempt = exemption.IsExempt;
                if (isExempt)
                    RecordPipeExemptionEvidence(
                        result,
                        device,
                        candidate,
                        exemption,
                        systemType,
                        systemEvidenceSource);

                var work = new ObstacleWork
                {
                    Key = candidate.SourceKey,
                    UniqueId = Safe(element.UniqueId),
                    Category = element.Category == null ? string.Empty : element.Category.Name,
                    Name = element.Name ?? string.Empty,
                    SystemType = systemType,
                    ExemptionReason = isExempt
                        ? exemption.Reason + "；证据源=" + systemEvidenceSource
                        : string.Empty,
                    IsExempt = isExempt,
                    IsFloor = candidate.Category == BuiltInCategory.OST_Floors,
                    IsRoof = candidate.Category == BuiltInCategory.OST_Roofs,
                    IsWall = candidate.Category == BuiltInCategory.OST_Walls,
                    FallbackBounds = candidate.WorldBounds,
                    GeometryUnverifiedReason = string.Empty
                };
                if (candidate.WorldBounds == null)
                {
                    work.IsGeometryUnverified = true;
                    work.GeometryUnverifiedReason = "candidate bounds unavailable";
                }
                if (candidate.MeshCount > 0 || !string.IsNullOrWhiteSpace(candidate.GeometryError))
                {
                    work.IsGeometryUnverified = true;
                    work.GeometryUnverifiedReason = string.IsNullOrWhiteSpace(candidate.GeometryError)
                        ? "mesh-only geometry"
                        : candidate.GeometryError;
                }
                if (candidate.Solids != null)
                {
                    for (int i = 0; i < candidate.Solids.Count; i++)
                    {
                        Solid solid = candidate.Solids[i];
                        if (solid == null) continue;
                        try
                        {
                            if (solid.Volume <= Epsilon) continue;
                        }
                        catch (Exception ex)
                        {
                            work.IsGeometryUnverified = true;
                            work.GeometryUnverifiedReason =
                                "solid volume unavailable: " + ex.GetType().Name;
                            continue;
                        }
                        Solid hostSolid = solid;
                        Transform toHost = candidate.ToHost;
                        if (toHost == null)
                        {
                            work.IsGeometryUnverified = true;
                            work.GeometryUnverifiedReason = "missing host transform";
                            continue;
                        }
                        if (!toHost.IsIdentity)
                        {
                            try { hostSolid = SolidUtils.CreateTransformed(solid, toHost); }
                            catch (Exception ex)
                            {
                                work.IsGeometryUnverified = true;
                                work.GeometryUnverifiedReason = "solid transform failed: " + ex.GetType().Name;
                                continue;
                            }
                        }
                        work.Solids.Add(hostSolid);
                        PlenumAnalysisService.Bounds3 solidBounds;
                        if (!TryBodyBounds(hostSolid, out solidBounds))
                        {
                            work.IsGeometryUnverified = true;
                            work.GeometryUnverifiedReason = "solid bounds unavailable";
                        }
                        work.SolidBounds.Add(solidBounds);
                    }
                }
                if (work.Solids.Count == 0)
                {
                    work.IsGeometryUnverified = true;
                    if (string.IsNullOrEmpty(work.GeometryUnverifiedReason))
                        work.GeometryUnverifiedReason = "no solid geometry";
                }
                if (isExempt) exempts.Add(work);
                else obstacles.Add(work);
            }
        }

        private static MaintenancePipeExemptionDecision EvaluatePipeExemption(
            PlenumAnalysisService.Candidate candidate,
            DeviceWork target,
            IList<DeviceWork> groupDevices,
            out string systemEvidence,
            out string systemEvidenceSource)
        {
            systemEvidence = string.Empty;
            systemEvidenceSource = string.Empty;
            MaintenancePipeCategoryKind category = GetPipeCategoryKind(candidate.Category);
            if (category == MaintenancePipeCategoryKind.Other)
                return MaintenancePipeExemptionPolicy.Evaluate(
                    new MaintenancePipeExemptionInput { Category = category });
            bool systemReliable = TryReadReliablePipeSystemEvidence(
                candidate.Element, out systemEvidence, out systemEvidenceSource);
            MaintenanceBounds3Mm pipeBounds = ToBoundsMm(candidate.WorldBounds);
            MaintenanceBounds3Mm targetBounds = DeviceBoundsMm(target);
            List<MaintenancePoint3> endPoints = GetPipeEndPointsMm(candidate);
            double otherTargetDistance = (groupDevices ?? new List<DeviceWork>())
                .Where(x => x != null && !ReferenceEquals(x, target))
                .Select(x =>
                {
                    MaintenanceBounds3Mm otherBounds = DeviceBoundsMm(x);
                    return endPoints.Count == 0
                        ? MaintenancePipeExemptionPolicy.DistanceBoundsToBounds(pipeBounds, otherBounds)
                        : endPoints.Min(p => MaintenancePipeExemptionPolicy.DistancePointToBounds(
                            p, otherBounds));
                })
                .DefaultIfEmpty(double.PositiveInfinity)
                .Min();
            var input = new MaintenancePipeExemptionInput
            {
                Category = category,
                SameSourceModel = SameSourceModel(candidate, target),
                SystemEvidenceReliable = systemReliable,
                SystemEvidence = systemEvidence,
                LengthMm = GetPipeLengthMm(candidate, category, pipeBounds),
                DiameterMm = GetPipeDiameterMm(candidate.Element),
                ElementBounds = pipeBounds,
                TargetBounds = targetBounds,
                NearestOtherTargetDistanceMm = otherTargetDistance
            };
            input.EndPoints.AddRange(endPoints);
            return MaintenancePipeExemptionPolicy.Evaluate(input);
        }

        private static void RecordPipeExemptionEvidence(
            HandReachAnalysisResult result,
            DeviceWork target,
            PlenumAnalysisService.Candidate candidate,
            MaintenancePipeExemptionDecision decision,
            string systemEvidence,
            string systemEvidenceSource)
        {
            if (result == null || target == null || candidate == null ||
                candidate.Element == null || decision == null || !decision.IsExempt)
                return;
            MaintenanceElementRef elementRef = ToElementRef(candidate);
            string groupKey = target.Info.GroupKey ?? string.Empty;
            string targetKey = target.Info.TargetKey ?? string.Empty;
            string elementKey = elementRef.GetStableKey();
            if (!result.ExemptPipeEvidence.Any(x => x != null && x.Element != null &&
                string.Equals(x.GroupKey, groupKey, StringComparison.Ordinal) &&
                string.Equals(x.TargetKey, targetKey, StringComparison.Ordinal) &&
                string.Equals(x.Element.GetStableKey(), elementKey, StringComparison.Ordinal)))
            {
                MaintenancePipeCategoryKind category = GetPipeCategoryKind(candidate.Category);
                MaintenanceBounds3Mm pipeBounds = ToBoundsMm(candidate.WorldBounds);
                result.ExemptPipeEvidence.Add(new MaintenancePipeExemptionEvidence
                {
                    GroupKey = groupKey,
                    TargetKey = targetKey,
                    Element = elementRef,
                    CategoryKind = category.ToString(),
                    SystemKind = decision.SystemKind,
                    SystemTypeEvidence = systemEvidence ?? string.Empty,
                    SystemEvidenceSource = systemEvidenceSource ?? string.Empty,
                    ReasonCode = decision.ReasonCode,
                    Reason = decision.Reason,
                    DistanceMm = decision.DistanceMm,
                    LengthMm = GetPipeLengthMm(candidate, category, pipeBounds),
                    DiameterMm = GetPipeDiameterMm(candidate.Element)
                });
            }
            AddPipeSystemEvidenceSources(result, candidate);
        }

        private static void AddPipeSystemEvidenceSources(
            HandReachAnalysisResult result,
            PlenumAnalysisService.Candidate candidate)
        {
            if (result == null || candidate == null || candidate.Element == null) return;
            Element pipe = candidate.Element;
            try
            {
                Parameter parameter = pipe.get_Parameter(
                    BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
                if (parameter != null && parameter.StorageType == StorageType.ElementId)
                {
                    ElementId typeId = parameter.AsElementId();
                    Element systemType = typeId == null || typeId == ElementId.InvalidElementId
                        ? null
                        : pipe.Document.GetElement(typeId);
                    AddEvidenceSource(result, ToElementRef(candidate, systemType));
                }
            }
            catch { }

            foreach (Connector connector in GetConnectors(pipe))
            {
                try
                {
                    MEPSystem system = connector.MEPSystem;
                    if (system == null) continue;
                    AddEvidenceSource(result, ToElementRef(candidate, system));
                    ElementId typeId = system.GetTypeId();
                    Element type = typeId == null || typeId == ElementId.InvalidElementId
                        ? null
                        : system.Document.GetElement(typeId);
                    AddEvidenceSource(result, ToElementRef(candidate, type));
                }
                catch { }
            }
        }

        private static MaintenancePipeCategoryKind GetPipeCategoryKind(
            BuiltInCategory category)
        {
            if (category == BuiltInCategory.OST_PipeCurves)
                return MaintenancePipeCategoryKind.PipeCurve;
            if (category == BuiltInCategory.OST_PipeFitting)
                return MaintenancePipeCategoryKind.PipeFitting;
            if (category == BuiltInCategory.OST_PipeAccessory)
                return MaintenancePipeCategoryKind.PipeAccessory;
            return MaintenancePipeCategoryKind.Other;
        }

        private static bool SameSourceModel(
            PlenumAnalysisService.Candidate candidate,
            DeviceWork target)
        {
            long candidateLinkId = candidate == null || candidate.Source == null ||
                                   !candidate.Source.LinkInstanceId.HasValue
                ? 0L
                : candidate.Source.LinkInstanceId.Value;
            return target != null && candidateLinkId == target.Info.LinkInstanceId;
        }

        private static MaintenanceBounds3Mm ToBoundsMm(
            PlenumAnalysisService.Bounds3 bounds)
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

        private static MaintenanceBounds3Mm DeviceBoundsMm(DeviceWork device)
        {
            if (device == null || device.HostVertices.Count == 0) return null;
            return new MaintenanceBounds3Mm
            {
                MinX = device.HostVertices.Min(x => x.X) * MmPerFoot,
                MinY = device.HostVertices.Min(x => x.Y) * MmPerFoot,
                MinZ = device.HostVertices.Min(x => x.Z) * MmPerFoot,
                MaxX = device.HostVertices.Max(x => x.X) * MmPerFoot,
                MaxY = device.HostVertices.Max(x => x.Y) * MmPerFoot,
                MaxZ = device.HostVertices.Max(x => x.Z) * MmPerFoot
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
                    parameterValue, out ignoredKind))
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

        private static string ReadParameterElementOrDisplayValue(
            Document document,
            Parameter parameter)
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
            if (candidate == null || candidate.Element == null || candidate.ToHost == null)
                return output;
            Transform toHost = candidate.ToHost;
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
            var distinct = new List<MaintenancePoint3>();
            foreach (MaintenancePoint3 point in output)
            {
                if (!distinct.Any(x =>
                    Math.Abs(x.X - point.X) <= 0.1 &&
                    Math.Abs(x.Y - point.Y) <= 0.1 &&
                    Math.Abs(x.Z - point.Z) <= 0.1))
                    distinct.Add(point);
            }
            return distinct;
        }

        private static void AddPoint(
            ICollection<MaintenancePoint3> output,
            XYZ point)
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

        /// <summary>
        /// 在具体候选点正下方投影楼板实体顶面。只有真实面投影命中且法向朝上才算支撑；
        /// bbox 仅作快速筛选，不能直接提供楼面标高。
        /// </summary>
        private static FloorSupportResult ResolveFloorSupport(
            IEnumerable<ObstacleWork> obstacles,
            double xMm,
            double yMm,
            double ceilingTopMm)
        {
            double xFt = xMm / MmPerFoot;
            double yFt = yMm / MmPerFoot;
            double topFt = ceilingTopMm / MmPerFoot;
            double minFloorFt = topFt - 5000.0 / MmPerFoot;
            double maxFloorFt = topFt - 100.0 / MmPerFoot;
            double xyToleranceFt = 2.0 / MmPerFoot;
            var best = new FloorSupportResult
            {
                State = MaintenanceCollisionState.Conflict,
                FloorMm = 0.0,
                Reason = "no floor solid below candidate"
            };
            double bestZ = double.MinValue;

            foreach (ObstacleWork work in obstacles.Where(x => x.IsFloor))
            {
                bool fallbackRelevant = work.FallbackBounds == null ||
                    BoundsContainsXY(work.FallbackBounds, xFt, yFt, xyToleranceFt) &&
                    work.FallbackBounds.MaxZ >= minFloorFt &&
                    work.FallbackBounds.MinZ <= maxFloorFt;
                if (!fallbackRelevant) continue;
                if (work.IsGeometryUnverified)
                {
                    return new FloorSupportResult
                    {
                        State = MaintenanceCollisionState.Unverified,
                        Work = work,
                        Reason = work.GeometryUnverifiedReason
                    };
                }

                for (int i = 0; i < work.Solids.Count; i++)
                {
                    PlenumAnalysisService.Bounds3 solidBounds =
                        work.SolidBounds.Count > i ? work.SolidBounds[i] : null;
                    if (solidBounds == null)
                    {
                        return new FloorSupportResult
                        {
                            State = MaintenanceCollisionState.Unverified,
                            Work = work,
                            Reason = "floor solid bounds unavailable"
                        };
                    }
                    if (!BoundsContainsXY(solidBounds, xFt, yFt, xyToleranceFt) ||
                        solidBounds.MaxZ < minFloorFt || solidBounds.MinZ > maxFloorFt)
                        continue;

                    Solid solid = work.Solids[i];
                    foreach (Face face in solid.Faces)
                    {
                        try
                        {
                            IntersectionResult projection = face.Project(
                                new XYZ(xFt, yFt, topFt));
                            if (projection == null || projection.XYZPoint == null) continue;
                            XYZ point = projection.XYZPoint;
                            double dx = point.X - xFt;
                            double dy = point.Y - yFt;
                            if (Math.Sqrt(dx * dx + dy * dy) > xyToleranceFt) continue;
                            if (!face.IsInside(projection.UVPoint)) continue;
                            XYZ normal = face.ComputeNormal(projection.UVPoint);
                            if (normal == null || normal.Z < 0.7) continue;
                            if (point.Z < minFloorFt || point.Z > maxFloorFt) continue;
                            if (point.Z <= bestZ) continue;
                            bestZ = point.Z;
                            best.State = MaintenanceCollisionState.Clear;
                            best.FloorMm = point.Z * MmPerFoot;
                            best.Work = work;
                            best.SolidIndex = i;
                            best.Reason = string.Empty;
                        }
                        catch (Exception ex)
                        {
                            return new FloorSupportResult
                            {
                                State = MaintenanceCollisionState.Unverified,
                                Work = work,
                                Reason = "floor face projection failed: " + ex.GetType().Name
                            };
                        }
                    }
                }
            }
            return best;
        }

        private static bool BoundsContainsXY(
            PlenumAnalysisService.Bounds3 bounds,
            double x,
            double y,
            double tolerance)
        {
            return bounds != null &&
                   x >= bounds.MinX - tolerance && x <= bounds.MaxX + tolerance &&
                   y >= bounds.MinY - tolerance && y <= bounds.MaxY + tolerance;
        }

        private static Solid MakeCylinder(XYZ a, XYZ b, double radiusFt)
        {
            XYZ axis = b - a;
            double len = axis.GetLength();
            if (len <= Epsilon) return null;
            XYZ dir = axis.Normalize();
            XYZ temp = Math.Abs(dir.Z) < 0.9 ? XYZ.BasisZ : XYZ.BasisX;
            XYZ u = dir.CrossProduct(temp).Normalize();
            XYZ v = dir.CrossProduct(u).Normalize();
            XYZ p0 = a + u.Multiply(radiusFt);
            XYZ p1 = a - u.Multiply(radiusFt);
            var loop = new CurveLoop();
            loop.Append(Arc.Create(p0, p1, a + v.Multiply(radiusFt)));
            loop.Append(Arc.Create(p1, p0, a - v.Multiply(radiusFt)));
            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { loop }, dir, len);
        }

        private static MaintenanceCollisionResult CheckCollision(
            Solid body,
            IEnumerable<ObstacleWork> obstacles,
            IList<FloorSupportResult> supports,
            string ignoredOwnerWallKey = null)
        {
            PlenumAnalysisService.Bounds3 bodyBounds;
            if (!TryBodyBounds(body, out bodyBounds))
                return CollisionResult(
                    MaintenanceCollisionState.Unverified,
                    "UNVERIFIED:body-geometry",
                    "clearance body bounds unavailable");

            foreach (ObstacleWork obstacle in obstacles)
            {
                // 侧墙开口与伸手通道穿过其所属墙体是预期行为；仅这两个调用阶段
                // 传入 owner key。梯具和操作区不传，仍把该墙当作真实障碍。
                if (!string.IsNullOrEmpty(ignoredOwnerWallKey) &&
                    obstacle.IsWall &&
                    string.Equals(obstacle.Key, ignoredOwnerWallKey, StringComparison.Ordinal))
                    continue;
                if (obstacle.IsGeometryUnverified &&
                    (obstacle.FallbackBounds == null ||
                     BoundsOverlap(bodyBounds, obstacle.FallbackBounds)))
                {
                    return CollisionResult(
                        MaintenanceCollisionState.Unverified,
                        obstacle.Key,
                        obstacle.GeometryUnverifiedReason);
                }

                for (int i = 0; i < obstacle.Solids.Count; i++)
                {
                    if (supports != null && supports.Any(support =>
                        support != null && support.State == MaintenanceCollisionState.Clear &&
                        ReferenceEquals(support.Work, obstacle) && support.SolidIndex == i))
                        continue;
                    PlenumAnalysisService.Bounds3 solidBounds =
                        obstacle.SolidBounds.Count > i ? obstacle.SolidBounds[i] : null;
                    if (solidBounds == null)
                    {
                        if (obstacle.FallbackBounds == null ||
                            BoundsOverlap(bodyBounds, obstacle.FallbackBounds))
                            return CollisionResult(
                                MaintenanceCollisionState.Unverified,
                                obstacle.Key,
                                "obstacle solid bounds unavailable");
                        continue;
                    }
                    if (!BoundsOverlap(bodyBounds, solidBounds)) continue;
                    try
                    {
                        Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                            body, obstacle.Solids[i], BooleanOperationsType.Intersect);
                        if (intersection == null)
                            return CollisionResult(
                                MaintenanceCollisionState.Unverified,
                                obstacle.Key,
                                "boolean intersection returned null");
                        if (intersection.Volume > Epsilon)
                            return CollisionResult(
                                MaintenanceCollisionState.Conflict,
                                obstacle.Key,
                                "solid intersection");
                    }
                    catch (Exception ex)
                    {
                        return CollisionResult(
                            MaintenanceCollisionState.Unverified,
                            obstacle.Key,
                            "boolean intersection failed: " + ex.GetType().Name);
                    }
                }
            }
            return CollisionResult(MaintenanceCollisionState.Clear, string.Empty, string.Empty);
        }

        private static MaintenanceCollisionResult CollisionResult(
            MaintenanceCollisionState state,
            string blockerKey,
            string reason)
        {
            return new MaintenanceCollisionResult
            {
                State = state,
                BlockerKey = blockerKey ?? string.Empty,
                Reason = reason ?? string.Empty
            };
        }

        private static bool TryCountExemptHits(
            Solid body,
            List<ObstacleWork> exempts,
            out int count,
            out string unverifiedReason)
        {
            count = 0;
            unverifiedReason = string.Empty;
            PlenumAnalysisService.Bounds3 bodyBounds;
            if (!TryBodyBounds(body, out bodyBounds))
            {
                unverifiedReason = "UNVERIFIED:exempt-body-geometry";
                return false;
            }
            foreach (ObstacleWork exempt in exempts)
            {
                if (exempt.IsGeometryUnverified &&
                    (exempt.FallbackBounds == null ||
                     BoundsOverlap(bodyBounds, exempt.FallbackBounds)))
                {
                    unverifiedReason = exempt.Key;
                    return false;
                }
                for (int i = 0; i < exempt.Solids.Count; i++)
                {
                    PlenumAnalysisService.Bounds3 solidBounds =
                        exempt.SolidBounds.Count > i ? exempt.SolidBounds[i] : null;
                    if (solidBounds == null)
                    {
                        if (exempt.FallbackBounds == null ||
                            BoundsOverlap(bodyBounds, exempt.FallbackBounds))
                        {
                            unverifiedReason = exempt.Key;
                            return false;
                        }
                        continue;
                    }
                    if (!BoundsOverlap(bodyBounds, solidBounds)) continue;
                    try
                    {
                        Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                            body, exempt.Solids[i], BooleanOperationsType.Intersect);
                        if (intersection == null)
                        {
                            unverifiedReason = exempt.Key;
                            return false;
                        }
                        if (intersection.Volume > Epsilon) count++;
                    }
                    catch
                    {
                        unverifiedReason = exempt.Key;
                        return false;
                    }
                }
            }
            return true;
        }

        private static int ClearCount(HandReachSample sample)
        {
            int count = 0;
            if (sample.CorridorClear != null)
                foreach (bool clear in sample.CorridorClear)
                    if (clear) count++;
            return count;
        }

        private static int MaxClearDiameter(HandReachSample sample, HandReachOptions options)
        {
            int max = 0;
            for (int d = 0; d < options.CorridorTestDiametersMm.Length; d++)
                if (sample.CorridorClear[d])
                    max = (int)Math.Round(options.CorridorTestDiametersMm[d]);
                else
                    break;
            return max;
        }

        private static bool TryBodyBounds(
            Solid solid,
            out PlenumAnalysisService.Bounds3 bounds)
        {
            bounds = null;
            if (solid == null) return false;
            try
            {
                if (solid.Volume <= Epsilon) return false;
            }
            catch
            {
                return false;
            }
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool hasVertex = false;
            foreach (Face face in solid.Faces)
            {
                try
                {
                    Mesh mesh = face.Triangulate();
                    if (mesh == null || mesh.Vertices == null || mesh.Vertices.Count == 0)
                        return false;
                    foreach (XYZ vertex in mesh.Vertices)
                    {
                        hasVertex = true;
                        minX = Math.Min(minX, vertex.X);
                        minY = Math.Min(minY, vertex.Y);
                        minZ = Math.Min(minZ, vertex.Z);
                        maxX = Math.Max(maxX, vertex.X);
                        maxY = Math.Max(maxY, vertex.Y);
                        maxZ = Math.Max(maxZ, vertex.Z);
                    }
                }
                catch
                {
                    return false;
                }
            }
            if (!hasVertex || minX > maxX) return false;
            bounds = new PlenumAnalysisService.Bounds3
            {
                MinX = minX, MinY = minY, MinZ = minZ,
                MaxX = maxX, MaxY = maxY, MaxZ = maxZ
            };
            return true;
        }

        private static bool BoundsOverlap(
            PlenumAnalysisService.Bounds3 a,
            PlenumAnalysisService.Bounds3 b)
        {
            return a != null && b != null
                && a.MaxX >= b.MinX && a.MinX <= b.MaxX
                && a.MaxY >= b.MinY && a.MinY <= b.MaxY
                && a.MaxZ >= b.MinZ && a.MinZ <= b.MaxZ;
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

        // ---------------------------------------------------------------- evidence

        /// <summary>复查入口：重算证据指纹（用于审批前的快照一致性校验）。</summary>
        internal static string ComputeFingerprintForReview(HandReachAnalysisResult result)
        {
            return result == null ? string.Empty : ComputeFingerprint(result);
        }

        private static string ComputeFingerprint(HandReachAnalysisResult result)
        {
            var builder = new StringBuilder();
            builder.AppendLine("JarviTools.MaintenanceHandReach.Evidence.v2");
            builder.AppendLine("group=" + result.GroupKey);
            builder.AppendLine("ceilingTopMm=" + result.CeilingTopMm.ToString("F1"));
            builder.AppendLine("model=" + result.ModelFingerprint);
            builder.AppendLine("coverageComplete=" + result.CoverageComplete);
            builder.AppendLine("linkScope=" +
                               MaintenanceLinkScopePolicy.BuildSignature(result.LinkScope));
            builder.AppendLine("hatch=" + result.Options.HatchSizeMm.ToString("F1") +
                               " grid=" + result.Options.GridSpacingMm.ToString("F1") +
                               " n=" + result.Options.GridPointsPerAxis +
                               " preference=" + result.Options.OpeningPreference +
                               " strictCeilingSelection=" +
                               result.Options.StrictCeilingSelection +
                               " ceilingPersonnelEntryRise=" +
                               result.Options.CeilingPersonnelEntryRiseMm.ToString("F1") +
                               " ceilingPersonnelFinalReachGap=" +
                               result.Options.CeilingPersonnelFinalReachGapMm.ToString("F1") +
                               " allowSideWallDistanceOver500Review=" +
                               result.Options.AllowSideWallDistanceOver500Review +
                               " sideWallReviewMaxDistance=" +
                               result.Options.SideWallReviewMaxDistanceMm.ToString("F1") +
                               " sideWallOperatorZone=" +
                               result.Options.SideWallOperatorZoneDepthMm.ToString("F1") + "x" +
                               result.Options.SideWallOperatorZoneWidthMm.ToString("F1") +
                               " ceilingDirectOperatorZone=" +
                               result.Options.CeilingDirectOperatorZoneLengthMm.ToString("F1") + "x" +
                               result.Options.CeilingDirectOperatorZoneWidthMm.ToString("F1") +
                               " corridor=" + result.Options.DefaultCorridorDiameterMm.ToString("F1") +
                               " tests=" + string.Join(",", result.Options.CorridorTestDiametersMm.Select(x => x.ToString("F1"))));
            foreach (string key in result.CeilingSources.Select(x => x.GetStableKey()).OrderBy(x => x, StringComparer.Ordinal))
                builder.AppendLine("ceiling=" + key);
            foreach (HandReachTargetResult target in result.TargetResults)
            {
                HandReachTargetInfo info = target.Target;
                builder.AppendLine("target=" + info.TargetKey + "|" + info.EquipmentName + "|" +
                                   info.Mark + "|schemeNo=" + info.SchemeNo +
                                   "|legacySchemeNos=" + string.Join(",",
                                       info.LegacySchemeNos.OrderBy(x => x)));
                builder.AppendLine("targetGroup=" + info.GroupKey +
                                   " ceilingTopMm=" + info.CeilingTopMm.ToString("F1"));
                builder.AppendLine("supply=" + info.SupplyDirectionX.ToString("F6") + "," + info.SupplyDirectionY.ToString("F6"));
                builder.AppendLine("service=" + info.ServiceDirectionX.ToString("F6") + "," + info.ServiceDirectionY.ToString("F6"));
                builder.AppendLine("proxy=" + info.ServiceFaceProxyX.ToString("F1") + "," +
                                   info.ServiceFaceProxyY.ToString("F1") + "," +
                                   info.ServiceFaceProxyZ.ToString("F1"));
                builder.AppendLine("ceilingPersonnelEntry=" +
                                   target.CeilingPersonnelEntryApplied + ",deviceMoved=false," +
                                   target.ModelVerticalDifferenceMm.ToString("F1") + "," +
                                   target.AnalysisVerticalDifferenceMm.ToString("F1") + "," +
                                   target.AnalysisServiceFaceProxyZ.ToString("F1"));
                builder.AppendLine("ceilingDirectReach=" +
                                   target.CeilingDirectReachApplied +
                                   ",startFromRoomSide=true");
                builder.AppendLine("modelDeviceBounds=" +
                                   target.ModelDeviceMinX.ToString("F1") + "," +
                                   target.ModelDeviceMinY.ToString("F1") + "," +
                                   target.ModelDeviceMinZ.ToString("F1") + "," +
                                   target.ModelDeviceMaxX.ToString("F1") + "," +
                                   target.ModelDeviceMaxY.ToString("F1") + "," +
                                   target.ModelDeviceMaxZ.ToString("F1") +
                                   " deviceOriginalZ=" +
                                   target.ModelDeviceMinZ.ToString("F1") + "," +
                                   target.ModelDeviceMaxZ.ToString("F1"));
                builder.AppendLine("opStatus=" + info.OperationPointStatus);
                builder.AppendLine("ladderFloor=" + target.LadderFloorMm.ToString("F1") +
                                   " status=" + target.LadderStatus);
                builder.AppendLine("selectedPlane=" + target.SelectedOpeningPlane +
                                   " hasSelectedOpening=" + target.HasSelectedOpening +
                                   " sideWallAttempted=" + target.SideWallAttempted);
                builder.AppendLine("sideWallCounts=" +
                                   target.SideWallRawSampleCount + "," +
                                   target.SideWallFaceFitCount + "," +
                                   target.SideWallDistanceOkCount + "," +
                                   target.SideWallOpeningFailCount + "," +
                                   target.SideWallCorridorFailCount + "," +
                                   target.SideWallLadderFailCount + "," +
                                   target.SideWallClearCount);
                builder.AppendLine("counts=" + target.RawSampleCount + "," + target.HatchInsideCount + "," +
                                   target.VerticalFailCount + "," + target.DistanceOkCount + "," +
                                   target.OpeningFailCount + "," +
                                   target.CorridorFailCount + "," + target.LadderFailCount + "," +
                                   target.ClearCount + "," + target.Regions4Count + "," + target.Regions8Count);
                builder.AppendLine("candidateAuditComplete=" + target.CandidateAuditComplete);
                builder.AppendLine("selectedCandidateAuditComplete=" +
                                   target.SelectedCandidateAuditComplete);
                foreach (HandReachRegion region in target.Regions)
                {
                    HandReachSample recommended = region.Recommended;
                    builder.AppendLine("region=" + region.RegionNo + "," + region.PointCount + "," +
                                       region.OpeningPlane + "," + region.SurfaceKey + "," +
                                       region.MinX.ToString("F1") + "," + region.MaxX.ToString("F1") + "," +
                                       region.MinY.ToString("F1") + "," + region.MaxY.ToString("F1") + "," +
                                       region.MinZ.ToString("F1") + "," + region.MaxZ.ToString("F1") + "," +
                                       recommended.CenterX.ToString("F1") + "," +
                                       recommended.CenterY.ToString("F1") + "," +
                                       recommended.CenterZ.ToString("F1") + "," +
                                       recommended.EdgeX.ToString("F1") + "," +
                                       recommended.EdgeY.ToString("F1") + "," +
                                       recommended.EdgeZ.ToString("F1") + "," +
                                       recommended.ChannelStartX.ToString("F1") + "," +
                                       recommended.ChannelStartY.ToString("F1") + "," +
                                       recommended.ChannelStartZ.ToString("F1") + "," +
                                       recommended.OpeningTangentX.ToString("F6") + "," +
                                       recommended.OpeningTangentY.ToString("F6") + "," +
                                       recommended.OpeningInwardX.ToString("F6") + "," +
                                       recommended.OpeningInwardY.ToString("F6") + "," +
                                       recommended.OpeningDepthMm.ToString("F1") + "," +
                                       recommended.LadderCenterX.ToString("F1") + "," +
                                       recommended.LadderCenterY.ToString("F1") + "," +
                                       recommended.LadderAlongX.ToString("F6") + "," +
                                       recommended.LadderAlongY.ToString("F6") + "," +
                                       recommended.ObliqueMm.ToString("F2") + "," +
                                       recommended.VerticalMm.ToString("F2") + "," +
                                       region.MaxTestedClearDiameterMm + "," +
                                       region.RecommendedLadderDirection + "," +
                                       region.RecommendedExemptIntersectCount);
                }
                foreach (string key in target.ExemptEvidence.Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal))
                    builder.AppendLine("exempt=" + key);
                foreach (string key in target.RealObstacles.Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal))
                    builder.AppendLine("obstacle=" + key);
                builder.AppendLine("conclusion=" + target.Conclusion + "|" + target.AttentionLevel);
            }
            foreach (HandReachCoverageFailure failure in result.CoverageFailures
                .Where(x => x != null)
                .OrderBy(x => x.Stage, StringComparer.Ordinal)
                .ThenBy(x => x.SourceKey, StringComparer.Ordinal)
                .ThenBy(x => x.Reason, StringComparer.Ordinal))
            {
                builder.AppendLine("coverageFailure=" + failure.Stage + "|" +
                                   failure.SourceKey + "|" + failure.Category + "|" +
                                   failure.Mark + "|" + failure.Reason);
            }
            foreach (MaintenancePipeExemptionEvidence evidence in result.ExemptPipeEvidence
                .Where(x => x != null && x.Element != null)
                .OrderBy(x => x.GroupKey, StringComparer.Ordinal)
                .ThenBy(x => x.TargetKey, StringComparer.Ordinal)
                .ThenBy(x => x.Element.GetStableKey(), StringComparer.Ordinal))
                builder.AppendLine("pipeExemption=" +
                    MaintenancePipeExemptionPolicy.BuildStoredEvidenceSignature(evidence));
            foreach (string limitation in result.CoverageLimitations
                .OrderBy(x => x, StringComparer.Ordinal))
                builder.AppendLine("coverageLimitation=" + limitation);
            foreach (string warning in result.Warnings.OrderBy(x => x, StringComparer.Ordinal))
                builder.AppendLine("warning=" + warning);
            return MaintenanceLedgerCsv.Sha256Hex(builder.ToString());
        }

        // ---------------------------------------------------------------- element helpers

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
            HandReachAnalysisResult result,
            string stage,
            IEnumerable<PlenumAnalysisService.CandidateCollectionFailure> failures)
        {
            if (failures == null) return;
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
                string failureKey = Safe(failure.SourceKey);
                if (IsLinkInstanceScanFailure(failure, source) &&
                    !linkScanKeys.Add(failureKey))
                    continue;
                RecordCoverageFailure(
                    result,
                    stage,
                    Safe(failure.SourceKey),
                    source == null ? null : source.LinkInstanceId,
                    source == null ? string.Empty : Safe(source.LinkInstanceUniqueId),
                    source == null ? 0L : source.ElementId,
                    source == null ? failure.Category.ToString() : Safe(source.Category),
                    string.Empty,
                    Safe(failure.Reason));
                AddEvidenceSource(result, ToElementRef(source));
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

        private static void RecordDeviceDiscoveryFailure(
            HandReachAnalysisResult result,
            PlenumAnalysisService.Candidate candidate,
            string reason)
        {
            PlenumSourceRef source = candidate == null ? null : candidate.Source;
            Element element = candidate == null ? null : candidate.Element;
            RecordCoverageFailure(
                result,
                "device_geometry",
                candidate == null ? string.Empty : Safe(candidate.SourceKey),
                source == null ? null : source.LinkInstanceId,
                source == null ? string.Empty : Safe(source.LinkInstanceUniqueId),
                source == null
                    ? (element == null ? 0L : element.Id.Value)
                    : source.ElementId,
                source == null
                    ? (element == null || element.Category == null
                        ? string.Empty
                        : element.Category.Name)
                    : Safe(source.Category),
                element == null ? string.Empty : ReadMark(element),
                reason);
            AddEvidenceSource(result, ToElementRef(candidate));
        }

        private static void RecordCoverageFailure(
            HandReachAnalysisResult result,
            string stage,
            string sourceKey,
            long? linkInstanceId,
            string linkInstanceUniqueId,
            long elementId,
            string category,
            string mark,
            string reason)
        {
            if (result == null) return;
            stage = Safe(stage);
            sourceKey = Safe(sourceKey);
            reason = Safe(reason);
            if (result.CoverageFailures.Any(x => x != null &&
                string.Equals(x.Stage, stage, StringComparison.Ordinal) &&
                string.Equals(x.SourceKey, sourceKey, StringComparison.Ordinal) &&
                string.Equals(x.Reason, reason, StringComparison.Ordinal)))
                return;
            result.CoverageComplete = false;
            result.CoverageFailures.Add(new HandReachCoverageFailure
            {
                Stage = stage,
                SourceKey = sourceKey,
                LinkInstanceId = linkInstanceId,
                LinkInstanceUniqueId = Safe(linkInstanceUniqueId),
                ElementId = elementId,
                Category = Safe(category),
                Mark = Safe(mark),
                Reason = reason
            });
            result.Warnings.Add("证据覆盖不完整 [" + stage + "] " + sourceKey +
                                (string.IsNullOrWhiteSpace(mark) ? string.Empty : " 标记=" + mark) +
                                "：" + reason + "；禁止正式审批或写入。");
        }

        private static string DeviceSourceKey(DeviceWork device)
        {
            return device == null ? string.Empty : Safe(device.Info.TargetKey);
        }

        private static MaintenanceElementRef ToElementRef(Document doc, Element element)
        {
            return new MaintenanceElementRef
            {
                DocumentTitle = doc == null ? string.Empty : doc.Title ?? string.Empty,
                ElementId = element.Id.Value,
                UniqueId = Safe(element.UniqueId),
                Category = element.Category == null ? string.Empty : element.Category.Name,
                Name = element.Name ?? string.Empty
            };
        }

        private static MaintenanceElementRef ToElementRef(
            PlenumAnalysisService.Candidate candidate)
        {
            if (candidate == null || candidate.Element == null) return null;
            PlenumSourceRef source = candidate.Source;
            return new MaintenanceElementRef
            {
                DocumentTitle = source == null ? candidate.Element.Document.Title : source.DocumentTitle,
                LinkInstanceId = source == null ? null : source.LinkInstanceId,
                LinkInstanceUniqueId = source == null
                    ? string.Empty
                    : Safe(source.LinkInstanceUniqueId),
                ElementId = source == null ? candidate.Element.Id.Value : source.ElementId,
                UniqueId = source == null ? Safe(candidate.Element.UniqueId) : Safe(source.UniqueId),
                Category = source == null
                    ? (candidate.Element.Category == null ? string.Empty : candidate.Element.Category.Name)
                    : Safe(source.Category),
                Name = source == null ? Safe(candidate.Element.Name) : Safe(source.Name)
            };
        }

        private static MaintenanceElementRef ToElementRef(
            PlenumAnalysisService.Candidate candidate,
            Element relatedElement)
        {
            if (candidate == null || candidate.Element == null || relatedElement == null ||
                relatedElement.Document != candidate.Element.Document)
                return null;
            PlenumSourceRef source = candidate.Source;
            return new MaintenanceElementRef
            {
                DocumentTitle = source == null
                    ? Safe(relatedElement.Document.Title)
                    : Safe(source.DocumentTitle),
                LinkInstanceId = source == null ? null : source.LinkInstanceId,
                LinkInstanceUniqueId = source == null
                    ? string.Empty
                    : Safe(source.LinkInstanceUniqueId),
                ElementId = relatedElement.Id.Value,
                UniqueId = Safe(relatedElement.UniqueId),
                Category = relatedElement.Category == null
                    ? string.Empty
                    : relatedElement.Category.Name,
                Name = Safe(relatedElement.Name)
            };
        }

        private static MaintenanceElementRef ToElementRef(PlenumSourceRef source)
        {
            if (source == null) return null;
            return new MaintenanceElementRef
            {
                DocumentTitle = Safe(source.DocumentTitle),
                LinkInstanceId = source.LinkInstanceId,
                LinkInstanceUniqueId = Safe(source.LinkInstanceUniqueId),
                ElementId = source.ElementId,
                UniqueId = Safe(source.UniqueId),
                Category = Safe(source.Category),
                Name = Safe(source.Name)
            };
        }

        private static MaintenanceElementRef ToElementRef(DeviceWork device)
        {
            if (device == null || device.Element == null) return null;
            return new MaintenanceElementRef
            {
                DocumentTitle = device.Element.Document == null
                    ? string.Empty
                    : Safe(device.Element.Document.Title),
                LinkInstanceId = device.Info.LinkInstanceId > 0
                    ? (long?)device.Info.LinkInstanceId
                    : null,
                LinkInstanceUniqueId = Safe(device.LinkInstanceUniqueId),
                ElementId = device.Info.ElementId,
                UniqueId = Safe(device.Element.UniqueId),
                Category = device.Element.Category == null
                    ? string.Empty
                    : device.Element.Category.Name,
                Name = Safe(device.Element.Name)
            };
        }

        private static void AddEvidenceSource(
            HandReachAnalysisResult result,
            MaintenanceElementRef source)
        {
            if (result == null || source == null) return;
            string key = source.GetStableKey();
            if (result.EvidenceSources.Any(x =>
                x != null && string.Equals(x.GetStableKey(), key, StringComparison.Ordinal)))
                return;
            result.EvidenceSources.Add(source);
        }

        private static void AddEvidenceSources(
            HandReachAnalysisResult result,
            IEnumerable<MaintenanceElementRef> sources)
        {
            if (result == null || sources == null) return;
            var keys = new HashSet<string>(
                result.EvidenceSources.Where(x => x != null).Select(x => x.GetStableKey()),
                StringComparer.Ordinal);
            foreach (MaintenanceElementRef source in sources.Where(x => x != null))
                if (keys.Add(source.GetStableKey()))
                    result.EvidenceSources.Add(source);
        }

        private static bool IsCeiling(Element element)
        {
            return element != null && element.Category != null &&
                   element.Category.Id.Value == (long)BuiltInCategory.OST_Ceilings;
        }

        private sealed class ElementIdComparer : IEqualityComparer<Element>
        {
            public bool Equals(Element x, Element y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x == null || y == null) return false;
                return x.Id.Value == y.Id.Value;
            }

            public int GetHashCode(Element obj)
            {
                return obj == null ? 0 : obj.Id.Value.GetHashCode();
            }
        }

        private static string ReadComments(Element element)
        {
            if (element == null) return string.Empty;
            string value = Safe(ReadParameterText(element, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS));
            return value ?? string.Empty;
        }

        private static string ReadMark(Element element)
        {
            if (element == null) return string.Empty;
            return Safe(ReadParameterText(element, BuiltInParameter.ALL_MODEL_MARK)) ?? string.Empty;
        }

        private static string ResolveEquipmentName(Element element)
        {
            if (element == null) return string.Empty;
            string name = element.Name;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            return element.Category == null ? string.Empty : element.Category.Name;
        }

        private static string ReadParameterText(Element element, string parameterName)
        {
            if (element == null) return string.Empty;
            Parameter parameter = element.LookupParameter(parameterName);
            if (parameter == null) return string.Empty;
            try
            {
                if (parameter.StorageType == StorageType.String)
                    return Safe(parameter.AsString()) ?? string.Empty;
                return Safe(parameter.AsValueString()) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ReadParameterText(Element element, BuiltInParameter builtInParameter)
        {
            if (element == null) return string.Empty;
            Parameter parameter = element.get_Parameter(builtInParameter);
            if (parameter == null) return string.Empty;
            try
            {
                if (parameter.StorageType == StorageType.String)
                    return Safe(parameter.AsString()) ?? string.Empty;
                return Safe(parameter.AsValueString()) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string Safe(string value)
        {
            return value ?? string.Empty;
        }

        private static void RecordBlocker(Dictionary<string, int> counts, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            int count;
            counts.TryGetValue(key, out count);
            counts[key] = count + 1;
        }
    }
}

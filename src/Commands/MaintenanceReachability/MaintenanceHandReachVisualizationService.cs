using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using JarviTools.Commands.MaintenanceReachability;

namespace JarviTools.Commands.MaintenanceReachability
{
    /// <summary>
    /// HandReach 正式可视化：正式 Owner ApplicationId、8 个共享参数全填、
    /// 统一颜色语义（蓝=入口、灰白=梯子、橙=伸手通道、紫=检修面代理点）、按需生成视图。
    /// 所有写操作在调用方事务内完成前由本服务自己开启事务。
    /// </summary>
    internal static class MaintenanceHandReachVisualizationService
    {
        internal const string FormalApplicationId = "JarviTools.MaintenanceHandReach.v1";
        private const string ManagedViewOwnerId =
            MaintenanceManagedViewPolicy.FormalManagedViewOwnerId;
        private const string AiManagedViewOwnerId =
            MaintenanceManagedViewService.AiInternalViewOwnerId;
        private const double MmPerFoot = 304.8;
        private static readonly Guid HandReachTraceSchemaGuid =
            new Guid("c60864f7-9bce-4cc0-8e52-5892dcc2656c");
        private const string ResultFingerprintField = "ResultFingerprint";
        private const string OpeningIdentityField = "OpeningIdentity";

        internal sealed class ShowStats
        {
            public int CreatedElementCount;
            public int DeletedPreviousElementCount;
            public int DeletedLegacyViewCount;
            public int CreatedViewCount;
            public readonly List<string> ViewNames = new List<string>();
            public readonly List<long> ViewIds = new List<long>();
            public readonly List<string> Warnings = new List<string>();
        }

        internal sealed class ClearStats
        {
            public int DeletedShapeCount;
            public int DeletedViewCount;
            public readonly List<string> Warnings = new List<string>();
            public int TotalDeletedCount
            {
                get { return DeletedShapeCount + DeletedViewCount; }
            }
        }

        private sealed class SavedUserState
        {
            public string Conclusion;
            public string ProfessionalNote;
            public string DecisionNote;
            public string EvidenceFingerprint;
            public string ResultFingerprint;
            public string OpeningIdentity;
        }

        private sealed class HandReachShapeTrace
        {
            public string ResultFingerprint = string.Empty;
            public string OpeningIdentity = string.Empty;
        }

        private sealed class ExistingShapeInfo
        {
            public DirectShape Shape;
            public string GroupKey;
            public string DeviceNo;
            public int SchemeNo;
            public string MaintenanceTarget;
            public string TargetHash;
            public string ComponentRole;
        }

        private sealed class TargetVisualizationScope
        {
            public HandReachTargetResult Target;
            public HandReachRegion Region;
            public string GroupKey;
            public string DeviceNo;
            public string MaintenanceTarget;
            public double CeilingTopMm;
            public int SchemeNo;
            public string EntryGroup;
            public bool AllowLegacyPairMatch;
            public bool ReplaceUnnumbered;
            public readonly HashSet<int> LegacySchemeNos = new HashSet<int>();
            public readonly Dictionary<string, SavedUserState> SavedStates =
                new Dictionary<string, SavedUserState>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> SavedProfessionalNotes =
                new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly List<ElementId> ShapeIds = new List<ElementId>();
        }

        internal static ShowStats Show(
            UIApplication uiapp,
            HandReachAnalysisResult result,
            bool createViews,
            string reviewer,
            string reviewNote,
            string approvedAtUtc)
        {
            if (uiapp == null) throw new ArgumentNullException("uiapp");
            if (result == null) throw new ArgumentNullException("result");
            UIDocument uidoc = uiapp.ActiveUIDocument;
            if (uidoc == null) throw new InvalidOperationException("Revit 没有活动文档。");
            Document doc = uidoc.Document;
            if (doc.IsModifiable)
                throw new InvalidOperationException("HandReach 可视化需要自主事务，不能在其他事务内调用。");

            var stats = new ShowStats();
            Dictionary<HandReachTargetInfo, int> approvedAssignments = result.TargetResults
                .Where(x => x != null && x.Target != null)
                .Select(x => x.Target)
                .Distinct()
                .ToDictionary(x => x, x => x.SchemeNo);
            ResolveSchemeAssignments(doc, result);
            List<HandReachTargetInfo> changedAssignments = approvedAssignments
                .Where(x => x.Value > 0 && x.Key.SchemeNo != x.Value)
                .Select(x => x.Key)
                .ToList();
            if (changedAssignments.Count > 0)
            {
                foreach (KeyValuePair<HandReachTargetInfo, int> pair in approvedAssignments)
                    pair.Key.SchemeNo = pair.Value;
                throw new InvalidOperationException(
                    "现有侧墙方案占号在审批后发生变化；为避免把已审批结果写成另一方案号，请重新分析并审批。");
            }

            List<ExistingShapeInfo> existing = ReadExistingMaintenanceShapes(doc);
            List<TargetVisualizationScope> scopes = BuildTargetScopes(result, existing, stats);
            CaptureSavedUserStates(scopes, existing);
            List<ElementId> legacyViewIds = FindManagedLegacySchemeViewIds(doc, scopes);
            var deletableLegacyViewIds = new List<ElementId>();
            foreach (ElementId viewId in legacyViewIds)
            {
                View view = doc.GetElement(viewId) as View;
                string reason;
                if (MaintenanceManagedViewService.CanSafelyDelete(doc, view, out reason))
                    deletableLegacyViewIds.Add(viewId);
                else if (view != null)
                    stats.Warnings.Add(view.Name + "：" + reason);
            }
            MoveAwayFromDeletedView(
                uidoc,
                deletableLegacyViewIds,
                scopes.Select(x => BuildOverviewViewName(x.GroupKey)));
            using (var tx = new Transaction(doc, "OpenRevit HandReach 正式结果写入"))
            {
                tx.Start();
                try
                {
                    // 8 参数绑定
                    MaintenanceParameterService.EnsureSharedParameters(
                        doc, MaintenanceParameterService.GetDefaultSharedParameterFilePath());

                    // 只替换本次 group + device + scheme，其他分组和方案保持不动。
                    List<ElementId> previous = existing
                        .Where(x => string.Equals(
                            x.Shape.ApplicationId,
                            FormalApplicationId,
                            StringComparison.Ordinal))
                        .Where(x => scopes.Any(scope => MatchesReplacementScope(scope, x)))
                        .Select(x => x.Shape.Id)
                        .Distinct()
                        .ToList();
                    if (previous.Count > 0)
                    {
                        doc.Delete(previous);
                        stats.DeletedPreviousElementCount = previous.Count;
                    }
                    if (deletableLegacyViewIds.Count > 0)
                    {
                        doc.Delete(deletableLegacyViewIds);
                        stats.DeletedLegacyViewCount = deletableLegacyViewIds.Count;
                    }

                    var genericModelId = new ElementId((long)BuiltInCategory.OST_GenericModel);
                    var newShapeIds = new List<ElementId>();

                    foreach (TargetVisualizationScope scope in scopes)
                    {
                        HandReachTargetResult target = scope.Target;
                        HandReachRegion region = scope.Region;
                        if (region == null) continue;
                        HandReachSample rec = region.Recommended;
                        HandReachTargetInfo info = target.Target;
                        string dataIdPrefix = BuildDataIdPrefix(scope);
                        double ceilingTopMm = scope.CeilingTopMm;
                        int hatchSize = (int)Math.Round(result.Options.HatchSizeMm);
                        int defaultDiameter = (int)Math.Round(result.Options.DefaultCorridorDiameterMm);

                        // 1) 检修口（蓝=入口）。天花口为水平薄盒；侧墙口按真实墙面切向
                        // 建竖向400/450证据体，不能把XY天花几何直接冒充墙洞。
                        bool sideWall = rec.OpeningPlane ==
                            HandReachOpeningPlaneKind.SideWallVertical;
                        bool ceilingDirectReach = !sideWall &&
                            target.CeilingDirectReachApplied;
                        XYZ openingTangent = sideWall
                            ? new XYZ(rec.OpeningTangentX, rec.OpeningTangentY, 0.0)
                            : XYZ.BasisX;
                        if (openingTangent.GetLength() <= 1e-9) openingTangent = XYZ.BasisX;
                        else openingTangent = openingTangent.Normalize();
                        XYZ openingInward = sideWall
                            ? new XYZ(rec.OpeningInwardX, rec.OpeningInwardY, 0.0)
                            : XYZ.Zero;
                        if (sideWall && openingInward.GetLength() <= 1e-9)
                            throw new InvalidOperationException("侧墙检修口缺少墙内侧指向设备的真实法向，拒绝生成错位洞体。");
                        if (sideWall) openingInward = openingInward.Normalize();
                        double openingBottomMm = sideWall
                            ? rec.CenterZ - result.Options.HatchSizeMm * 0.5
                            : (ceilingDirectReach
                                ? ceilingTopMm - result.Options.OpeningHeightMm
                                : ceilingTopMm);
                        double openingDepthMm = sideWall
                            ? Math.Max(20.0, rec.OpeningDepthMm)
                            : result.Options.HatchSizeMm;
                        XYZ openingCenter = new XYZ(
                            rec.CenterX / MmPerFoot,
                            rec.CenterY / MmPerFoot,
                            openingBottomMm / MmPerFoot);
                        if (sideWall)
                            openingCenter -= openingInward.Multiply(
                                openingDepthMm * 0.5 / MmPerFoot);

                        // 侧墙伸手口与人员门使用同一条原则：墙不是项目预建实体，
                        // 而是由天花边界自动生成。只为本方案生成承载该方口的墙段，
                        // 并把当前400/450开口从透明墙体中真实留空。
                        if (sideWall && rec.UsesVirtualBoundaryWall)
                        {
                            List<Solid> wallPieces = BuildVirtualBoundaryWallSolids(
                                rec, result.Options.HatchSizeMm);
                            if (wallPieces.Count == 0)
                                throw new InvalidOperationException(
                                    "侧墙检修口缺少可写入的天花边界虚拟墙体，拒绝生成孤立洞体。");
                            DirectShape wallShape = CreateShape(doc, genericModelId,
                                dataIdPrefix + "|VirtualBoundaryWall|Surface" +
                                HashShort(rec.SurfaceKey),
                                wallPieces.Cast<GeometryObject>().ToList());
                            ApplyParameters(wallShape, scope,
                                scope.GroupKey + "-设备" + scope.DeviceNo + "-虚拟边界墙",
                                "虚拟边界墙",
                                "由天花真实顶面边界自动生成；墙厚" +
                                openingDepthMm.ToString("F0") + "mm；" +
                                hatchSize + "×" + hatchSize + "侧墙口已留洞。",
                                result.EvidenceFingerprint,
                                result.ResultFingerprint,
                                stats);
                            StampReviewTrace(wallShape, result, reviewer, reviewNote, approvedAtUtc);
                            newShapeIds.Add(wallShape.Id);
                            scope.ShapeIds.Add(wallShape.Id);
                        }
                        Solid hatch = MaintenanceGeometryService.MakeBox(
                            openingCenter,
                            result.Options.HatchSizeMm / MmPerFoot,
                            sideWall
                                ? openingDepthMm / MmPerFoot
                                : result.Options.HatchSizeMm / MmPerFoot,
                            sideWall
                                ? result.Options.HatchSizeMm / MmPerFoot
                                : result.Options.OpeningHeightMm / MmPerFoot,
                            openingTangent);
                        DirectShape hatchShape = CreateShape(doc, genericModelId,
                            dataIdPrefix + "|Opening" + rec.OpeningPlane +
                            "|Surface" + HashShort(rec.SurfaceKey) + "|Hatch" + hatchSize,
                            new GeometryObject[] { hatch });
                        ApplyParameters(hatchShape, scope,
                            hatchSize + "×" + hatchSize +
                            (sideWall ? "侧墙检修口" : "天花检修口"),
                            sideWall ? "侧墙检修口" : "天花检修口",
                            hatchSize + "口；实际距离" +
                            rec.ObliqueMm.ToString("F1") + "mm；垂直高差" +
                            rec.VerticalMm.ToString("F1") + "mm；默认通道" + defaultDiameter +
                            "；最大通道" + region.MaxTestedClearDiameterMm + "；关注级" +
                            target.AttentionLevel,
                            result.EvidenceFingerprint,
                            result.ResultFingerprint,
                            stats);
                        StampReviewTrace(hatchShape, result, reviewer, reviewNote, approvedAtUtc);
                        newShapeIds.Add(hatchShape.Id);
                        scope.ShapeIds.Add(hatchShape.Id);

                        // 2) 只有高位天花450口建立人员钻入包络（橙色透明）；
                        // 贴近天花的设备直接从洞口下方伸手，不建立人体包络。
                        if (!sideWall && rec.PersonnelEntryTopZ >
                            ceilingTopMm + result.Options.OpeningHeightMm + 1.0)
                        {
                            Solid personnelEnvelope = MaintenanceGeometryService.MakeBox(
                                new XYZ(rec.CenterX / MmPerFoot,
                                    rec.CenterY / MmPerFoot,
                                    (ceilingTopMm + result.Options.OpeningHeightMm) /
                                        MmPerFoot),
                                result.Options.HatchSizeMm / MmPerFoot,
                                result.Options.HatchSizeMm / MmPerFoot,
                                (rec.PersonnelEntryTopZ - ceilingTopMm -
                                    result.Options.OpeningHeightMm) / MmPerFoot,
                                XYZ.BasisX);
                            DirectShape personnelShape = CreateShape(
                                doc,
                                genericModelId,
                                dataIdPrefix + "|PersonnelEntry" + hatchSize +
                                    "|Top" + rec.PersonnelEntryTopZ.ToString("F1"),
                                new GeometryObject[] { personnelEnvelope });
                            ApplyParameters(
                                personnelShape,
                                scope,
                                "人员钻入包络" + hatchSize,
                                "人员钻入包络",
                                "设备保持模型原高度；人员从" + hatchSize +
                                    "×" + hatchSize + "天花口向吊顶内探入至" +
                                    rec.PersonnelEntryTopZ.ToString("F1") + "mm。",
                                result.EvidenceFingerprint,
                                result.ResultFingerprint,
                                stats);
                            StampReviewTrace(personnelShape, result, reviewer,
                                reviewNote, approvedAtUtc);
                            newShapeIds.Add(personnelShape.Id);
                            scope.ShapeIds.Add(personnelShape.Id);
                        }

                        // 3) 侧墙和贴近天花方案为直接伸手通道；
                        // 高位天花方案为人员钻入后的最后操作伸手段（橙）。
                        XYZ edgePt = new XYZ(rec.EdgeX / MmPerFoot, rec.EdgeY / MmPerFoot,
                            (sideWall ? rec.EdgeZ :
                                (rec.EdgeZ != 0.0 ? rec.EdgeZ : ceilingTopMm)) / MmPerFoot);
                        XYZ startPt;
                        if (rec.ChannelStartX != 0.0 || rec.ChannelStartY != 0.0 ||
                            rec.ChannelStartZ != 0.0)
                        {
                            startPt = new XYZ(rec.ChannelStartX / MmPerFoot,
                                rec.ChannelStartY / MmPerFoot,
                                rec.ChannelStartZ / MmPerFoot);
                        }
                        else
                        {
                            XYZ inward = new XYZ((rec.CenterX - rec.EdgeX) / MmPerFoot,
                                (rec.CenterY - rec.EdgeY) / MmPerFoot, 0.0);
                            if (inward.GetLength() <= 1e-9) inward = XYZ.BasisX;
                            else inward = inward.Normalize();
                            startPt = edgePt + inward.Multiply(
                                result.Options.ChannelInwardOffsetMm / MmPerFoot) +
                                XYZ.BasisZ.Multiply(
                                    result.Options.ChannelCeilingLiftMm / MmPerFoot);
                        }
                        double analysisProxyZ = target.AnalysisServiceFaceProxyZ != 0.0
                            ? target.AnalysisServiceFaceProxyZ
                            : info.ServiceFaceProxyZ;
                        XYZ endPt = new XYZ(info.ServiceFaceProxyX / MmPerFoot,
                            info.ServiceFaceProxyY / MmPerFoot,
                            analysisProxyZ / MmPerFoot);
                        Solid corridor = MakeCylinder(startPt, endPt,
                            result.Options.DefaultCorridorDiameterMm * 0.5 / MmPerFoot);
                        DirectShape corridorShape = CreateShape(doc, genericModelId,
                            dataIdPrefix + (sideWall || ceilingDirectReach
                                ? "|HandReach"
                                : "|FinalHandReach") +
                            defaultDiameter +
                            "|Distance" + rec.ObliqueMm.ToString("F1"),
                            new GeometryObject[] { corridor });
                        ApplyParameters(corridorShape, scope,
                            sideWall || ceilingDirectReach
                                ? "伸手通道" + defaultDiameter
                                : "钻入后操作伸手段" + defaultDiameter,
                            sideWall || ceilingDirectReach
                                ? "伸手通道"
                                : "钻入后操作伸手段",
                            (sideWall
                                ? "从侧墙口最近边缘到检修面代理点；"
                                : (ceilingDirectReach
                                    ? "从" + hatchSize + "×" + hatchSize + "天花口室内侧直接到检修面代理点；不建立人员钻入包络；"
                                    : "人员钻入包络顶面到检修面代理点；设备未移动；")) +
                            "通道直径" + defaultDiameter + "mm；最大测试通道" +
                            region.MaxTestedClearDiameterMm + "mm；冷媒/冷凝水豁免" +
                             (target.ExemptSolidCount > 0 ? "已记录" : "无"),
                            result.EvidenceFingerprint,
                            result.ResultFingerprint,
                            stats);
                        StampReviewTrace(corridorShape, result, reviewer, reviewNote, approvedAtUtc);
                        newShapeIds.Add(corridorShape.Id);
                        scope.ShapeIds.Add(corridorShape.Id);

                        // 4) 检修面代理点（紫）
                        Solid proxyMarker = MaintenanceGeometryService.MakeBox(
                            new XYZ((info.ServiceFaceProxyX - 100.0) / MmPerFoot,
                                    (info.ServiceFaceProxyY - 100.0) / MmPerFoot,
                                    (analysisProxyZ - 100.0) / MmPerFoot),
                            200.0 / MmPerFoot, 200.0 / MmPerFoot, 200.0 / MmPerFoot, XYZ.BasisX);
                        DirectShape proxyShape = CreateShape(doc, genericModelId,
                            dataIdPrefix + "|ServiceFaceProxy",
                            new GeometryObject[] { proxyMarker });
                        // 验证细节留在台账；明细表中的代理点判断说明保持为空。
                        ApplyParameters(proxyShape, scope,
                            "检修面代理点", "检修面代理点", string.Empty,
                            result.EvidenceFingerprint, result.ResultFingerprint, stats);
                        StampReviewTrace(proxyShape, result, reviewer, reviewNote, approvedAtUtc);
                        newShapeIds.Add(proxyShape.Id);
                        scope.ShapeIds.Add(proxyShape.Id);

                        // 5) 人字梯（灰白）
                        if (target.LadderStatus == HandReachLadderStatus.Validated &&
                            !string.IsNullOrEmpty(region.RecommendedLadderDirection))
                        {
                            XYZ ladderDir = new XYZ(rec.LadderAlongX, rec.LadderAlongY, 0.0);
                            if (ladderDir.GetLength() <= 1e-9)
                                ladderDir = region.RecommendedLadderDirection == "Y"
                                    ? XYZ.BasisY
                                    : XYZ.BasisX;
                            else ladderDir = ladderDir.Normalize();
                            double ladderCenterX = sideWall || rec.LadderCenterX != 0.0 || rec.LadderCenterY != 0.0
                                ? rec.LadderCenterX
                                : rec.CenterX;
                            double ladderCenterY = sideWall || rec.LadderCenterX != 0.0 || rec.LadderCenterY != 0.0
                                ? rec.LadderCenterY
                                : rec.CenterY;
                            List<Solid> ladderSolids = MaintenanceGeometryService.BuildAFrameLadder(
                                new XYZ(ladderCenterX / MmPerFoot, ladderCenterY / MmPerFoot, 0.0),
                                ladderDir,
                                target.LadderFloorMm / MmPerFoot,
                                target.LadderTopMm / MmPerFoot);
                            DirectShape ladderShape = CreateShape(doc, genericModelId,
                                dataIdPrefix + "|AFrame|Direction" +
                                region.RecommendedLadderDirection,
                                ladderSolids.Cast<GeometryObject>().ToList());
                            // 验证细节留在台账；明细表中的梯具判断说明保持为空。
                        ApplyParameters(ladderShape, scope,
                                "人字梯", "人字梯", string.Empty,
                                result.EvidenceFingerprint, result.ResultFingerprint, stats);
                            StampReviewTrace(ladderShape, result, reviewer, reviewNote, approvedAtUtc);
                            newShapeIds.Add(ladderShape.Id);
                            scope.ShapeIds.Add(ladderShape.Id);
                        }
                    }

                    stats.CreatedElementCount = newShapeIds.Count;

                    // 视图生成（按需；默认只算数据不建视图）。
                    // DirectShape 刚写入事务时，其 BoundingBox 可能尚未由 Revit 计算；
                    // 分块更新同一分组时若直接刷新视图，裁剪框会只包住旧构件。
                    if (createViews)
                    {
                        doc.Regenerate();
                        CreateOrRefreshViews(uidoc, doc, scopes, stats);
                    }
                    else
                        HideFromAllViews(doc, newShapeIds, new List<ElementId>());

                    JarviTools.Core.TransactionSafety.Commit(tx, "OpenRevit HandReach 正式结果写入");
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }
            return stats;
        }

        internal static int Clear(UIApplication uiapp)
        {
            return ClearCurrentDetailed(uiapp).TotalDeletedCount;
        }

        internal static ClearStats ClearCurrentDetailed(UIApplication uiapp)
        {
            if (uiapp == null) throw new ArgumentNullException("uiapp");
            UIDocument uidoc = uiapp.ActiveUIDocument;
            if (uidoc == null) throw new InvalidOperationException("Revit 没有活动文档。");
            Document doc = uidoc.Document;
            if (doc.IsModifiable)
                throw new InvalidOperationException("HandReach 清理需要自主事务，不能在其他事务内调用。");

            HandReachAnalysisResult current =
                JarviTools.Mcp.Tools.MaintenanceHandReachStore.Get(doc);
            if (current == null)
                throw new InvalidOperationException(
                    "当前文档没有 HandReach 快照，无法安全判断清理范围；拒绝全局删除。");

            List<ExistingShapeInfo> existing = ReadExistingMaintenanceShapes(doc);
            var ignoredStats = new ShowStats();
            List<TargetVisualizationScope> scopes = BuildTargetScopes(current, existing, ignoredStats);
            List<ElementId> ids = existing
                .Where(x => string.Equals(
                    x.Shape.ApplicationId,
                    FormalApplicationId,
                    StringComparison.Ordinal))
                .Where(x => scopes.Any(scope => MatchesReplacementScope(scope, x)))
                .Select(x => x.Shape.Id)
                .Distinct()
                .ToList();
            List<ElementId> viewIds = FindManagedSchemeViewIds(doc, scopes);
            return ClearCore(
                uidoc,
                ids,
                viewIds,
                scopes.Select(x => BuildOverviewViewName(x.GroupKey)),
                "OpenRevit HandReach 当前快照定向清理");
        }

        /// <summary>
        /// 为后续 MCP 定向清理入口提供最小安全落点；仅删除明确的 group+device+scheme。
        /// </summary>
        internal static int Clear(
            UIApplication uiapp,
            string groupKey,
            string deviceNo,
            int schemeNo)
        {
            return ClearDetailed(uiapp, groupKey, deviceNo, schemeNo).TotalDeletedCount;
        }

        internal static ClearStats ClearDetailed(
            UIApplication uiapp,
            string groupKey,
            string deviceNo,
            int schemeNo)
        {
            return ClearDetailed(uiapp, groupKey, null, deviceNo, schemeNo);
        }

        internal static ClearStats ClearDetailed(
            UIApplication uiapp,
            string groupKey,
            string targetKey,
            string deviceNo,
            int schemeNo)
        {
            if (uiapp == null) throw new ArgumentNullException("uiapp");
            UIDocument uidoc = uiapp.ActiveUIDocument;
            if (uidoc == null) throw new InvalidOperationException("Revit 没有活动文档。");
            if (string.IsNullOrWhiteSpace(groupKey))
                throw new ArgumentException("groupKey 不能为空。", "groupKey");
            if (string.IsNullOrWhiteSpace(deviceNo))
                throw new ArgumentException("deviceNo 不能为空。", "deviceNo");
            if (schemeNo < 1)
                throw new ArgumentOutOfRangeException("schemeNo");

            Document doc = uidoc.Document;
            if (doc.IsModifiable)
                throw new InvalidOperationException("HandReach 清理需要自主事务，不能在其他事务内调用。");
            List<ExistingShapeInfo> matched = ReadExistingMaintenanceShapes(doc)
                .Where(x => string.Equals(
                    x.Shape.ApplicationId,
                    FormalApplicationId,
                    StringComparison.Ordinal))
                .Where(x => string.Equals(x.GroupKey, groupKey.Trim(), StringComparison.Ordinal))
                .Where(x => string.Equals(x.DeviceNo, NormalizeDeviceNo(deviceNo), StringComparison.Ordinal))
                .Where(x => x.SchemeNo == schemeNo)
                .ToList();
            if (!string.IsNullOrWhiteSpace(targetKey))
                matched = matched.Where(x => string.Equals(
                    x.TargetHash, HashShort(targetKey), StringComparison.Ordinal)).ToList();
            else
            {
                int targetCount = matched.Select(x => !string.IsNullOrWhiteSpace(x.TargetHash)
                        ? "HASH:" + x.TargetHash
                        : "NAME:" + x.MaintenanceTarget)
                    .Where(x => !x.EndsWith(":", StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                if (targetCount != 1)
                    throw new InvalidOperationException(
                        "旧三元清理身份不能唯一定位设备目标；请提供 targetKey 后重试。");
                targetKey = matched.Select(x => x.TargetHash).FirstOrDefault(x =>
                    !string.IsNullOrWhiteSpace(x));
            }
            List<ElementId> ids = matched
                .Select(x => x.Shape.Id)
                .Distinct()
                .ToList();
            string normalizedDeviceNo = NormalizeDeviceNo(deviceNo);
            string targetHash = string.IsNullOrWhiteSpace(targetKey)
                ? string.Empty
                : (targetKey.Length == 16 && targetKey.All(Uri.IsHexDigit)
                    ? targetKey
                    : HashShort(targetKey));
            IEnumerable<int> legacySchemeNos = Enumerable.Empty<int>();
            HandReachAnalysisResult current =
                JarviTools.Mcp.Tools.MaintenanceHandReachStore.Get(doc);
            HandReachTargetInfo currentTarget = current == null
                ? null
                : current.TargetResults
                    .Where(x => x != null && x.Target != null)
                    .Select(x => x.Target)
                    .FirstOrDefault(x =>
                        string.Equals(
                            string.IsNullOrWhiteSpace(x.GroupKey)
                                ? current.GroupKey
                                : x.GroupKey,
                            groupKey.Trim(),
                            StringComparison.Ordinal) &&
                        string.Equals(NormalizeDeviceNo(x.DeviceNo),
                            normalizedDeviceNo, StringComparison.Ordinal) &&
                        string.Equals(HashShort(x.TargetKey), targetHash,
                            StringComparison.Ordinal) &&
                        x.SchemeNo == schemeNo);
            if (currentTarget != null)
                legacySchemeNos = currentTarget.LegacySchemeNos;
            List<string> exactViewIdentities =
                MaintenanceLegacySchemeViewPolicy.ResolveManagedSchemes(
                        schemeNo, legacySchemeNos, currentTarget != null)
                    .Select(x => BuildSchemeViewIdentityFromHash(
                        groupKey.Trim(), normalizedDeviceNo, x, targetHash))
                    .ToList();
            List<ElementId> viewIds = FindManagedSchemeViewIds(
                doc,
                exactViewIdentities);
            return ClearCore(
                uidoc,
                ids,
                viewIds,
                new[] { BuildOverviewViewName(groupKey.Trim()) },
                "OpenRevit HandReach 指定方案定向清理");
        }

        private static ClearStats ClearCore(
            UIDocument uidoc,
            List<ElementId> shapeIds,
            List<ElementId> viewIds,
            IEnumerable<string> preferredFallbackViewNames,
            string transactionName)
        {
            Document doc = uidoc.Document;
            var stats = new ClearStats { DeletedShapeCount = shapeIds.Count };
            var deletableViewIds = new List<ElementId>();
            foreach (ElementId viewId in viewIds.Distinct())
            {
                View view = doc.GetElement(viewId) as View;
                string reason;
                if (MaintenanceManagedViewService.CanSafelyDelete(doc, view, out reason))
                    deletableViewIds.Add(viewId);
                else if (view != null)
                    stats.Warnings.Add(view.Name + "：" + reason);
            }
            MoveAwayFromDeletedView(uidoc, deletableViewIds, preferredFallbackViewNames);
            using (var tx = new Transaction(doc, transactionName))
            {
                tx.Start();
                try
                {
                    List<ElementId> ids = shapeIds
                        .Concat(deletableViewIds)
                        .Distinct()
                        .ToList();
                    if (ids.Count > 0)
                        doc.Delete(ids);
                    JarviTools.Core.TransactionSafety.Commit(tx, transactionName);
                    stats.DeletedViewCount = deletableViewIds.Count;
                    return stats;
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }
        }

        private static List<ElementId> FindManagedSchemeViewIds(
            Document doc,
            IEnumerable<TargetVisualizationScope> scopes)
        {
            return FindManagedSchemeViewIds(
                doc,
                scopes.SelectMany(x => BuildManagedSchemeViewIdentities(x, true)));
        }

        private static List<ElementId> FindManagedLegacySchemeViewIds(
            Document doc,
            IEnumerable<TargetVisualizationScope> scopes)
        {
            return FindManagedSchemeViewIds(
                doc,
                scopes.SelectMany(x => BuildManagedSchemeViewIdentities(x, false)));
        }

        private static IEnumerable<string> BuildManagedSchemeViewIdentities(
            TargetVisualizationScope scope,
            bool includeCurrent)
        {
            if (scope == null || scope.Target == null || scope.Target.Target == null)
                return Enumerable.Empty<string>();
            IEnumerable<int> schemes = includeCurrent
                ? MaintenanceLegacySchemeViewPolicy.ResolveManagedSchemes(
                    scope.SchemeNo, scope.LegacySchemeNos, true)
                : MaintenanceLegacySchemeViewPolicy.ResolveManagedSchemes(
                    0, scope.LegacySchemeNos, true);
            return schemes.Select(x => BuildSchemeViewIdentity(
                scope.GroupKey,
                scope.DeviceNo,
                x,
                scope.Target.Target.TargetKey));
        }

        private static List<ElementId> FindManagedSchemeViewIds(
            Document doc,
            IEnumerable<string> exactViewIdentities)
        {
            var identities = new HashSet<string>(
                exactViewIdentities.Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.Ordinal);
            if (identities.Count == 0) return new List<ElementId>();
            return MaintenanceManagedViewService.GetOwned3DViews(
                    doc, ManagedViewOwnerId, identities)
                .Select(x => x.Id)
                .Distinct()
                .ToList();
        }

        private static void MoveAwayFromDeletedView(
            UIDocument uidoc,
            IEnumerable<ElementId> viewIds,
            IEnumerable<string> preferredFallbackViewNames)
        {
            var deleted = new HashSet<long>(viewIds.Select(x => x.Value));
            if (deleted.Count == 0 || !deleted.Contains(uidoc.ActiveView.Id.Value)) return;
            var preferred = new HashSet<string>(
                (preferredFallbackViewNames ?? Enumerable.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.Ordinal);

            View3D fallback = new FilteredElementCollector(uidoc.Document)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(x => !x.IsTemplate && !deleted.Contains(x.Id.Value))
                .OrderByDescending(x => preferred.Contains(x.Name))
                .ThenByDescending(x => x.Name.EndsWith("-设备方案总览", StringComparison.Ordinal))
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            if (fallback == null)
                throw new InvalidOperationException(
                    "当前正打开待删除的 HandReach 方案视图，且没有其他三维视图可安全切换。");
            uidoc.ActiveView = fallback;
        }

        /// <summary>
        /// 只读取当前模型中的正式侧墙/HandReach 身份，为每个设备分配可共存且可重复的方案号。
        /// 不开启事务、不写 Revit；SchemeNo=0 表示尚未分配。
        /// </summary>
        internal static void ResolveSchemeAssignments(
            Document doc,
            HandReachAnalysisResult result)
        {
            if (doc == null) throw new ArgumentNullException("doc");
            if (result == null) throw new ArgumentNullException("result");
            BuildTargetScopes(
                result,
                ReadExistingMaintenanceShapes(doc),
                new ShowStats());
        }

        private static void ResolveStableDeviceNumbers(
            HandReachAnalysisResult result,
            IList<ExistingShapeInfo> existing)
        {
            List<HandReachTargetInfo> targets = result.TargetResults
                .Where(x => x != null && x.Target != null)
                .Select(x => x.Target)
                .ToList();
            var requested = new Dictionary<string, string>(StringComparer.Ordinal);
            var current = new Dictionary<string, string>(StringComparer.Ordinal);
            var orderedKeys = new List<string>();
            foreach (ExistingShapeInfo item in existing.Where(x =>
                !string.IsNullOrWhiteSpace(x.GroupKey) &&
                !string.IsNullOrWhiteSpace(x.TargetHash) &&
                !string.IsNullOrWhiteSpace(x.DeviceNo)))
            {
                string key = MaintenanceDeviceIdentityPolicy.BuildBucketedTargetKey(
                    item.GroupKey, item.TargetHash);
                if (!current.ContainsKey(key)) current[key] = item.DeviceNo;
            }
            foreach (HandReachTargetInfo target in targets
                .OrderBy(x => x.GroupKey, StringComparer.Ordinal)
                .ThenBy(x => x.TargetKey, StringComparer.Ordinal))
            {
                string group = string.IsNullOrWhiteSpace(target.GroupKey)
                    ? Safe(result.GroupKey)
                    : target.GroupKey.Trim();
                string stableKey = MaintenanceDeviceIdentityPolicy.BuildBucketedTargetKey(
                    group, HashShort(target.TargetKey));
                orderedKeys.Add(stableKey);
                requested[stableKey] = target.DeviceNo;
                ExistingShapeInfo match = existing
                    .Where(x => string.Equals(x.GroupKey, group, StringComparison.Ordinal))
                    .Where(x => string.Equals(x.TargetHash, HashShort(target.TargetKey),
                        StringComparison.Ordinal))
                    .Where(x => !string.IsNullOrWhiteSpace(x.DeviceNo))
                    .OrderBy(x => x.DeviceNo, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (match == null)
                {
                    string requestedDevice = NormalizeDeviceNo(target.DeviceNo);
                    List<ExistingShapeInfo> legacy = existing
                        .Where(x => string.Equals(x.GroupKey, group,
                            StringComparison.Ordinal))
                        .Where(x => string.IsNullOrWhiteSpace(x.TargetHash))
                        .Where(x => string.Equals(x.MaintenanceTarget,
                            NormalizeTarget(target.GetDisplayName()),
                            StringComparison.Ordinal))
                        .Where(x => string.Equals(x.DeviceNo, requestedDevice,
                            StringComparison.Ordinal))
                        .ToList();
                    int logicalPairCount = legacy
                        .Select(x => x.GroupKey + "|" + x.MaintenanceTarget + "|" +
                                     x.DeviceNo)
                        .Distinct(StringComparer.Ordinal)
                        .Count();
                    if (logicalPairCount == 1)
                        match = legacy.OrderBy(x => x.Shape.Id.Value).FirstOrDefault();
                }
                if (match != null) current[stableKey] = match.DeviceNo;
            }
            Dictionary<string, string> resolved =
                MaintenanceDeviceIdentityPolicy.ResolveDeviceNumbers(
                    orderedKeys, current, requested);
            foreach (HandReachTargetInfo target in targets)
            {
                string group = string.IsNullOrWhiteSpace(target.GroupKey)
                    ? Safe(result.GroupKey)
                    : target.GroupKey.Trim();
                target.DeviceNo = resolved[
                    MaintenanceDeviceIdentityPolicy.BuildBucketedTargetKey(
                        group, HashShort(target.TargetKey))];
            }
        }

        private static List<TargetVisualizationScope> BuildTargetScopes(
            HandReachAnalysisResult result,
            List<ExistingShapeInfo> existing,
            ShowStats stats)
        {
            var scopes = new List<TargetVisualizationScope>();
            ResolveStableDeviceNumbers(result, existing);
            foreach (HandReachTargetResult target in result.TargetResults.Where(x => x != null && x.Target != null))
            {
                if (target.Regions == null || target.Regions.Count == 0)
                    continue;
                HandReachTargetInfo info = target.Target;
                string groupKey = string.IsNullOrWhiteSpace(info.GroupKey)
                    ? Safe(result.GroupKey)
                    : info.GroupKey.Trim();
                if (string.IsNullOrWhiteSpace(groupKey))
                    throw new InvalidOperationException("HandReach 目标缺少天花分组身份，拒绝写入无法定向清理的图元。");
                if (string.IsNullOrWhiteSpace(info.GroupKey) && groupKey.Contains("+"))
                    throw new InvalidOperationException(
                        "HandReach 多分组快照缺少逐设备 GroupKey；请重新分析后再生成视图。");

                double ceilingTopMm = info.CeilingTopMm > 0.0
                    ? info.CeilingTopMm
                    : result.CeilingTopMm;
                if (ceilingTopMm <= 0.0)
                    throw new InvalidOperationException(
                        "HandReach 目标“" + info.GetDisplayName() + "”缺少本组天花顶标高。");

                var scope = new TargetVisualizationScope
                {
                    Target = target,
                    Region = target.Regions.FirstOrDefault(),
                    GroupKey = groupKey,
                    DeviceNo = NormalizeDeviceNo(info.DeviceNo),
                    MaintenanceTarget = NormalizeTarget(info.GetDisplayName()),
                    CeilingTopMm = ceilingTopMm
                };
                foreach (int legacySchemeNo in info.LegacySchemeNos
                    .Where(x => x > 0))
                    scope.LegacySchemeNos.Add(legacySchemeNo);
                scope.AllowLegacyPairMatch = existing.Any(x =>
                    string.IsNullOrWhiteSpace(x.TargetHash) &&
                    string.Equals(x.GroupKey, scope.GroupKey, StringComparison.Ordinal) &&
                    string.Equals(x.DeviceNo, scope.DeviceNo, StringComparison.Ordinal) &&
                    string.Equals(x.MaintenanceTarget, scope.MaintenanceTarget,
                        StringComparison.Ordinal));
                if (existing.Any(x =>
                    string.IsNullOrWhiteSpace(x.TargetHash) &&
                    string.Equals(x.GroupKey, scope.GroupKey, StringComparison.Ordinal) &&
                    (string.Equals(x.DeviceNo, scope.DeviceNo, StringComparison.Ordinal) ||
                     string.Equals(x.MaintenanceTarget, scope.MaintenanceTarget,
                         StringComparison.Ordinal)) &&
                    !(string.Equals(x.DeviceNo, scope.DeviceNo, StringComparison.Ordinal) &&
                      string.Equals(x.MaintenanceTarget, scope.MaintenanceTarget,
                          StringComparison.Ordinal))))
                    stats.Warnings.Add(
                        "设备" + scope.DeviceNo + " 存在缺少 TargetKey 的旧图元，但 group+维修对象+设备编号不能同时精确匹配；已保留且不继承、不替换。"
                    );
                if (string.IsNullOrWhiteSpace(scope.DeviceNo))
                    throw new InvalidOperationException(
                        "HandReach 目标“" + info.GetDisplayName() + "”缺少设备编号。");

                List<ExistingShapeInfo> sameTarget = existing
                    .Where(x => SameTarget(scope, x))
                    .ToList();
                HashSet<int> reservedByOtherModes = new HashSet<int>(sameTarget
                    .Where(x => !string.Equals(
                        x.Shape.ApplicationId,
                        FormalApplicationId,
                        StringComparison.Ordinal))
                    .Where(x => x.SchemeNo > 0)
                    .Select(x => x.SchemeNo));
                List<int> handReachSchemes = sameTarget
                    .Where(x => string.Equals(
                        x.Shape.ApplicationId,
                        FormalApplicationId,
                        StringComparison.Ordinal))
                    .Where(x => x.SchemeNo > 0)
                    .Select(x => x.SchemeNo)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();

                int requestedScheme = info.SchemeNo;
                if (requestedScheme > 0 && !reservedByOtherModes.Contains(requestedScheme))
                {
                    scope.SchemeNo = requestedScheme;
                }
                else
                {
                    int reusable = handReachSchemes.FirstOrDefault(x =>
                        !reservedByOtherModes.Contains(x));
                    if (reusable > 0)
                    {
                        scope.SchemeNo = reusable;
                    }
                    else
                    {
                        int candidate = 1;
                        while (reservedByOtherModes.Contains(candidate) || handReachSchemes.Contains(candidate))
                            candidate++;
                        scope.SchemeNo = candidate;
                    }
                }
                foreach (int oldScheme in handReachSchemes)
                {
                    if (oldScheme != scope.SchemeNo && reservedByOtherModes.Contains(oldScheme))
                        scope.LegacySchemeNos.Add(oldScheme);
                }
                scope.ReplaceUnnumbered = sameTarget.Any(x =>
                    string.Equals(x.Shape.ApplicationId, FormalApplicationId, StringComparison.Ordinal) &&
                    x.SchemeNo <= 0);
                info.SchemeNo = scope.SchemeNo;
                scope.LegacySchemeNos.Remove(scope.SchemeNo);
                info.LegacySchemeNos.Clear();
                info.LegacySchemeNos.AddRange(scope.LegacySchemeNos.OrderBy(x => x));
                scope.EntryGroup = BuildEntryGroup(
                    scope.DeviceNo,
                    scope.SchemeNo,
                    scope.Region.OpeningPlane,
                    scope.Target.CeilingDirectReachApplied);

                if (requestedScheme > 0 && requestedScheme != scope.SchemeNo)
                {
                    stats.Warnings.Add(
                        "设备" + scope.DeviceNo + " 请求的" + FormatScheme(requestedScheme) +
                        "已被侧墙方案占用，HandReach 已改用" + FormatScheme(scope.SchemeNo) + "。");
                }
                else if (scope.LegacySchemeNos.Count > 0)
                {
                    stats.Warnings.Add(
                        "设备" + scope.DeviceNo + " 的旧 HandReach 方案号与侧墙方案冲突，已迁移为" +
                        FormatScheme(scope.SchemeNo) + "。");
                }
                scopes.Add(scope);
            }
            return scopes;
        }

        private static List<ExistingShapeInfo> ReadExistingMaintenanceShapes(Document doc)
        {
            var output = new List<ExistingShapeInfo>();
            foreach (DirectShape shape in new FilteredElementCollector(doc)
                .OfClass(typeof(DirectShape))
                .Cast<DirectShape>()
                .Where(x => string.Equals(
                                x.ApplicationId,
                                FormalApplicationId,
                                StringComparison.Ordinal) ||
                            string.Equals(
                                x.ApplicationId,
                                MaintenanceVisualizationService.OwnerApplicationId,
                                StringComparison.Ordinal) ||
                            string.Equals(
                                x.ApplicationId,
                                MaintenanceWallAlternativeVisualizationService.OwnerApplicationId,
                                StringComparison.Ordinal)))
            {
                string dataId = Safe(shape.ApplicationDataId);
                string entryGroup = Safe(ReadText(shape, MaintenanceParameterService.EntryGroupGuid));
                string groupKey = Safe(
                    ReadText(shape, MaintenanceParameterService.CeilingGroupGuid)).Trim();
                if (string.IsNullOrWhiteSpace(groupKey))
                    groupKey = ReadDataIdGroup(dataId);
                string deviceNo;
                int schemeNo;
                ReadDeviceAndScheme(entryGroup, dataId, out deviceNo, out schemeNo);
                output.Add(new ExistingShapeInfo
                {
                    Shape = shape,
                    GroupKey = groupKey,
                    DeviceNo = deviceNo,
                    SchemeNo = schemeNo,
                    MaintenanceTarget = NormalizeTarget(
                        ReadText(shape, MaintenanceParameterService.MaintenanceTargetGuid)),
                    TargetHash = ReadTargetHash(dataId),
                    ComponentRole = Safe(
                        ReadText(shape, MaintenanceParameterService.ElementRoleGuid))
                });
            }
            return output;
        }

        private static void CaptureSavedUserStates(
            IEnumerable<TargetVisualizationScope> scopes,
            IEnumerable<ExistingShapeInfo> existing)
        {
            foreach (TargetVisualizationScope scope in scopes)
            {
                foreach (ExistingShapeInfo item in existing.Where(x =>
                    string.Equals(x.Shape.ApplicationId, FormalApplicationId, StringComparison.Ordinal) &&
                    MatchesReplacementScope(scope, x)))
                {
                    string role = Safe(item.ComponentRole);
                    if (string.IsNullOrWhiteSpace(role)) continue;
                    string note = ReadText(
                        item.Shape,
                        MaintenanceParameterService.ProfessionalNoteGuid);
                    if (!string.IsNullOrWhiteSpace(note))
                        scope.SavedProfessionalNotes[role] = note;

                    HandReachShapeTrace handReachTrace = ReadHandReachShapeTrace(item.Shape);
                    string currentOpeningIdentity = BuildOpeningIdentity(scope.Region);
                    bool legacyCeilingIdentity = string.IsNullOrWhiteSpace(
                            handReachTrace.OpeningIdentity) &&
                        scope.Region != null &&
                        scope.Region.OpeningPlane ==
                            HandReachOpeningPlaneKind.CeilingHorizontal;
                    if (!legacyCeilingIdentity && !string.Equals(
                        handReachTrace.OpeningIdentity,
                        currentOpeningIdentity,
                        StringComparison.Ordinal))
                        continue;

                    SavedUserState saved;
                    if (!scope.SavedStates.TryGetValue(role, out saved))
                    {
                        saved = new SavedUserState();
                        scope.SavedStates[role] = saved;
                    }
                    string conclusion = ReadText(
                        item.Shape,
                        MaintenanceParameterService.MaintenanceConclusionGuid);
                    saved.Conclusion = conclusion;
                    saved.ProfessionalNote = note;
                    saved.DecisionNote = ReadText(
                        item.Shape,
                        MaintenanceParameterService.DecisionNoteGuid);
                    saved.EvidenceFingerprint =
                        MaintenanceVisualizationService.ReadReviewTrace(item.Shape)
                            .EvidenceFingerprint;
                    saved.ResultFingerprint = handReachTrace.ResultFingerprint;
                    saved.OpeningIdentity = handReachTrace.OpeningIdentity;
                }
            }
        }

        private static bool MatchesReplacementScope(
            TargetVisualizationScope scope,
            ExistingShapeInfo existing)
        {
            if (!SameTarget(scope, existing)) return false;
            return existing.SchemeNo == scope.SchemeNo ||
                   scope.LegacySchemeNos.Contains(existing.SchemeNo) ||
                   (scope.ReplaceUnnumbered && existing.SchemeNo <= 0);
        }

        private static bool SameTarget(
            TargetVisualizationScope scope,
            ExistingShapeInfo existing)
        {
            string targetHash = HashShort(scope.Target == null || scope.Target.Target == null
                ? string.Empty
                : scope.Target.Target.TargetKey);
            return MaintenanceTargetIdentityPolicy.IsSameTarget(
                scope.GroupKey,
                targetHash,
                scope.MaintenanceTarget,
                scope.DeviceNo,
                existing.GroupKey,
                existing.TargetHash,
                existing.MaintenanceTarget,
                existing.DeviceNo,
                scope.AllowLegacyPairMatch);
        }

        private static string BuildDataIdPrefix(TargetVisualizationScope scope)
        {
            HandReachRegion region = scope == null ? null : scope.Region;
            return NormalizeDataIdPart(scope.GroupKey) +
                   "|Device" + NormalizeDataIdPart(scope.DeviceNo) +
                   "|Scheme" + scope.SchemeNo.ToString("D2") +
                   "|Target" + HashShort(scope.Target.Target.TargetKey) +
                   "|Opening" + (region == null
                       ? HandReachOpeningPlaneKind.CeilingHorizontal.ToString()
                       : region.OpeningPlane.ToString()) +
                   "|Surface" + HashShort(region == null ? string.Empty : region.SurfaceKey);
        }

        private static string BuildEntryGroup(
            string deviceNo,
            int schemeNo,
            HandReachOpeningPlaneKind openingPlane,
            bool ceilingDirectReach)
        {
            return "设备" + deviceNo + "-" + FormatScheme(schemeNo) + "-" +
                   (openingPlane == HandReachOpeningPlaneKind.SideWallVertical
                       ? "侧墙伸手检修"
                       : (ceilingDirectReach
                           ? "天花直接伸手检修"
                           : "天花钻入检修"));
        }

        private static string BuildSchemeViewName(
            string groupKey,
            string deviceNo,
            int schemeNo,
            HandReachOpeningPlaneKind openingPlane,
            bool ceilingDirectReach)
        {
            return "天花" + Safe(groupKey).Trim() +
                   "-设备" + NormalizeDeviceNo(deviceNo) +
                   "-" + FormatScheme(schemeNo) +
                   (openingPlane == HandReachOpeningPlaneKind.SideWallVertical
                       ? "-伸手检修"
                       : (ceilingDirectReach
                           ? "-伸手检修"
                           : "-450钻入检修"));
        }

        private static string BuildSchemeViewIdentity(
            string groupKey,
            string deviceNo,
            int schemeNo,
            string targetKey)
        {
            return BuildSchemeViewIdentityFromHash(
                groupKey, deviceNo, schemeNo, HashShort(targetKey));
        }

        private static string BuildSchemeViewIdentityFromHash(
            string groupKey,
            string deviceNo,
            int schemeNo,
            string targetHash)
        {
            return "handreach|" + NormalizeDataIdPart(groupKey) +
                   "|Device" + NormalizeDeviceNo(deviceNo) +
                   "|Scheme" + schemeNo.ToString("D2") +
                   "|Target" + Safe(targetHash);
        }

        private static string BuildOverviewViewName(string groupKey)
        {
            return MaintenanceManagedViewPolicy.BuildEquipmentOverviewViewName(
                groupKey);
        }

        private static string BuildOverviewViewIdentity(string groupKey)
        {
            return MaintenanceManagedViewPolicy.BuildEquipmentOverviewViewIdentity(
                groupKey);
        }

        private static string FormatScheme(int schemeNo)
        {
            return "方案" + schemeNo.ToString("D2");
        }

        private static int ReadViewSchemeNo(string viewName, string prefix)
        {
            string safeName = Safe(viewName);
            string safePrefix = Safe(prefix);
            return safeName.StartsWith(safePrefix, StringComparison.Ordinal)
                ? ReadLeadingInt(safeName.Substring(safePrefix.Length))
                : 0;
        }

        private static string NormalizeDataIdPart(string value)
        {
            string safe = Safe(value).Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
            return string.IsNullOrWhiteSpace(safe) ? "G" : safe;
        }

        private static string NormalizeTarget(string value)
        {
            return Safe(value)
                .Replace('｜', '|')
                .Replace(" | ", "|")
                .Replace("| ", "|")
                .Replace(" |", "|")
                .Trim();
        }

        private static string NormalizeDeviceNo(string value)
        {
            string safe = Safe(value).Trim();
            int number;
            return int.TryParse(safe, out number) && number >= 0
                ? number.ToString("D2")
                : safe;
        }

        private static string ReadDataIdGroup(string dataId)
        {
            int separator = Safe(dataId).IndexOf('|');
            return separator <= 0 ? string.Empty : dataId.Substring(0, separator).Trim();
        }

        private static string ReadTargetHash(string dataId)
        {
            foreach (string part in Safe(dataId).Split('|'))
                if (part.StartsWith("Target", StringComparison.Ordinal))
                    return part.Substring("Target".Length);
            return string.Empty;
        }

        private static string HashShort(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes), 0, 8)
                    .Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void ReadDeviceAndScheme(
            string entryGroup,
            string dataId,
            out string deviceNo,
            out int schemeNo)
        {
            deviceNo = string.Empty;
            schemeNo = 0;
            string entry = Safe(entryGroup);
            int deviceStart = entry.IndexOf("设备", StringComparison.Ordinal);
            int schemeStart = entry.IndexOf("-方案", StringComparison.Ordinal);
            if (deviceStart >= 0 && schemeStart > deviceStart + 2)
            {
                deviceNo = NormalizeDeviceNo(entry.Substring(deviceStart + 2, schemeStart - deviceStart - 2));
                schemeNo = ReadLeadingInt(entry.Substring(schemeStart + 3));
            }
            if (!string.IsNullOrWhiteSpace(deviceNo) && schemeNo > 0) return;

            foreach (string part in Safe(dataId).Split('|'))
            {
                if (part.StartsWith("Device", StringComparison.Ordinal))
                    deviceNo = NormalizeDeviceNo(part.Substring("Device".Length));
                else if (part.StartsWith("Scheme", StringComparison.Ordinal))
                    schemeNo = ReadLeadingInt(part.Substring("Scheme".Length));
            }
        }

        private static int ReadLeadingInt(string value)
        {
            string digits = new string(Safe(value).TakeWhile(char.IsDigit).ToArray());
            int number;
            return int.TryParse(digits, out number) ? number : 0;
        }

        private static string ReadText(Element element, Guid guid)
        {
            Parameter parameter = element == null ? null : element.get_Parameter(guid);
            return parameter == null ? null : parameter.AsString();
        }

        private static string Safe(string value)
        {
            return value ?? string.Empty;
        }

        private static void StampReviewTrace(
            DirectShape shape,
            HandReachAnalysisResult result,
            string reviewer,
            string reviewNote,
            string approvedAtUtc)
        {
            MaintenanceVisualizationService.WriteReviewTrace(
                shape,
                result.EvidenceFingerprint ?? string.Empty,
                reviewer ?? string.Empty,
                reviewNote ?? string.Empty,
                approvedAtUtc ?? string.Empty);
            WriteHandReachShapeTrace(
                shape,
                result.ResultFingerprint ?? string.Empty,
                ExtractOpeningIdentity(shape.ApplicationDataId));
        }

        private static string BuildOpeningIdentity(HandReachRegion region)
        {
            if (region == null) return string.Empty;
            return "Opening" + region.OpeningPlane + "|Surface" +
                   HashShort(region.SurfaceKey);
        }

        private static string ExtractOpeningIdentity(string dataId)
        {
            if (string.IsNullOrWhiteSpace(dataId)) return string.Empty;
            string[] parts = dataId.Split('|');
            for (int index = 0; index + 1 < parts.Length; index++)
            {
                if (parts[index].StartsWith("Opening", StringComparison.Ordinal) &&
                    parts[index + 1].StartsWith("Surface", StringComparison.Ordinal))
                    return parts[index] + "|" + parts[index + 1];
            }
            return string.Empty;
        }

        private static void WriteHandReachShapeTrace(
            Element element,
            string resultFingerprint,
            string openingIdentity)
        {
            if (element == null) return;
            Schema schema = GetOrCreateHandReachTraceSchema();
            var entity = new Entity(schema);
            entity.Set(schema.GetField(ResultFingerprintField), Safe(resultFingerprint));
            entity.Set(schema.GetField(OpeningIdentityField), Safe(openingIdentity));
            element.SetEntity(entity);
        }

        private static HandReachShapeTrace ReadHandReachShapeTrace(Element element)
        {
            var output = new HandReachShapeTrace();
            if (element == null) return output;
            Schema schema = Schema.Lookup(HandReachTraceSchemaGuid);
            if (schema == null) return output;
            Entity entity = element.GetEntity(schema);
            if (entity == null || !entity.IsValid()) return output;
            output.ResultFingerprint =
                entity.Get<string>(schema.GetField(ResultFingerprintField)) ?? string.Empty;
            output.OpeningIdentity =
                entity.Get<string>(schema.GetField(OpeningIdentityField)) ?? string.Empty;
            return output;
        }

        private static Schema GetOrCreateHandReachTraceSchema()
        {
            Schema existing = Schema.Lookup(HandReachTraceSchemaGuid);
            if (existing != null) return existing;
            var builder = new SchemaBuilder(HandReachTraceSchemaGuid);
            builder.SetSchemaName("JarviToolsMaintenanceHandReachTraceV1");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(ResultFingerprintField, typeof(string));
            builder.AddSimpleField(OpeningIdentityField, typeof(string));
            return builder.Finish();
        }

        private static void CreateOrRefreshViews(
            UIDocument uidoc,
            Document doc,
            List<TargetVisualizationScope> scopes,
            ShowStats stats)
        {
            List<ExistingShapeInfo> currentShapes = ReadExistingMaintenanceShapes(doc);
            List<ExistingShapeInfo> allOwned = currentShapes
                .Where(x => string.Equals(
                    x.Shape.ApplicationId,
                    FormalApplicationId,
                    StringComparison.Ordinal))
                .ToList();
            List<ElementId> allFormalMaintenanceIds = currentShapes
                .Where(x => MaintenanceManagedViewPolicy
                    .IsFormalMaintenanceApplicationId(x.Shape.ApplicationId))
                .Select(x => x.Shape.Id)
                .Distinct()
                .ToList();

            foreach (IGrouping<string, TargetVisualizationScope> groupScopes in scopes
                .Where(x => x.ShapeIds.Count > 0)
                .GroupBy(x => x.GroupKey, StringComparer.Ordinal))
            {
                CreateOrRefreshGroupViews(
                    uidoc,
                    doc,
                    groupScopes.Key,
                    groupScopes.ToList(),
                    allOwned
                        .Where(x => string.Equals(
                            x.GroupKey,
                            groupScopes.Key,
                            StringComparison.Ordinal))
                        .Select(x => x.Shape.Id)
                        .ToList(),
                    allFormalMaintenanceIds,
                    currentShapes,
                    stats);
            }
        }

        private static void CreateOrRefreshGroupViews(
            UIDocument uidoc,
            Document doc,
            string group,
            List<TargetVisualizationScope> scopes,
            List<ElementId> groupOwnedIds,
            List<ElementId> allFormalMaintenanceIds,
            List<ExistingShapeInfo> currentShapes,
            ShowStats stats)
        {
            List<ElementId> shapeIds = scopes
                .SelectMany(x => x.ShapeIds)
                .Distinct()
                .ToList();
            View3D active3D = uidoc.ActiveView as View3D;
            List<View3D> all3DViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(x => !x.IsTemplate)
                .ToList();

            string overviewName = BuildOverviewViewName(group);
            View3D sourceView = active3D ?? all3DViews.FirstOrDefault();
            if (sourceView == null)
                throw new InvalidOperationException("文档中没有可作为基准的普通三维视图。");
            bool overviewCreated;
            View3D overview = MaintenanceManagedViewService.GetOrCreate3D(
                doc,
                sourceView,
                overviewName,
                ManagedViewOwnerId,
                BuildOverviewViewIdentity(group),
                MaintenanceManagedViewPurpose.FormalReachability,
                out overviewCreated);
            if (overviewCreated)
            {
                stats.CreatedViewCount++;
                all3DViews.Add(overview);
            }
            if (overview.IsLocked) overview.Unlock();

            string aiViewName = MaintenanceManagedViewPolicy.BuildAiAnalysisViewName(group);
            bool aiViewCreated;
            View3D aiView = MaintenanceManagedViewService.GetOrCreate3D(
                doc,
                overview,
                aiViewName,
                AiManagedViewOwnerId,
                "maintenance-ai|" + NormalizeDataIdPart(group),
                MaintenanceManagedViewPurpose.AiInternalAnalysis,
                out aiViewCreated);
            if (aiViewCreated)
            {
                stats.CreatedViewCount++;
                all3DViews.Add(aiView);
            }
            if (aiView.IsLocked) aiView.Unlock();

            var schemeViews = new Dictionary<TargetVisualizationScope, View3D>();
            foreach (TargetVisualizationScope scope in scopes)
            {
                string viewName = BuildSchemeViewName(
                    group,
                    scope.DeviceNo,
                    scope.SchemeNo,
                    scope.Region.OpeningPlane,
                    scope.Target.CeilingDirectReachApplied);
                bool schemeCreated;
                View3D scheme = MaintenanceManagedViewService.GetOrCreate3D(
                    doc,
                    overview,
                    viewName,
                    ManagedViewOwnerId,
                    BuildSchemeViewIdentity(group, scope.DeviceNo, scope.SchemeNo,
                        scope.Target.Target.TargetKey),
                    MaintenanceManagedViewPurpose.FormalReachability,
                    out schemeCreated);
                if (schemeCreated)
                {
                    stats.CreatedViewCount++;
                    all3DViews.Add(scheme);
                }
                if (scheme.IsLocked) scheme.Unlock();
                schemeViews[scope] = scheme;
                stats.ViewNames.Add(scheme.Name);
                stats.ViewIds.Add(scheme.Id.Value);
            }

            var viewsToStyle = new List<View3D> { overview, aiView };
            viewsToStyle.AddRange(schemeViews.Values);
            foreach (View3D view in viewsToStyle)
                ApplyPresentationTemplate(doc, view);

            foreach (View3D view in viewsToStyle)
            {
                try
                {
                    MaintenanceParameterService.EnsureViewPresentation(doc, view);
                }
                catch (Exception ex)
                {
                    stats.Warnings.Add(view.Name + " 视图演示资源失败：" + ex.Message);
                }
            }

            List<ElementId> groupContextIds = currentShapes
                .Where(x => string.Equals(x.GroupKey, group, StringComparison.Ordinal))
                .Select(x => x.Shape.Id)
                .Distinct()
                .ToList();
            List<ElementId> otherContextIds = currentShapes
                .Select(x => x.Shape.Id)
                .Where(id => !groupContextIds.Contains(id))
                .Distinct()
                .ToList();
            List<ElementId> overviewIds = groupContextIds
                .Distinct()
                .ToList();
            BoundingBoxXYZ section = BuildSectionBox(doc, overviewIds);
            if (section != null)
            {
                foreach (View3D view in viewsToStyle)
                {
                    view.IsSectionBoxActive = true;
                    view.SetSectionBox(section);
                }
            }

            FillPatternElement solidFill = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(x => x.GetFillPattern().IsSolidFill);
            if (solidFill != null)
            {
                var blue = Style(new Color(30, 180, 255), solidFill.Id, 20);
                var gray = Style(new Color(195, 205, 215), solidFill.Id, 10);
                var orange = Style(new Color(255, 155, 25), solidFill.Id, 35);
                var personnelOrange = Style(new Color(255, 185, 20), solidFill.Id, 65);
                var magenta = Style(new Color(235, 70, 220), solidFill.Id, 20);
                foreach (View3D view in viewsToStyle)
                {
                    IEnumerable<ElementId> idsToStyle =
                        view.Id == overview.Id || view.Id == aiView.Id
                        ? groupOwnedIds
                        : schemeViews.First(x => x.Value.Id == view.Id).Key.ShapeIds;
                    foreach (ElementId id in idsToStyle)
                    {
                        DirectShape shape = doc.GetElement(id) as DirectShape;
                        if (shape == null) continue;
                        string dataId = Safe(shape.ApplicationDataId);
                        if (dataId.Contains("|Hatch")) view.SetElementOverrides(id, blue);
                        else if (dataId.Contains("|AFrame")) view.SetElementOverrides(id, gray);
                        else if (dataId.Contains("|PersonnelEntry"))
                            view.SetElementOverrides(id, personnelOrange);
                        else if (dataId.Contains("|HandReach") ||
                                 dataId.Contains("|FinalHandReach"))
                            view.SetElementOverrides(id, orange);
                        else if (dataId.Contains("|ServiceFaceProxy")) view.SetElementOverrides(id, magenta);
                    }
                }
            }

            TryUnhide(overview, groupContextIds);
            TryHide(overview, otherContextIds);

            List<ElementId> groupInternalIds = currentShapes
                .Where(x => string.Equals(x.GroupKey, group, StringComparison.Ordinal))
                .Select(x => x.Shape.Id)
                .Distinct()
                .ToList();
            List<ElementId> allMaintenanceIds = currentShapes
                .Select(x => x.Shape.Id)
                .Distinct()
                .ToList();
            TryUnhide(aiView, groupInternalIds);
            TryHide(aiView, allMaintenanceIds
                .Where(id => !groupInternalIds.Contains(id))
                .ToList());

            List<View3D> floorOverviewViews = FindFloorOverviewViews(doc, group, stats);
            string floorKey = MaintenanceManagedViewPolicy.ResolveFloorKey(group);
            List<ExistingShapeInfo> formalShapes = currentShapes
                .Where(x => MaintenanceManagedViewPolicy
                    .IsFormalMaintenanceApplicationId(x.Shape.ApplicationId))
                .ToList();
            List<ElementId> floorFormalIds = formalShapes
                .Where(x => MaintenanceManagedViewPolicy.GroupBelongsToFloor(
                    x.GroupKey, floorKey))
                .Select(x => x.Shape.Id)
                .Distinct()
                .ToList();
            List<ElementId> otherFloorFormalIds = formalShapes
                .Select(x => x.Shape.Id)
                .Where(id => !floorFormalIds.Contains(id))
                .Distinct()
                .ToList();
            foreach (View3D floorOverview in floorOverviewViews)
            {
                TryUnhide(floorOverview, floorFormalIds);
                TryHide(floorOverview, otherFloorFormalIds);
            }
            if (solidFill != null)
            {
                var floorBlue = Style(new Color(30, 180, 255), solidFill.Id, 20);
                var floorGray = Style(new Color(195, 205, 215), solidFill.Id, 10);
                var floorOrange = Style(new Color(255, 155, 25), solidFill.Id, 35);
                var floorPersonnelOrange = Style(new Color(255, 185, 20), solidFill.Id, 65);
                var floorMagenta = Style(new Color(235, 70, 220), solidFill.Id, 20);
                foreach (View3D floorOverview in floorOverviewViews)
                {
                    foreach (ElementId id in floorFormalIds)
                    {
                        DirectShape shape = doc.GetElement(id) as DirectShape;
                        if (shape == null) continue;
                        string dataId = Safe(shape.ApplicationDataId);
                        if (dataId.Contains("|Hatch")) floorOverview.SetElementOverrides(id, floorBlue);
                        else if (dataId.Contains("|AFrame")) floorOverview.SetElementOverrides(id, floorGray);
                        else if (dataId.Contains("|PersonnelEntry"))
                            floorOverview.SetElementOverrides(id, floorPersonnelOrange);
                        else if (dataId.Contains("|HandReach") ||
                                 dataId.Contains("|FinalHandReach"))
                            floorOverview.SetElementOverrides(id, floorOrange);
                        else if (dataId.Contains("|ServiceFaceProxy"))
                            floorOverview.SetElementOverrides(id, floorMagenta);
                    }
                }
            }
            foreach (KeyValuePair<TargetVisualizationScope, View3D> pair in schemeViews)
            {
                List<ElementId> own = pair.Key.ShapeIds;
                List<ElementId> others = allFormalMaintenanceIds
                    .Where(id => !own.Contains(id))
                    .ToList();
                TryUnhide(pair.Value, own);
                TryHide(pair.Value, others.Distinct().ToList());
            }

            var visibleViews = new List<ElementId>(viewsToStyle.Select(x => x.Id));
            visibleViews.AddRange(floorOverviewViews.Select(x => x.Id));
            HideFromAllViews(doc, shapeIds, visibleViews);
            stats.ViewNames.Add(aiView.Name);
            stats.ViewIds.Add(aiView.Id.Value);
            foreach (View3D floorOverview in floorOverviewViews)
            {
                stats.ViewNames.Add(floorOverview.Name);
                stats.ViewIds.Add(floorOverview.Id.Value);
            }
            stats.ViewNames.Add(overview.Name);
            stats.ViewIds.Add(overview.Id.Value);
        }

        private static List<View3D> FindFloorOverviewViews(
            Document doc,
            string group,
            ShowStats stats)
        {
            string expectedName =
                MaintenanceManagedViewPolicy.BuildFloorOverviewViewName(group);
            if (string.IsNullOrWhiteSpace(expectedName))
            {
                stats.Warnings.Add("无法从天花分组“" + Safe(group) +
                                   "”识别楼层，未同步整层可达视图。");
                return new List<View3D>();
            }

            List<View3D> matches = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(x => !x.IsTemplate &&
                            string.Equals(x.Name, expectedName,
                                StringComparison.Ordinal))
                .OrderBy(x => x.Id.Value)
                .ToList();
            if (matches.Count == 0)
                stats.Warnings.Add("未找到“" + expectedName +
                                   "”，本次未同步整层可达视图。");
            return matches;
        }

        private const string PresentationTemplateName = "codex空间可达性分析";

        private static void ApplyPresentationTemplate(Document doc, View3D view)
        {
            if (doc == null || view == null) return;
            View template = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(x => x.IsTemplate &&
                    string.Equals(x.Name, PresentationTemplateName, StringComparison.Ordinal));
            if (template == null) return;
            // 一次性套用（相当于右键"应用视图样板"），不建立持久链接：
            // 避免之后样板被编辑时重新套用、清掉插件挂载的 CODEX 过滤器与统一剖面框。
            view.ApplyViewTemplateParameters(template);
        }

        private static void HideFromAllViews(
            Document doc,
            List<ElementId> shapeIds,
            List<ElementId> visibleViewIds)
        {
            if (shapeIds == null || shapeIds.Count == 0) return;
            foreach (View view in new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Where(x => !x.IsTemplate && !visibleViewIds.Contains(x.Id)))
            {
                TryHide(view, shapeIds);
            }
        }

        private static void TryHide(View view, IEnumerable<ElementId> ids)
        {
            if (view == null || ids == null) return;
            List<ElementId> visible = ids
                .Where(x => x != null && x != ElementId.InvalidElementId)
                .Distinct()
                .Where(x =>
                {
                    try
                    {
                        Element element = view.Document.GetElement(x);
                        return element != null &&
                               element.CanBeHidden(view) &&
                               !element.IsHidden(view);
                    }
                    catch { return false; }
                })
                .ToList();
            if (visible.Count == 0) return;
            try { view.HideElements(visible); }
            catch { }
        }

        private static void TryUnhide(View view, IEnumerable<ElementId> ids)
        {
            if (view == null || ids == null) return;
            List<ElementId> hidden = ids
                .Where(x => x != null && x != ElementId.InvalidElementId)
                .Distinct()
                .Where(x =>
                {
                    try
                    {
                        Element element = view.Document.GetElement(x);
                        return element != null && element.IsHidden(view);
                    }
                    catch { return false; }
                })
                .ToList();
            if (hidden.Count == 0) return;
            try { view.UnhideElements(hidden); }
            catch { }
        }

        private static BoundingBoxXYZ BuildSectionBox(Document doc, List<ElementId> shapeIds)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (ElementId id in shapeIds)
            {
                Element element = doc.GetElement(id);
                if (element == null) continue;
                BoundingBoxXYZ box = element.get_BoundingBox(null);
                if (box == null) continue;
                minX = Math.Min(minX, box.Min.X);
                minY = Math.Min(minY, box.Min.Y);
                minZ = Math.Min(minZ, box.Min.Z);
                maxX = Math.Max(maxX, box.Max.X);
                maxY = Math.Max(maxY, box.Max.Y);
                maxZ = Math.Max(maxZ, box.Max.Z);
            }
            if (minX > maxX) return null;
            double margin = 2000.0 / MmPerFoot;
            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX - margin, minY - margin, minZ - margin),
                Max = new XYZ(maxX + margin, maxY + margin, maxZ + margin)
            };
        }

        private static OverrideGraphicSettings Style(Color color, ElementId fill, int transparency)
        {
            OverrideGraphicSettings graphics = new OverrideGraphicSettings();
            graphics.SetSurfaceForegroundPatternId(fill);
            graphics.SetSurfaceForegroundPatternColor(color);
            graphics.SetSurfaceForegroundPatternVisible(true);
            graphics.SetProjectionLineColor(color);
            graphics.SetProjectionLineWeight(6);
            graphics.SetSurfaceTransparency(Math.Max(0, Math.Min(100, transparency)));
            return graphics;
        }

        private static DirectShape CreateShape(
            Document doc,
            ElementId categoryId,
            string dataId,
            IList<GeometryObject> geometry)
        {
            DirectShape shape = DirectShape.CreateElement(doc, categoryId);
            shape.ApplicationId = FormalApplicationId;
            shape.ApplicationDataId = dataId;
            shape.SetShape(new List<GeometryObject>(geometry));
            return shape;
        }

        private static void ApplyParameters(
            DirectShape shape,
            TargetVisualizationScope scope,
            string componentName,
            string componentRole,
            string decisionNote,
            string evidenceFingerprint,
            string resultFingerprint,
            ShowStats stats)
        {
            SavedUserState saved;
            scope.SavedStates.TryGetValue(componentRole, out saved);
            bool hasManualConclusion = saved != null &&
                (string.Equals(saved.Conclusion,
                    MaintenanceParameterService.ConclusionMaintainable,
                    StringComparison.Ordinal) ||
                 string.Equals(saved.Conclusion,
                    MaintenanceParameterService.ConclusionNotMaintainable,
                    StringComparison.Ordinal));
            bool legacyCeilingResult = saved != null &&
                string.IsNullOrWhiteSpace(saved.ResultFingerprint) &&
                string.IsNullOrWhiteSpace(saved.OpeningIdentity) &&
                scope.Region != null &&
                scope.Region.OpeningPlane ==
                    HandReachOpeningPlaneKind.CeilingHorizontal;
            bool sameResult = saved != null &&
                (legacyCeilingResult || string.Equals(
                    saved.ResultFingerprint,
                    resultFingerprint,
                    StringComparison.Ordinal));
            bool inheritConclusion = hasManualConclusion && sameResult &&
                MaintenanceManualStatePolicy.ShouldInheritConclusion(
                    saved.EvidenceFingerprint,
                    evidenceFingerprint,
                    saved.DecisionNote,
                    decisionNote);
            if (hasManualConclusion && !inheritConclusion && stats != null)
            {
                string warning = "设备" + scope.DeviceNo + " " + componentRole +
                    " 的旧人工维修结论因证据或算法理由已变化而未继承；专业备注已保留。";
                if (!stats.Warnings.Contains(warning)) stats.Warnings.Add(warning);
            }
            MaintenanceParameterService.ApplyToDirectShape(shape, new MaintenanceParameterValues
            {
                ElementName = componentName,
                CeilingGroup = scope.GroupKey,
                EntryGroup = scope.EntryGroup,
                ElementRole = componentRole,
                MaintenanceTarget = scope.Target.Target.GetDisplayName(),
                MaintenanceConclusion = inheritConclusion
                    ? saved.Conclusion
                    : MaintenanceParameterService.ConclusionPending,
                DecisionNote = decisionNote,
                ProfessionalNote = MaintenanceManualStatePolicy.ResolveProfessionalNote(
                    null,
                    scope.SavedProfessionalNotes.ContainsKey(componentRole)
                        ? scope.SavedProfessionalNotes[componentRole]
                        : (saved == null ? null : saved.ProfessionalNote))
            });
        }

        private static Solid MakeCylinder(XYZ a, XYZ b, double radiusFt)
        {
            XYZ axis = b - a;
            double len = axis.GetLength();
            if (len <= 1e-9) return null;
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

        private static List<Solid> BuildVirtualBoundaryWallSolids(
            HandReachSample sample,
            double openingSizeMm)
        {
            var output = new List<Solid>();
            if (sample == null || !sample.UsesVirtualBoundaryWall ||
                openingSizeMm <= 0.0 ||
                sample.VirtualWallTopMm <= sample.VirtualWallBottomMm + 1.0)
                return output;

            var start = new MaintenancePoint2(
                sample.BoundaryStartX, sample.BoundaryStartY);
            var end = new MaintenancePoint2(
                sample.BoundaryEndX, sample.BoundaryEndY);
            MaintenancePoint2 tangent2 = (end - start).Normalize();
            double lengthMm = start.DistanceTo(end);
            if (lengthMm <= openingSizeMm + 2.0) return output;
            var inward2 = new MaintenancePoint2(
                sample.OpeningInwardX, sample.OpeningInwardY).Normalize();
            if (inward2.Length() <= 1e-9) return output;

            double openingAtMm =
                (sample.CenterX - start.X) * tangent2.X +
                (sample.CenterY - start.Y) * tangent2.Y;
            double half = openingSizeMm * 0.5;
            double openingStartMm = Math.Max(0.0, openingAtMm - half);
            double openingEndMm = Math.Min(lengthMm, openingAtMm + half);
            double openingBottomMm = sample.CenterZ - half;
            double openingTopMm = sample.CenterZ + half;
            double depthMm = Math.Max(20.0, sample.OpeningDepthMm);

            AddVirtualWallPiece(output, start, tangent2, inward2, depthMm,
                0.0, openingStartMm,
                sample.VirtualWallBottomMm, sample.VirtualWallTopMm);
            AddVirtualWallPiece(output, start, tangent2, inward2, depthMm,
                openingEndMm, lengthMm,
                sample.VirtualWallBottomMm, sample.VirtualWallTopMm);
            AddVirtualWallPiece(output, start, tangent2, inward2, depthMm,
                openingStartMm, openingEndMm,
                sample.VirtualWallBottomMm,
                Math.Min(openingBottomMm, sample.VirtualWallTopMm));
            AddVirtualWallPiece(output, start, tangent2, inward2, depthMm,
                openingStartMm, openingEndMm,
                Math.Max(openingTopMm, sample.VirtualWallBottomMm),
                sample.VirtualWallTopMm);
            return output;
        }

        private static void AddVirtualWallPiece(
            ICollection<Solid> output,
            MaintenancePoint2 boundaryStart,
            MaintenancePoint2 tangent,
            MaintenancePoint2 inward,
            double depthMm,
            double alongStartMm,
            double alongEndMm,
            double bottomMm,
            double topMm)
        {
            double lengthMm = alongEndMm - alongStartMm;
            double heightMm = topMm - bottomMm;
            if (lengthMm <= 1.0 || heightMm <= 1.0) return;
            double alongCenterMm = (alongStartMm + alongEndMm) * 0.5;
            double centerX = boundaryStart.X + tangent.X * alongCenterMm -
                             inward.X * depthMm * 0.5;
            double centerY = boundaryStart.Y + tangent.Y * alongCenterMm -
                             inward.Y * depthMm * 0.5;
            output.Add(MaintenanceGeometryService.MakeBox(
                new XYZ(centerX / MmPerFoot, centerY / MmPerFoot,
                    bottomMm / MmPerFoot),
                lengthMm / MmPerFoot,
                depthMm / MmPerFoot,
                heightMm / MmPerFoot,
                new XYZ(tangent.X, tangent.Y, 0.0)));
        }
    }
}

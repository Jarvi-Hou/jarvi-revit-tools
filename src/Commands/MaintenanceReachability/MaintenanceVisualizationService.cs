using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;

namespace JarviTools.Commands.MaintenanceReachability
{
    internal sealed class MaintenanceVisualizationStats
    {
        public int CreatedElementCount;
        public int DeletedPreviousElementCount;
        public int GroupCount;
        public int EntryGroupCount;
        public long TargetViewId;
        public string TargetViewName;
        public int CreatedViewCount;
        public readonly List<string> ContextViewNames = new List<string>();
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Writes one formal Generic Model DirectShape for every immutable analysis render
    /// item.  The service deliberately uses view filters and materials prepared by
    /// MaintenanceParameterService; element-level graphic overrides are never applied.
    /// </summary>
    internal static class MaintenanceVisualizationService
    {
        internal const string OwnerApplicationId = "JarviTools.MaintenanceReachability.v1";
        private const double MmPerFoot = 304.8;
        private const double GeometryToleranceFt = 1e-7;
        private const int ItemRevealDelayMilliseconds = 75;
        private static readonly Guid ReviewTraceSchemaGuid =
            new Guid("a2c9d67e-7d4a-4fc2-a1f7-94a8d51eaf11");
        private static readonly Guid StableShapeIdentitySchemaGuid =
            new Guid("65340f5e-4fd2-4dd9-a48d-28aed07a26d7");
        private const string EvidenceField = "EvidenceFingerprint";
        private const string ReviewerField = "Reviewer";
        private const string ReviewNoteField = "ReviewNote";
        private const string ApprovedAtField = "ApprovedAtUtc";
        private const string TargetHashesField = "TargetHashes";
        private const string GroupBackgroundField = "GroupBackground";

        [DllImport("user32.dll")]
        private static extern bool EnableWindow(IntPtr windowHandle, bool enable);

        [DllImport("user32.dll")]
        private static extern bool UpdateWindow(IntPtr windowHandle);

        private sealed class SavedUserState
        {
            public string Conclusion;
            public string ProfessionalNote;
            public string DecisionNote;
            public string EvidenceFingerprint;
        }

        public static MaintenanceVisualizationStats Show(
            UIApplication uiapp,
            MaintenanceAnalysisResult result)
        {
            if (uiapp == null || uiapp.ActiveUIDocument == null)
                throw new InvalidOperationException("Revit 没有活动文档。");
            if (result == null)
                throw new ArgumentNullException("result");

            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;
            View3D view = doc.ActiveView as View3D;
            if (view == null || view.IsTemplate)
                throw new InvalidOperationException("维修可达分析结果只能写入当前普通三维视图。");
            if (doc.IsModifiable)
                throw new InvalidOperationException("维修可达可视化需要自主事务，不能在其他事务内调用。");

            ElementId genericModelCategoryId = new ElementId(
                (long)BuiltInCategory.OST_GenericModel);
            if (view.GetCategoryHidden(genericModelCategoryId))
                throw new InvalidOperationException(
                    "当前三维视图隐藏了“常规模型”类别，请先显示该类别。");

            List<MaintenanceRenderItem> orderedItems = result.RenderItems
                .Where(x => x != null)
                .OrderBy(x => Safe(x.Parameters == null ? null : x.Parameters.CeilingGroup),
                    StringComparer.Ordinal)
                .ThenBy(x => Safe(x.Parameters == null ? null : x.Parameters.EntryGroup),
                    StringComparer.Ordinal)
                .ThenBy(x => RevealOrder(x.Role))
                .ThenBy(x => Safe(x.RenderKey), StringComparer.Ordinal)
                .ToList();

            var stats = new MaintenanceVisualizationStats
            {
                GroupCount = result.Groups.Count,
                EntryGroupCount = orderedItems
                    .Select(x => Safe(x.Parameters == null ? null : x.Parameters.EntryGroup))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
                TargetViewId = view.Id.Value,
                TargetViewName = view.Name
            };

            Dictionary<string, SavedUserState> savedStates =
                CaptureUserStates(doc);
            var createdShapeIds = new List<ElementId>();
            var replacedGroups = new HashSet<string>(
                result.Groups
                    .Where(x => x != null)
                    .Select(x => Safe(x.GroupKey)),
                StringComparer.Ordinal);
            var transactionGroup = new TransactionGroup(
                doc,
                "生成维修可达分析模型");
            IntPtr mainWindowHandle = uiapp.MainWindowHandle;
            bool mainWindowDisabled = false;
            try
            {
                transactionGroup.Start();
                if (mainWindowHandle != IntPtr.Zero)
                {
                    EnableWindow(mainWindowHandle, false);
                    mainWindowDisabled = true;
                }

                var setupTransaction = new Transaction(
                    doc,
                    "准备维修可达分析显示");
                try
                {
                    setupTransaction.Start();
                    string sharedParameterPath =
                        MaintenanceParameterService.GetDefaultSharedParameterFilePath();
                    MaintenanceParameterService.EnsureSharedParameters(doc, sharedParameterPath);
                    MaintenanceParameterService.EnsureViewPresentation(doc, view);
                    stats.DeletedPreviousElementCount = ClearOwnedCore(
                        doc,
                        replacedGroups);
                    JarviTools.Core.TransactionSafety.Commit(setupTransaction, "Prepare maintenance visualization");
                }
                catch
                {
                    if (setupTransaction.HasStarted() && !setupTransaction.HasEnded())
                        setupTransaction.RollBack();
                    throw;
                }

                // First clear the previous result, then reveal each newly committed
                // component.  All child transactions are assimilated below, so the
                // complete animation still produces exactly one Revit undo item.
                RefreshAndPause(uidoc, mainWindowHandle);

                var dataIdUseCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (MaintenanceRenderItem item in orderedItems)
                {
                    string stableBasis = BuildStableBasis(item);
                    int duplicateIndex;
                    if (!dataIdUseCounts.TryGetValue(stableBasis, out duplicateIndex))
                        duplicateIndex = 0;
                    dataIdUseCounts[stableBasis] = duplicateIndex + 1;
                    if (duplicateIndex > 0)
                        stableBasis += "#" + duplicateIndex;

                    string applicationDataId = HashDataId(stableBasis);
                    SavedUserState savedState;
                    if (!savedStates.TryGetValue("ID:" + applicationDataId, out savedState))
                        savedStates.TryGetValue(
                            "LOGIC:" + BuildUserStateKey(item.Parameters),
                            out savedState);

                    var itemTransaction = new Transaction(
                        doc,
                        "生成维修可达构件");
                    try
                    {
                        itemTransaction.Start();
                        DirectShape createdShape = CreateOwnedShape(
                            doc,
                            genericModelCategoryId,
                            item,
                            applicationDataId,
                            savedState);
                        createdShapeIds.Add(createdShape.Id);
                        JarviTools.Core.TransactionSafety.Commit(itemTransaction, "Create maintenance visualization item");
                    }
                    catch
                    {
                        if (itemTransaction.HasStarted() && !itemTransaction.HasEnded())
                            itemTransaction.RollBack();
                        throw;
                    }

                    stats.CreatedElementCount++;
                    RefreshAndPause(uidoc, mainWindowHandle);
                }

                var viewSyncTransaction = new Transaction(
                    doc,
                    "同步维修可达分析视图");
                try
                {
                    viewSyncTransaction.Start();
                    foreach (string group in replacedGroups
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .OrderBy(x => x, StringComparer.Ordinal))
                    {
                        MaintenanceContextViewSyncResult sync =
                            MaintenanceManagedViewService.SynchronizeContextViews(
                                doc, view, group);
                        if (sync.AiViewCreated) stats.CreatedViewCount++;
                        if (sync.AiView != null)
                            stats.ContextViewNames.Add(sync.AiView.Name);
                        stats.ContextViewNames.AddRange(
                            sync.FloorOverviewViews.Select(x => x.Name));
                        stats.Warnings.AddRange(sync.Warnings);
                    }
                    MaintenanceManagedViewService.HideFromOtherDedicatedSchemeViews(
                        doc,
                        createdShapeIds,
                        null);
                    JarviTools.Core.TransactionSafety.Commit(
                        viewSyncTransaction,
                        "Synchronize maintenance context views");
                }
                catch
                {
                    if (viewSyncTransaction.HasStarted() &&
                        !viewSyncTransaction.HasEnded())
                        viewSyncTransaction.RollBack();
                    throw;
                }

                JarviTools.Core.TransactionSafety.Assimilate(
                    transactionGroup,
                    "Generate maintenance reachability visualization");
                return stats;
            }
            catch
            {
                if (transactionGroup.HasStarted() && !transactionGroup.HasEnded())
                    transactionGroup.RollBack();
                uidoc.RefreshActiveView();
                throw;
            }
            finally
            {
                if (mainWindowDisabled)
                    EnableWindow(mainWindowHandle, true);
            }
        }

        private static DirectShape CreateOwnedShape(
            Document doc,
            ElementId genericModelCategoryId,
            MaintenanceRenderItem item,
            string applicationDataId,
            SavedUserState savedState)
        {
            MaintenanceParameterValues parameterValues =
                BuildParameterValues(item, savedState);
            ElementId materialId = IsDecisionGeometry(item.Role)
                ? MaintenanceParameterService.GetConclusionMaterialId(
                    doc,
                    parameterValues.MaintenanceConclusion)
                : ElementId.InvalidElementId;
            List<GeometryObject> geometry = CreateGeometry(item, materialId);
            if (geometry.Count == 0)
                throw new InvalidOperationException(
                    "维修可达构件没有可写入的几何：" +
                    (string.IsNullOrWhiteSpace(item.RenderKey)
                        ? item.Role.ToString()
                        : item.RenderKey));

            DirectShape shape = DirectShape.CreateElement(doc, genericModelCategoryId);
            shape.ApplicationId = OwnerApplicationId;
            shape.ApplicationDataId = applicationDataId;
            DirectShapeOptions options = shape.GetOptions();
            options.ReferencingOption = DirectShapeReferencingOption.NotReferenceable;
            shape.SetOptions(options);
            shape.SetShape(geometry);
            MaintenanceParameterService.ApplyToDirectShape(shape, parameterValues);
            WriteStableShapeIdentity(shape, item);
            WriteReviewTrace(shape, item);
            return shape;
        }

        private static void RefreshAndPause(
            UIDocument uidoc,
            IntPtr mainWindowHandle)
        {
            uidoc.RefreshActiveView();
            if (mainWindowHandle != IntPtr.Zero)
                UpdateWindow(mainWindowHandle);
            Thread.Sleep(ItemRevealDelayMilliseconds);
        }

        public static int Clear(UIApplication uiapp)
        {
            if (uiapp == null || uiapp.ActiveUIDocument == null)
                throw new InvalidOperationException("Revit 没有活动文档。");
            Document doc = uiapp.ActiveUIDocument.Document;
            if (doc.IsModifiable)
                throw new InvalidOperationException("清除维修可达结果需要自主事务。");

            var transaction = new Transaction(doc, "清除维修可达分析模型");
            try
            {
                transaction.Start();
                int deleted = ClearOwnedCore(doc, null);
                JarviTools.Core.TransactionSafety.Commit(transaction, "Clear maintenance visualization");
                try { uiapp.ActiveUIDocument.RefreshActiveView(); }
                catch { /* deletion is already committed; UI refresh is best-effort */ }
                return deleted;
            }
            catch
            {
                if (transaction.HasStarted() && !transaction.HasEnded())
                    transaction.RollBack();
                throw;
            }
        }

        private static Dictionary<string, SavedUserState> CaptureUserStates(Document doc)
        {
            var states = new Dictionary<string, SavedUserState>(StringComparer.Ordinal);
            foreach (DirectShape shape in GetOwnedShapes(doc))
            {
                MaintenanceReviewTrace trace = ReadReviewTrace(shape);
                var state = new SavedUserState
                {
                    Conclusion = ReadText(shape, MaintenanceParameterService.MaintenanceConclusionGuid),
                    ProfessionalNote = ReadText(shape, MaintenanceParameterService.ProfessionalNoteGuid),
                    DecisionNote = ReadText(shape, MaintenanceParameterService.DecisionNoteGuid),
                    EvidenceFingerprint = trace.EvidenceFingerprint
                };
                if (!string.IsNullOrWhiteSpace(shape.ApplicationDataId))
                    states["ID:" + shape.ApplicationDataId] = state;
                string logicalKey = BuildUserStateKey(new MaintenanceInstanceParameters
                {
                    ComponentName = ReadText(shape, MaintenanceParameterService.ElementNameGuid),
                    CeilingGroup = ReadText(shape, MaintenanceParameterService.CeilingGroupGuid),
                    EntryGroup = ReadText(shape, MaintenanceParameterService.EntryGroupGuid),
                    ComponentRole = ReadText(shape, MaintenanceParameterService.ElementRoleGuid),
                    MaintenanceTarget = ReadText(shape, MaintenanceParameterService.MaintenanceTargetGuid)
                });
                if (!string.IsNullOrWhiteSpace(logicalKey))
                    states["LOGIC:" + logicalKey] = state;
            }
            return states;
        }

        private static string ReadText(Element element, Guid guid)
        {
            Parameter parameter = element.get_Parameter(guid);
            return parameter == null ? null : parameter.AsString();
        }

        private static MaintenanceParameterValues BuildParameterValues(
            MaintenanceRenderItem item,
            SavedUserState savedState)
        {
            MaintenanceInstanceParameters source =
                item.Parameters ?? new MaintenanceInstanceParameters();
            string role = string.IsNullOrWhiteSpace(source.ComponentRole)
                ? GetRoleName(item.Role)
                : source.ComponentRole;
            string generatedConclusion = IsDecisionGeometry(item.Role)
                ? (string.IsNullOrWhiteSpace(source.MaintenanceConclusion)
                    ? GetDecisionName(item.Decision)
                    : source.MaintenanceConclusion)
                : null;

            // A professional may resolve a generated yellow result.  Preserve that
            // explicit confirmation on regeneration, but never let it mask a newly
            // computed red/green result.
            if (savedState != null &&
                string.Equals(generatedConclusion,
                    MaintenanceParameterService.ConclusionPending,
                    StringComparison.Ordinal) &&
                string.Equals(Safe(savedState.DecisionNote),
                    Safe(source.DecisionReason),
                    StringComparison.Ordinal) &&
                string.Equals(Safe(savedState.EvidenceFingerprint),
                    Safe(item.EvidenceFingerprint),
                    StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(savedState.Conclusion,
                    MaintenanceParameterService.ConclusionMaintainable,
                    StringComparison.Ordinal) ||
                 string.Equals(savedState.Conclusion,
                    MaintenanceParameterService.ConclusionNotMaintainable,
                    StringComparison.Ordinal)))
            {
                generatedConclusion = savedState.Conclusion;
            }

            string professionalNote = source.ProfessionalNote;
            if (string.IsNullOrWhiteSpace(professionalNote) && savedState != null)
                professionalNote = savedState.ProfessionalNote;

            return new MaintenanceParameterValues
            {
                ElementName = string.IsNullOrWhiteSpace(source.ComponentName)
                    ? BuildComponentName(source, role)
                    : source.ComponentName,
                CeilingGroup = Safe(source.CeilingGroup),
                EntryGroup = Safe(source.EntryGroup),
                ElementRole = role,
                MaintenanceTarget = Safe(source.MaintenanceTarget),
                MaintenanceConclusion = generatedConclusion,
                DecisionNote = Safe(source.DecisionReason),
                ProfessionalNote = professionalNote
            };
        }

        private static string BuildUserStateKey(MaintenanceInstanceParameters values)
        {
            if (values == null) return string.Empty;
            return string.Join("|", new[]
            {
                Safe(values.ComponentName),
                Safe(values.CeilingGroup),
                Safe(values.EntryGroup),
                Safe(values.ComponentRole),
                Safe(values.MaintenanceTarget)
            });
        }

        private static string BuildComponentName(
            MaintenanceInstanceParameters source,
            string role)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(source.CeilingGroup))
                parts.Add(source.CeilingGroup);
            if (!string.IsNullOrWhiteSpace(source.EntryGroup))
                parts.Add(source.EntryGroup);
            parts.Add(role);
            return string.Join("｜", parts);
        }

        private static string GetRoleName(MaintenanceComponentRole role)
        {
            switch (role)
            {
                case MaintenanceComponentRole.VirtualBoundaryWall:
                    return "虚拟边界墙";
                case MaintenanceComponentRole.WallDoor:
                    return "侧墙检修门";
                case MaintenanceComponentRole.CeilingHatch:
                    return "天花检修口";
                case MaintenanceComponentRole.AFrameLadder:
                    return "人字梯";
                case MaintenanceComponentRole.StraightLadder:
                    return "一字梯";
                case MaintenanceComponentRole.EntryTurnZone:
                    return "入口转身区";
                case MaintenanceComponentRole.AccessRoute:
                    return "维修路线";
                case MaintenanceComponentRole.HumanEnvelope:
                    return "人员通行包络";
                case MaintenanceComponentRole.ServicePocket:
                    return "设备维修区";
                case MaintenanceComponentRole.TargetEquipment:
                    return "维修对象";
                default:
                    return "维修可达构件";
            }
        }

        private static string GetDecisionName(MaintenanceDecision decision)
        {
            switch (decision)
            {
                case MaintenanceDecision.Pass:
                    return MaintenanceParameterService.ConclusionMaintainable;
                case MaintenanceDecision.Fail:
                    return MaintenanceParameterService.ConclusionNotMaintainable;
                default:
                    return MaintenanceParameterService.ConclusionPending;
            }
        }

        private static int RevealOrder(MaintenanceComponentRole role)
        {
            switch (role)
            {
                case MaintenanceComponentRole.VirtualBoundaryWall: return 0;
                case MaintenanceComponentRole.WallDoor:
                case MaintenanceComponentRole.CeilingHatch: return 1;
                case MaintenanceComponentRole.AFrameLadder:
                case MaintenanceComponentRole.StraightLadder: return 2;
                case MaintenanceComponentRole.EntryTurnZone: return 3;
                case MaintenanceComponentRole.HumanEnvelope: return 4;
                case MaintenanceComponentRole.AccessRoute: return 5;
                case MaintenanceComponentRole.TargetEquipment: return 6;
                case MaintenanceComponentRole.ServicePocket: return 7;
                default: return 8;
            }
        }

        private static bool IsDecisionGeometry(MaintenanceComponentRole role)
        {
            return role == MaintenanceComponentRole.ServicePocket;
        }

        internal static List<GeometryObject> CreateGeometry(
            MaintenanceRenderItem item,
            ElementId materialId)
        {
            var geometry = new List<GeometryObject>();
            if (item.Role == MaintenanceComponentRole.AFrameLadder ||
                item.Role == MaintenanceComponentRole.StraightLadder)
            {
                if (item.Points.Count < 2)
                    throw new InvalidOperationException("梯子缺少楼面和顶部标高。");
                XYZ bottom = ToFeet(item.Points[0]);
                XYZ top = ToFeet(item.Points[item.Points.Count - 1]);
                XYZ planCenter = new XYZ(bottom.X, bottom.Y, 0.0);
                XYZ along = new XYZ(item.Direction.X, item.Direction.Y, 0.0);
                List<Solid> ladder = item.Role == MaintenanceComponentRole.AFrameLadder
                    ? MaintenanceGeometryService.BuildAFrameLadder(
                        planCenter, along, bottom.Z, top.Z)
                    : MaintenanceGeometryService.BuildStraightLadder(
                        planCenter, along, bottom.Z, top.Z);
                geometry.AddRange(ladder.Cast<GeometryObject>());
                return geometry;
            }
            if ((item.Role == MaintenanceComponentRole.AccessRoute ||
                 item.Role == MaintenanceComponentRole.HumanEnvelope) &&
                item.Points.Count > 0)
            {
                List<XYZ> points = item.Points.Select(ToFeet).ToList();
                geometry.AddRange(MaintenanceGeometryService.BuildRoute(
                    points,
                    Math.Max(20.0, item.WidthMm * 0.5) / MmPerFoot,
                    Math.Max(20.0, item.HeightMm) / MmPerFoot).Cast<GeometryObject>());
                return geometry;
            }
            switch (item.GeometryType)
            {
                case MaintenanceRenderGeometryType.ExtrudedPolygon:
                    geometry.Add(CreateExtrudedPolygon(item, materialId));
                    break;
                case MaintenanceRenderGeometryType.Polyline:
                    geometry.AddRange(CreatePolyline(item, materialId));
                    break;
                case MaintenanceRenderGeometryType.Box:
                case MaintenanceRenderGeometryType.Marker:
                default:
                    geometry.Add(CreateOrientedBox(
                        item.Center,
                        item.Direction,
                        item.WidthMm,
                        item.DepthMm,
                        item.HeightMm,
                        materialId));
                    break;
            }
            return geometry.Where(x => x != null).ToList();
        }

        private static Solid CreateExtrudedPolygon(
            MaintenanceRenderItem item,
            ElementId materialId)
        {
            List<MaintenancePoint3> points = RemoveRepeatedClosingPoint(item.Points);
            if (points.Count < 3)
                throw new InvalidOperationException("拉伸多边形至少需要三个不同顶点。");

            double heightFt = PositiveFeet(item.HeightMm, "高度");
            double baseZFt = (item.Center.Z - item.HeightMm * 0.5) / MmPerFoot;
            var loop = new CurveLoop();
            for (int index = 0; index < points.Count; index++)
            {
                MaintenancePoint3 from = points[index];
                MaintenancePoint3 to = points[(index + 1) % points.Count];
                XYZ p0 = new XYZ(from.X / MmPerFoot, from.Y / MmPerFoot, baseZFt);
                XYZ p1 = new XYZ(to.X / MmPerFoot, to.Y / MmPerFoot, baseZFt);
                if (p0.DistanceTo(p1) <= GeometryToleranceFt) continue;
                loop.Append(Line.CreateBound(p0, p1));
            }
            if (loop.NumberOfCurves() < 3)
                throw new InvalidOperationException("拉伸多边形的有效边不足三个。");

            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { loop },
                XYZ.BasisZ,
                heightFt,
                new SolidOptions(materialId, ElementId.InvalidElementId));
        }

        private static List<GeometryObject> CreatePolyline(
            MaintenanceRenderItem item,
            ElementId materialId)
        {
            var geometry = new List<GeometryObject>();
            double thicknessMm = item.WidthMm > 0.0 ? item.WidthMm : 120.0;
            double heightMm = item.HeightMm > 0.0 ? item.HeightMm : 120.0;
            if (item.Points.Count < 2)
            {
                geometry.Add(CreateOrientedBox(
                    item.Center,
                    item.Direction,
                    thicknessMm,
                    item.DepthMm > 0.0 ? item.DepthMm : thicknessMm,
                    heightMm,
                    materialId));
                return geometry;
            }

            for (int index = 1; index < item.Points.Count; index++)
            {
                MaintenancePoint3 from = item.Points[index - 1];
                MaintenancePoint3 to = item.Points[index];
                double dx = to.X - from.X;
                double dy = to.Y - from.Y;
                double dz = to.Z - from.Z;
                double horizontalLength = Math.Sqrt(dx * dx + dy * dy);
                if (horizontalLength <= 1e-6 && Math.Abs(dz) <= 1e-6) continue;

                double segmentHeight = Math.Max(heightMm, Math.Abs(dz) + heightMm);
                var center = new MaintenancePoint3(
                    (from.X + to.X) * 0.5,
                    (from.Y + to.Y) * 0.5,
                    (from.Z + to.Z) * 0.5);
                MaintenancePoint2 direction = horizontalLength <= 1e-6
                    ? new MaintenancePoint2(1.0, 0.0)
                    : new MaintenancePoint2(dx, dy);
                double segmentLength = Math.Max(thicknessMm,
                    horizontalLength + thicknessMm);
                geometry.Add(CreateOrientedBox(
                    center,
                    direction,
                    segmentLength,
                    thicknessMm,
                    segmentHeight,
                    materialId));
            }
            return geometry;
        }

        private static Solid CreateOrientedBox(
            MaintenancePoint3 center,
            MaintenancePoint2 direction,
            double widthMm,
            double depthMm,
            double heightMm,
            ElementId materialId)
        {
            double widthFt = PositiveFeet(widthMm, "宽度");
            double depthFt = PositiveFeet(depthMm, "深度");
            double heightFt = PositiveFeet(heightMm, "高度");
            MaintenancePoint2 xDirection = direction.Normalize();
            if (xDirection.Length() <= 1e-9)
                xDirection = new MaintenancePoint2(1.0, 0.0);
            MaintenancePoint2 yDirection = xDirection.LeftNormal();

            XYZ centerFt = new XYZ(
                center.X / MmPerFoot,
                center.Y / MmPerFoot,
                (center.Z - heightMm * 0.5) / MmPerFoot);
            XYZ x = new XYZ(xDirection.X, xDirection.Y, 0.0);
            XYZ y = new XYZ(yDirection.X, yDirection.Y, 0.0);
            XYZ p0 = centerFt - x * (widthFt * 0.5) - y * (depthFt * 0.5);
            XYZ p1 = centerFt + x * (widthFt * 0.5) - y * (depthFt * 0.5);
            XYZ p2 = centerFt + x * (widthFt * 0.5) + y * (depthFt * 0.5);
            XYZ p3 = centerFt - x * (widthFt * 0.5) + y * (depthFt * 0.5);

            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(p0, p1));
            loop.Append(Line.CreateBound(p1, p2));
            loop.Append(Line.CreateBound(p2, p3));
            loop.Append(Line.CreateBound(p3, p0));
            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { loop },
                XYZ.BasisZ,
                heightFt,
                new SolidOptions(materialId, ElementId.InvalidElementId));
        }

        private static double PositiveFeet(double millimetres, string dimensionName)
        {
            if (millimetres <= 1e-6)
                throw new InvalidOperationException(
                    "维修可达构件的" + dimensionName + "必须大于零。");
            return millimetres / MmPerFoot;
        }

        private static List<MaintenancePoint3> RemoveRepeatedClosingPoint(
            IList<MaintenancePoint3> source)
        {
            var result = source == null
                ? new List<MaintenancePoint3>()
                : source.ToList();
            if (result.Count > 1 && result[0].Equals(result[result.Count - 1]))
                result.RemoveAt(result.Count - 1);
            return result;
        }

        private static int ClearOwnedCore(
            Document doc,
            ISet<string> ceilingGroups)
        {
            List<ElementId> ids = GetOwnedShapes(doc)
                .Where(x => ceilingGroups == null || ceilingGroups.Contains(
                    Safe(ReadText(x, MaintenanceParameterService.CeilingGroupGuid))))
                .Select(x => x.Id)
                .ToList();
            if (ids.Count > 0) doc.Delete(ids);
            return ids.Count;
        }

        private static IEnumerable<DirectShape> GetOwnedShapes(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(DirectShape))
                .Cast<DirectShape>()
                .Where(x => string.Equals(
                    x.ApplicationId,
                    OwnerApplicationId,
                    StringComparison.Ordinal));
        }

        private static string BuildStableBasis(MaintenanceRenderItem item)
        {
            return MaintenanceDirectShapeIdentityPolicy.BuildStableBasis(item);
        }

        private static void WriteReviewTrace(
            Element element,
            MaintenanceRenderItem item)
        {
            if (element == null || item == null) return;
            WriteReviewTrace(element, item.EvidenceFingerprint, item.ApprovalReviewer, item.ApprovalNote, item.ApprovedAtUtc);
        }

        internal static void WriteReviewTrace(
            Element element,
            string evidenceFingerprint,
            string reviewer,
            string note,
            string approvedAtUtc)
        {
            if (element == null) return;
            Schema schema = GetOrCreateReviewTraceSchema();
            var entity = new Entity(schema);
            entity.Set(schema.GetField(EvidenceField), Safe(evidenceFingerprint));
            entity.Set(schema.GetField(ReviewerField), Safe(reviewer));
            entity.Set(schema.GetField(ReviewNoteField), Safe(note));
            entity.Set(schema.GetField(ApprovedAtField), Safe(approvedAtUtc));
            element.SetEntity(entity);
        }

        internal static MaintenanceReviewTrace ReadReviewTrace(Element element)
        {
            var trace = new MaintenanceReviewTrace();
            if (element == null) return trace;
            Schema schema = Schema.Lookup(ReviewTraceSchemaGuid);
            if (schema == null) return trace;
            Entity entity = element.GetEntity(schema);
            if (entity == null || !entity.IsValid()) return trace;
            trace.EvidenceFingerprint = entity.Get<string>(schema.GetField(EvidenceField)) ?? string.Empty;
            trace.Reviewer = entity.Get<string>(schema.GetField(ReviewerField)) ?? string.Empty;
            trace.ReviewNote = entity.Get<string>(schema.GetField(ReviewNoteField)) ?? string.Empty;
            trace.ApprovedAtUtc = entity.Get<string>(schema.GetField(ApprovedAtField)) ?? string.Empty;
            return trace;
        }

        internal static bool HasStableTargetIdentity(
            Element element,
            string stableTargetKey)
        {
            if (element == null || string.IsNullOrWhiteSpace(stableTargetKey)) return false;
            Schema schema = Schema.Lookup(StableShapeIdentitySchemaGuid);
            if (schema == null) return false;
            Entity entity = element.GetEntity(schema);
            if (entity == null || !entity.IsValid()) return false;
            string value = entity.Get<string>(schema.GetField(TargetHashesField)) ?? string.Empty;
            return MaintenanceDirectShapeIdentityPolicy.ContainsTargetHash(
                value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries),
                stableTargetKey);
        }

        internal static bool IsStableGroupBackground(Element element)
        {
            if (element == null) return false;
            Schema schema = Schema.Lookup(StableShapeIdentitySchemaGuid);
            if (schema == null) return false;
            Entity entity = element.GetEntity(schema);
            return entity != null && entity.IsValid() &&
                   entity.Get<bool>(schema.GetField(GroupBackgroundField));
        }

        private static void WriteStableShapeIdentity(
            Element element,
            MaintenanceRenderItem item)
        {
            if (element == null || item == null) return;
            Schema schema = GetOrCreateStableShapeIdentitySchema();
            var entity = new Entity(schema);
            entity.Set(schema.GetField(TargetHashesField), string.Join(",",
                MaintenanceDirectShapeIdentityPolicy.GetTargetHashes(item)));
            entity.Set(schema.GetField(GroupBackgroundField),
                item.Role == MaintenanceComponentRole.VirtualBoundaryWall);
            element.SetEntity(entity);
        }

        private static Schema GetOrCreateStableShapeIdentitySchema()
        {
            Schema existing = Schema.Lookup(StableShapeIdentitySchemaGuid);
            if (existing != null) return existing;
            var builder = new SchemaBuilder(StableShapeIdentitySchemaGuid);
            builder.SetSchemaName("JarviToolsMaintenanceShapeIdentityV1");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(TargetHashesField, typeof(string));
            builder.AddSimpleField(GroupBackgroundField, typeof(bool));
            return builder.Finish();
        }

        private static Schema GetOrCreateReviewTraceSchema()
        {
            Schema existing = Schema.Lookup(ReviewTraceSchemaGuid);
            if (existing != null) return existing;
            var builder = new SchemaBuilder(ReviewTraceSchemaGuid);
            builder.SetSchemaName("JarviToolsMaintenanceReviewTraceV1");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(EvidenceField, typeof(string));
            builder.AddSimpleField(ReviewerField, typeof(string));
            builder.AddSimpleField(ReviewNoteField, typeof(string));
            builder.AddSimpleField(ApprovedAtField, typeof(string));
            return builder.Finish();
        }

        private static string HashDataId(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            byte[] hash;
            using (SHA256 algorithm = SHA256.Create())
                hash = algorithm.ComputeHash(bytes);
            var builder = new StringBuilder("MR1-");
            for (int index = 0; index < 16; index++)
                builder.Append(hash[index].ToString("x2"));
            return builder.ToString();
        }

        private static string Safe(string value)
        {
            return value ?? string.Empty;
        }

        private static XYZ ToFeet(MaintenancePoint3 point)
        {
            return new XYZ(point.X / MmPerFoot, point.Y / MmPerFoot, point.Z / MmPerFoot);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace JarviTools.Commands.MaintenanceReachability
{
    internal static class MaintenanceWallAlternativeVisualizationService
    {
        internal const string OwnerApplicationId =
            "JarviTools.MaintenanceWallAlternative.v1";
        internal const string ViewOwnerId =
            "JarviTools.MaintenanceWallAlternative.View.v1";

        internal sealed class ShowStats
        {
            public int CreatedElementCount;
            public int DeletedPreviousElementCount;
            public int CreatedViewCount;
            public int ReusedFormalElementCount;
            public long ViewId;
            public string ViewName;
            public long OverviewViewId;
            public string OverviewViewName;
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
        }

        internal static void ResolveSchemeAssignments(
            Document doc,
            MaintenanceAnalysisResult result)
        {
            if (doc == null) throw new ArgumentNullException("doc");
            if (result == null) throw new ArgumentNullException("result");
            List<DirectShape> shapes = GetMaintenanceShapes(doc).ToList();
            ResolveStableDeviceNumbers(result, shapes);
            foreach (MaintenanceWallAlternativeResult alternative in result.WallAlternatives
                .Where(x => x != null && x.CanVisualize)
                .OrderBy(x => x.GroupKey, StringComparer.Ordinal)
                .ThenBy(x => x.DeviceNo, StringComparer.Ordinal)
                .ThenBy(x => x.TargetKey, StringComparer.Ordinal))
            {
                HashSet<int> reserved = new HashSet<int>(shapes
                    .Where(x => !string.Equals(x.ApplicationId, OwnerApplicationId,
                        StringComparison.Ordinal))
                    .Where(x => SameDevice(x, alternative.GroupKey, alternative.DeviceNo))
                    .Select(ReadSchemeNo)
                    .Where(x => x > 0));
                List<int> reusable = shapes
                    .Where(x => string.Equals(x.ApplicationId, OwnerApplicationId,
                        StringComparison.Ordinal))
                    .Where(x => SameDevice(x, alternative.GroupKey,
                        alternative.DeviceNo))
                    .Where(x => string.Equals(ReadTargetHash(x),
                        HashShort(alternative.TargetKey), StringComparison.Ordinal))
                    .Select(ReadSchemeNo)
                    .Where(x => x > 0 && !reserved.Contains(x))
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();
                int scheme = alternative.SchemeNo;
                if (scheme <= 0 || reserved.Contains(scheme))
                {
                    scheme = reusable.FirstOrDefault();
                    if (scheme <= 0)
                    {
                        scheme = 1;
                        while (reserved.Contains(scheme)) scheme++;
                    }
                }
                alternative.SchemeNo = scheme;
                alternative.EntryGroup = MaintenanceAnalysisService.BuildWallAlternativeEntryGroup(
                    alternative.GroupKey, alternative.DeviceNo, scheme);
                alternative.ViewName = MaintenanceAnalysisService.BuildWallAlternativeViewName(
                    alternative.GroupKey, alternative.DeviceNo, scheme);
                foreach (MaintenanceRenderItem item in alternative.RenderItems.Where(x => x != null))
                {
                    item.Parameters.CeilingGroup = alternative.GroupKey;
                    item.Parameters.EntryGroup = alternative.EntryGroup;
                }
                alternative.GeometryFingerprint =
                    MaintenanceWallAlternativePolicy.ComputeFingerprint(new[] { alternative });
            }
            result.WallAlternativeFingerprint =
                MaintenanceWallAlternativePolicy.ComputeFingerprint(result.WallAlternatives);
        }

        internal static ShowStats Show(
            UIApplication uiapp,
            MaintenanceAnalysisResult result,
            MaintenanceWallAlternativeResult alternative)
        {
            if (uiapp == null || uiapp.ActiveUIDocument == null)
                throw new InvalidOperationException("Revit 没有活动文档。");
            if (result == null) throw new ArgumentNullException("result");
            if (alternative == null) throw new ArgumentNullException("alternative");
            if (!alternative.CanVisualize || alternative.RenderItems.Count == 0)
                throw new InvalidOperationException(
                    "该侧墙备选没有完整可建模几何，拒绝生成猜测模型。");
            if (!MaintenanceWallAlternativePolicy.IsRenderGeometryComplete(
                alternative.RenderItems))
                throw new InvalidOperationException(
                    "该侧墙备选缺少完整的门、梯具、转身区、路线、检修区或边界墙几何。");

            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;
            if (doc.IsModifiable)
                throw new InvalidOperationException("侧墙备选显示需要自主事务，不能在其他事务内调用。");
            View3D sourceView = uidoc.ActiveView as View3D;
            if (sourceView == null || sourceView.IsTemplate)
                sourceView = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D)).Cast<View3D>()
                    .Where(x => !x.IsTemplate)
                    .OrderBy(x => x.Name, StringComparer.Ordinal)
                    .FirstOrDefault();
            if (sourceView == null)
                throw new InvalidOperationException("文档中没有可作为基准的普通三维视图。");

            int approvedScheme = alternative.SchemeNo;
            ResolveSchemeAssignments(doc, result);
            if (approvedScheme > 0 && approvedScheme != alternative.SchemeNo)
            {
                alternative.SchemeNo = approvedScheme;
                throw new InvalidOperationException(
                    "侧墙备选方案号在审批后被其他模式占用；请重新分析并审批。"
                );
            }

            string prefix = BuildDataPrefix(alternative.GroupKey, alternative.DeviceNo,
                alternative.SchemeNo, alternative.TargetKey, true);
            List<DirectShape> previous = GetOwnedShapes(doc)
                .Where(x => Safe(x.ApplicationDataId).StartsWith(prefix,
                    StringComparison.Ordinal))
                .ToList();
            Dictionary<string, SavedUserState> saved = CaptureUserState(previous);
            List<DirectShape> sameTargetFormal = alternative.SameAsRouteFormal
                ? FindSameTargetFormalShapes(doc, alternative)
                : new List<DirectShape>();
            List<DirectShape> reusableFormal = sameTargetFormal
                .Where(x => string.Equals(
                    MaintenanceVisualizationService.ReadReviewTrace(x)
                        .EvidenceFingerprint,
                    result.EvidenceFingerprint,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            bool formalRolesComplete = HasCompleteFormalRoleSet(reusableFormal);
            bool sameTargetFormalShapesExist = sameTargetFormal.Any(x =>
                MaintenanceVisualizationService.HasStableTargetIdentity(
                    x, alternative.TargetKey));
            if (MaintenanceFormalReusePolicy.MustRejectPotentialDuplicate(
                alternative.SameAsRouteFormal,
                sameTargetFormalShapesExist,
                reusableFormal.Count > 0,
                formalRolesComplete))
                throw new InvalidOperationException(
                    "检测到该设备已有正式侧墙模型，但当前审批证据与它不完全一致或必要角色不完整；为避免重复建模，已拒绝生成第二套。请直接使用刚完成正式显示的同一分析快照重新审批，或清理旧正式结果后重新分析。");
            bool reuseFormal = MaintenanceFormalReusePolicy.ShouldReuse(
                alternative.SameAsRouteFormal,
                reusableFormal.Count > 0,
                formalRolesComplete);
            var stats = new ShowStats();
            View3D targetView = null;
            View3D overviewView = null;
            List<ElementId> newIds = new List<ElementId>();
            using (var tx = new Transaction(doc, "OpenRevit 侧墙备选显示"))
            {
                tx.Start();
                try
                {
                    MaintenanceParameterService.EnsureSharedParameters(
                        doc,
                        MaintenanceParameterService.GetDefaultSharedParameterFilePath());
                    if (previous.Count > 0)
                    {
                        doc.Delete(previous.Select(x => x.Id).ToList());
                        stats.DeletedPreviousElementCount = previous.Count;
                    }

                    if (reuseFormal)
                    {
                        newIds.AddRange(reusableFormal.Select(x => x.Id));
                        stats.ReusedFormalElementCount = reusableFormal.Count;
                    }
                    else
                    {
                        ElementId categoryId = new ElementId(
                            (long)BuiltInCategory.OST_GenericModel);
                        int index = 0;
                        foreach (MaintenanceRenderItem item in alternative.RenderItems
                            .Where(x => x != null)
                            .OrderBy(x => x.RenderKey, StringComparer.Ordinal))
                        {
                            string role = Safe(item.Parameters == null
                                ? null
                                : item.Parameters.ComponentRole);
                            SavedUserState userState;
                            saved.TryGetValue(role, out userState);
                            MaintenanceParameterValues values = BuildValues(
                                item, userState, alternative, stats);
                            ElementId materialId = item.Role == MaintenanceComponentRole.ServicePocket
                                ? MaintenanceParameterService.GetConclusionMaterialId(
                                    doc, values.MaintenanceConclusion)
                                : ElementId.InvalidElementId;
                            List<GeometryObject> geometry =
                                MaintenanceVisualizationService.CreateGeometry(item, materialId);
                            if (geometry.Count == 0)
                                throw new InvalidOperationException("侧墙备选构件缺少可写入几何：" + item.RenderKey);
                            DirectShape shape = DirectShape.CreateElement(doc, categoryId);
                            shape.ApplicationId = OwnerApplicationId;
                            shape.ApplicationDataId = prefix + "|Item" + (++index).ToString("D3") +
                                                      "|" + HashShort(item.RenderKey);
                            DirectShapeOptions options = shape.GetOptions();
                            options.ReferencingOption = DirectShapeReferencingOption.NotReferenceable;
                            shape.SetOptions(options);
                            shape.SetShape(geometry);
                            MaintenanceParameterService.ApplyToDirectShape(shape, values);
                            MaintenanceVisualizationService.WriteReviewTrace(
                                shape,
                                item.EvidenceFingerprint,
                                item.ApprovalReviewer,
                                item.ApprovalNote,
                                item.ApprovedAtUtc);
                            newIds.Add(shape.Id);
                        }
                    }

                    bool created;
                    targetView = MaintenanceManagedViewService.GetOrCreate3D(
                        doc,
                        sourceView,
                        alternative.ViewName,
                        ViewOwnerId,
                        BuildViewIdentity(alternative.GroupKey, alternative.DeviceNo,
                            alternative.SchemeNo, alternative.TargetKey),
                        MaintenanceManagedViewPurpose.FormalReachability,
                        out created);
                    if (created) stats.CreatedViewCount++;
                    if (targetView.IsLocked) targetView.Unlock();
                    MaintenanceParameterService.EnsureViewPresentation(doc, targetView);
                    BoundingBoxXYZ section = BuildSectionBox(doc, newIds);
                    if (section != null)
                    {
                        targetView.IsSectionBoxActive = true;
                        targetView.SetSectionBox(section);
                    }
                    List<DirectShape> allMaintenanceShapes = GetMaintenanceShapes(doc)
                        .ToList();
                    List<ElementId> groupMaintenanceIds = allMaintenanceShapes
                        .Where(x => string.Equals(
                            ReadText(x, MaintenanceParameterService.CeilingGroupGuid),
                            alternative.GroupKey,
                            StringComparison.Ordinal))
                        .Select(x => x.Id)
                        .Distinct()
                        .ToList();
                    List<ElementId> otherMaintenanceShapes = allMaintenanceShapes
                        .Select(x => x.Id)
                        .Where(x => !newIds.Contains(x))
                        .Distinct()
                        .ToList();
                    TryUnhide(targetView, newIds);
                    TryHide(targetView, otherMaintenanceShapes);

                    bool overviewCreated;
                    overviewView = MaintenanceManagedViewService.GetOrCreate3D(
                        doc,
                        targetView,
                        MaintenanceManagedViewPolicy.BuildEquipmentOverviewViewName(
                            alternative.GroupKey),
                        MaintenanceManagedViewPolicy.FormalManagedViewOwnerId,
                        MaintenanceManagedViewPolicy
                            .BuildEquipmentOverviewViewIdentity(alternative.GroupKey),
                        MaintenanceManagedViewPurpose.FormalReachability,
                        out overviewCreated);
                    if (overviewCreated) stats.CreatedViewCount++;
                    if (overviewView.IsLocked) overviewView.Unlock();
                    MaintenanceParameterService.EnsureViewPresentation(doc, overviewView);
                    BoundingBoxXYZ overviewSection = BuildSectionBox(
                        doc, groupMaintenanceIds);
                    if (overviewSection != null)
                    {
                        overviewView.IsSectionBoxActive = true;
                        overviewView.SetSectionBox(overviewSection);
                    }
                    TryUnhide(overviewView, groupMaintenanceIds);
                    TryHide(overviewView, allMaintenanceShapes
                        .Select(x => x.Id)
                        .Where(x => !groupMaintenanceIds.Contains(x))
                        .Distinct()
                        .ToList());

                    MaintenanceContextViewSyncResult contextSync =
                        MaintenanceManagedViewService.SynchronizeContextViews(
                            doc, targetView, alternative.GroupKey);
                    if (contextSync.AiViewCreated) stats.CreatedViewCount++;
                    stats.Warnings.AddRange(contextSync.Warnings);
                    if (!reuseFormal)
                    {
                        var visibleViewIds = new List<ElementId>
                        {
                            targetView.Id,
                            overviewView.Id,
                            contextSync.AiView.Id
                        };
                        visibleViewIds.AddRange(
                            contextSync.FloorOverviewViews.Select(x => x.Id));
                        HideFromOtherViews(
                            doc,
                            newIds,
                            visibleViewIds);
                    }
                    else
                        MaintenanceManagedViewService.HideFromOtherDedicatedSchemeViews(
                            doc,
                            newIds,
                            new[] { targetView.Id });

                    JarviTools.Core.TransactionSafety.Commit(tx,
                        "Show maintenance side-wall alternative");
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }

            try
            {
                uidoc.ActiveView = targetView;
                uidoc.RefreshActiveView();
            }
            catch (Exception exception)
            {
                stats.Warnings.Add(
                    "侧墙备选模型/视图已提交，但切换或刷新活动视图失败：" +
                    exception.Message);
            }
            stats.CreatedElementCount = reuseFormal ? 0 : newIds.Count;
            stats.ViewId = targetView.Id.Value;
            stats.ViewName = targetView.Name;
            stats.OverviewViewId = overviewView == null
                ? 0L
                : overviewView.Id.Value;
            stats.OverviewViewName = overviewView == null
                ? string.Empty
                : overviewView.Name;
            return stats;
        }

        private static List<DirectShape> FindSameTargetFormalShapes(
            Document doc,
            MaintenanceWallAlternativeResult alternative)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(DirectShape))
                .Cast<DirectShape>()
                .Where(x => string.Equals(x.ApplicationId,
                    MaintenanceVisualizationService.OwnerApplicationId,
                    StringComparison.Ordinal))
                .Where(x => string.Equals(ReadText(x,
                    MaintenanceParameterService.CeilingGroupGuid),
                    alternative.GroupKey,
                    StringComparison.Ordinal))
                .Where(x => MaintenanceVisualizationService
                                .IsStableGroupBackground(x) ||
                            MaintenanceVisualizationService
                                .HasStableTargetIdentity(x, alternative.TargetKey))
                .ToList();
        }

        private static bool HasCompleteFormalRoleSet(IEnumerable<DirectShape> shapes)
        {
            var roles = new HashSet<string>((shapes ?? Enumerable.Empty<DirectShape>())
                .Select(x => ReadText(x, MaintenanceParameterService.ElementRoleGuid)),
                StringComparer.Ordinal);
            return roles.Contains("侧墙检修门") &&
                   (roles.Contains("人字梯") || roles.Contains("一字梯")) &&
                   roles.Contains("入口转身区") &&
                   roles.Contains("维修路线") &&
                   roles.Contains("人员通行包络") &&
                   roles.Contains("设备检修区") &&
                   roles.Contains("维修对象") &&
                   roles.Contains("虚拟边界墙");
        }

        internal static ClearStats Clear(
            UIApplication uiapp,
            string groupKey,
            string targetKey,
            string deviceNo,
            int schemeNo)
        {
            if (uiapp == null || uiapp.ActiveUIDocument == null)
                throw new InvalidOperationException("Revit 没有活动文档。");
            if (string.IsNullOrWhiteSpace(groupKey))
                throw new ArgumentException("groupKey 不能为空。", "groupKey");
            if (string.IsNullOrWhiteSpace(targetKey))
                throw new ArgumentException("targetKey 不能为空。", "targetKey");
            if (string.IsNullOrWhiteSpace(deviceNo))
                throw new ArgumentException("deviceNo 不能为空。", "deviceNo");
            if (schemeNo < 1) throw new ArgumentOutOfRangeException("schemeNo");

            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;
            if (doc.IsModifiable)
                throw new InvalidOperationException("侧墙备选清理需要自主事务。");
            string prefix = BuildDataPrefix(groupKey.Trim(), NormalizeDeviceNo(deviceNo),
                schemeNo, targetKey, true);
            List<ElementId> shapeIds = GetOwnedShapes(doc)
                .Where(x => Safe(x.ApplicationDataId).StartsWith(prefix,
                    StringComparison.Ordinal))
                .Select(x => x.Id)
                .Distinct()
                .ToList();
            string identity = BuildViewIdentity(groupKey.Trim(), NormalizeDeviceNo(deviceNo),
                schemeNo, targetKey);
            List<View3D> ownedViews = MaintenanceManagedViewService.GetOwned3DViews(
                doc, ViewOwnerId, new[] { identity }).ToList();
            var stats = new ClearStats();
            var deletableViews = new List<View3D>();
            foreach (View3D view in ownedViews)
            {
                string reason;
                if (MaintenanceManagedViewService.CanSafelyDelete(doc, view, out reason))
                    deletableViews.Add(view);
                else
                    stats.Warnings.Add(view.Name + "：" + reason);
            }
            MoveAwayFromDeletedView(uidoc, deletableViews.Select(x => x.Id));
            using (var tx = new Transaction(doc, "OpenRevit 侧墙备选定向清理"))
            {
                tx.Start();
                try
                {
                    List<ElementId> ids = shapeIds
                        .Concat(deletableViews.Select(x => x.Id))
                        .Distinct()
                        .ToList();
                    if (ids.Count > 0) doc.Delete(ids);
                    JarviTools.Core.TransactionSafety.Commit(tx,
                        "Clear maintenance side-wall alternative");
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }
            try { uidoc.RefreshActiveView(); }
            catch (Exception exception)
            {
                stats.Warnings.Add(
                    "侧墙备选删除已提交，但活动视图刷新失败：" + exception.Message);
            }
            stats.DeletedShapeCount = shapeIds.Count;
            stats.DeletedViewCount = deletableViews.Count;
            return stats;
        }

        private static MaintenanceParameterValues BuildValues(
            MaintenanceRenderItem item,
            SavedUserState saved,
            MaintenanceWallAlternativeResult alternative,
            ShowStats stats)
        {
            MaintenanceInstanceParameters source = item.Parameters ??
                new MaintenanceInstanceParameters();
            string conclusion = null;
            if (item.Role == MaintenanceComponentRole.ServicePocket)
            {
                conclusion = string.IsNullOrWhiteSpace(source.MaintenanceConclusion)
                    ? (item.Decision == MaintenanceDecision.Pass
                        ? MaintenanceParameterService.ConclusionMaintainable
                        : (item.Decision == MaintenanceDecision.Fail
                            ? MaintenanceParameterService.ConclusionNotMaintainable
                            : MaintenanceParameterService.ConclusionPending))
                    : source.MaintenanceConclusion;
                bool hasManualConclusion = saved != null &&
                    (string.Equals(saved.Conclusion,
                        MaintenanceParameterService.ConclusionMaintainable,
                        StringComparison.Ordinal) ||
                     string.Equals(saved.Conclusion,
                        MaintenanceParameterService.ConclusionNotMaintainable,
                        StringComparison.Ordinal));
                bool inheritConclusion = hasManualConclusion &&
                    string.Equals(conclusion, MaintenanceParameterService.ConclusionPending,
                        StringComparison.Ordinal) &&
                    MaintenanceManualStatePolicy.ShouldInheritConclusion(
                        saved.EvidenceFingerprint,
                        item.EvidenceFingerprint,
                        saved.DecisionNote,
                        source.DecisionReason);
                if (inheritConclusion)
                    conclusion = saved.Conclusion;
                else if (hasManualConclusion && stats != null)
                {
                    string warning = "设备" + alternative.DeviceNo +
                        " 侧墙备选的旧人工维修结论因证据或算法理由已变化而未继承；专业备注已保留。";
                    if (!stats.Warnings.Contains(warning)) stats.Warnings.Add(warning);
                }
            }
            return new MaintenanceParameterValues
            {
                ElementName = source.ComponentName,
                CeilingGroup = source.CeilingGroup,
                EntryGroup = source.EntryGroup,
                ElementRole = source.ComponentRole,
                MaintenanceTarget = source.MaintenanceTarget,
                MaintenanceConclusion = conclusion,
                DecisionNote = source.DecisionReason,
                ProfessionalNote = MaintenanceManualStatePolicy.ResolveProfessionalNote(
                    source.ProfessionalNote,
                    saved == null ? null : saved.ProfessionalNote)
            };
        }

        private static Dictionary<string, SavedUserState> CaptureUserState(
            IEnumerable<DirectShape> shapes)
        {
            var output = new Dictionary<string, SavedUserState>(StringComparer.Ordinal);
            foreach (DirectShape shape in shapes ?? Enumerable.Empty<DirectShape>())
            {
                string role = ReadText(shape, MaintenanceParameterService.ElementRoleGuid);
                if (string.IsNullOrWhiteSpace(role)) continue;
                output[role] = new SavedUserState
                {
                    Conclusion = ReadText(shape,
                        MaintenanceParameterService.MaintenanceConclusionGuid),
                    ProfessionalNote = ReadText(shape,
                        MaintenanceParameterService.ProfessionalNoteGuid),
                    DecisionNote = ReadText(shape,
                        MaintenanceParameterService.DecisionNoteGuid),
                    EvidenceFingerprint =
                        MaintenanceVisualizationService.ReadReviewTrace(shape)
                            .EvidenceFingerprint
                };
            }
            return output;
        }

        private static IEnumerable<DirectShape> GetOwnedShapes(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(DirectShape))
                .Cast<DirectShape>()
                .Where(x => string.Equals(x.ApplicationId, OwnerApplicationId,
                    StringComparison.Ordinal));
        }

        private static IEnumerable<DirectShape> GetMaintenanceShapes(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(DirectShape))
                .Cast<DirectShape>()
                .Where(x => string.Equals(x.ApplicationId, OwnerApplicationId,
                                StringComparison.Ordinal) ||
                            string.Equals(x.ApplicationId,
                                MaintenanceVisualizationService.OwnerApplicationId,
                                StringComparison.Ordinal) ||
                            string.Equals(x.ApplicationId,
                                MaintenanceHandReachVisualizationService.FormalApplicationId,
                                StringComparison.Ordinal));
        }

        private static bool SameDevice(
            DirectShape shape,
            string groupKey,
            string deviceNo)
        {
            return string.Equals(ReadText(shape,
                       MaintenanceParameterService.CeilingGroupGuid),
                       groupKey,
                       StringComparison.Ordinal) &&
                   string.Equals(ReadDeviceNo(shape), NormalizeDeviceNo(deviceNo),
                       StringComparison.Ordinal);
        }

        private static void ResolveStableDeviceNumbers(
            MaintenanceAnalysisResult result,
            IList<DirectShape> shapes)
        {
            var existing = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DirectShape shape in shapes)
            {
                string group = ReadText(shape,
                    MaintenanceParameterService.CeilingGroupGuid);
                string targetHash = ReadTargetHash(shape);
                string device = ReadDeviceNo(shape);
                if (string.IsNullOrWhiteSpace(group) ||
                    string.IsNullOrWhiteSpace(targetHash) ||
                    string.IsNullOrWhiteSpace(device)) continue;
                string key = MaintenanceDeviceIdentityPolicy.BuildBucketedTargetKey(
                    group, targetHash);
                if (!existing.ContainsKey(key)) existing[key] = device;
            }
            var requested = new Dictionary<string, string>(StringComparer.Ordinal);
            List<string> keys = result.WallAlternatives
                .Where(x => x != null)
                .OrderBy(x => x.GroupKey, StringComparer.Ordinal)
                .ThenBy(x => x.TargetKey, StringComparer.Ordinal)
                .Select(x => MaintenanceDeviceIdentityPolicy.BuildBucketedTargetKey(
                    x.GroupKey, HashShort(x.TargetKey)))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            foreach (MaintenanceWallAlternativeResult alternative in
                result.WallAlternatives.Where(x => x != null))
                requested[MaintenanceDeviceIdentityPolicy.BuildBucketedTargetKey(
                    alternative.GroupKey, HashShort(alternative.TargetKey))] =
                    alternative.DeviceNo;
            Dictionary<string, string> resolved =
                MaintenanceDeviceIdentityPolicy.ResolveDeviceNumbers(
                    keys, existing, requested);
            foreach (MaintenanceWallAlternativeResult alternative in
                result.WallAlternatives.Where(x => x != null))
            {
                string key = MaintenanceDeviceIdentityPolicy.BuildBucketedTargetKey(
                    alternative.GroupKey, HashShort(alternative.TargetKey));
                alternative.DeviceNo = resolved[key];
            }
        }

        private static string ReadDeviceNo(DirectShape shape)
        {
            foreach (string part in Safe(shape.ApplicationDataId).Split('|'))
                if (part.StartsWith("Device", StringComparison.Ordinal))
                    return NormalizeDeviceNo(part.Substring("Device".Length));
            string entry = ReadText(shape, MaintenanceParameterService.EntryGroupGuid);
            int deviceAt = Safe(entry).IndexOf("设备", StringComparison.Ordinal);
            int schemeAt = Safe(entry).IndexOf("-方案", StringComparison.Ordinal);
            return deviceAt >= 0 && schemeAt > deviceAt + 2
                ? NormalizeDeviceNo(entry.Substring(deviceAt + 2,
                    schemeAt - deviceAt - 2))
                : string.Empty;
        }

        private static int ReadSchemeNo(DirectShape shape)
        {
            foreach (string part in Safe(shape.ApplicationDataId).Split('|'))
            {
                if (!part.StartsWith("Scheme", StringComparison.Ordinal)) continue;
                int value;
                string digits = new string(part.Substring("Scheme".Length)
                    .TakeWhile(char.IsDigit).ToArray());
                return int.TryParse(digits, out value) ? value : 0;
            }
            string entry = ReadText(shape, MaintenanceParameterService.EntryGroupGuid);
            int at = Safe(entry).IndexOf("-方案", StringComparison.Ordinal);
            if (at < 0) return 0;
            int parsed;
            string suffix = entry.Substring(at + 3);
            string number = new string(suffix.TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(number, out parsed) ? parsed : 0;
        }

        private static string ReadTargetHash(DirectShape shape)
        {
            foreach (string part in Safe(shape == null ? null : shape.ApplicationDataId)
                .Split('|'))
                if (part.StartsWith("Target", StringComparison.Ordinal))
                    return part.Substring("Target".Length);
            return string.Empty;
        }

        private static string BuildDataPrefix(
            string groupKey,
            string deviceNo,
            int schemeNo,
            string targetKey,
            bool includeScheme)
        {
            string prefix = NormalizePart(groupKey) + "|Device" +
                            NormalizeDeviceNo(deviceNo) + "|";
            if (includeScheme) prefix += "Scheme" + schemeNo.ToString("D2") + "|";
            return prefix + "Target" + HashShort(targetKey);
        }

        private static string BuildViewIdentity(
            string groupKey,
            string deviceNo,
            int schemeNo,
            string targetKey)
        {
            return "wall-alternative|" + NormalizePart(groupKey) + "|Device" +
                   NormalizeDeviceNo(deviceNo) + "|Scheme" + schemeNo.ToString("D2") +
                   "|Target" + HashShort(targetKey);
        }

        private static string HashShort(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes), 0, 8)
                    .Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string NormalizePart(string value)
        {
            return Safe(value).Replace('|', '/').Replace('\r', ' ')
                .Replace('\n', ' ').Trim();
        }

        private static string NormalizeDeviceNo(string value)
        {
            int number;
            string safe = Safe(value).Trim();
            return int.TryParse(safe, out number) && number >= 0
                ? number.ToString("D2")
                : safe;
        }

        private static string ReadText(Element element, Guid guid)
        {
            Parameter parameter = element == null ? null : element.get_Parameter(guid);
            return parameter == null ? string.Empty : parameter.AsString() ?? string.Empty;
        }

        private static string Safe(string value)
        {
            return value ?? string.Empty;
        }

        private static BoundingBoxXYZ BuildSectionBox(
            Document doc,
            IEnumerable<ElementId> ids)
        {
            XYZ min = null;
            XYZ max = null;
            foreach (ElementId id in ids ?? Enumerable.Empty<ElementId>())
            {
                Element element = doc.GetElement(id);
                BoundingBoxXYZ box = element == null ? null : element.get_BoundingBox(null);
                if (box == null) continue;
                XYZ boxMin = box.Transform.OfPoint(box.Min);
                XYZ boxMax = box.Transform.OfPoint(box.Max);
                min = min == null
                    ? boxMin
                    : new XYZ(Math.Min(min.X, boxMin.X), Math.Min(min.Y, boxMin.Y),
                        Math.Min(min.Z, boxMin.Z));
                max = max == null
                    ? boxMax
                    : new XYZ(Math.Max(max.X, boxMax.X), Math.Max(max.Y, boxMax.Y),
                        Math.Max(max.Z, boxMax.Z));
            }
            if (min == null || max == null) return null;
            double margin = 1000.0 / 304.8;
            return new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = min - new XYZ(margin, margin, margin),
                Max = max + new XYZ(margin, margin, margin)
            };
        }

        private static void HideFromOtherViews(
            Document doc,
            IEnumerable<ElementId> ids,
            IEnumerable<ElementId> visibleViewIds)
        {
            var visible = new HashSet<ElementId>(
                visibleViewIds ?? Enumerable.Empty<ElementId>());
            foreach (View view in new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Where(x => !x.IsTemplate && !visible.Contains(x.Id)))
                TryHide(view, ids);
        }

        private static void TryHide(View view, IEnumerable<ElementId> ids)
        {
            List<ElementId> visible = (ids ?? Enumerable.Empty<ElementId>())
                .Distinct()
                .Where(x =>
                {
                    try
                    {
                        Element element = view.Document.GetElement(x);
                        return element != null && element.CanBeHidden(view) &&
                               !element.IsHidden(view);
                    }
                    catch { return false; }
                }).ToList();
            if (visible.Count == 0) return;
            try { view.HideElements(visible); }
            catch { }
        }

        private static void TryUnhide(View view, IEnumerable<ElementId> ids)
        {
            List<ElementId> hidden = (ids ?? Enumerable.Empty<ElementId>())
                .Distinct()
                .Where(x =>
                {
                    try
                    {
                        Element element = view.Document.GetElement(x);
                        return element != null && element.IsHidden(view);
                    }
                    catch { return false; }
                }).ToList();
            if (hidden.Count == 0) return;
            try { view.UnhideElements(hidden); }
            catch { }
        }

        private static void MoveAwayFromDeletedView(
            UIDocument uidoc,
            IEnumerable<ElementId> deletedViewIds)
        {
            var deleted = new HashSet<long>((deletedViewIds ??
                Enumerable.Empty<ElementId>()).Select(x => x.Value));
            if (!deleted.Contains(uidoc.ActiveView.Id.Value)) return;
            View3D fallback = new FilteredElementCollector(uidoc.Document)
                .OfClass(typeof(View3D)).Cast<View3D>()
                .Where(x => !x.IsTemplate && !deleted.Contains(x.Id.Value))
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            if (fallback == null)
                throw new InvalidOperationException(
                    "当前打开的是待删除侧墙备选视图，且没有其他三维视图可切换。");
            uidoc.ActiveView = fallback;
        }
    }
}

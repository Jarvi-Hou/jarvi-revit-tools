using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace JarviTools.Commands.MaintenanceReachability
{
    internal sealed class MaintenanceContextViewSyncResult
    {
        public View3D AiView;
        public bool AiViewCreated;
        public readonly List<View3D> FloorOverviewViews = new List<View3D>();
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Stable ownership for plugin-created views.  A display name is never ownership
    /// evidence: user-created or legacy unmarked views with the same name are left
    /// untouched and a safe suffixed name is created instead.
    /// </summary>
    internal static class MaintenanceManagedViewService
    {
        internal const string AiInternalViewOwnerId =
            "JarviTools.Maintenance.AiInternal.View.v1";
        private static readonly Guid OwnedViewSchemaGuid =
            new Guid("895c392f-0544-4c44-9dd1-6c7655f95816");
        private const string OwnerField = "Owner";
        private const string IdentityField = "Identity";

        internal static View3D GetOrCreate3D(
            Document doc,
            View3D source,
            string desiredName,
            string owner,
            string identity,
            MaintenanceManagedViewPurpose purpose,
            out bool created)
        {
            if (doc == null) throw new ArgumentNullException("doc");
            if (source == null || source.IsTemplate)
                throw new InvalidOperationException("请先打开一个普通三维视图作为视图生成基准。");
            if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("owner 不能为空。", "owner");
            if (string.IsNullOrWhiteSpace(identity)) throw new ArgumentException("identity 不能为空。", "identity");

            View3D existing = GetOwned3DViews(doc, owner, new[] { identity })
                .OrderBy(x => x.Id.Value)
                .FirstOrDefault();
            if (existing != null)
            {
                EnsurePurposeType(doc, existing, source, purpose);
                created = false;
                return existing;
            }

            var occupied = new HashSet<string>(
                new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(x => !x.IsTemplate)
                    .Select(x => x.Name),
                StringComparer.Ordinal);
            string name = MaintenanceManagedViewPolicy.BuildAvailableName(desiredName, occupied);
            View3D view = doc.GetElement(source.Duplicate(ViewDuplicateOption.Duplicate)) as View3D;
            if (view == null) throw new InvalidOperationException("无法复制三维视图。");
            view.Name = name;
            EnsurePurposeType(doc, view, source, purpose);
            MarkOwned(view, owner, identity);
            created = true;
            return view;
        }

        /// <summary>
        /// Project Browser grouping is driven by the 3D ViewFamilyType in this
        /// project.  Never let a managed view inherit the source view's generic
        /// type (for example {三维}) by accident.
        /// </summary>
        internal static void EnsurePurposeType(
            Document doc,
            View3D view,
            View3D source,
            MaintenanceManagedViewPurpose purpose)
        {
            if (doc == null) throw new ArgumentNullException("doc");
            if (view == null || view.IsTemplate)
                throw new InvalidOperationException("只能给普通三维视图设置维修可达视图类别。");

            string desiredTypeName =
                MaintenanceManagedViewPolicy.ResolveViewFamilyTypeName(purpose);
            ViewFamilyType targetType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .Where(x => x.ViewFamily == ViewFamily.ThreeDimensional)
                .FirstOrDefault(x => string.Equals(
                    x.Name, desiredTypeName, StringComparison.Ordinal));
            if (targetType == null)
            {
                ViewFamilyType basis = doc.GetElement(view.GetTypeId()) as ViewFamilyType;
                if (basis == null && source != null)
                    basis = doc.GetElement(source.GetTypeId()) as ViewFamilyType;
                if (basis == null || basis.ViewFamily != ViewFamily.ThreeDimensional)
                    throw new InvalidOperationException(
                        "无法取得三维视图类型，不能建立“" + desiredTypeName + "”分类。");
                targetType = basis.Duplicate(desiredTypeName) as ViewFamilyType;
                if (targetType == null)
                    throw new InvalidOperationException(
                        "无法建立三维视图类型“" + desiredTypeName + "”。");
            }

            if (view.GetTypeId() != targetType.Id)
                view.ChangeTypeId(targetType.Id);
        }

        internal static IEnumerable<View3D> GetOwned3DViews(
            Document doc,
            string owner,
            IEnumerable<string> identities)
        {
            if (doc == null) return Enumerable.Empty<View3D>();
            var expected = new HashSet<string>(
                (identities ?? Enumerable.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.Ordinal);
            if (expected.Count == 0) return Enumerable.Empty<View3D>();
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(x => !x.IsTemplate)
                .Where(x => expected.Any(identity => IsOwned(x, owner, identity)))
                .ToList();
        }

        internal static bool IsOwned(View view, string owner, string identity)
        {
            if (view == null) return false;
            string actualOwner;
            string actualIdentity;
            ReadOwnership(view, out actualOwner, out actualIdentity);
            return MaintenanceManagedViewPolicy.IsExactOwner(
                owner, identity, actualOwner, actualIdentity);
        }

        internal static bool CanSafelyDelete(Document doc, View view, out string reason)
        {
            reason = string.Empty;
            if (doc == null || view == null)
            {
                reason = "视图不存在。";
                return false;
            }
            bool placed;
            try
            {
                placed = new FilteredElementCollector(doc)
                    .OfClass(typeof(Viewport))
                    .Cast<Viewport>()
                    .Any(x => x.ViewId == view.Id);
            }
            catch
            {
                reason = "无法验证视图是否已放图纸，已保留。";
                return false;
            }
            if (placed)
            {
                reason = "视图已放置到图纸，已保留。";
                return false;
            }
            bool hasUserAnnotations;
            try
            {
                hasUserAnnotations = new FilteredElementCollector(doc, view.Id)
                    .WhereElementIsNotElementType()
                    .Any(x => x != null && x.Category != null &&
                              x.Category.CategoryType == CategoryType.Annotation);
            }
            catch
            {
                reason = "无法验证视图内人工注释，已保留。";
                return false;
            }
            if (hasUserAnnotations)
            {
                reason = "视图含人工注释，已保留。";
                return false;
            }
            return true;
        }

        internal static int HideFromOtherDedicatedSchemeViews(
            Document doc,
            IEnumerable<ElementId> elementIds,
            IEnumerable<ElementId> excludedViewIds)
        {
            if (doc == null) throw new ArgumentNullException("doc");
            List<ElementId> ids = (elementIds ?? Enumerable.Empty<ElementId>())
                .Where(x => x != null && x != ElementId.InvalidElementId)
                .Distinct()
                .ToList();
            if (ids.Count == 0) return 0;
            var excluded = new HashSet<ElementId>(
                (excludedViewIds ?? Enumerable.Empty<ElementId>())
                    .Where(x => x != null && x != ElementId.InvalidElementId));
            int changedViewCount = 0;
            foreach (View3D view in new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(x => !x.IsTemplate && !excluded.Contains(x.Id))
                .OrderBy(x => x.Id.Value))
            {
                string owner;
                string identity;
                ReadOwnership(view, out owner, out identity);
                if (!MaintenanceManagedViewPolicy.IsDedicatedSchemeView(
                        owner,
                        identity))
                    continue;
                List<ElementId> visible = ids
                    .Where(x =>
                    {
                        try
                        {
                            Element element = doc.GetElement(x);
                            return element != null &&
                                   element.CanBeHidden(view) &&
                                   !element.IsHidden(view);
                        }
                        catch
                        {
                            return false;
                        }
                    })
                    .ToList();
                if (visible.Count == 0) continue;
                view.HideElements(visible);
                changedViewCount++;
            }
            return changedViewCount;
        }

        /// <summary>
        /// Keeps the two cross-workflow context views in sync after any maintenance
        /// visualization: one AI-only view per annotated ceiling group, plus the
        /// existing user-owned whole-floor overview.  The whole-floor view is never
        /// created or renamed here.
        /// </summary>
        internal static MaintenanceContextViewSyncResult SynchronizeContextViews(
            Document doc,
            View3D source,
            string groupKey)
        {
            if (doc == null) throw new ArgumentNullException("doc");
            if (source == null || source.IsTemplate)
                throw new InvalidOperationException("缺少可复制的普通三维视图。");
            string group = string.IsNullOrWhiteSpace(groupKey)
                ? string.Empty
                : groupKey.Trim();
            if (group.Length == 0)
                throw new InvalidOperationException("天花分组为空，不能同步维修可达视图。");

            var result = new MaintenanceContextViewSyncResult();
            List<DirectShape> allMaintenanceShapes = new FilteredElementCollector(doc)
                .OfClass(typeof(DirectShape))
                .Cast<DirectShape>()
                .Where(IsMaintenanceShape)
                .ToList();
            List<ElementId> groupIds = allMaintenanceShapes
                .Where(x => string.Equals(ReadCeilingGroup(x), group,
                    StringComparison.Ordinal))
                .Select(x => x.Id)
                .Distinct()
                .ToList();
            List<ElementId> otherMaintenanceIds = allMaintenanceShapes
                .Select(x => x.Id)
                .Where(x => !groupIds.Contains(x))
                .Distinct()
                .ToList();

            string aiName = MaintenanceManagedViewPolicy.BuildAiAnalysisViewName(group);
            result.AiView = GetOrCreate3D(
                doc,
                source,
                aiName,
                AiInternalViewOwnerId,
                "maintenance-ai|" + group,
                MaintenanceManagedViewPurpose.AiInternalAnalysis,
                out result.AiViewCreated);
            if (result.AiView.IsLocked) result.AiView.Unlock();
            EnsureGenericModelsVisible(result.AiView);
            try
            {
                MaintenanceParameterService.EnsureViewPresentation(doc, result.AiView);
            }
            catch (Exception ex)
            {
                result.Warnings.Add(result.AiView.Name +
                                    " 视图演示资源失败：" + ex.Message);
            }
            BoundingBoxXYZ aiSection = BuildSectionBox(doc, groupIds);
            if (aiSection != null)
            {
                result.AiView.IsSectionBoxActive = true;
                result.AiView.SetSectionBox(aiSection);
            }
            TryUnhide(result.AiView, groupIds);
            TryHide(result.AiView, otherMaintenanceIds);

            string floorName =
                MaintenanceManagedViewPolicy.BuildFloorOverviewViewName(group);
            if (string.IsNullOrWhiteSpace(floorName))
            {
                result.Warnings.Add("无法从天花分组“" + group +
                                    "”识别楼层，未同步整层可达视图。");
                return result;
            }

            result.FloorOverviewViews.AddRange(new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(x => !x.IsTemplate && string.Equals(
                    x.Name, floorName, StringComparison.Ordinal))
                .OrderBy(x => x.Id.Value));
            if (result.FloorOverviewViews.Count == 0)
            {
                result.Warnings.Add("未找到“" + floorName +
                                    "”，本次未同步整层可达视图。");
                return result;
            }

            string floorKey = MaintenanceManagedViewPolicy.ResolveFloorKey(group);
            List<DirectShape> formalShapes = allMaintenanceShapes
                .Where(IsFormalMaintenanceShape)
                .ToList();
            List<ElementId> floorFormalIds = formalShapes
                .Where(x => MaintenanceManagedViewPolicy.GroupBelongsToFloor(
                    ReadCeilingGroup(x), floorKey))
                .Select(x => x.Id)
                .Distinct()
                .ToList();
            List<ElementId> otherFormalIds = formalShapes
                .Select(x => x.Id)
                .Where(x => !floorFormalIds.Contains(x))
                .Distinct()
                .ToList();
            foreach (View3D floorOverview in result.FloorOverviewViews)
            {
                EnsureGenericModelsVisible(floorOverview);
                try
                {
                    MaintenanceParameterService.EnsureViewPresentation(
                        doc, floorOverview);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add(floorOverview.Name +
                                        " 视图演示资源失败：" + ex.Message);
                }
                TryUnhide(floorOverview, floorFormalIds);
                TryHide(floorOverview, otherFormalIds);
            }
            return result;
        }

        private static bool IsMaintenanceShape(DirectShape shape)
        {
            return shape != null &&
                   (string.Equals(shape.ApplicationId,
                        MaintenanceVisualizationService.OwnerApplicationId,
                        StringComparison.Ordinal) ||
                    string.Equals(shape.ApplicationId,
                        MaintenanceHandReachVisualizationService.FormalApplicationId,
                        StringComparison.Ordinal) ||
                    string.Equals(shape.ApplicationId,
                        MaintenanceWallAlternativeVisualizationService.OwnerApplicationId,
                        StringComparison.Ordinal));
        }

        private static bool IsFormalMaintenanceShape(DirectShape shape)
        {
            return shape != null &&
                   MaintenanceManagedViewPolicy.IsFormalMaintenanceApplicationId(
                       shape.ApplicationId);
        }

        private static string ReadCeilingGroup(DirectShape shape)
        {
            if (shape == null) return string.Empty;
            try
            {
                Parameter parameter = shape.get_Parameter(
                    MaintenanceParameterService.CeilingGroupGuid);
                return parameter == null || !parameter.HasValue
                    ? string.Empty
                    : (parameter.AsString() ?? string.Empty).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void EnsureGenericModelsVisible(View view)
        {
            if (view == null) return;
            try
            {
                var categoryId = new ElementId(
                    (long)BuiltInCategory.OST_GenericModel);
                if (view.GetCategoryHidden(categoryId))
                    view.SetCategoryHidden(categoryId, false);
            }
            catch { }
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
                        return element != null && element.CanBeHidden(view) &&
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

        private static BoundingBoxXYZ BuildSectionBox(
            Document doc,
            IEnumerable<ElementId> ids)
        {
            double minX = double.MaxValue, minY = double.MaxValue,
                minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue,
                maxZ = double.MinValue;
            foreach (ElementId id in ids ?? Enumerable.Empty<ElementId>())
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
            if (minX == double.MaxValue) return null;
            double margin = 2000.0 / 304.8;
            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX - margin, minY - margin, minZ - margin),
                Max = new XYZ(maxX + margin, maxY + margin, maxZ + margin)
            };
        }

        private static void MarkOwned(View view, string owner, string identity)
        {
            Schema schema = GetOrCreateSchema();
            var entity = new Entity(schema);
            entity.Set(schema.GetField(OwnerField), owner);
            entity.Set(schema.GetField(IdentityField), identity);
            view.SetEntity(entity);
        }

        private static void ReadOwnership(
            View view,
            out string owner,
            out string identity)
        {
            owner = string.Empty;
            identity = string.Empty;
            Schema schema = Schema.Lookup(OwnedViewSchemaGuid);
            if (schema == null || view == null) return;
            Entity entity = view.GetEntity(schema);
            if (entity == null || !entity.IsValid()) return;
            owner = entity.Get<string>(schema.GetField(OwnerField)) ?? string.Empty;
            identity = entity.Get<string>(schema.GetField(IdentityField)) ?? string.Empty;
        }

        private static Schema GetOrCreateSchema()
        {
            Schema schema = Schema.Lookup(OwnedViewSchemaGuid);
            if (schema != null) return schema;
            var builder = new SchemaBuilder(OwnedViewSchemaGuid);
            builder.SetSchemaName("OpenRevitMaintenanceManagedViewV1");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(OwnerField, typeof(string));
            builder.AddSimpleField(IdentityField, typeof(string));
            return builder.Finish();
        }
    }
}

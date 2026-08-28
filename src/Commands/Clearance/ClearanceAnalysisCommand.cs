using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using JarviTools.Commands.Common;
using JarviTools.Mcp.Server;

namespace JarviTools.Commands.Clearance
{
    /// <summary>
    /// 构件级净高分析：真实几何最低点 → 净高（双标高基准）→
    /// 复制专用视图逐构件着色 + 文字图例 + 结果清单（定位/导出）。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ClearanceAnalysisCommand : IExternalCommand
    {
        private const double M_TO_FT = 1 / 0.3048;
        private const string VIEW_PREFIX = "净高检查-";
        private static readonly Guid OwnedViewSchemaGuid =
            new Guid("aef277ca-4780-4e47-a64b-19f1bb1e08f5");

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("净高分析", "请先打开一个 Revit 项目文件。");
                return Result.Cancelled;
            }
            Document doc = uidoc.Document;

            try
            {
                var plan = doc.ActiveView as ViewPlan;
                if (plan == null || plan.GenLevel == null)
                {
                    TaskDialog.Show("净高分析",
                        "请在楼层平面视图中运行此命令。\n当前视图：" + doc.ActiveView.ViewType);
                    return Result.Cancelled;
                }
                if (IsOwnedResultView(plan))
                {
                    TaskDialog.Show("净高分析",
                        "当前视图是上次分析生成的结果视图，直接在它上面重跑会残留旧颜色。\n\n" +
                        "请切换回原始的楼层平面视图后再运行。");
                    return Result.Cancelled;
                }

                // ---- 标高清单 ----
                // 一律用 ProjectElevation（相对项目内部原点），与几何点坐标同基准；
                // Level.Elevation 在共享坐标项目里可能返回测量点高程，会导致净高整体错位。
                List<Level> allLevels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.ProjectElevation).ToList();
                if (allLevels.Count == 0)
                {
                    TaskDialog.Show("净高分析", "项目中没有标高，无法分析。");
                    return Result.Cancelled;
                }
                var choices = allLevels.Select(l => new LevelChoice
                {
                    Name = l.Name,
                    ElevationM = l.ProjectElevation * 0.3048
                }).ToList();

                // ---- 设置窗 ----
                int preselected = uidoc.Selection.GetElementIds().Count;
                var settings = JsonSettingsStore.Load<ClearanceSettings>("Clearance");
                ScopeMode scopeMode;
                LevelChoice primaryChoice, compareChoice;
                using (var form = new ClearanceSettingsForm(settings, choices, plan.GenLevel.Name, preselected))
                {
                    if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                        return Result.Cancelled;
                    form.Apply(settings);
                    scopeMode = form.Scope;
                    primaryChoice = form.PrimaryLevel;
                    compareChoice = form.CompareLevel;
                }
                JsonSettingsStore.Save("Clearance", settings);

                // ---- 解析基准/类别/范围 ----
                Level primary = allLevels.First(l => l.Name == primaryChoice.Name);
                Level compare = compareChoice == null
                    ? null
                    : allLevels.FirstOrDefault(l => l.Name == compareChoice.Name);

                var scope = new ClearanceScope
                {
                    DatumZFt = primary.ProjectElevation + settings.OffsetMm / 1000.0 * M_TO_FT,
                    CompareZFt = compare != null ? compare.ProjectElevation : double.NaN,
                    IncludeLinks = settings.IncludeLinks,
                    ExcludeRisers = settings.ExcludeRisers,
                    Categories = ResolveCategories(settings)
                };
                // 上边界取"高于主基准至少 1.5m 的下一个标高"：
                // 成对的建筑/结构标高（相差几厘米）不算下一层，避免 Z 带被压缩成几厘米导致漏检。
                Level next = allLevels.FirstOrDefault(
                    l => l.ProjectElevation > primary.ProjectElevation + 1.5 * M_TO_FT);
                if (next != null) scope.ZTopFt = next.ProjectElevation - 0.003;
                double topWorldZFt = next != null
                    ? next.ProjectElevation - 0.003
                    : primary.ProjectElevation + 6.0 * M_TO_FT;

                if (scopeMode == ScopeMode.CurrentSelection)
                {
                    scope.SelectionIds = new HashSet<ElementId>(uidoc.Selection.GetElementIds());
                }
                else if (scopeMode == ScopeMode.PickRectangle)
                {
                    try
                    {
                        PickedBox pick = uidoc.Selection.PickBox(PickBoxStyle.Crossing, "框选净高分析区域");
                        scope.HasRect = true;
                        scope.MinXFt = Math.Min(pick.Min.X, pick.Max.X);
                        scope.MaxXFt = Math.Max(pick.Min.X, pick.Max.X);
                        scope.MinYFt = Math.Min(pick.Min.Y, pick.Max.Y);
                        scope.MaxYFt = Math.Max(pick.Min.Y, pick.Max.Y);
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        return Result.Cancelled;
                    }
                }

                // ---- 计算（带进度/取消） ----
                // 计算期间禁用 Revit 主窗口：ProgressForm 的 DoEvents 会泵消息，
                // 若不禁用，用户可在 API 上下文内切视图/触发命令，造成不受支持的重入。
                List<ClearanceResult> results;
                int skippedRisers = 0;
                var gridLocator = new GridLocator(doc);
                IntPtr revitWnd = commandData.Application.MainWindowHandle;
                var progressForm = new ProgressForm("净高分析");
                try
                {
                    NativeMethods.EnableWindow(revitWnd, false);
                    progressForm.Show(new Win32Window(revitWnd));
                    results = ClearanceCalculator.Run(doc, scope, gridLocator,
                        progressForm.Report, () => progressForm.Cancelled, out skippedRisers);
                }
                finally
                {
                    NativeMethods.EnableWindow(revitWnd, true);
                    progressForm.Close();
                    progressForm.Dispose();
                }
                if (results == null) return Result.Cancelled; // 用户取消
                if (results.Count == 0)
                {
                    TaskDialog.Show("净高分析", "范围内没有找到符合条件的构件。\n\n" +
                        "请检查：类别勾选、标高基准是否正确，以及构件是否在该层高度范围内。");
                    return Result.Cancelled;
                }

                // ---- 着色视图 + 图例 ----
                List<ColorBand> bands = settings.Bands.OrderByDescending(b => b.MinM).ToList();
                View colorView;
                using (var tx = new Transaction(doc, "净高分析着色"))
                {
                    tx.Start();
                    if (settings.DeleteOldViews) DeleteOldViews(doc);
                    colorView = BuildColorView(doc, plan, primary.Name, results, bands, topWorldZFt);
                    JarviTools.Core.TransactionSafety.Commit(tx, "Clearance visualization");
                }

                uidoc.ActiveView = colorView;
                uidoc.RefreshActiveView();

                // ---- 汇总 + 清单 ----
                string summary = BuildSummary(results, compare != null, skippedRisers, gridLocator.Count);
                using (var form = new ClearanceResultForm(results, bands, summary, r => ZoomTo(uidoc, r)))
                {
                    form.ShowDialog();
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Logger.Error("ClearanceAnalysisCommand failed", ex);
                TaskDialog.Show("净高分析 — 错误", "执行失败：\n" + ex.Message);
                return Result.Failed;
            }
        }

        // ==================== 类别解析 ====================

        private static List<BuiltInCategory> ResolveCategories(ClearanceSettings s)
        {
            var all = CategoryOption.All();
            IEnumerable<string> names = (s.EnabledCategories != null && s.EnabledCategories.Count > 0)
                ? s.EnabledCategories
                : all.Where(c => c.DefaultOn).Select(c => c.BicName);
            var result = new List<BuiltInCategory>();
            foreach (string n in names)
            {
                BuiltInCategory bic;
                if (Enum.TryParse(n, out bic)) result.Add(bic);
            }
            if (result.Count == 0)
                throw new InvalidOperationException("没有勾选任何分析类别。");
            return result;
        }

        // ==================== 视图着色 ====================

        private static void DeleteOldViews(Document doc)
        {
            // 只删除带有本插件所有权标记的视图。名称前缀仅供人阅读，
            // 不能作为删除证据，否则会伤到用户自己创建的同名平面视图。
            var old = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate
                            && IsOwnedResultView(v)
                            && CanSafelyDeleteOwnedView(doc, v)
                            && v.Id != doc.ActiveView.Id)
                .Select(v => v.Id)
                .ToList();
            if (old.Count > 0) doc.Delete(old);
        }

        private static View BuildColorView(Document doc, ViewPlan basePlan, string levelName,
                                           List<ClearanceResult> results, List<ColorBand> bands,
                                           double topWorldZFt)
        {
            ElementId newId = basePlan.Duplicate(ViewDuplicateOption.Duplicate);
            var view = (View)doc.GetElement(newId);
            view.ViewTemplateId = ElementId.InvalidElementId; // 摘掉视图样板，确保着色可见
            view.Name = UniqueViewName(doc, VIEW_PREFIX + levelName + "-" + DateTime.Now.ToString("HHmm"));
            MarkOwnedResultView(view);

            // 抬高视图范围的剖切面到层顶附近：吊顶高度的风管水管在普通平面里
            // 常在剖切面之上不显示，不调高的话着色视图会"看起来是空的"。
            var vp = view as ViewPlan;
            if (vp != null && vp.GenLevel != null)
            {
                try
                {
                    PlanViewRange range = vp.GetViewRange();
                    double topOff = topWorldZFt - vp.GenLevel.ProjectElevation;
                    if (topOff > 0.3)
                    {
                        range.SetLevelId(PlanViewPlane.TopClipPlane, vp.GenLevel.Id);
                        range.SetOffset(PlanViewPlane.TopClipPlane, topOff);
                        range.SetLevelId(PlanViewPlane.CutPlane, vp.GenLevel.Id);
                        range.SetOffset(PlanViewPlane.CutPlane, topOff - 0.1);
                        vp.SetViewRange(range);
                    }
                }
                catch { /* 个别视图范围约束冲突时保持原范围，不影响主功能 */ }
            }

            FillPatternElement solidFill = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);

            foreach (ClearanceResult r in results)
            {
                if (r.IsLinked) continue; // Revit 不支持对链接内单个构件着色（清单里仍可查/可定位）
                var color = BandRevitColor(bands, r.ClearPrimaryM);
                var ogs = new OverrideGraphicSettings();
                ogs.SetProjectionLineColor(color);
                ogs.SetCutLineColor(color);
                if (solidFill != null)
                {
                    ogs.SetSurfaceForegroundPatternId(solidFill.Id);
                    ogs.SetSurfaceForegroundPatternColor(color);
                    ogs.SetCutForegroundPatternId(solidFill.Id);
                    ogs.SetCutForegroundPatternColor(color);
                }
                try { view.SetElementOverrides(r.Id, ogs); }
                catch { /* 个别元素不支持图形替换，忽略 */ }
            }

            PlaceLegend(doc, view, results, bands);
            return view;
        }

        private static Schema GetOrCreateOwnedViewSchema()
        {
            var schema = Schema.Lookup(OwnedViewSchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(OwnedViewSchemaGuid);
            builder.SetSchemaName("OpenRevitClearanceResultView");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("Owner", typeof(string));
            return builder.Finish();
        }

        private static void MarkOwnedResultView(View view)
        {
            var schema = GetOrCreateOwnedViewSchema();
            var entity = new Entity(schema);
            entity.Set(schema.GetField("Owner"), "OpenRevit.Clearance.v1");
            view.SetEntity(entity);
        }

        private static bool IsOwnedResultView(View view)
        {
            if (view == null) return false;
            var schema = Schema.Lookup(OwnedViewSchemaGuid);
            if (schema == null) return false;

            var entity = view.GetEntity(schema);
            return entity.IsValid()
                   && string.Equals(entity.Get<string>(schema.GetField("Owner")),
                       "OpenRevit.Clearance.v1", StringComparison.Ordinal);
        }

        private static bool CanSafelyDeleteOwnedView(Document doc, View view)
        {
            if (doc == null || view == null) return false;

            // A view placed on a sheet is a delivery artifact and must never be removed implicitly.
            var isPlaced = new FilteredElementCollector(doc)
                .OfClass(typeof(Viewport)).Cast<Viewport>()
                .Any(vp => vp.ViewId == view.Id);
            if (isPlaced) return false;

            // The generated legend is one TextNote. Additional annotations/detail items indicate
            // user work; preserve the complete view instead of trying to guess ownership per item.
            var annotationCount = new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType()
                .Count(e => e != null && e.Category != null
                            && e.Category.CategoryType == CategoryType.Annotation);
            return annotationCount <= 1;
        }

        private static Autodesk.Revit.DB.Color BandRevitColor(List<ColorBand> bands, double clearM)
        {
            foreach (ColorBand b in bands)
                if (clearM >= b.MinM)
                    return new Autodesk.Revit.DB.Color((byte)b.R, (byte)b.G, (byte)b.B);
            ColorBand last = bands[bands.Count - 1];
            return new Autodesk.Revit.DB.Color((byte)last.R, (byte)last.G, (byte)last.B);
        }

        private static void PlaceLegend(Document doc, View view,
                                        List<ClearanceResult> results, List<ColorBand> bands)
        {
            ElementId typeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            if (typeId == null || typeId == ElementId.InvalidElementId) return;

            var sb = new StringBuilder();
            sb.AppendLine("净高图例（主基准）");
            for (int i = 0; i < bands.Count; i++)
            {
                ColorBand b = bands[i];
                int count = results.Count(r => InBand(bands, i, r.ClearPrimaryM));
                string range;
                if (b.MinM <= ColorBand.BOTTOM)
                    range = i > 0 ? "＜" + bands[i - 1].MinM.ToString("0.0#") + "m" : "全部";
                else
                    range = "≥" + b.MinM.ToString("0.0#") + "m";
                sb.AppendLine(ColorName(b) + " " + range + "：" + count + " 个");
            }
            ClearanceResult lowest = results[0]; // 已按净高升序
            sb.Append("最低：").Append(lowest.Category).Append(" ")
              .Append(lowest.GridLabel).Append("  净高 ")
              .Append(lowest.ClearPrimaryM.ToString("0.00")).Append("m");

            // 放在分析范围左下角外侧一点
            double minX = results.Min(r => r.LowestPoint.X) - 3 * M_TO_FT;
            double minY = results.Min(r => r.LowestPoint.Y) - 3 * M_TO_FT;
            try
            {
                TextNote.Create(doc, view.Id, new XYZ(minX, minY, 0), sb.ToString(), typeId);
            }
            catch { /* 图例创建失败不影响主功能 */ }
        }

        private static bool InBand(List<ColorBand> bands, int index, double clearM)
        {
            for (int i = 0; i < bands.Count; i++)
                if (clearM >= bands[i].MinM)
                    return i == index;
            return index == bands.Count - 1;
        }

        private static string ColorName(ColorBand b)
        {
            if (b.G > 140 && b.R < 100) return "【绿】";
            if (b.R > 200 && b.G > 170) return "【黄】";
            if (b.R > 200 && b.G > 90 && b.G <= 170) return "【橙】";
            if (b.R > 180 && b.G <= 90) return "【红】";
            return "【色】";
        }

        // ==================== 汇总 / 定位 ====================

        private static string BuildSummary(List<ClearanceResult> results, bool hasCompare,
                                           int skippedRisers, int gridCount)
        {
            ClearanceResult lowest = results[0];
            string s = "共 " + results.Count + " 个构件；最低：" + lowest.Category + " " +
                   lowest.TypeLabel + "，净高 " + lowest.ClearPrimaryM.ToString("0.00") +
                   "m，位置 " + lowest.GridLabel +
                   (hasCompare ? "（清单含对比基准列）" : "");
            if (skippedRisers > 0)
                s += "  已排除竖直立管 " + skippedRisers + " 段";
            if (gridCount == 0)
                s += "  ⚠ 未找到轴网（结构链接可能未载入），位置列显示坐标";
            return s;
        }

        private static void ZoomTo(UIDocument uidoc, ClearanceResult r)
        {
            if (!r.IsLinked)
            {
                var ids = new List<ElementId> { r.Id };
                uidoc.Selection.SetElementIds(ids);
                uidoc.ShowElements(ids);
            }
            else
            {
                // 链接构件无法选中，按其最低点位置缩放当前视图
                UIView uiview = uidoc.GetOpenUIViews()
                    .FirstOrDefault(v => v.ViewId == uidoc.ActiveView.Id);
                if (uiview == null) return;
                double d = 3 * M_TO_FT;
                XYZ p = r.LowestPoint;
                uiview.ZoomAndCenterRectangle(
                    new XYZ(p.X - d, p.Y - d, p.Z - d),
                    new XYZ(p.X + d, p.Y + d, p.Z + d));
            }
        }

        private static string UniqueViewName(Document doc, string desired)
        {
            string name = desired;
            int suffix = 2;
            while (new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                       .Any(v => string.Equals(v.Name, name, StringComparison.Ordinal)))
                name = desired + " (" + (suffix++) + ")";
            return name;
        }
    }
}

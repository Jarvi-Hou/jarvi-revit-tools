using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace JarviTools.Commands.Plenum
{
    internal sealed class PlenumVisualizationStats
    {
        public int CreatedElementCount;
        public int RenderedCellCount;
        public int RenderedFreeSegmentCount;
        public int SkippedBoundaryCellCount;
        public int FailedGeometryCellCount;
        public int DeletedPreviousElementCount;
        public long TargetViewId;
        public string TargetViewName;
    }

    internal static class PlenumVisualizationService
    {
        internal const string OwnerApplicationId = "JarviTools.PlenumSpaceField.v1";
        private const double MmPerFoot = 304.8;
        private const double QuantizedUnitsPerFoot = MmPerFoot * 1000.0;

        private sealed class VisualBand
        {
            public string Key;
            public Color Color;
            public int Transparency;
        }

        private sealed class VisualSegment
        {
            public int TraceId;
            public PlenumCellResult Cell;
            public string BandKey;
            public string StateKey;
            public double StartHeightFt;
            public double HeightFt;
            public bool IsFree;
            public PlenumMergeCell MergeCell;
        }

        private sealed class RenderableRegion
        {
            public string BandKey;
            public Solid Solid;
            public List<VisualSegment> Segments = new List<VisualSegment>();
        }

        public static PlenumVisualizationStats Show(UIApplication uiapp, PlenumAnalysisResult result)
        {
            if (uiapp == null || uiapp.ActiveUIDocument == null)
                throw new InvalidOperationException("Revit 没有活动文档。");
            if (result == null)
                throw new InvalidOperationException("没有可显示的负空间分析结果。");

            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;
            EnsureSameDocument(doc, result);
            View3D view = doc.GetElement(new ElementId(result.ViewId)) as View3D;
            if (view == null || view.IsTemplate)
                throw new InvalidOperationException("原分析三维视图已不存在，请重新分析。");
            var genericModelCategoryId = new ElementId((long)BuiltInCategory.OST_GenericModel);
            if (view.GetCategoryHidden(genericModelCategoryId))
                throw new InvalidOperationException("原分析视图已隐藏“常规模型”类别，色块无法可见。请先显示该类别。");
            if (doc.IsModifiable)
                throw new InvalidOperationException("负空间可视化需要自主事务，不能在其他事务内调用。");

            Dictionary<string, VisualBand> bands = CreateBands();
            var stats = new PlenumVisualizationStats
            {
                TargetViewId = view.Id.Value,
                TargetViewName = view.Name
            };
            var segments = new List<VisualSegment>();
            int nextTraceId = 1;
            foreach (PlenumCellResult cell in result.Cells)
            {
                // V1 不用矩形越过吊顶实际边界，边界非整单元保守略过。
                if (cell.CoverageFraction < 0.999)
                {
                    stats.SkippedBoundaryCellCount++;
                    continue;
                }

                if (cell.IsUnknown || cell.IsMixed)
                {
                    string bandKey = cell.IsUnknown ? "Unknown" : "MixedAtLeaf";
                    VisualSegment segment = CreateSegment(
                        nextTraceId++, cell, bandKey, bandKey,
                        0.0, result.SearchTopZFt - result.CeilingTopZFt, false);
                    if (segment != null)
                    {
                        segments.Add(segment);
                    }
                    else
                    {
                        stats.FailedGeometryCellCount++;
                    }
                }
                else
                {
                    foreach (PlenumVerticalInterval interval in cell.VerticalIntervals
                                 .Where(x => string.Equals(x.State, "Free", StringComparison.Ordinal)
                                             && x.ThicknessFt > 1e-9))
                    {
                        string key = PlenumCellResult.HeightBand(interval.ThicknessFt * MmPerFoot);
                        VisualSegment segment = CreateSegment(
                            nextTraceId++, cell, key, "Free",
                            interval.StartHeightFt, interval.ThicknessFt, true);
                        if (segment != null)
                        {
                            segments.Add(segment);
                        }
                        else
                        {
                            stats.FailedGeometryCellCount++;
                        }
                    }
                }
            }

            var renderedCellIds = new HashSet<int>();
            List<RenderableRegion> renderableRegions = BuildRenderableRegions(
                segments, stats, renderedCellIds);
            stats.RenderedCellCount = renderedCellIds.Count;

            if (stats.SkippedBoundaryCellCount > 0)
            {
                string warning = stats.SkippedBoundaryCellCount +
                    " 个吊顶边界非整单元未生成 DirectShape，统计仍保留。";
                if (!result.Warnings.Contains(warning)) result.Warnings.Add(warning);
            }
            int mixedCount = result.Cells.Count(x => !x.IsUnknown && x.IsMixed);
            if (mixedCount > 0)
            {
                string warning = mixedCount +
                    " 个 MixedAtLeaf 单元以紫色不确定柱显示，不生成均质 Free 体块。";
                if (!result.Warnings.Contains(warning)) result.Warnings.Add(warning);
            }
            const string persistentWarning =
                "V1 色块是可撤销、可清除的模型级 DirectShape，不是纯临时视图图形；保存前可调用 clear_plenum_analysis 清除。";
            if (!result.Warnings.Contains(persistentWarning)) result.Warnings.Add(persistentWarning);

            var tx = new Transaction(doc, "装饰负空间三维可视化");
            try
            {
                tx.Start();
                stats.DeletedPreviousElementCount = ClearOwnedCore(doc);
                FillPatternElement solidFill = new FilteredElementCollector(doc)
                    .OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(x => x.GetFillPattern().IsSolidFill);

                foreach (IGrouping<string, RenderableRegion> grouping in renderableRegions
                             .GroupBy(x => x.BandKey, StringComparer.Ordinal)
                             .OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    VisualBand band = bands[grouping.Key];
                    int regionIndex = 0;
                    foreach (RenderableRegion region in grouping)
                    {
                        DirectShape shape = DirectShape.CreateElement(
                            doc, new ElementId((long)BuiltInCategory.OST_GenericModel));
                        shape.ApplicationId = OwnerApplicationId;
                        shape.ApplicationDataId = result.AnalysisId + ":" + band.Key + ":"
                                                  + regionIndex++ + ":"
                                                  + region.Segments.Min(x => x.Cell.CellId) + ":"
                                                  + region.Segments.Count;
                        DirectShapeOptions options = shape.GetOptions();
                        options.ReferencingOption = DirectShapeReferencingOption.NotReferenceable;
                        shape.SetOptions(options);
                        shape.SetShape(new List<GeometryObject> { region.Solid });

                        var graphics = new OverrideGraphicSettings();
                        graphics.SetProjectionLineColor(band.Color);
                        graphics.SetCutLineColor(band.Color);
                        graphics.SetSurfaceTransparency(band.Transparency);
                        if (solidFill != null)
                        {
                            graphics.SetSurfaceForegroundPatternId(solidFill.Id);
                            graphics.SetSurfaceForegroundPatternColor(band.Color);
                            graphics.SetCutForegroundPatternId(solidFill.Id);
                            graphics.SetCutForegroundPatternColor(band.Color);
                        }
                        view.SetElementOverrides(shape.Id, graphics);
                        stats.CreatedElementCount++;
                    }
                }

                JarviTools.Core.TransactionSafety.Commit(tx, "Create plenum visualization");
                result.DirectShapeCount = stats.CreatedElementCount;
                result.RenderedCellCount = stats.RenderedCellCount;
                result.RenderedFreeSegmentCount = stats.RenderedFreeSegmentCount;
                result.SkippedBoundaryCellCount = stats.SkippedBoundaryCellCount;
                result.FailedGeometryCellCount = stats.FailedGeometryCellCount;
                result.DeletedPreviousShapeCount = stats.DeletedPreviousElementCount;
                if (doc.ActiveView.Id == view.Id) uidoc.RefreshActiveView();
                return stats;
            }
            catch
            {
                if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                throw;
            }
        }

        public static int Clear(UIApplication uiapp, bool clearStoredAnalysis)
        {
            if (uiapp == null || uiapp.ActiveUIDocument == null)
                throw new InvalidOperationException("Revit 没有活动文档。");
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;
            if (doc.IsModifiable)
                throw new InvalidOperationException("清除负空间可视化需要自主事务，不能在其他事务内调用。");
            var tx = new Transaction(doc, "清除装饰负空间可视化");
            try
            {
                tx.Start();
                int deleted = ClearOwnedCore(doc);
                JarviTools.Core.TransactionSafety.Commit(tx, "Clear plenum visualization");
                if (clearStoredAnalysis) PlenumAnalysisStore.Clear(doc);
                uidoc.RefreshActiveView();
                return deleted;
            }
            catch
            {
                if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                throw;
            }
        }

        private static void EnsureSameDocument(Document doc, PlenumAnalysisResult result)
        {
            bool samePath = string.IsNullOrWhiteSpace(result.DocumentPath)
                || string.IsNullOrWhiteSpace(doc.PathName)
                || string.Equals(result.DocumentPath, doc.PathName, StringComparison.OrdinalIgnoreCase);
            if (!samePath || !string.Equals(result.DocumentTitle, doc.Title, StringComparison.Ordinal))
                throw new InvalidOperationException("上次分析结果不属于当前 Revit 文档，请重新分析。");
        }

        private static int ClearOwnedCore(Document doc)
        {
            List<ElementId> ids = new FilteredElementCollector(doc)
                .OfClass(typeof(DirectShape))
                .Cast<DirectShape>()
                .Where(x => string.Equals(x.ApplicationId, OwnerApplicationId, StringComparison.Ordinal))
                .Select(x => x.Id)
                .ToList();
            if (ids.Count > 0) doc.Delete(ids);
            return ids.Count;
        }

        private static Solid CreateCellSolid(PlenumCellResult cell, double startHeightFt, double requestedHeightFt)
        {
            return PlenumCellSolidFactory.Create(
                cell.P00,
                cell.P10,
                cell.P11,
                cell.P01,
                startHeightFt,
                requestedHeightFt);
        }

        private static VisualSegment CreateSegment(
            int traceId,
            PlenumCellResult cell,
            string bandKey,
            string stateKey,
            double startHeightFt,
            double requestedHeightFt,
            bool isFree)
        {
            if (cell == null) return null;
            double heightFt = NormalizeHeight(requestedHeightFt);
            double startFt = Math.Max(0.0, startHeightFt);
            long[] baseZ = new[] { cell.P00, cell.P10, cell.P11, cell.P01 }
                .Select(x => QuantizeFeet(x.Z)).ToArray();
            bool horizontalAtTolerance = baseZ.All(x => x == baseZ[0]);

            var segment = new VisualSegment
            {
                TraceId = traceId,
                Cell = cell,
                BandKey = bandKey,
                StateKey = stateKey,
                StartHeightFt = startFt,
                HeightFt = heightFt,
                IsFree = isFree
            };
            if (!horizontalAtTolerance) return segment;

            long bottomZ = QuantizeFeet(cell.P00.Z + startFt);
            long topZ = QuantizeFeet(cell.P00.Z + startFt + heightFt);
            long uMin = QuantizeFeet(cell.UMin);
            long uMax = QuantizeFeet(cell.UMax);
            long vMin = QuantizeFeet(cell.VMin);
            long vMax = QuantizeFeet(cell.VMax);
            if (uMax <= uMin || vMax <= vMin || topZ <= bottomZ) return null;

            segment.MergeCell = new PlenumMergeCell
            {
                TraceId = traceId,
                PlaneKey = CreatePlaneKey(cell),
                StateKey = stateKey + "\u001f" + bandKey,
                BottomZ = bottomZ,
                TopZ = topZ,
                UMin = uMin,
                UMax = uMax,
                VMin = vMin,
                VMax = vMax
            };
            return segment;
        }

        private static List<RenderableRegion> BuildRenderableRegions(
            List<VisualSegment> segments,
            PlenumVisualizationStats stats,
            HashSet<int> renderedCellIds)
        {
            var result = new List<RenderableRegion>();
            var segmentByTrace = segments.ToDictionary(x => x.TraceId);
            List<PlenumMergeCell> mergeCells = segments
                .Where(x => x.MergeCell != null)
                .Select(x => x.MergeCell)
                .ToList();

            foreach (PlenumMergedRegion merged in PlenumRegionMerger.Merge(mergeCells))
            {
                List<VisualSegment> sources = merged.TraceIds
                    .Select(x => segmentByTrace[x]).ToList();
                Solid solid = CreateRegionSolid(merged, sources[0].Cell);
                if (solid == null)
                {
                    stats.FailedGeometryCellCount += sources.Count;
                    continue;
                }
                AddSuccessfulRegion(result, sources[0].BandKey, solid, sources, stats, renderedCellIds);
            }

            // Non-horizontal cells cannot satisfy the strict equal-world-Z merge rule.
            // Preserve their previous one-cell visualization without joining them.
            foreach (VisualSegment segment in segments.Where(x => x.MergeCell == null))
            {
                Solid solid = CreateCellSolid(segment.Cell, segment.StartHeightFt, segment.HeightFt);
                if (solid == null)
                {
                    stats.FailedGeometryCellCount++;
                    continue;
                }
                AddSuccessfulRegion(result, segment.BandKey, solid,
                    new List<VisualSegment> { segment }, stats, renderedCellIds);
            }
            return result;
        }

        private static void AddSuccessfulRegion(
            ICollection<RenderableRegion> target,
            string bandKey,
            Solid solid,
            List<VisualSegment> segments,
            PlenumVisualizationStats stats,
            HashSet<int> renderedCellIds)
        {
            target.Add(new RenderableRegion { BandKey = bandKey, Solid = solid, Segments = segments });
            foreach (VisualSegment segment in segments)
            {
                renderedCellIds.Add(segment.Cell.CellId);
                if (segment.IsFree) stats.RenderedFreeSegmentCount++;
            }
        }

        private static Solid CreateRegionSolid(PlenumMergedRegion region, PlenumCellResult referenceCell)
        {
            try
            {
                var loops = new List<CurveLoop>();
                foreach (PlenumRegionLoop sourceLoop in region.Loops
                             .OrderByDescending(x => Math.Abs(x.SignedArea)))
                {
                    if (sourceLoop.Points.Count < 3) return null;
                    var points = sourceLoop.Points
                        .Select(x => PointFromUv(referenceCell, x, region.BottomZ))
                        .ToList();
                    var loop = new CurveLoop();
                    for (int i = 0; i < points.Count; i++)
                        loop.Append(Line.CreateBound(points[i], points[(i + 1) % points.Count]));
                    loops.Add(loop);
                }

                double heightFt = (region.TopZ - region.BottomZ) / QuantizedUnitsPerFoot;
                return GeometryCreationUtilities.CreateExtrusionGeometry(loops, XYZ.BasisZ, heightFt);
            }
            catch
            {
                return null;
            }
        }

        private static XYZ PointFromUv(
            PlenumCellResult referenceCell,
            PlenumRegionPoint point,
            long bottomZ)
        {
            double u = point.U / QuantizedUnitsPerFoot;
            double v = point.V / QuantizedUnitsPerFoot;
            double uLength = referenceCell.UMax - referenceCell.UMin;
            double vLength = referenceCell.VMax - referenceCell.VMin;
            XYZ uAxis = (referenceCell.P10 - referenceCell.P00).Divide(uLength);
            XYZ vAxis = (referenceCell.P01 - referenceCell.P00).Divide(vLength);
            XYZ world = referenceCell.P00
                        + uAxis.Multiply(u - referenceCell.UMin)
                        + vAxis.Multiply(v - referenceCell.VMin);
            return new XYZ(world.X, world.Y, bottomZ / QuantizedUnitsPerFoot);
        }

        private static string CreatePlaneKey(PlenumCellResult cell)
        {
            XYZ normal = (cell.P10 - cell.P00).CrossProduct(cell.P01 - cell.P00).Normalize();
            if (normal.Z < 0.0
                || (Math.Abs(normal.Z) < 1e-12 && normal.Y < 0.0)
                || (Math.Abs(normal.Z) < 1e-12 && Math.Abs(normal.Y) < 1e-12 && normal.X < 0.0))
                normal = normal.Negate();
            long nx = (long)Math.Round(normal.X * 1000000000.0, MidpointRounding.AwayFromZero);
            long ny = (long)Math.Round(normal.Y * 1000000000.0, MidpointRounding.AwayFromZero);
            long nz = (long)Math.Round(normal.Z * 1000000000.0, MidpointRounding.AwayFromZero);
            long offset = QuantizeFeet(normal.DotProduct(cell.P00));
            return nx + ":" + ny + ":" + nz + ":" + offset;
        }

        private static double NormalizeHeight(double requestedHeightFt)
        {
            double heightFt = Math.Min(requestedHeightFt, 10000.0 / MmPerFoot);
            return heightFt < 1.0 / MmPerFoot ? 1.0 / MmPerFoot : heightFt;
        }

        private static long QuantizeFeet(double valueFt)
        {
            return checked((long)Math.Round(
                valueFt * QuantizedUnitsPerFoot,
                MidpointRounding.AwayFromZero));
        }

        private static Dictionary<string, VisualBand> CreateBands()
        {
            return new[]
            {
                new VisualBand { Key = "0-399mm", Color = new Color(220, 45, 45), Transparency = 55 },
                new VisualBand { Key = "400-699mm", Color = new Color(245, 135, 35), Transparency = 55 },
                new VisualBand { Key = "700-999mm", Color = new Color(230, 205, 35), Transparency = 55 },
                new VisualBand { Key = ">=1000mm", Color = new Color(45, 175, 85), Transparency = 60 },
                new VisualBand { Key = "MixedAtLeaf", Color = new Color(135, 75, 190), Transparency = 78 },
                new VisualBand { Key = "Unknown", Color = new Color(145, 145, 155), Transparency = 82 }
            }.ToDictionary(x => x.Key, StringComparer.Ordinal);
        }

    }
}

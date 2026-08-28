using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace JarviTools.Commands.Plenum
{
    internal enum PlenumState
    {
        Free,
        MepOccupied,
        Structure,
        Unknown
    }

    internal sealed class PlenumAnalysisConfig
    {
        public double BaseCellMm = 200.0;
        public double FeatureCellMm = 40.0;
        public double FeatureSpacingMm = 20.0;
        public double SearchHeightMm = 3000.0;
        public int MaxDepth = 9;
        public int MaxCells = 25000;
        public bool ShowVisualization = true;

        public void Validate()
        {
            if (BaseCellMm < 50.0 || BaseCellMm > 1000.0)
                throw new ArgumentOutOfRangeException("BaseCellMm", "基础单元必须在 50–1000 mm 之间。");
            if (FeatureCellMm < 10.0 || FeatureCellMm > BaseCellMm)
                throw new ArgumentOutOfRangeException("FeatureCellMm", "特征单元必须在 10 mm 与基础单元之间。");
            if (FeatureSpacingMm < 5.0 || FeatureSpacingMm > BaseCellMm)
                throw new ArgumentOutOfRangeException("FeatureSpacingMm", "特征探针间距必须在 5 mm 与基础单元之间。");
            if (SearchHeightMm < 500.0 || SearchHeightMm > 10000.0)
                throw new ArgumentOutOfRangeException("SearchHeightMm", "搜索高度必须在 500–10000 mm 之间。");
            if (MaxDepth < 1 || MaxDepth > 12)
                throw new ArgumentOutOfRangeException("MaxDepth");
            if (MaxCells < 100 || MaxCells > 50000)
                throw new ArgumentOutOfRangeException("MaxCells");
        }
    }

    internal sealed class PlenumSourceRef
    {
        public string SourceType;
        public string DocumentTitle;
        public long? LinkInstanceId;
        public string LinkInstanceUniqueId;
        public long ElementId;
        public string UniqueId;
        public string Category;
        public string Name;
        public string BlockerKind;

        public JObject ToJson()
        {
            return new JObject
            {
                ["sourceType"] = SourceType,
                ["documentTitle"] = DocumentTitle,
                ["linkInstanceId"] = LinkInstanceId.HasValue ? new JValue(LinkInstanceId.Value) : JValue.CreateNull(),
                ["linkInstanceUniqueId"] = string.IsNullOrWhiteSpace(LinkInstanceUniqueId)
                    ? JValue.CreateNull()
                    : new JValue(LinkInstanceUniqueId),
                ["elementId"] = ElementId,
                ["uniqueId"] = UniqueId,
                ["category"] = Category,
                ["name"] = Name,
                ["blockerKind"] = BlockerKind
            };
        }
    }

    internal sealed class PlenumVerticalInterval
    {
        public string State;
        public double StartHeightFt;
        public double EndHeightFt;
        public List<PlenumSourceRef> Sources = new List<PlenumSourceRef>();

        public double ThicknessFt => Math.Max(0.0, EndHeightFt - StartHeightFt);

        public JObject ToJson()
        {
            var sources = new JArray();
            foreach (PlenumSourceRef source in Sources.Take(10)) sources.Add(source.ToJson());
            return new JObject
            {
                ["state"] = State,
                ["fromCeilingMm"] = Math.Round(StartHeightFt * 304.8, 1),
                ["toCeilingMm"] = Math.Round(EndHeightFt * 304.8, 1),
                ["thicknessMm"] = Math.Round(ThicknessFt * 304.8, 1),
                ["sources"] = sources
            };
        }
    }

    internal sealed class PlenumProbeProfile
    {
        public XYZ Point;
        public bool IsFeatureProbe;
        public bool IsUnknown;
        public double ConnectedFreeHeightFt;
        public double StructureBoundaryHeightFt;
        public PlenumSourceRef FirstBlocker;
        public string Warning;
        public List<PlenumVerticalInterval> VerticalIntervals = new List<PlenumVerticalInterval>();
        public List<PlenumVerticalInterval> ObservedEvidenceIntervals = new List<PlenumVerticalInterval>();
        public List<string> UnknownCandidateKeys = new List<string>();

        public JObject ToJson()
        {
            var intervals = new JArray();
            foreach (PlenumVerticalInterval interval in VerticalIntervals) intervals.Add(interval.ToJson());
            var evidence = new JArray();
            foreach (PlenumVerticalInterval interval in ObservedEvidenceIntervals) evidence.Add(interval.ToJson());
            var unknownKeys = new JArray();
            foreach (string key in UnknownCandidateKeys) unknownKeys.Add(key);
            return new JObject
            {
                ["pointMm"] = new JArray(
                    Math.Round(Point.X * 304.8, 1),
                    Math.Round(Point.Y * 304.8, 1),
                    Math.Round(Point.Z * 304.8, 1)),
                ["isFeatureProbe"] = IsFeatureProbe,
                ["state"] = IsUnknown ? "Unknown" : "Known",
                ["ceilingConnectedFreeHeightMm"] = IsUnknown
                    ? JValue.CreateNull()
                    : new JValue(Math.Round(ConnectedFreeHeightFt * 304.8, 1)),
                ["structureBoundaryHeightMm"] = IsUnknown || double.IsNaN(StructureBoundaryHeightFt)
                    ? JValue.CreateNull()
                    : new JValue(Math.Round(StructureBoundaryHeightFt * 304.8, 1)),
                ["verticalIntervals"] = intervals,
                ["observedEvidenceIntervals"] = evidence,
                ["unknownCandidateKeys"] = unknownKeys,
                ["firstBlocker"] = FirstBlocker == null
                    ? (JToken)JValue.CreateNull()
                    : FirstBlocker.ToJson(),
                ["warning"] = string.IsNullOrWhiteSpace(Warning)
                    ? JValue.CreateNull()
                    : new JValue(Warning)
            };
        }
    }

    internal sealed class PlenumCellResult
    {
        public int CellId;
        public int Depth;
        public double UMin;
        public double UMax;
        public double VMin;
        public double VMax;
        public double CoverageFraction;
        public double AreaFt2;
        public double ResolutionMm;
        public XYZ Center;
        public XYZ P00;
        public XYZ P10;
        public XYZ P11;
        public XYZ P01;
        public bool IsFullFootprintInsideCeiling;
        public bool IsUnknown;
        public bool IsMixed;
        public double ConnectedFreeHeightFt;
        public double StructureBoundaryHeightFt;
        public int ProbeCount;
        public int FeatureProbeCount;
        public PlenumSourceRef FirstBlocker;
        public string Warning;
        public List<PlenumVerticalInterval> VerticalIntervals = new List<PlenumVerticalInterval>();
        public List<PlenumProbeProfile> ProbeProfiles = new List<PlenumProbeProfile>();
        public List<string> ProjectionMissCandidateKeys = new List<string>();
        public List<string> GeometryUnverifiedCandidateKeys = new List<string>();
        public List<string> UnverifiedCandidateKeys = new List<string>();
        public List<string> FreeEnvelopeConflictCandidateKeys = new List<string>();
        public List<string> FreeEnvelopeUnverifiedCandidateKeys = new List<string>();

        public double ConnectedFreeHeightMm => ConnectedFreeHeightFt * 304.8;
        public double StructureBoundaryHeightMm => StructureBoundaryHeightFt * 304.8;
        public double AreaM2 => AreaFt2 * 0.09290304;
        public double TotalFreeHeightFt => VerticalIntervals
            .Where(x => string.Equals(x.State, "Free", StringComparison.Ordinal))
            .Sum(x => x.ThicknessFt);

        public JObject ToJson()
        {
            var footprint = new JArray();
            foreach (XYZ p in new[] { P00, P10, P11, P01 })
                footprint.Add(new JArray(
                    Math.Round(p.X * 304.8, 1),
                    Math.Round(p.Y * 304.8, 1),
                    Math.Round(p.Z * 304.8, 1)));
            var intervals = new JArray();
            foreach (PlenumVerticalInterval interval in VerticalIntervals) intervals.Add(interval.ToJson());
            var profiles = new JArray();
            foreach (PlenumProbeProfile profile in ProbeProfiles) profiles.Add(profile.ToJson());
            var projectionMissKeys = new JArray();
            foreach (string key in ProjectionMissCandidateKeys) projectionMissKeys.Add(key);
            var geometryUnverifiedKeys = new JArray();
            foreach (string key in GeometryUnverifiedCandidateKeys) geometryUnverifiedKeys.Add(key);
            var unverifiedKeys = new JArray();
            foreach (string key in UnverifiedCandidateKeys) unverifiedKeys.Add(key);
            var freeEnvelopeConflictKeys = new JArray();
            foreach (string key in FreeEnvelopeConflictCandidateKeys)
                freeEnvelopeConflictKeys.Add(key);
            var freeEnvelopeUnverifiedKeys = new JArray();
            foreach (string key in FreeEnvelopeUnverifiedCandidateKeys)
                freeEnvelopeUnverifiedKeys.Add(key);
            JToken firstBlockerToken = FirstBlocker == null
                ? (JToken)JValue.CreateNull()
                : FirstBlocker.ToJson();

            return new JObject
            {
                ["cellId"] = CellId,
                ["depth"] = Depth,
                ["centerMm"] = new JArray(Center.X * 304.8, Center.Y * 304.8, Center.Z * 304.8),
                ["areaM2"] = Math.Round(AreaM2, 5),
                ["resolutionMm"] = Math.Round(ResolutionMm, 1),
                ["coverageFraction"] = Math.Round(CoverageFraction, 6),
                ["isFullFootprintInsideCeiling"] = IsFullFootprintInsideCeiling,
                ["state"] = IsUnknown
                    ? "Unknown"
                    : (IsMixed ? "MixedAtLeaf" : "SampledCeilingConnectedFreeSpace"),
                ["heightBand"] = IsUnknown ? "Unknown" : HeightBand(ConnectedFreeHeightMm),
                ["isMixed"] = IsMixed,
                ["eligibleForHomogeneousFreeBlockCandidate"] =
                    IsFullFootprintInsideCeiling && !IsUnknown && !IsMixed,
                ["ceilingConnectedFreeHeightMm"] = IsUnknown
                    ? JValue.CreateNull()
                    : new JValue(Math.Round(ConnectedFreeHeightMm, 1)),
                ["structureBoundaryHeightMm"] = IsUnknown || double.IsNaN(StructureBoundaryHeightFt)
                    ? JValue.CreateNull()
                    : new JValue(Math.Round(StructureBoundaryHeightMm, 1)),
                ["probeCount"] = ProbeCount,
                ["featureProbeCount"] = FeatureProbeCount,
                ["footprintMm"] = footprint,
                ["verticalIntervals"] = intervals,
                ["intervalInterpretation"] = IsUnknown
                    ? "Unknown"
                    : (IsMixed
                        ? "SampledConservativeEnvelopeMixedAtLeaf"
                        : "SampledConservativeEnvelope"),
                ["probeProfiles"] = profiles,
                ["projectionMissCandidateKeys"] = projectionMissKeys,
                ["geometryUnverifiedCandidateKeys"] = geometryUnverifiedKeys,
                ["unverifiedCandidateKeys"] = unverifiedKeys,
                ["freeEnvelopeConflictCandidateKeys"] = freeEnvelopeConflictKeys,
                ["freeEnvelopeUnverifiedCandidateKeys"] = freeEnvelopeUnverifiedKeys,
                ["sampledConservativeFreeHeightEstimateMm"] = IsUnknown
                    ? JValue.CreateNull()
                    : new JValue(Math.Round(TotalFreeHeightFt * 304.8, 1)),
                ["firstBlocker"] = firstBlockerToken,
                ["warning"] = string.IsNullOrEmpty(Warning) ? JValue.CreateNull() : new JValue(Warning)
            };
        }

        internal static string HeightBand(double heightMm)
        {
            if (heightMm < 400.0) return "0-399mm";
            if (heightMm < 700.0) return "400-699mm";
            if (heightMm < 1000.0) return "700-999mm";
            return ">=1000mm";
        }
    }

    internal sealed class PlenumAnalysisResult
    {
        public string AnalysisId;
        public string CreatedAtUtc;
        public string DocumentTitle;
        public string DocumentPath;
        public long ViewId;
        public string ViewName;
        public long CeilingId;
        public string CeilingUniqueId;
        public string CeilingName;
        public double CeilingTopZFt;
        public double SearchTopZFt;
        public double CeilingAreaFt2;
        public double CeilingMeshAreaFt2;
        public double CeilingMeshRelativeError;
        public PlenumAnalysisConfig Config;
        public List<PlenumCellResult> Cells = new List<PlenumCellResult>();
        public List<string> LoadedLinks = new List<string>();
        public List<PlenumAnalysisService.CandidateCollectionFailure> CandidateCollectionFailures =
            new List<PlenumAnalysisService.CandidateCollectionFailure>();
        public List<string> Warnings = new List<string>();
        public int CandidateCount;
        public int MepCandidateCount;
        public int StructureCandidateCount;
        public int UnsupportedCandidateCount;
        public int FeatureSeedCount;
        public int UniformProbeCount;
        public int FeatureProbeCount;
        public int CoveredCandidateCount;
        public int ProjectedCandidateCount;
        public int DirectShapeCount;
        public int RenderedCellCount;
        public int RenderedFreeSegmentCount;
        public int SkippedBoundaryCellCount;
        public int FailedGeometryCellCount;
        public int DeletedPreviousShapeCount;
        public long ElapsedMs;

        public JObject ToSummaryJson(bool includePath = false)
        {
            var known = Cells.Where(c => !c.IsUnknown).ToList();
            var unknown = Cells.Where(c => c.IsUnknown).ToList();
            double analyzedArea = Cells.Sum(c => c.AreaM2);
            double unknownArea = unknown.Sum(c => c.AreaM2);
            double connectedVolume = known.Sum(c => c.AreaFt2 * c.ConnectedFreeHeightFt) * 0.028316846592;
            double totalFreeVolume = known.Sum(c => c.AreaFt2 * c.TotalFreeHeightFt) * 0.028316846592;

            var links = new JArray();
            foreach (string link in LoadedLinks) links.Add(link);
            var warnings = new JArray();
            foreach (string warning in Warnings) warnings.Add(warning);

            var heightBands = new JArray();
            foreach (var group in known.GroupBy(c => PlenumCellResult.HeightBand(c.ConnectedFreeHeightMm))
                         .OrderBy(g => g.Min(c => c.ConnectedFreeHeightMm)))
            {
                heightBands.Add(new JObject
                {
                    ["band"] = group.Key,
                    ["cells"] = group.Count(),
                    ["areaM2"] = Math.Round(group.Sum(c => c.AreaM2), 3)
                });
            }
            if (unknown.Count > 0)
            {
                heightBands.Add(new JObject
                {
                    ["band"] = "Unknown",
                    ["cells"] = unknown.Count,
                    ["areaM2"] = Math.Round(unknown.Sum(c => c.AreaM2), 3)
                });
            }

            var blockers = new JArray();
            foreach (var group in known.Where(c => c.FirstBlocker != null)
                         .GroupBy(c => string.Join("|",
                             c.FirstBlocker.SourceType,
                             c.FirstBlocker.LinkInstanceId,
                             c.FirstBlocker.DocumentTitle,
                             c.FirstBlocker.ElementId))
                         .OrderByDescending(g => g.Sum(c => c.AreaM2))
                         .Take(20))
            {
                PlenumSourceRef source = group.First().FirstBlocker;
                blockers.Add(new JObject
                {
                    ["source"] = source.ToJson(),
                    ["cells"] = group.Count(),
                    ["areaM2"] = Math.Round(group.Sum(c => c.AreaM2), 3),
                    ["minConnectedFreeHeightMm"] = Math.Round(group.Min(c => c.ConnectedFreeHeightMm), 1)
                });
            }

            return new JObject
            {
                ["analysisId"] = AnalysisId,
                ["algorithmVersion"] = "plenum-space-field-v1.2-poc",
                ["mixedClassification"] =
                    "MEP profile differences below the minimum structure boundary, plus exact blocker/free-prism conflicts",
                ["createdAtUtc"] = CreatedAtUtc,
                ["documentTitle"] = DocumentTitle,
                ["documentPath"] = includePath && !string.IsNullOrWhiteSpace(DocumentPath)
                    ? (JToken)DocumentPath
                    : JValue.CreateNull(),
                ["pathIncluded"] = includePath,
                ["viewId"] = ViewId,
                ["viewName"] = ViewName,
                ["ceilingElementId"] = CeilingId,
                ["ceilingUniqueId"] = CeilingUniqueId,
                ["ceilingName"] = CeilingName,
                ["ceilingTopElevationMm"] = Math.Round(CeilingTopZFt * 304.8, 1),
                ["ceilingAreaM2"] = Math.Round(CeilingAreaFt2 * 0.09290304, 3),
                ["ceilingTriangulatedAreaM2"] = Math.Round(CeilingMeshAreaFt2 * 0.09290304, 3),
                ["ceilingTriangulationDifferenceRatio"] = Math.Round(CeilingMeshRelativeError, 6),
                ["analyzedAreaM2"] = Math.Round(analyzedArea, 3),
                ["footprintAreaMethod"] =
                    "Revit Face.Triangulate(LOD=1.0) 后逐单元做三角形-矩形裁剪；曲线边界是受校验的网格近似。",
                ["unknownAreaM2"] = Math.Round(unknownArea, 3),
                ["unknownRatio"] = analyzedArea <= 1e-9 ? 1.0 : Math.Round(unknownArea / analyzedArea, 4),
                ["connectedFreeVolumeSampledConservativeEstimateM3"] = Math.Round(connectedVolume, 3),
                ["totalFreeVolumeSampledConservativeEstimateM3"] = Math.Round(totalFreeVolume, 3),
                ["volumeInterpretation"] =
                    "每个自适应单元采用已采探针中的最低结构边界，并对已采机电占用区段取并集；这是当前分辨率下的采样保守估算，不是连续实体布尔体积、数学下界或无碰撞证明。",
                ["ceilingConnectedFreeHeightMm"] = new JObject
                {
                    ["min"] = WeightedPercentile(known, 0.0),
                    ["p10"] = WeightedPercentile(known, 0.10),
                    ["median"] = WeightedPercentile(known, 0.50),
                    ["p90"] = WeightedPercentile(known, 0.90),
                    ["max"] = WeightedPercentile(known, 1.0),
                    ["weighting"] = "ceilingArea",
                    ["cellAggregation"] = "minimumAcrossProbeProfiles",
                    ["interpretation"] = "sampledConservativeEstimateAtCurrentResolution"
                },
                ["cells"] = Cells.Count,
                ["knownCells"] = known.Count,
                ["unknownCells"] = unknown.Count,
                ["mixedCells"] = known.Count(c => c.IsMixed),
                ["mixedAtLeafAreaM2"] = Math.Round(known.Where(c => c.IsMixed).Sum(c => c.AreaM2), 3),
                ["exactEnvelopeConflictCells"] = known.Count(c =>
                    c.FreeEnvelopeConflictCandidateKeys.Count > 0),
                ["exactEnvelopeConflictAreaM2"] = Math.Round(known.Where(c =>
                    c.FreeEnvelopeConflictCandidateKeys.Count > 0).Sum(c => c.AreaM2), 3),
                ["exactEnvelopeUnverifiedCells"] = Cells.Count(c =>
                    c.FreeEnvelopeUnverifiedCandidateKeys.Count > 0),
                ["exactEnvelopeUnverifiedAreaM2"] = Math.Round(Cells.Where(c =>
                    c.FreeEnvelopeUnverifiedCandidateKeys.Count > 0).Sum(c => c.AreaM2), 3),
                ["homogeneousKnownCells"] = known.Count(c => !c.IsMixed),
                ["homogeneousKnownAreaM2"] = Math.Round(known.Where(c => !c.IsMixed).Sum(c => c.AreaM2), 3),
                ["heightBands"] = heightBands,
                ["dominantFirstBlockers"] = blockers,
                ["featureSeedCount"] = FeatureSeedCount,
                ["uniformProbeCount"] = UniformProbeCount,
                ["featureProbeCount"] = FeatureProbeCount,
                ["candidateCount"] = CandidateCount,
                ["mepCandidateCount"] = MepCandidateCount,
                ["structureCandidateCount"] = StructureCandidateCount,
                ["unsupportedCandidateCount"] = UnsupportedCandidateCount,
                ["coveredCandidateCount"] = CoveredCandidateCount,
                ["projectedCandidateCount"] = ProjectedCandidateCount,
                ["baseCellMm"] = Config.BaseCellMm,
                ["featureCellMm"] = Config.FeatureCellMm,
                ["directShapeCount"] = DirectShapeCount,
                ["renderedCellCount"] = RenderedCellCount,
                ["renderedFreeSegmentCount"] = RenderedFreeSegmentCount,
                ["skippedBoundaryCellCount"] = SkippedBoundaryCellCount,
                ["failedGeometryCellCount"] = FailedGeometryCellCount,
                ["deletedPreviousShapeCount"] = DeletedPreviousShapeCount,
                ["elapsedMs"] = ElapsedMs,
                ["loadedLinks"] = links,
                ["coordinateSystem"] = "RevitInternal",
                ["coordinateUnit"] = "mm",
                ["warnings"] = warnings,
                ["snapshotStatement"] = "这是显式计算快照；模型、链接、剖面框或吊顶变化后不会自动更新，必须重新分析。",
                ["scopeStatement"] = "Sampled Free 表示本次已加载且纳入计算的 Revit 实体在已采探针处支持为空闲；它不是探针之间连续体积的无碰撞证明。MixedAtLeaf 和 Unknown 均不得当作均质 Free 体块。"
            };
        }

        public JObject Query(double? maxHeightMm, double? minHeightMm, bool unknownOnly, int offset, int limit)
        {
            IEnumerable<PlenumCellResult> query = Cells;
            if (unknownOnly)
            {
                query = query.Where(c => c.IsUnknown);
            }
            else
            {
                query = query.Where(c => !c.IsUnknown);
                if (maxHeightMm.HasValue) query = query.Where(c => c.ConnectedFreeHeightMm <= maxHeightMm.Value);
                if (minHeightMm.HasValue) query = query.Where(c => c.ConnectedFreeHeightMm >= minHeightMm.Value);
                query = query.OrderBy(c => c.ConnectedFreeHeightMm);
            }

            var matches = query.ToList();
            var selected = matches.Skip(offset).Take(limit).ToList();
            var arr = new JArray();
            foreach (var cell in selected) arr.Add(cell.ToJson());
            return new JObject
            {
                ["analysisId"] = AnalysisId,
                ["totalMatches"] = matches.Count,
                ["offset"] = offset,
                ["returned"] = selected.Count,
                ["hasMore"] = offset + selected.Count < matches.Count,
                ["limit"] = limit,
                ["cells"] = arr
            };
        }

        private static JToken WeightedPercentile(List<PlenumCellResult> cells, double fraction)
        {
            if (cells == null || cells.Count == 0) return JValue.CreateNull();
            var ordered = cells.OrderBy(c => c.ConnectedFreeHeightMm).ToList();
            if (fraction <= 0.0) return new JValue(Math.Round(ordered[0].ConnectedFreeHeightMm, 1));
            if (fraction >= 1.0) return new JValue(Math.Round(ordered[ordered.Count - 1].ConnectedFreeHeightMm, 1));
            double totalArea = ordered.Sum(c => Math.Max(0.0, c.AreaM2));
            if (totalArea <= 1e-12)
                return new JValue(Math.Round(ordered[ordered.Count / 2].ConnectedFreeHeightMm, 1));
            double threshold = totalArea * fraction;
            double cumulative = 0.0;
            foreach (PlenumCellResult cell in ordered)
            {
                cumulative += Math.Max(0.0, cell.AreaM2);
                if (cumulative >= threshold)
                    return new JValue(Math.Round(cell.ConnectedFreeHeightMm, 1));
            }
            return new JValue(Math.Round(ordered[ordered.Count - 1].ConnectedFreeHeightMm, 1));
        }
    }

    internal static class PlenumAnalysisStore
    {
        private static readonly object Gate = new object();
        private static PlenumAnalysisResult _last;
        private static Document _documentLifetimeAnchor;
        private static bool _hasDocument;
        private static int _documentHashCode;
        private static string _documentPath;
        private static string _documentTitle;
        private static string _projectInfoUniqueId;

        public static void Set(Document document, PlenumAnalysisResult result)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (result == null) throw new ArgumentNullException("result");
            lock (Gate)
            {
                CaptureDocumentIdentity(document);
                _last = result;
            }
        }

        public static PlenumAnalysisResult Get(Document document)
        {
            lock (Gate)
            {
                // Revit 可在不同 ExternalEvent 请求间返回同一原生文档的新托管包装器，
                // 因此不能用 WeakReference + ReferenceEquals 绑定快照。
                if (_last == null || !MatchesDocument(document))
                    return null;
                return _last;
            }
        }

        public static void Clear(Document document)
        {
            lock (Gate)
            {
                if (_hasDocument && document != null && !MatchesDocument(document))
                    return;
                _last = null;
                _documentLifetimeAnchor = null;
                _hasDocument = false;
                _documentHashCode = 0;
                _documentPath = null;
                _documentTitle = null;
                _projectInfoUniqueId = null;
            }
        }

        private static void CaptureDocumentIdentity(Document document)
        {
            // 强引用只用于识别文档生命周期；跨 ExternalEvent 的匹配仍用下方复合身份。
            _documentLifetimeAnchor = document;
            _hasDocument = true;
            _documentHashCode = document.GetHashCode();
            _documentPath = document.PathName ?? string.Empty;
            _documentTitle = document.Title ?? string.Empty;
            _projectInfoUniqueId = GetProjectInfoUniqueId(document);
        }

        private static bool MatchesDocument(Document document)
        {
            if (!_hasDocument || _documentLifetimeAnchor == null
                || !_documentLifetimeAnchor.IsValidObject
                || document == null || !document.IsValidObject)
                return false;
            return document.GetHashCode() == _documentHashCode
                   && string.Equals(document.PathName ?? string.Empty, _documentPath,
                       StringComparison.OrdinalIgnoreCase)
                   && string.Equals(document.Title ?? string.Empty, _documentTitle,
                       StringComparison.Ordinal)
                   && string.Equals(GetProjectInfoUniqueId(document), _projectInfoUniqueId,
                       StringComparison.Ordinal);
        }

        private static string GetProjectInfoUniqueId(Document document)
        {
            try
            {
                ProjectInfo projectInfo = document.ProjectInformation;
                return projectInfo == null ? string.Empty : projectInfo.UniqueId ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}

using System;
using System.Collections.Generic;

namespace JarviTools.Commands.MaintenanceReachability
{
    // HandReach 数据模型：全部坐标与尺寸单位为 mm，与 Revit 解耦以便纯逻辑测试。
    // 规则来源：《5B维修可达正式收口提示词》与 2026-08-18 交接备忘/原型代码。

    internal enum HandReachOperationPointStatus
    {
        Missing,
        Provided
    }

    internal enum HandReachAttentionLevel
    {
        Normal,
        High,
        OrangeReview,
        Rejected
    }

    internal enum HandReachDistanceGrade
    {
        AWithin300,
        BWithin400,
        CWithin500,
        RejectedOver500
    }

    internal enum HandReachVerticalGrade
    {
        RecommendedWithin300,
        AttentionWithin500,
        RejectedOver500,
        PersonnelEntryNotDistanceLimited
    }

    internal enum HandReachLadderStatus
    {
        Validated,
        Rejected,
        NotValidatedMissingFloor
    }

    internal sealed class HandReachOptions
    {
        public HandReachOpeningPreference OpeningPreference =
            HandReachOpeningPreference.SideWallOnly;
        public bool StrictCeilingSelection;
        public bool AllowSideWallDistanceOver500Review;
        // 天花口固定 450×450；侧墙 HandReach 默认 450×450，显式 SideWallOnly
        // 复核可使用 400×400 缩小口。400 不能隐式回退到天花。
        public double HatchSizeMm = 450.0;
        public double GridSpacingMm = 40.0;
        public int GridPointsPerAxis = 41;              // 41×41 = 1681，与 8/18 原型一致
        public double DefaultCorridorDiameterMm = 200.0;
        public double[] CorridorTestDiametersMm = { 200.0, 250.0, 300.0, 350.0, 400.0 };
        public double MaxDistanceMm = 500.0;
        public double SideWallReviewMaxDistanceMm = 600.0;
        public double VerticalRecommendedMm = 300.0;
        public double VerticalAttentionMm = 500.0;
        public double OpeningHeightMm = 120.0;          // 检修口实体的垂直厚度（原型值）
        public double ChannelInwardOffsetMm = 100.0;    // 通道圆柱中心线向口内缩进
        public double ChannelCeilingLiftMm = 10.0;      // 通道圆柱起点在天花上方抬高
        public double LadderTopAboveCeilingMm = 80.0;   // 梯顶 = 天花顶 + 80
        public double OperationZoneLengthMm = 1200.0;   // 操作区沿梯向
        public double OperationZoneWidthMm = 2500.0;    // 操作区垂直梯向
        public double CeilingDirectOperatorZoneLengthMm = 600.0; // 天花直接伸手的梯上局部人体站位
        public double CeilingDirectOperatorZoneWidthMm = 600.0;
        public double SideWallOperatorZoneDepthMm = 600.0; // 侧墙口外梯上局部人体站位，不是转身区
        public double SideWallOperatorZoneWidthMm = 600.0;
        public double CeilingPersonnelEntryRiseMm = 850.0; // 人员从天花口向吊顶内探入的验算高度
        public double CeilingPersonnelFinalReachGapMm = 125.0; // 人体包络顶到检修面的优先剩余距离
        public long[] RelevantLinkInstanceIds;

        public HandReachOptions Clone()
        {
            var copy = (HandReachOptions)MemberwiseClone();
            copy.CorridorTestDiametersMm = CorridorTestDiametersMm == null
                ? null
                : (double[])CorridorTestDiametersMm.Clone();
            copy.RelevantLinkInstanceIds = RelevantLinkInstanceIds == null
                ? null
                : (long[])RelevantLinkInstanceIds.Clone();
            return copy;
        }
    }

    internal sealed class HandReachDeviceInput
    {
        public long LinkInstanceId;
        public long ElementId;

        public HandReachDeviceInput(long linkInstanceId, long elementId)
        {
            LinkInstanceId = linkInstanceId;
            ElementId = elementId;
        }
    }

    internal sealed class HandReachTargetInfo
    {
        public string TargetKey;
        public string GroupKey;
        public int SchemeNo;
        public readonly List<int> LegacySchemeNos;
        public string DeviceNo;
        public string EquipmentName;
        public string Mark;
        public long LinkInstanceId;
        public long ElementId;
        public double SupplyDirectionX;
        public double SupplyDirectionY;
        public double ServiceDirectionX;
        public double ServiceDirectionY;
        public double ServiceFaceProxyX;
        public double ServiceFaceProxyY;
        public double ServiceFaceProxyZ;
        public double CeilingTopMm;
        public HandReachOperationPointStatus OperationPointStatus;
        public string OperationPointNote;

        public HandReachTargetInfo()
        {
            TargetKey = string.Empty;
            GroupKey = string.Empty;
            LegacySchemeNos = new List<int>();
            DeviceNo = string.Empty;
            EquipmentName = string.Empty;
            Mark = string.Empty;
            OperationPointStatus = HandReachOperationPointStatus.Missing;
            OperationPointNote = string.Empty;
        }

        public string GetDisplayName()
        {
            if (string.IsNullOrWhiteSpace(Mark)) return EquipmentName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(EquipmentName)) return Mark;
            return EquipmentName + " | " + Mark;
        }
    }

    internal sealed class HandReachSample
    {
        public HandReachOpeningPlaneKind OpeningPlane;
        public string SurfaceKey;
        public int BoundaryLoopIndex;
        public int BoundarySegmentIndex;
        public int BoundarySampleIndex;
        public int Ix;
        public int Iy;
        public double CenterX;
        public double CenterY;
        public double CenterZ;
        public double EdgeX;
        public double EdgeY;
        public double EdgeZ;
        public double OpeningTangentX;
        public double OpeningTangentY;
        public double OpeningInwardX;
        public double OpeningInwardY;
        public double OpeningDepthMm;
        public bool UsesVirtualBoundaryWall;
        public double BoundaryStartX;
        public double BoundaryStartY;
        public double BoundaryEndX;
        public double BoundaryEndY;
        public double VirtualWallBottomMm;
        public double VirtualWallTopMm;
        public double ChannelStartX;
        public double ChannelStartY;
        public double ChannelStartZ;
        public double PersonnelEntryTopZ;
        public double HorizontalMm;
        public double ObliqueMm;
        public double VerticalMm;
        public HandReachDistanceGrade DistanceGrade;
        public bool[] CorridorClear;                   // 与 CorridorTestDiametersMm 对应
        public string LadderDirection;                 // "X" / "Y" / ""（未验证）
        public double LadderCenterX;
        public double LadderCenterY;
        public double LadderAlongX;
        public double LadderAlongY;
        public double LadderFloorMm;
        public bool OperationZoneClear;
        public int ExemptIntersectCount;               // 默认通道与豁免实体的相交数（证据）
        public string BlockerKey;                      // 首个非豁免障碍（空=无）

        public HandReachSample()
        {
            OpeningPlane = HandReachOpeningPlaneKind.CeilingHorizontal;
            SurfaceKey = string.Empty;
            CorridorClear = new bool[0];
            LadderDirection = string.Empty;
        }
    }

    /// <summary>
    /// 由一组天花真实顶面边界求出的外侧墙段。坐标单位为 mm；Inward 指向
    /// 天花投影内部。该类型只承载纯几何结果，不依赖 Revit API，便于定向测试。
    /// </summary>
    internal sealed class HandReachVirtualBoundarySegment
    {
        public int FootprintIndex;
        public int LoopIndex;
        public int SegmentIndex;
        public MaintenancePoint2 Start;
        public MaintenancePoint2 End;
        public MaintenancePoint2 Tangent;
        public MaintenancePoint2 Inward;
        public double LengthMm;
        public string StableKey;

        public HandReachVirtualBoundarySegment()
        {
            StableKey = string.Empty;
        }
    }

    internal sealed class HandReachObstacle
    {
        public string Key;
        public string UniqueId;
        public string Category;
        public string Name;
        public string SystemType;
        public string Relation;

        public HandReachObstacle()
        {
            Key = string.Empty;
            UniqueId = string.Empty;
            Category = string.Empty;
            Name = string.Empty;
            SystemType = string.Empty;
            Relation = string.Empty;
        }
    }

    internal sealed class HandReachRegion
    {
        public HandReachOpeningPlaneKind OpeningPlane;
        public string SurfaceKey;
        public int RegionNo;
        public int PointCount;
        public double MinX;
        public double MaxX;
        public double MinY;
        public double MaxY;
        public double MinZ;
        public double MaxZ;
        public double AreaM2;
        public HandReachSample Recommended;
        public int MaxTestedClearDiameterMm;
        public bool[] RecommendedCorridorClear;
        public int RecommendedExemptIntersectCount;
        public string RecommendedBlockerKey;
        public HandReachVerticalGrade RecommendedVerticalGrade;
        public string RecommendedLadderDirection;
        public bool RecommendedOperationZoneClear;

        public HandReachRegion()
        {
            OpeningPlane = HandReachOpeningPlaneKind.CeilingHorizontal;
            SurfaceKey = string.Empty;
            RecommendedCorridorClear = new bool[0];
            RecommendedBlockerKey = string.Empty;
            RecommendedLadderDirection = string.Empty;
        }
    }

    internal sealed class HandReachTargetResult
    {
        public HandReachTargetInfo Target;
        public bool HasSelectedOpening;
        public HandReachOpeningPlaneKind SelectedOpeningPlane;
        public bool SideWallAttempted;
        public int SideWallRawSampleCount;
        public int SideWallFaceFitCount;
        public int SideWallDistanceOkCount;
        public int SideWallOpeningFailCount;
        public int SideWallCorridorFailCount;
        public int SideWallLadderFailCount;
        public int SideWallClearCount;
        public int RawSampleCount;
        public int HatchInsideCount;
        public int VerticalFailCount;
        public int DistanceOkCount;
        public int OpeningFailCount;
        public int CorridorFailCount;
        public int LadderFailCount;
        public int ClearCount;                         // 默认200通道成立的候选数
        public int Regions4Count;
        public int Regions8Count;
        public bool ConnectivityAgreed;
        public bool CandidateAuditComplete;
        public bool SelectedCandidateAuditComplete;
        public int ObstacleSolidCount;
        public int ExemptSolidCount;
        public bool CeilingDirectReachApplied;
        public bool CeilingPersonnelEntryApplied;
        public double ModelVerticalDifferenceMm;
        public double AnalysisVerticalDifferenceMm;
        public double AnalysisServiceFaceProxyZ;
        public double ModelDeviceMinX;
        public double ModelDeviceMinY;
        public double ModelDeviceMinZ;
        public double ModelDeviceMaxX;
        public double ModelDeviceMaxY;
        public double ModelDeviceMaxZ;
        public HandReachLadderStatus LadderStatus;
        public double LadderFloorMm;
        public double LadderTopMm;
        public HandReachAttentionLevel AttentionLevel;
        public string Conclusion;
        public string ConclusionReason;
        public readonly List<HandReachRegion> Regions;
        public readonly List<HandReachObstacle> RealObstacles;
        public readonly List<HandReachObstacle> ExemptEvidence;

        public HandReachTargetResult()
        {
            SelectedOpeningPlane = HandReachOpeningPlaneKind.CeilingHorizontal;
            CandidateAuditComplete = true;
            SelectedCandidateAuditComplete = true;
            Conclusion = string.Empty;
            ConclusionReason = string.Empty;
            Regions = new List<HandReachRegion>();
            RealObstacles = new List<HandReachObstacle>();
            ExemptEvidence = new List<HandReachObstacle>();
        }

        /// <summary>
        /// 标记候选集合中存在未验证项，但不连带否定已经独立完成全链验证的最佳候选。
        /// 若最佳候选自身证据不完整，调用处必须显式设置 SelectedCandidateAuditComplete=false。
        /// </summary>
        public void MarkCandidateSetAuditIncomplete()
        {
            CandidateAuditComplete = false;
        }
    }

    internal sealed class HandReachAnalysisResult
    {
        public string AnalysisId;
        public DateTime CreatedAtUtc;
        public string GroupKey;
        public double CeilingTopMm;
        public string ModelFingerprint;
        public string ResultFingerprint;
        public string EvidenceFingerprint;
        public HandReachOptions Options;
        public bool WindowLimitedSampling;             // 诚实标注：仅采样代理点周边窗口
        public bool CoverageComplete;
        public readonly List<MaintenanceElementRef> CeilingSources;
        public readonly List<MaintenanceElementRef> EvidenceSources;
        public readonly List<MaintenancePipeExemptionEvidence> ExemptPipeEvidence;
        public MaintenanceLinkScopeSnapshot LinkScope;
        public readonly List<HandReachCoverageFailure> CoverageFailures;
        public readonly List<string> CoverageLimitations;
        public readonly List<HandReachTargetResult> TargetResults;
        public readonly List<string> Warnings;

        public HandReachAnalysisResult()
        {
            AnalysisId = Guid.NewGuid().ToString("N");
            CreatedAtUtc = DateTime.UtcNow;
            GroupKey = string.Empty;
            ModelFingerprint = string.Empty;
            ResultFingerprint = string.Empty;
            EvidenceFingerprint = string.Empty;
            WindowLimitedSampling = true;
            CoverageComplete = true;
            CeilingSources = new List<MaintenanceElementRef>();
            EvidenceSources = new List<MaintenanceElementRef>();
            ExemptPipeEvidence = new List<MaintenancePipeExemptionEvidence>();
            LinkScope = new MaintenanceLinkScopeSnapshot();
            CoverageFailures = new List<HandReachCoverageFailure>();
            CoverageLimitations = new List<string>();
            TargetResults = new List<HandReachTargetResult>();
            Warnings = new List<string>();
        }
    }

    internal sealed class HandReachCoverageFailure
    {
        public string Stage;
        public string SourceKey;
        public long? LinkInstanceId;
        public string LinkInstanceUniqueId;
        public long ElementId;
        public string Category;
        public string Mark;
        public string Reason;

        public HandReachCoverageFailure()
        {
            Stage = string.Empty;
            SourceKey = string.Empty;
            Category = string.Empty;
            LinkInstanceUniqueId = string.Empty;
            Mark = string.Empty;
            Reason = string.Empty;
        }
    }
}

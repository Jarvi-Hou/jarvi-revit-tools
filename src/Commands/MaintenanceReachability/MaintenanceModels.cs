using System;
using System.Collections.Generic;

namespace JarviTools.Commands.MaintenanceReachability
{
    // All coordinates and dimensions in these DTOs are millimetres. Keeping the
    // contract independent of Revit makes the decision layer testable in isolation.
    internal enum MaintenanceEntryType
    {
        None,
        WallDoor,
        CeilingHatch
    }

    internal enum MaintenanceLadderType
    {
        None,
        AFrame,
        Straight
    }

    internal enum MaintenanceDoorHingeSide
    {
        None,
        Left,
        Right
    }

    internal enum MaintenanceDoorSwingStatus
    {
        NotApplicable,
        Clear,
        Conflict,
        Unverified
    }

    internal enum MaintenanceDecision
    {
        PendingReview,
        Pass,
        Fail
    }

    internal enum MaintenanceAccessProfile
    {
        Full700,
        Limited600
    }

    internal enum MaintenanceCandidateScope
    {
        Entry,
        Route
    }

    internal enum MaintenanceCandidateStatus
    {
        Rejected,
        Unverified,
        Feasible
    }

    internal enum MaintenanceCandidateStage
    {
        Sample,
        Footprint,
        TurnZone,
        Opening,
        DoorFrame,
        DoorSwing,
        Portal,
        StartCell,
        Ladder,
        TargetGoal,
        Connectivity,
        Route,
        ServicePocket,
        Complete
    }

    internal enum MaintenanceComponentRole
    {
        Unknown,
        VirtualBoundaryWall,
        WallDoor,
        CeilingHatch,
        AFrameLadder,
        StraightLadder,
        EntryTurnZone,
        AccessRoute,
        HumanEnvelope,
        ServicePocket,
        TargetEquipment
    }

    internal enum MaintenanceRenderGeometryType
    {
        Box,
        ExtrudedPolygon,
        Polyline,
        Marker
    }

    internal enum MaintenanceWallAlternativeStatus
    {
        Available,
        AvailablePendingReview,
        UnavailableNoModelableWall,
        UnavailableIncompleteGeometry,
        UnavailableEvidenceCollectionIncomplete
    }

    internal struct MaintenancePoint2 : IEquatable<MaintenancePoint2>
    {
        public readonly double X;
        public readonly double Y;

        public MaintenancePoint2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double DistanceTo(MaintenancePoint2 other)
        {
            double dx = other.X - X;
            double dy = other.Y - Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public double Length()
        {
            return Math.Sqrt(X * X + Y * Y);
        }

        public MaintenancePoint2 Normalize()
        {
            double length = Length();
            if (length <= 1e-9) return new MaintenancePoint2(0.0, 0.0);
            return new MaintenancePoint2(X / length, Y / length);
        }

        public MaintenancePoint2 LeftNormal()
        {
            MaintenancePoint2 unit = Normalize();
            return new MaintenancePoint2(-unit.Y, unit.X);
        }

        public static MaintenancePoint2 operator +(MaintenancePoint2 left, MaintenancePoint2 right)
        {
            return new MaintenancePoint2(left.X + right.X, left.Y + right.Y);
        }

        public static MaintenancePoint2 operator -(MaintenancePoint2 left, MaintenancePoint2 right)
        {
            return new MaintenancePoint2(left.X - right.X, left.Y - right.Y);
        }

        public static MaintenancePoint2 operator *(MaintenancePoint2 point, double scale)
        {
            return new MaintenancePoint2(point.X * scale, point.Y * scale);
        }

        public bool Equals(MaintenancePoint2 other)
        {
            return X.Equals(other.X) && Y.Equals(other.Y);
        }

        public override bool Equals(object obj)
        {
            return obj is MaintenancePoint2 && Equals((MaintenancePoint2)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (X.GetHashCode() * 397) ^ Y.GetHashCode();
            }
        }
    }

    internal struct MaintenancePoint3 : IEquatable<MaintenancePoint3>
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public MaintenancePoint3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public MaintenancePoint2 ToPoint2()
        {
            return new MaintenancePoint2(X, Y);
        }

        public bool Equals(MaintenancePoint3 other)
        {
            return X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
        }

        public override bool Equals(object obj)
        {
            return obj is MaintenancePoint3 && Equals((MaintenancePoint3)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X.GetHashCode();
                hash = (hash * 397) ^ Y.GetHashCode();
                hash = (hash * 397) ^ Z.GetHashCode();
                return hash;
            }
        }
    }

    internal sealed class MaintenanceElementRef
    {
        public string DocumentTitle;
        public long? LinkInstanceId;
        public string LinkInstanceUniqueId;
        public long ElementId;
        public string UniqueId;
        public string Category;
        public string Name;

        public MaintenanceElementRef()
        {
            DocumentTitle = string.Empty;
            LinkInstanceUniqueId = string.Empty;
            UniqueId = string.Empty;
            Category = string.Empty;
            Name = string.Empty;
        }

        public string GetStableKey()
        {
            if (LinkInstanceId.HasValue)
            {
                if (!string.IsNullOrWhiteSpace(LinkInstanceUniqueId) &&
                    !string.IsNullOrWhiteSpace(UniqueId))
                    return MaintenanceStableIdentity.LinkedElementKey(
                        LinkInstanceUniqueId, UniqueId);
                return "LINK:" + LinkInstanceId.Value + ":" + ElementId;
            }
            if (!string.IsNullOrWhiteSpace(UniqueId))
                return MaintenanceStableIdentity.HostElementKey(UniqueId);
            return "HOST:" + ElementId;
        }
    }

    internal static class MaintenanceStableIdentity
    {
        public static string HostElementKey(string elementUniqueId)
        {
            if (string.IsNullOrWhiteSpace(elementUniqueId))
                throw new ArgumentException("宿主图元 UniqueId 不能为空。", "elementUniqueId");
            return "HUID:" + elementUniqueId.Trim();
        }

        public static string LinkedElementKey(
            string linkInstanceUniqueId,
            string linkedElementUniqueId)
        {
            if (string.IsNullOrWhiteSpace(linkInstanceUniqueId))
                throw new ArgumentException("链接实例 UniqueId 不能为空。", "linkInstanceUniqueId");
            if (string.IsNullOrWhiteSpace(linkedElementUniqueId))
                throw new ArgumentException("链接图元 UniqueId 不能为空。", "linkedElementUniqueId");
            return "LUID:" + linkInstanceUniqueId.Trim() + ":" +
                   linkedElementUniqueId.Trim();
        }
    }

    internal sealed class MaintenanceCeilingGroup
    {
        public string GroupKey;
        public double CeilingTopMm;
        public double StructureBottomMm;
        public readonly List<MaintenanceElementRef> CeilingSources;
        public readonly List<List<MaintenancePoint2>> BoundaryLoops;
        public readonly List<MaintenanceTarget> Targets;

        public MaintenanceCeilingGroup()
        {
            GroupKey = string.Empty;
            CeilingSources = new List<MaintenanceElementRef>();
            BoundaryLoops = new List<List<MaintenancePoint2>>();
            Targets = new List<MaintenanceTarget>();
        }
    }

    internal sealed class MaintenanceTarget
    {
        public string TargetKey;
        public string DeviceNo;
        public MaintenanceElementRef Source;
        public string EquipmentName;
        public string Mark;
        public MaintenancePoint3 Center;
        public MaintenancePoint2 SupplyDirection;
        public MaintenancePoint2 ServiceSideDirection;
        public MaintenancePoint3 ServicePocketCenter;
        public double ServicePocketWidthMm;
        public double ServicePocketDepthMm;
        public double ServicePocketHeightMm;

        public MaintenanceTarget()
        {
            TargetKey = string.Empty;
            DeviceNo = string.Empty;
            EquipmentName = string.Empty;
            Mark = string.Empty;
        }

        public string GetDisplayName()
        {
            if (string.IsNullOrWhiteSpace(Mark)) return EquipmentName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(EquipmentName)) return Mark;
            return EquipmentName + " | " + Mark;
        }
    }

    internal sealed class MaintenancePipeExemptionEvidence
    {
        public string GroupKey;
        public string TargetKey;
        public MaintenanceElementRef Element;
        public string CategoryKind;
        public string SystemKind;
        public string SystemTypeEvidence;
        public string SystemEvidenceSource;
        public string ReasonCode;
        public string Reason;
        public double DistanceMm;
        public double LengthMm;
        public double DiameterMm;

        public MaintenancePipeExemptionEvidence()
        {
            GroupKey = string.Empty;
            TargetKey = string.Empty;
            CategoryKind = string.Empty;
            SystemKind = string.Empty;
            SystemTypeEvidence = string.Empty;
            SystemEvidenceSource = string.Empty;
            ReasonCode = string.Empty;
            Reason = string.Empty;
        }
    }

    internal sealed class MaintenanceEntryCandidate
    {
        public string CandidateKey;
        public string GroupKey;
        public string TargetKey;
        public MaintenanceEntryType EntryType;
        public MaintenanceLadderType LadderType;
        public int BoundaryLoopIndex;
        public int BoundarySegmentIndex;
        public MaintenancePoint3 Center;
        public MaintenancePoint2 InwardDirection;
        public double OpeningWidthMm;
        public double OpeningHeightMm;
        public MaintenanceDoorHingeSide DoorHingeSide;
        public MaintenanceDoorSwingStatus LeftDoorSwingStatus;
        public MaintenanceDoorSwingStatus RightDoorSwingStatus;
        public double LadderFloorMm;
        public int CoveredTargetCount;
        public bool IsFeasible;
        public string RejectionReason;
        public readonly List<string> OpeningHostSourceKeys;
        public readonly List<string> LadderSupportSourceKeys;
        public readonly List<MaintenanceElementRef> LeftDoorSwingBlockers;
        public readonly List<MaintenanceElementRef> RightDoorSwingBlockers;
        public readonly List<MaintenanceElementRef> Blockers;

        public MaintenanceEntryCandidate()
        {
            CandidateKey = string.Empty;
            GroupKey = string.Empty;
            TargetKey = string.Empty;
            LadderFloorMm = double.NaN;
            RejectionReason = string.Empty;
            OpeningHostSourceKeys = new List<string>();
            LadderSupportSourceKeys = new List<string>();
            LeftDoorSwingBlockers = new List<MaintenanceElementRef>();
            RightDoorSwingBlockers = new List<MaintenanceElementRef>();
            Blockers = new List<MaintenanceElementRef>();
        }
    }

    /// <summary>
    /// Immutable evidence row for one search alternative. Entry rows explain why
    /// a location was rejected before routing; route rows preserve one complete
    /// entry-to-target attempt. Coordinates remain machine evidence and are not
    /// intended to be shown in the project-manager presentation.
    /// </summary>
    internal sealed class MaintenanceCandidateEvaluation
    {
        public string EvaluationKey;
        public string CandidateKey;
        public string GroupKey;
        public string TargetKey;
        public MaintenanceCandidateScope Scope;
        public MaintenanceAccessProfile Profile;
        public MaintenanceEntryType EntryType;
        public MaintenanceLadderType LadderType;
        public MaintenanceCandidateStatus Status;
        public MaintenanceCandidateStage Stage;
        public bool IsSelected;
        public int Rank;
        public int BoundaryLoopIndex;
        public int BoundarySegmentIndex;
        public MaintenancePoint3 EntryCenter;
        public double OpeningWidthMm;
        public double OpeningHeightMm;
        public MaintenanceDoorHingeSide DoorHingeSide;
        public MaintenanceDoorSwingStatus LeftDoorSwingStatus;
        public MaintenanceDoorSwingStatus RightDoorSwingStatus;
        public double LadderFloorMm;
        public int CoveredTargetCount;
        public double RouteLengthMm;
        public string ReasonCode;
        public string Reason;
        public string SelectionReason;
        public string DominatedByCandidateKey;
        public string DominatedByEvaluationKey;
        public int SourceSampleCount;
        public readonly List<MaintenancePoint3> Route;
        public readonly List<string> OpeningHostSourceKeys;
        public readonly List<string> LadderSupportSourceKeys;
        public readonly List<MaintenanceElementRef> LeftDoorSwingBlockers;
        public readonly List<MaintenanceElementRef> RightDoorSwingBlockers;
        public readonly List<MaintenanceElementRef> Blockers;

        public MaintenanceCandidateEvaluation()
        {
            EvaluationKey = string.Empty;
            CandidateKey = string.Empty;
            GroupKey = string.Empty;
            TargetKey = string.Empty;
            ReasonCode = string.Empty;
            Reason = string.Empty;
            SelectionReason = string.Empty;
            DominatedByCandidateKey = string.Empty;
            DominatedByEvaluationKey = string.Empty;
            LadderFloorMm = double.NaN;
            SourceSampleCount = 1;
            Route = new List<MaintenancePoint3>();
            OpeningHostSourceKeys = new List<string>();
            LadderSupportSourceKeys = new List<string>();
            LeftDoorSwingBlockers = new List<MaintenanceElementRef>();
            RightDoorSwingBlockers = new List<MaintenanceElementRef>();
            Blockers = new List<MaintenanceElementRef>();
        }
    }

    internal sealed class MaintenanceCandidateSearchStats
    {
        public string GroupKey;
        public string TargetKey;
        public MaintenanceAccessProfile Profile;
        public MaintenanceEntryType EntryType;
        public int RawSampleCount;
        public int EligibleSampleCount;
        public int DeduplicatedCount;
        public int RetainedCount;
        public int OmittedCount;
        public bool Truncated;
        public double RepresentativeSpacingMm;
        public bool AllPathsEnumerated;
        public string AlgorithmVersion;
        public int SampledCount;
        public int RetainedEntryCount;
        public int EvaluatedRouteCount;
        public int RejectedCount;
        public int UnverifiedCount;
        public int FeasibleCount;
        public int SelectedCount;
        public bool Complete;
        public string Strategy;

        public MaintenanceCandidateSearchStats()
        {
            GroupKey = string.Empty;
            TargetKey = string.Empty;
            AlgorithmVersion = string.Empty;
            Strategy = string.Empty;
        }
    }

    internal sealed class MaintenanceAnalysisOptions
    {
        internal const double DefaultDoorWidthMm = 600.0;
        internal const double DefaultDoorHeightMm = 600.0;
        internal const double MinimumDoorDimensionMm = 100.0;
        internal const double MaximumDoorDimensionMm = 3000.0;

        public bool PreserveCandidateAudit;
        public bool StrictCeilingSelection;
        public bool CombineSelectedCeilingsForSharedEntry;
        public int MaxHatchCandidatesPerTarget;
        public long[] RelevantLinkInstanceIds;
        public double DoorWidthMm;
        public double DoorHeightMm;

        public MaintenanceAnalysisOptions()
        {
            MaxHatchCandidatesPerTarget = 32;
            DoorWidthMm = DefaultDoorWidthMm;
            DoorHeightMm = DefaultDoorHeightMm;
        }

        internal static void ValidateDoorDimensions(double widthMm, double heightMm)
        {
            ValidateDoorDimension(widthMm, "doorWidthMm");
            ValidateDoorDimension(heightMm, "doorHeightMm");
        }

        private static void ValidateDoorDimension(double valueMm, string name)
        {
            if (double.IsNaN(valueMm) || double.IsInfinity(valueMm) ||
                valueMm < MinimumDoorDimensionMm || valueMm > MaximumDoorDimensionMm)
                throw new ArgumentOutOfRangeException(
                    name,
                    valueMm,
                    name + " must be between " + MinimumDoorDimensionMm +
                    " and " + MaximumDoorDimensionMm + " mm.");
        }
    }

    internal static class MaintenanceDoorOpeningPolicy
    {
        internal static bool SupportsAccessProfile(
            double doorWidthMm,
            double doorHeightMm,
            double profileDiameterMm,
            double profileHeightMm)
        {
            MaintenanceAnalysisOptions.ValidateDoorDimensions(
                doorWidthMm,
                doorHeightMm);
            return doorWidthMm + 1e-6 >= profileDiameterMm &&
                   doorHeightMm + 1e-6 >= profileHeightMm;
        }

        internal static string BuildRejectionReason(
            double doorWidthMm,
            double doorHeightMm,
            double profileDiameterMm,
            double profileHeightMm)
        {
            return "侧墙检修门净开口 " +
                   Math.Round(doorWidthMm, 1).ToString("0.#") + "×" +
                   Math.Round(doorHeightMm, 1).ToString("0.#") +
                   " mm 小于该通行档位所需 " +
                   Math.Round(profileDiameterMm, 1).ToString("0.#") + "×" +
                   Math.Round(profileHeightMm, 1).ToString("0.#") +
                   " mm，已拒绝该档位，不能以较小门洞假通过。";
        }

        internal static bool ShouldEvaluateLimited600Wall(
            bool full700EntryIsWallDoor,
            bool full700RouteIsClear)
        {
            // A clear Full700 ceiling hatch must not suppress the independent
            // 600 mm side-wall door pass.  Limited600 is unnecessary only when
            // a Full700 wall-door chain itself is already clear.
            return !full700EntryIsWallDoor || !full700RouteIsClear;
        }

        internal static bool ShouldSelectLimited600Result(
            bool full700ResultAlreadySelected,
            MaintenanceEntryType limitedEntryType)
        {
            // A clear Limited600 wall door has priority over a previously
            // selected ceiling entry.  A Limited600 ceiling fallback must not
            // replace an already-clear Full700 result.
            return !full700ResultAlreadySelected ||
                   limitedEntryType == MaintenanceEntryType.WallDoor;
        }
    }

    internal static class MaintenanceTurnZonePolicy
    {
        internal const string PolicyVersion = "profile_specific_turn_zone_v1";
        private const double SafetyMarginMm = 30.0;

        internal static double GetValidationWidthMm(MaintenanceAccessProfile profile)
        {
            switch (profile)
            {
                case MaintenanceAccessProfile.Full700:
                    // Preserve the accepted 900 mm full-body turn zone, plus
                    // 30 mm collision clearance on every side.
                    return 900.0 + SafetyMarginMm * 2.0;
                case MaintenanceAccessProfile.Limited600:
                    // Limited access is a 600 mm crouched/crawling envelope;
                    // it must not inherit the full-body 900 mm turn contract.
                    return 600.0 + SafetyMarginMm * 2.0;
                default:
                    throw new ArgumentOutOfRangeException("profile");
            }
        }
    }

    internal static class MaintenanceDoorSwingPolicy
    {
        internal const string PolicyVersion = "outward_both_hinges_v1";
        internal const double LeafThicknessMm = 30.0;
        internal const double OutboardOffsetMm = 130.0;

        internal static MaintenanceDoorHingeSide Select(
            MaintenanceDoorSwingStatus left,
            MaintenanceDoorSwingStatus right)
        {
            // Preserve the user's reviewed right-hinge preference when both
            // outward swings are clear, while still evaluating both sides.
            if (right == MaintenanceDoorSwingStatus.Clear)
                return MaintenanceDoorHingeSide.Right;
            if (left == MaintenanceDoorSwingStatus.Clear)
                return MaintenanceDoorHingeSide.Left;
            return MaintenanceDoorHingeSide.None;
        }
    }

    internal static class MaintenanceOpeningHostWallPolicy
    {
        internal const string PolicyVersion = "single_exact_aligned_owner_wall_v2";
        internal const double MaximumAlignmentAngleDegrees = 10.0;

        internal static bool IsDirectionAligned(double directionDotProduct)
        {
            if (double.IsNaN(directionDotProduct) ||
                double.IsInfinity(directionDotProduct))
                return false;
            double minimumAbsoluteDot = Math.Cos(
                MaximumAlignmentAngleDegrees * Math.PI / 180.0);
            return Math.Abs(directionDotProduct) + 1e-9 >= minimumAbsoluteDot;
        }
    }

    internal sealed class MaintenanceInstanceParameters
    {
        // These eight values are the user-facing schedule/filter contract.
        public string ComponentName;
        public string CeilingGroup;
        public string EntryGroup;
        public string ComponentRole;
        public string MaintenanceTarget;
        public string MaintenanceConclusion;
        public string DecisionReason;
        public string ProfessionalNote;

        public MaintenanceInstanceParameters()
        {
            ComponentName = string.Empty;
            CeilingGroup = string.Empty;
            EntryGroup = string.Empty;
            ComponentRole = string.Empty;
            MaintenanceTarget = string.Empty;
            MaintenanceConclusion = string.Empty;
            DecisionReason = string.Empty;
            ProfessionalNote = string.Empty;
        }
    }

    internal sealed class MaintenanceReviewTrace
    {
        public string EvidenceFingerprint;
        public string Reviewer;
        public string ReviewNote;
        public string ApprovedAtUtc;

        public MaintenanceReviewTrace()
        {
            EvidenceFingerprint = string.Empty;
            Reviewer = string.Empty;
            ReviewNote = string.Empty;
            ApprovedAtUtc = string.Empty;
        }
    }

    internal sealed class MaintenanceRenderItem
    {
        public string RenderKey;
        public MaintenanceRenderGeometryType GeometryType;
        public MaintenanceComponentRole Role;
        public MaintenanceDecision Decision;
        public MaintenancePoint3 Center;
        public MaintenancePoint2 Direction;
        public double WidthMm;
        public double DepthMm;
        public double HeightMm;
        public readonly List<MaintenancePoint3> Points;
        public MaintenanceInstanceParameters Parameters;

        // Internal trace fields stay out of the user-facing Revit parameters.
        public string AnalysisId;
        public string TargetKey;
        public string EvidenceFingerprint;
        public string ApprovalReviewer;
        public string ApprovalNote;
        public string ApprovedAtUtc;
        public readonly List<string> SourceKeys;

        public MaintenanceRenderItem()
        {
            RenderKey = string.Empty;
            AnalysisId = string.Empty;
            TargetKey = string.Empty;
            EvidenceFingerprint = string.Empty;
            ApprovalReviewer = string.Empty;
            ApprovalNote = string.Empty;
            ApprovedAtUtc = string.Empty;
            Points = new List<MaintenancePoint3>();
            SourceKeys = new List<string>();
            Parameters = new MaintenanceInstanceParameters();
        }
    }

    internal sealed class MaintenanceTargetResult
    {
        public string GroupKey;
        public MaintenanceTarget Target;
        public MaintenanceAccessProfile Profile;
        public MaintenanceDecision Decision;
        public string DecisionReason;
        public MaintenanceEntryCandidate SelectedEntry;
        public bool CompleteChainSucceeded;
        public double RouteLengthMm;
        public readonly List<MaintenancePoint3> Route;
        public readonly List<MaintenanceRenderItem> RenderItems;
        public readonly List<MaintenanceElementRef> Blockers;

        public MaintenanceTargetResult()
        {
            GroupKey = string.Empty;
            DecisionReason = string.Empty;
            Route = new List<MaintenancePoint3>();
            RenderItems = new List<MaintenanceRenderItem>();
            Blockers = new List<MaintenanceElementRef>();
        }
    }

    /// <summary>
    /// A single side-wall alternative retained while the route analysis still owns
    /// the complete EntryWork geometry.  RenderItems are deliberately separate from
    /// the formal result so visualization can never replace the selected scheme.
    /// </summary>
    internal sealed class MaintenanceWallAlternativeResult
    {
        public string AlternativeKey;
        public string GroupKey;
        public string TargetKey;
        public string DeviceNo;
        public int SchemeNo;
        public string EntryGroup;
        public string ViewName;
        public MaintenanceWallAlternativeStatus Status;
        public bool CanVisualize;
        public bool SameAsRouteFormal;
        public string Reason;
        public MaintenanceAccessProfile Profile;
        public MaintenanceEntryType EntryType;
        public MaintenanceLadderType LadderType;
        public MaintenanceDecision Decision;
        public string DecisionReason;
        public double RouteLengthMm;
        public MaintenanceEntryCandidate SelectedEntry;
        public string GeometryFingerprint;
        public readonly List<MaintenancePoint3> Route;
        public readonly List<MaintenanceRenderItem> RenderItems;
        public readonly List<MaintenanceElementRef> Blockers;

        public MaintenanceWallAlternativeResult()
        {
            AlternativeKey = string.Empty;
            GroupKey = string.Empty;
            TargetKey = string.Empty;
            DeviceNo = string.Empty;
            EntryGroup = string.Empty;
            ViewName = string.Empty;
            Reason = string.Empty;
            DecisionReason = string.Empty;
            GeometryFingerprint = string.Empty;
            Route = new List<MaintenancePoint3>();
            RenderItems = new List<MaintenanceRenderItem>();
            Blockers = new List<MaintenanceElementRef>();
        }
    }

    internal sealed class MaintenanceAnalysisResult
    {
        public string AnalysisId;
        public DateTime CreatedAtUtc;
        public double DoorWidthMm;
        public double DoorHeightMm;
        public double CeilingHatchSizeMm;
        public bool SharedCeilingEntryReview;
        public string ModelFingerprint;
        public string EvidenceFingerprint;
        public string ApprovalReviewer;
        public string ApprovalNote;
        public DateTime? ApprovedAtUtc;
        public readonly List<MaintenanceCeilingGroup> Groups;
        public readonly List<MaintenanceTargetResult> TargetResults;
        public readonly List<MaintenanceRenderItem> RenderItems;
        public readonly List<MaintenanceWallAlternativeResult> WallAlternatives;
        public readonly List<MaintenanceSharedCeilingEntryAlternative> SharedCeilingEntryAlternatives;
        public string WallAlternativeFingerprint;
        public readonly List<MaintenanceElementRef> EvidenceSources;
        public readonly List<MaintenancePipeExemptionEvidence> ExemptPipeEvidence;
        public readonly List<MaintenanceCandidateEvaluation> CandidateEvaluations;
        public readonly List<MaintenanceCandidateSearchStats> CandidateSearchStats;
        public readonly List<string> Warnings;
        public readonly List<string> CoverageLimitations;
        public string EvidenceScopeDefinition;
        public MaintenanceLinkScopeSnapshot LinkScope;
        public bool EvidenceCollectionComplete;
        public readonly List<MaintenanceEvidenceCollectionFailure> CollectionFailures;
        public bool CandidateAuditEnabled;
        public bool CandidateAuditComplete;
        public string CandidateAuditStrategy;
        public string CandidateAuditScopeDefinition;
        public string CandidateAuditScopeDescription;
        public bool CandidateAuditAllPathsEnumerated;
        public string CandidateAuditRoutePolicy;
        public string CandidateAuditSelectionPolicy;
        public string CandidateAuditDisplayRankingPolicy;
        public string CandidateAuditFingerprint;

        public MaintenanceAnalysisResult()
        {
            AnalysisId = Guid.NewGuid().ToString("N");
            CreatedAtUtc = DateTime.UtcNow;
            DoorWidthMm = MaintenanceAnalysisOptions.DefaultDoorWidthMm;
            DoorHeightMm = MaintenanceAnalysisOptions.DefaultDoorHeightMm;
            CeilingHatchSizeMm = MaintenanceSharedCeilingEntryPolicy.DefaultHatchSizeMm;
            ModelFingerprint = string.Empty;
            EvidenceFingerprint = string.Empty;
            ApprovalReviewer = string.Empty;
            ApprovalNote = string.Empty;
            Groups = new List<MaintenanceCeilingGroup>();
            TargetResults = new List<MaintenanceTargetResult>();
            RenderItems = new List<MaintenanceRenderItem>();
            WallAlternatives = new List<MaintenanceWallAlternativeResult>();
            SharedCeilingEntryAlternatives =
                new List<MaintenanceSharedCeilingEntryAlternative>();
            WallAlternativeFingerprint = string.Empty;
            EvidenceSources = new List<MaintenanceElementRef>();
            ExemptPipeEvidence = new List<MaintenancePipeExemptionEvidence>();
            CandidateEvaluations = new List<MaintenanceCandidateEvaluation>();
            CandidateSearchStats = new List<MaintenanceCandidateSearchStats>();
            Warnings = new List<string>();
            CoverageLimitations = new List<string>();
            EvidenceScopeDefinition = string.Empty;
            LinkScope = new MaintenanceLinkScopeSnapshot();
            EvidenceCollectionComplete = true;
            CollectionFailures = new List<MaintenanceEvidenceCollectionFailure>();
            CandidateAuditStrategy = string.Empty;
            CandidateAuditScopeDefinition = string.Empty;
            CandidateAuditScopeDescription = string.Empty;
            CandidateAuditRoutePolicy = string.Empty;
            CandidateAuditSelectionPolicy = string.Empty;
            CandidateAuditDisplayRankingPolicy = string.Empty;
            CandidateAuditFingerprint = string.Empty;
        }
    }

    internal sealed class MaintenanceEvidenceCollectionFailure
    {
        public string GroupKey;
        public string SourceKey;
        public long? LinkInstanceId;
        public string LinkInstanceUniqueId;
        public long ElementId;
        public string Category;
        public string Reason;

        public MaintenanceEvidenceCollectionFailure()
        {
            GroupKey = string.Empty;
            SourceKey = string.Empty;
            LinkInstanceUniqueId = string.Empty;
            Category = string.Empty;
            Reason = string.Empty;
        }
    }
}

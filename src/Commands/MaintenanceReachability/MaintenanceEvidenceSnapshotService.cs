using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace JarviTools.Commands.MaintenanceReachability
{
    /// <summary>
    /// Builds a live Revit evidence fingerprint from the exact host/link elements
    /// that participated in an analysis.  This deliberately re-reads VersionGuid,
    /// link transforms and link load state instead of hashing the cached result.
    /// </summary>
    internal static class MaintenanceEvidenceSnapshotService
    {
        internal static string Compute(
            Document document,
            IEnumerable<MaintenanceElementRef> sources)
        {
            return Compute(
                document,
                sources,
                Enumerable.Empty<MaintenancePipeExemptionEvidence>(),
                null);
        }

        internal static string Compute(
            Document document,
            IEnumerable<MaintenanceElementRef> sources,
            IEnumerable<MaintenancePipeExemptionEvidence> exemptPipeEvidence)
        {
            return Compute(document, sources, exemptPipeEvidence, null);
        }

        internal static string Compute(
            Document document,
            IEnumerable<MaintenanceElementRef> sources,
            IEnumerable<MaintenancePipeExemptionEvidence> exemptPipeEvidence,
            MaintenanceLinkScopeSnapshot linkScope)
        {
            if (document == null) throw new ArgumentNullException("document");

            var proxy = new MaintenanceAnalysisResult
            {
                LinkScope = linkScope ?? new MaintenanceLinkScopeSnapshot()
            };
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (MaintenanceElementRef source in (sources ?? Enumerable.Empty<MaintenanceElementRef>())
                .Where(x => x != null)
                .OrderBy(x => x.GetStableKey(), StringComparer.Ordinal))
            {
                if (keys.Add(source.GetStableKey())) proxy.EvidenceSources.Add(source);
            }
            foreach (MaintenancePipeExemptionEvidence evidence in
                (exemptPipeEvidence ?? Enumerable.Empty<MaintenancePipeExemptionEvidence>())
                .Where(x => x != null && x.Element != null)
                .OrderBy(x => x.GroupKey, StringComparer.Ordinal)
                .ThenBy(x => x.TargetKey, StringComparer.Ordinal)
                .ThenBy(x => x.Element.GetStableKey(), StringComparer.Ordinal))
                proxy.ExemptPipeEvidence.Add(evidence);

            return MaintenanceAnalysisService.ComputeEvidenceFingerprint(document, proxy);
        }
    }
}

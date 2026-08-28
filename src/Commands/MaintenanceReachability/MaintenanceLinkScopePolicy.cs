using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JarviTools.Commands.MaintenanceReachability
{
    internal sealed class MaintenanceLinkScopeEntry
    {
        public long LinkInstanceId;
        public string LinkInstanceUniqueId;
        public string InstanceName;
        public string TypeName;
        public bool LoadedAtAnalysis;

        public MaintenanceLinkScopeEntry()
        {
            LinkInstanceUniqueId = string.Empty;
            InstanceName = string.Empty;
            TypeName = string.Empty;
        }

        public string GetStableKey()
        {
            return string.IsNullOrWhiteSpace(LinkInstanceUniqueId)
                ? "LINK:" + LinkInstanceId.ToString(CultureInfo.InvariantCulture) + ":*"
                : "LUID:" + LinkInstanceUniqueId + ":*";
        }
    }

    internal sealed class MaintenanceLinkScopeSnapshot
    {
        public bool Explicit;
        public readonly List<MaintenanceLinkScopeEntry> RelevantLinks;
        public readonly List<MaintenanceLinkScopeEntry> OutOfScopeLinks;

        public MaintenanceLinkScopeSnapshot()
        {
            RelevantLinks = new List<MaintenanceLinkScopeEntry>();
            OutOfScopeLinks = new List<MaintenanceLinkScopeEntry>();
        }

        public bool Includes(long? linkInstanceId, string linkInstanceUniqueId)
        {
            if (!linkInstanceId.HasValue) return true; // Host is always in scope.
            if (!Explicit) return true;
            if (!string.IsNullOrWhiteSpace(linkInstanceUniqueId))
                return RelevantLinks.Any(x => string.Equals(
                    x.LinkInstanceUniqueId,
                    linkInstanceUniqueId,
                    StringComparison.Ordinal));
            return RelevantLinks.Any(x => x.LinkInstanceId == linkInstanceId.Value);
        }
    }

    internal static class MaintenanceLinkScopePolicy
    {
        internal const string ContractVersion =
            "JarviTools.MaintenanceLinkScope.explicit-positive.v1";

        internal static MaintenanceLinkScopeSnapshot Resolve(
            IEnumerable<MaintenanceLinkScopeEntry> availableLinks,
            IEnumerable<long> relevantLinkInstanceIds)
        {
            List<MaintenanceLinkScopeEntry> available =
                (availableLinks ?? Enumerable.Empty<MaintenanceLinkScopeEntry>())
                .Where(x => x != null)
                .GroupBy(x => x.LinkInstanceId)
                .Select(x => Copy(x.First()))
                .OrderBy(x => x.LinkInstanceId)
                .ToList();
            var snapshot = new MaintenanceLinkScopeSnapshot
            {
                Explicit = relevantLinkInstanceIds != null
            };
            if (!snapshot.Explicit)
            {
                snapshot.RelevantLinks.AddRange(available);
                return snapshot;
            }

            var requested = new HashSet<long>(relevantLinkInstanceIds);
            var availableIds = new HashSet<long>(available.Select(x => x.LinkInstanceId));
            List<long> missing = requested
                .Where(x => !availableIds.Contains(x))
                .OrderBy(x => x)
                .ToList();
            if (missing.Count > 0)
                throw new ArgumentException(
                    "relevantLinkInstanceIds contains non-link or missing ids: " +
                    string.Join(",", missing.Select(x =>
                        x.ToString(CultureInfo.InvariantCulture))));

            foreach (MaintenanceLinkScopeEntry link in available)
            {
                if (requested.Contains(link.LinkInstanceId))
                {
                    if (string.IsNullOrWhiteSpace(link.LinkInstanceUniqueId))
                        throw new InvalidOperationException(
                            "Relevant Revit link " + link.LinkInstanceId +
                            " has no persistent UniqueId; analysis stopped.");
                    snapshot.RelevantLinks.Add(link);
                }
                else
                {
                    snapshot.OutOfScopeLinks.Add(link);
                }
            }
            return snapshot;
        }

        internal static string BuildSignature(MaintenanceLinkScopeSnapshot scope)
        {
            if (scope == null) return ContractVersion + "|missing";
            var parts = new List<string>
            {
                ContractVersion,
                scope.Explicit ? "explicit" : "all_links"
            };
            foreach (MaintenanceLinkScopeEntry link in scope.RelevantLinks
                .Where(x => x != null)
                .OrderBy(x => x.GetStableKey(), StringComparer.Ordinal))
                parts.Add("relevant=" + LinkSignature(link));
            foreach (MaintenanceLinkScopeEntry link in scope.OutOfScopeLinks
                .Where(x => x != null)
                .OrderBy(x => x.GetStableKey(), StringComparer.Ordinal))
                parts.Add("outOfScope=" + LinkSignature(link));
            return string.Join("|", parts);
        }

        private static string LinkSignature(MaintenanceLinkScopeEntry link)
        {
            return string.Join("~", new[]
            {
                link.GetStableKey(),
                link.LoadedAtAnalysis ? "loaded" : "unloaded"
            });
        }

        private static MaintenanceLinkScopeEntry Copy(MaintenanceLinkScopeEntry source)
        {
            return new MaintenanceLinkScopeEntry
            {
                LinkInstanceId = source.LinkInstanceId,
                LinkInstanceUniqueId = source.LinkInstanceUniqueId ?? string.Empty,
                InstanceName = source.InstanceName ?? string.Empty,
                TypeName = source.TypeName ?? string.Empty,
                LoadedAtAnalysis = source.LoadedAtAnalysis
            };
        }
    }
}

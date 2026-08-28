using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace JarviTools.Commands.MaintenanceReachability
{
    internal static class MaintenanceLinkScopeService
    {
        internal static MaintenanceLinkScopeSnapshot Resolve(
            Document hostDocument,
            IEnumerable<long> relevantLinkInstanceIds)
        {
            if (hostDocument == null) throw new ArgumentNullException("hostDocument");
            List<RevitLinkInstance> instances;
            try
            {
                instances = new FilteredElementCollector(hostDocument)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .OrderBy(x => x.Id.Value)
                    .ToList();
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "Cannot enumerate Revit link instances for the evidence scope.",
                    exception);
            }

            var entries = new List<MaintenanceLinkScopeEntry>();
            foreach (RevitLinkInstance instance in instances)
            {
                Element type = null;
                try { type = hostDocument.GetElement(instance.GetTypeId()); }
                catch { }
                bool loaded = false;
                try { loaded = instance.GetLinkDocument() != null; }
                catch { }
                entries.Add(new MaintenanceLinkScopeEntry
                {
                    LinkInstanceId = instance.Id.Value,
                    LinkInstanceUniqueId = TryGet(() => instance.UniqueId),
                    InstanceName = TryGet(() => instance.Name),
                    TypeName = type == null ? string.Empty : TryGet(() => type.Name),
                    LoadedAtAnalysis = loaded
                });
            }
            return MaintenanceLinkScopePolicy.Resolve(entries, relevantLinkInstanceIds);
        }

        internal static IEnumerable<MaintenanceElementRef> RelevantLinkEvidenceSources(
            Document hostDocument,
            MaintenanceLinkScopeSnapshot scope)
        {
            if (hostDocument == null || scope == null)
                return Enumerable.Empty<MaintenanceElementRef>();
            return scope.RelevantLinks
                .Where(x => x != null)
                .Select(x => new MaintenanceElementRef
                {
                    DocumentTitle = hostDocument.Title ?? string.Empty,
                    ElementId = x.LinkInstanceId,
                    UniqueId = x.LinkInstanceUniqueId ?? string.Empty,
                    Category = "RevitLinkInstance",
                    Name = string.IsNullOrWhiteSpace(x.InstanceName)
                        ? x.TypeName ?? string.Empty
                        : x.InstanceName
                })
                .ToList();
        }

        internal static void AddScopeLimitation(
            ICollection<string> limitations,
            MaintenanceLinkScopeSnapshot scope)
        {
            if (limitations == null || scope == null || !scope.Explicit) return;
            string statement =
                "链接证据采用显式正向范围：宿主模型始终纳入；仅 " +
                scope.RelevantLinks.Count + " 个指定 Revit 链接参与候选与失败审计；" +
                scope.OutOfScopeLinks.Count +
                " 个链接列为 outOfScope 且未参与分析，不能据此断言其中没有障碍。";
            if (!limitations.Contains(statement)) limitations.Add(statement);
        }

        private static string TryGet(Func<string> getter)
        {
            try { return getter() ?? string.Empty; }
            catch { return string.Empty; }
        }
    }
}

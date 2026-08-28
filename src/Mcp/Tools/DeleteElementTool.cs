using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// Two-stage deletion with an actual rolled-back cascade preview.
    /// Confirmation succeeds only when the same targets still exist and a second
    /// in-transaction preview produces the exact same affected-id set.
    /// </summary>
    public class DeleteElementTool : IRevitTool
    {
        private static readonly object PreviewGate = new object();
        private static readonly Dictionary<string, PreviewRecord> Previews =
            new Dictionary<string, PreviewRecord>(StringComparer.Ordinal);
        private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(5);

        public string Name => "delete_element";
        public string Description =>
            "Two-stage destructive delete. First call with dryRun=true to roll back a real Revit cascade preview. Confirm within five minutes with the returned token; execution re-previews and refuses if the affected set changed. Maximum 100 unique targets.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["elementIds"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "integer" },
                    ["description"] = "ElementIds to preview/delete. Maximum 100 unique ids."
                },
                ["dryRun"] = new JObject
                {
                    ["type"] = "boolean",
                    ["default"] = true
                },
                ["confirmationToken"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Required for dryRun=false; expires after five minutes and is single-use."
                }
            },
            ["required"] = new JArray { "elementIds" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            var array = input["elementIds"] as JArray
                ?? throw new ArgumentException("'elementIds' must be an array of integers.");
            var requested = new HashSet<long>();
            foreach (JToken token in array)
            {
                long value;
                try { value = (long)token; }
                catch (Exception ex) { throw new ArgumentException("Every elementId must be an integer.", ex); }
                if (value <= 0) throw new ArgumentException("Every elementId must be positive.");
                requested.Add(value);
            }
            if (requested.Count == 0) throw new ArgumentException("At least one elementId is required.");
            if (requested.Count > 100)
                throw new InvalidOperationException("delete_element accepts at most 100 unique target ids per call.");

            var targets = new List<ElementId>();
            var targetUniqueIds = new Dictionary<long, string>();
            var missing = new List<long>();
            foreach (long value in requested.OrderBy(x => x))
            {
                var element = doc.GetElement(new ElementId(value));
                if (element == null) missing.Add(value);
                else
                {
                    targets.Add(element.Id);
                    targetUniqueIds[value] = element.UniqueId;
                }
            }
            if (targets.Count == 0)
                throw new InvalidOperationException("None of the requested elements currently exists.");

            bool dryRun = input["dryRun"] == null || (bool)input["dryRun"];
            if (dryRun)
                return ExecutePreview(doc, targets, targetUniqueIds, missing);

            string tokenValue = (string)input["confirmationToken"];
            PreviewRecord preview = TakeValidPreview(tokenValue, doc, targetUniqueIds);
            ICollection<ElementId> actualAffected;
            using (var transaction = new Transaction(doc, "Delete confirmed elements"))
            {
                if (transaction.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("Could not start the deletion transaction.");
                try
                {
                    actualAffected = doc.Delete(targets);
                    long[] actualIds = actualAffected.Select(x => x.Value).OrderBy(x => x).ToArray();
                    if (!actualIds.SequenceEqual(preview.AffectedIds))
                    {
                        transaction.RollBack();
                        throw new InvalidOperationException(
                            "Deletion cascade changed after preview. Nothing was deleted. Run dryRun=true again and review the new affected set.");
                    }
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Delete transaction did not commit.");
                }
                catch
                {
                    if (transaction.HasStarted() && !transaction.HasEnded()) transaction.RollBack();
                    throw;
                }
            }

            return BuildResponse(false, targets.Count, actualAffected, missing, null,
                "Deletion committed after the cascade matched the approved preview.");
        }

        private static JObject ExecutePreview(
            Document doc,
            IList<ElementId> targets,
            IDictionary<long, string> targetUniqueIds,
            IList<long> missing)
        {
            ICollection<ElementId> affected;
            using (var transaction = new Transaction(doc, "Preview element deletion"))
            {
                if (transaction.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("Could not start the deletion preview transaction.");
                try
                {
                    affected = doc.Delete(targets);
                    if (transaction.RollBack() != TransactionStatus.RolledBack)
                        throw new InvalidOperationException("Could not roll back the deletion preview.");
                }
                catch
                {
                    if (transaction.HasStarted() && !transaction.HasEnded()) transaction.RollBack();
                    throw;
                }
            }

            string token = Guid.NewGuid().ToString("N");
            var record = new PreviewRecord
            {
                DocumentKey = BuildDocumentKey(doc),
                TargetUniqueIds = new Dictionary<long, string>(targetUniqueIds),
                AffectedIds = affected.Select(x => x.Value).OrderBy(x => x).ToArray(),
                ExpiresUtc = DateTime.UtcNow.Add(PreviewLifetime)
            };
            lock (PreviewGate)
            {
                RemoveExpiredPreviews();
                Previews[token] = record;
            }
            return BuildResponse(true, targets.Count, affected, missing, token,
                "Preview only; Revit rolled back the deletion. Review affectedIds and confirm within five minutes.");
        }

        private static PreviewRecord TakeValidPreview(
            string token,
            Document doc,
            IDictionary<long, string> currentTargets)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("confirmationToken is required. Run dryRun=true first.");

            PreviewRecord record;
            lock (PreviewGate)
            {
                RemoveExpiredPreviews();
                if (!Previews.TryGetValue(token, out record))
                    throw new InvalidOperationException("The confirmation token is missing, expired, or already used.");
                Previews.Remove(token);
            }
            if (!string.Equals(record.DocumentKey, BuildDocumentKey(doc), StringComparison.Ordinal))
                throw new InvalidOperationException("The confirmation token belongs to a different Revit document.");
            if (record.TargetUniqueIds.Count != currentTargets.Count ||
                record.TargetUniqueIds.Any(pair =>
                    !currentTargets.ContainsKey(pair.Key) ||
                    !string.Equals(currentTargets[pair.Key], pair.Value, StringComparison.Ordinal)))
                throw new InvalidOperationException("The target elements changed after preview. Run dryRun=true again.");
            return record;
        }

        private static JObject BuildResponse(
            bool dryRun,
            int targetCount,
            ICollection<ElementId> affected,
            IList<long> missing,
            string token,
            string message)
        {
            int cascaded = Math.Max(0, affected.Count - targetCount);
            return new JObject
            {
                ["dryRun"] = dryRun,
                ["targetCount"] = targetCount,
                ["affectedCount"] = affected.Count,
                ["affectedIds"] = new JArray(affected.Select(x => (JToken)x.Value).OrderBy(x => (long)x)),
                ["cascadedCount"] = cascaded,
                ["missingIds"] = new JArray(missing),
                ["confirmationToken"] = dryRun ? token : null,
                ["confirmationExpiresInSeconds"] = dryRun ? (int)PreviewLifetime.TotalSeconds : (JToken)JValue.CreateNull(),
                ["message"] = message
            };
        }

        private static string BuildDocumentKey(Document doc)
        {
            string projectInfo = string.Empty;
            try { projectInfo = doc.ProjectInformation == null ? string.Empty : doc.ProjectInformation.UniqueId; }
            catch { }
            return (doc.PathName ?? string.Empty) + "|" + (doc.Title ?? string.Empty) + "|" + projectInfo;
        }

        private static void RemoveExpiredPreviews()
        {
            DateTime now = DateTime.UtcNow;
            foreach (string key in Previews.Where(x => x.Value.ExpiresUtc <= now).Select(x => x.Key).ToList())
                Previews.Remove(key);
        }

        private sealed class PreviewRecord
        {
            internal string DocumentKey;
            internal Dictionary<long, string> TargetUniqueIds;
            internal long[] AffectedIds;
            internal DateTime ExpiresUtc;
        }
    }
}

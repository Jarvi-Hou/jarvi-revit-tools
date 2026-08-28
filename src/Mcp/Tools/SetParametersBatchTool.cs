using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// Applies a batch of parameter edits atomically.
    /// </summary>
    public class SetParametersBatchTool : IRevitTool
    {
        public string Name => "set_parameters_batch";

        public string Description =>
            "Atomically set multiple Revit parameters. Prefer parameterGuid; display names are accepted only when unambiguous. Any failed edit rolls back the complete batch.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["edits"] = new JObject
                {
                    ["type"] = "array",
                    ["description"] = "Each edit provides elementId, value, and exactly one of parameterName or parameterGuid.",
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["elementId"] = new JObject { ["type"] = "integer" },
                            ["parameterName"] = new JObject { ["type"] = "string" },
                            ["parameterGuid"] = new JObject { ["type"] = "string" },
                            ["value"] = new JObject()
                        },
                        ["required"] = new JArray { "elementId", "value" },
                        ["additionalProperties"] = false
                    }
                },
                ["transactionName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional transaction label shown in Revit's undo stack."
                }
            },
            ["required"] = new JArray { "edits" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            var edits = input["edits"] as JArray
                ?? throw new ArgumentException("'edits' must be an array.");
            if (edits.Count == 0)
                throw new ArgumentException("'edits' must contain at least one entry.");

            string txName = (string)input["transactionName"];
            if (string.IsNullOrWhiteSpace(txName))
                txName = "Batch parameter update (" + edits.Count + " edits)";

            var results = new JArray();
            var failures = new List<string>();
            string firstError = null;

            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                try
                {
                    for (int i = 0; i < edits.Count; i++)
                    {
                        var edit = edits[i] as JObject;
                        if (edit == null)
                        {
                            const string invalidObject = "not an object";
                            failures.Add("edit[" + i + "]: " + invalidObject);
                            results.Add(BuildResult(null, null, null, null, null, false, invalidObject));
                            firstError = firstError ?? failures[failures.Count - 1];
                            continue;
                        }

                        long eid = 0;
                        string pname = null;
                        string pguid = null;
                        string error = null;
                        string oldValue = null;
                        string newValue = null;
                        try
                        {
                            eid = ReadLong(edit, "elementId");
                            pname = (string)edit["parameterName"];
                            pguid = (string)edit["parameterGuid"];
                            bool hasName = !string.IsNullOrWhiteSpace(pname);
                            bool hasGuid = !string.IsNullOrWhiteSpace(pguid);
                            if (hasName == hasGuid)
                                throw new ArgumentException("Provide exactly one of 'parameterName' or 'parameterGuid'.");

                            var valueToken = edit["value"] ?? throw new ArgumentException("'value' is required.");
                            var element = doc.GetElement(new ElementId(eid))
                                          ?? throw new InvalidOperationException("Element with id " + eid + " not found.");
                            var parameter = SetElementParameterTool.ResolveParameter(element, pname, pguid);
                            pname = parameter.Definition == null ? pname : parameter.Definition.Name;
                            if (parameter.IsReadOnly)
                                throw new InvalidOperationException("Parameter '" + pname + "' is read-only.");

                            oldValue = SetElementParameterTool.SafeAsValueString(parameter);
                            SetElementParameterTool.ApplyValue(parameter, valueToken);
                            newValue = SetElementParameterTool.SafeAsValueString(parameter);
                            if (string.IsNullOrWhiteSpace(pguid) && parameter.IsShared)
                                pguid = parameter.GUID.ToString("D");
                        }
                        catch (Exception ex)
                        {
                            error = ex.Message;
                            failures.Add("edit[" + i + "] (elementId=" + eid + ", param='" + (pname ?? pguid) + "'): " + error);
                            firstError = firstError ?? failures[failures.Count - 1];
                        }

                        results.Add(BuildResult(eid, pname, pguid, oldValue, newValue, error == null, error));
                    }

                    if (failures.Count > 0)
                    {
                        tx.RollBack();
                        throw new InvalidOperationException(
                            "batch_rolled_back: " + failures.Count + "/" + edits.Count +
                            " edits failed. First error: " + firstError);
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        throw new InvalidOperationException(
                            "Revit did not commit the batch. Transaction status: " + status + ".");

                    return new JObject
                    {
                        ["applied"] = edits.Count,
                        ["transactionName"] = txName,
                        ["transactionStatus"] = status.ToString(),
                        ["results"] = results
                    };
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }
        }

        private static JObject BuildResult(
            long? elementId,
            string parameterName,
            string parameterGuid,
            string oldValue,
            string newValue,
            bool ok,
            string error)
        {
            var result = new JObject
            {
                ["elementId"] = elementId.HasValue ? (JToken)elementId.Value : JValue.CreateNull(),
                ["parameterName"] = parameterName ?? (JToken)JValue.CreateNull(),
                ["parameterGuid"] = parameterGuid ?? (JToken)JValue.CreateNull(),
                ["oldValue"] = oldValue ?? (JToken)JValue.CreateNull(),
                ["newValue"] = newValue ?? (JToken)JValue.CreateNull(),
                ["ok"] = ok
            };
            if (!ok) result["error"] = error ?? "unknown";
            return result;
        }

        private static long ReadLong(JObject input, string key)
        {
            var token = input[key] ?? throw new ArgumentException("'" + key + "' is required.");
            if (token.Type == JTokenType.Null)
                throw new ArgumentException("'" + key + "' cannot be null.");
            try { return (long)token; }
            catch (Exception ex)
            {
                throw new ArgumentException("'" + key + "' must be an integer.", ex);
            }
        }
    }
}

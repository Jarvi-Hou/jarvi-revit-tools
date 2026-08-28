using System;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// Safely sets one parameter in a Revit transaction.
    /// </summary>
    public class SetElementParameterTool : IRevitTool
    {
        public string Name => "set_element_parameter";

        public string Description =>
            "Set one Revit parameter. Prefer parameterGuid for shared parameters; a display name is accepted only when it resolves unambiguously. Numeric Double values use Revit internal units, while strings are parsed by Revit using the parameter's actual unit type.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["elementId"] = new JObject { ["type"] = "integer", ["description"] = "Numeric element id." },
                ["parameterName"] = new JObject { ["type"] = "string", ["description"] = "Parameter display name. Rejected when more than one parameter has this name." },
                ["parameterGuid"] = new JObject { ["type"] = "string", ["description"] = "Shared-parameter GUID. Preferred for deterministic writes." },
                ["value"] = new JObject { ["description"] = "New value. Numbers for Double use Revit internal units; unit-bearing strings are parsed by Revit according to the parameter's actual data type." }
            },
            ["required"] = new JArray { "elementId", "value" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            long elementIdLong = ReadLong(input, "elementId");
            string parameterName = (string)input["parameterName"];
            string parameterGuid = (string)input["parameterGuid"];
            bool hasName = !string.IsNullOrWhiteSpace(parameterName);
            bool hasGuid = !string.IsNullOrWhiteSpace(parameterGuid);
            if (hasName == hasGuid)
                throw new ArgumentException("Provide exactly one of 'parameterName' or 'parameterGuid'.");

            var valueToken = input["value"] ?? throw new ArgumentException("'value' is required.");
            var elem = doc.GetElement(new ElementId(elementIdLong))
                       ?? throw new InvalidOperationException("Element with id " + elementIdLong + " not found.");
            var param = ResolveParameter(elem, parameterName, parameterGuid);
            parameterName = param.Definition == null ? parameterName : param.Definition.Name;

            if (param.IsReadOnly)
                throw new InvalidOperationException("Parameter '" + parameterName + "' is read-only.");

            string oldDisplay = SafeAsValueString(param);
            using (var tx = new Transaction(doc, "Set " + parameterName))
            {
                tx.Start();
                try
                {
                    ApplyValue(param, valueToken);
                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        throw new InvalidOperationException(
                            "Revit did not commit the parameter change. Transaction status: " + status + ".");

                    return new JObject
                    {
                        ["elementId"] = elementIdLong,
                        ["parameterName"] = parameterName,
                        ["parameterGuid"] = SafeGuid(param),
                        ["oldValue"] = oldDisplay,
                        ["newValue"] = SafeAsValueString(param),
                        ["storageType"] = param.StorageType.ToString(),
                        ["transactionStatus"] = status.ToString()
                    };
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }
        }

        internal static void ApplyValue(Parameter param, JToken valueToken)
        {
            switch (param.StorageType)
            {
                case StorageType.String:
                    var s = valueToken.Type == JTokenType.Null ? null : valueToken.ToString();
                    if (!param.Set(s))
                        throw new InvalidOperationException("Parameter.Set(string) returned false.");
                    break;

                case StorageType.Integer:
                    int iv;
                    try { iv = (int)valueToken; }
                    catch (Exception ex)
                    {
                        throw new ArgumentException(
                            "Value must be an integer for parameter '" + param.Definition.Name + "'.", ex);
                    }
                    if (!param.Set(iv))
                        throw new InvalidOperationException("Parameter.Set(int) returned false.");
                    break;

                case StorageType.Double:
                    if (valueToken.Type == JTokenType.String)
                    {
                        var raw = (string)valueToken;
                        if (string.IsNullOrWhiteSpace(raw) || !param.SetValueString(raw))
                            throw new ArgumentException(
                                "Revit could not parse '" + raw + "' for parameter '" + param.Definition.Name +
                                "'. Use a value valid for this parameter and the document units, or pass a numeric value in Revit internal units.");
                        break;
                    }

                    double dv;
                    try { dv = (double)valueToken; }
                    catch (Exception ex)
                    {
                        throw new ArgumentException(
                            "Value must be a number in Revit internal units, or a unit-bearing string valid for parameter '" +
                            param.Definition.Name + "'.", ex);
                    }
                    if (!param.Set(dv))
                        throw new InvalidOperationException("Parameter.Set(double) returned false.");
                    break;

                case StorageType.ElementId:
                    long eidLong;
                    try { eidLong = (long)valueToken; }
                    catch (Exception ex)
                    {
                        throw new ArgumentException(
                            "Value must be an integer element id for parameter '" + param.Definition.Name + "'.", ex);
                    }
                    if (!param.Set(new ElementId(eidLong)))
                        throw new InvalidOperationException("Parameter.Set(ElementId) returned false.");
                    break;

                default:
                    throw new InvalidOperationException("Unsupported StorageType: " + param.StorageType);
            }
        }

        internal static Parameter ResolveParameter(Element element, string parameterName, string parameterGuid)
        {
            if (element == null) throw new ArgumentNullException("element");

            if (!string.IsNullOrWhiteSpace(parameterGuid))
            {
                Guid guid;
                if (!Guid.TryParse(parameterGuid, out guid))
                    throw new ArgumentException("'parameterGuid' must be a valid GUID.");
                var byGuid = element.get_Parameter(guid);
                if (byGuid == null)
                    throw new InvalidOperationException(
                        "Shared parameter " + guid.ToString("D") + " was not found on element " + element.Id.Value + ".");
                return byGuid;
            }

            if (string.IsNullOrWhiteSpace(parameterName))
                throw new ArgumentException("'parameterName' is required when 'parameterGuid' is not provided.");

            var matches = element.GetParameters(parameterName);
            if (matches == null || matches.Count == 0)
                throw new InvalidOperationException(
                    "Parameter '" + parameterName + "' was not found on element " + element.Id.Value + ".");
            if (matches.Count > 1)
                throw new InvalidOperationException(
                    "Parameter name '" + parameterName + "' is ambiguous on element " + element.Id.Value +
                    " (" + matches.Count + " matches). Use 'parameterGuid' for a deterministic write.");
            return matches[0];
        }

        internal static string SafeAsValueString(Parameter p)
        {
            try
            {
                var vs = p.AsValueString();
                if (!string.IsNullOrEmpty(vs)) return vs;
                switch (p.StorageType)
                {
                    case StorageType.String: return p.AsString();
                    case StorageType.Integer: return p.AsInteger().ToString(CultureInfo.InvariantCulture);
                    case StorageType.Double: return p.AsDouble().ToString(CultureInfo.InvariantCulture);
                    case StorageType.ElementId: return p.AsElementId().Value.ToString(CultureInfo.InvariantCulture);
                    default: return null;
                }
            }
            catch { return null; }
        }

        internal static JToken SafeGuid(Parameter parameter)
        {
            try
            {
                if (parameter != null && parameter.IsShared)
                    return parameter.GUID.ToString("D");
            }
            catch { }
            return JValue.CreateNull();
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

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// Read all readable parameters of a single element.
    /// Inputs: { elementId: int (required) }
    /// </summary>
    public class GetElementParametersTool : IRevitTool
    {
        public string Name => "get_element_parameters";

        public string Description =>
            "读取单个元素的所有参数名称和值。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["elementId"] = new JObject
                {
                    ["type"] = "integer",
                    ["description"] = "Integer ElementId of the element to inspect."
                }
            },
            ["required"] = new JArray { "elementId" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null)
                throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument;
            if (uidoc == null)
                throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document;
            if (doc == null)
                throw new InvalidOperationException("Active UIDocument has no Document.");

            if (input == null)
                throw new ArgumentException("Input is required.");

            var idToken = input["elementId"];
            if (idToken == null || idToken.Type == JTokenType.Null)
                throw new ArgumentException("'elementId' is required.");

            long idLong;
            try { idLong = (long)idToken; }
            catch (Exception ex) { throw new ArgumentException("'elementId' must be an integer.", ex); }

            var elemId = new ElementId(idLong);
            var elem = doc.GetElement(elemId);
            if (elem == null)
                throw new InvalidOperationException("Element with id " + idLong + " not found.");

            string categoryName = null;
            try
            {
                if (elem.Category != null)
                    categoryName = elem.Category.Name;
            }
            catch
            {
                categoryName = null;
            }

            var parameters = new JArray();
            // Deduplicate by parameter name (some elements expose duplicate definitions).
            var seenNames = new HashSet<string>(StringComparer.Ordinal);

            var paramSet = elem.Parameters;
            if (paramSet != null)
            {
                foreach (Parameter p in paramSet)
                {
                    if (p == null) continue;

                    string pname = SafeGetParamName(p);
                    if (string.IsNullOrEmpty(pname)) continue;
                    if (!seenNames.Add(pname)) continue;

                    parameters.Add(BuildParameterEntry(p, pname));
                }
            }

            return new JObject
            {
                ["elementId"] = idLong,
                ["category"] = categoryName == null ? (JToken)JValue.CreateNull() : categoryName,
                ["parameters"] = parameters
            };
        }

        private static string SafeGetParamName(Parameter p)
        {
            try
            {
                var def = p.Definition;
                return def != null ? def.Name : null;
            }
            catch
            {
                return null;
            }
        }

        private static JObject BuildParameterEntry(Parameter p, string name)
        {
            string storageType;
            try { storageType = p.StorageType.ToString(); }
            catch { storageType = "Unknown"; }

            bool isReadOnly = true;
            try { isReadOnly = p.IsReadOnly; } catch { isReadOnly = true; }

            string displayValue = null;
            try
            {
                displayValue = p.AsValueString();
                if (string.IsNullOrEmpty(displayValue))
                {
                    // Fall back to raw representation based on storage type.
                    switch (p.StorageType)
                    {
                        case StorageType.String:
                            displayValue = p.AsString();
                            break;
                        case StorageType.Integer:
                            displayValue = p.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture);
                            break;
                        case StorageType.Double:
                            displayValue = p.AsDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
                            break;
                        case StorageType.ElementId:
                            var eid = p.AsElementId();
                            displayValue = eid != null ? eid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
                            break;
                    }
                }
            }
            catch
            {
                displayValue = null;
            }

            return new JObject
            {
                ["name"] = name,
                ["value"] = displayValue == null ? (JToken)JValue.CreateNull() : displayValue,
                // Phase 1: unit is intentionally null. Values from AsValueString() are already
                // formatted per project units; explicit unit metadata is future work.
                ["unit"] = JValue.CreateNull(),
                ["isReadOnly"] = isReadOnly,
                ["storageType"] = storageType
            };
        }
    }
}

using System;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using JarviTools.Mcp.Units;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// Create a new Level in the active document at a given elevation, wrapped in a Transaction.
    /// Elevation accepts either a number (assumed feet) or a unit-bearing string ("3m", "10ft", "3000mm").
    /// </summary>
    public class CreateLevelTool : IRevitTool
    {
        public string Name => "create_level";

        public string Description =>
            "创建新标高。标高名称必须唯一。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["name"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Display name for the new level (must be unique within the project)."
                },
                ["elevation"] = new JObject
                {
                    ["type"] = new JArray { "number", "string" },
                    ["description"] = "Elevation. Number = Revit internal units (feet). String may include units (e.g., '3m', '10ft', '3000mm', '3'-6\")."
                }
            },
            ["required"] = new JArray { "name", "elevation" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc   = uidoc.Document       ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            string name = (string)input["name"];
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("'name' is required and must be non-empty.");

            var elevationToken = input["elevation"] ?? throw new ArgumentException("'elevation' is required.");
            double elevationFeet = ParseElevation(elevationToken);

            string txName = "Create level: " + name;

            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                try
                {
                    Level level;
                    try
                    {
                        level = Level.Create(doc, elevationFeet);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            "Failed to create Level at elevation " +
                            elevationFeet.ToString("G", CultureInfo.InvariantCulture) + " ft: " + ex.Message, ex);
                    }
                    if (level == null)
                        throw new InvalidOperationException("Level.Create returned null.");

                    try
                    {
                        level.Name = name;
                    }
                    catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                    {
                        // Most common cause: a Level with this name already exists.
                        throw new InvalidOperationException(
                            "Cannot set level name to '" + name + "'. A level with this name may already exist, " +
                            "or the name contains invalid characters. (" + ex.Message + ")", ex);
                    }
                    catch (ArgumentException ex)
                    {
                        throw new InvalidOperationException(
                            "Cannot set level name to '" + name + "'. A level with this name may already exist, " +
                            "or the name contains invalid characters. (" + ex.Message + ")", ex);
                    }

                    JarviTools.Core.TransactionSafety.Commit(tx, "Create level");

                    return new JObject
                    {
                        ["elementId"]        = level.Id.Value,
                        ["name"]             = level.Name,
                        ["elevationFeet"]    = elevationFeet,
                        ["elevationDisplay"] = FormatElevationDisplay(elevationFeet)
                    };
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }
        }

        private static double ParseElevation(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return (double)token;
                case JTokenType.String:
                    var s = (string)token;
                    try { return UnitParser.ParseLengthToFeet(s); }
                    catch (FormatException ex)
                    {
                        throw new ArgumentException("Cannot parse elevation '" + s + "': " + ex.Message, ex);
                    }
                default:
                    throw new ArgumentException("'elevation' must be a number (feet) or a unit-bearing string.");
            }
        }

        private static string FormatElevationDisplay(double feet)
        {
            // Lightweight, locale-stable display. Not a substitute for Revit's UnitFormatUtils,
            // but useful confirmation for the caller.
            return feet.ToString("G", CultureInfo.InvariantCulture) + " ft";
        }
    }
}

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace JarviTools.Mcp.Units
{
    /// <summary>
    /// Lightweight parser turning human-friendly unit strings ("1000mm", "3'-6\"", "90deg")
    /// into Revit internal units (length=feet, angle=radians).
    ///
    /// Intentionally avoids Revit's own UnitFormatUtils because that API requires
    /// ForgeTypeId / version-specific Spec handling. The supported formats here cover
    /// the 99% case for length and angle inputs from LLMs.
    /// </summary>
    public static class UnitParser
    {
        // Conversion factors to Revit internal units.
        private const double FeetPerMillimeter = 1.0 / 304.8;
        private const double FeetPerCentimeter = 1.0 / 30.48;
        private const double FeetPerMeter      = 1.0 / 0.3048;
        private const double FeetPerInch       = 1.0 / 12.0;

        // Single number with optional decimal, optional leading sign.
        //   Group 1 = number, Group 2 = suffix (may be empty)
        private static readonly Regex SimpleLengthRegex = new Regex(
            @"^\s*(-?\d+(?:\.\d+)?)\s*([a-zA-Z""”']*)\s*$",
            RegexOptions.Compiled);

        // Feet-inches imperial: 3'-6", 3' 6", 3'6", 3'  (trailing inches optional)
        //   Group 1 = feet, Group 2 = inches (optional)
        private static readonly Regex FeetInchesRegex = new Regex(
            @"^\s*(-?\d+(?:\.\d+)?)\s*'(?:\s*-?\s*(\d+(?:\.\d+)?)\s*[""”]?)?\s*$",
            RegexOptions.Compiled);

        // Angle: number followed by optional unit suffix (deg, °, rad).
        private static readonly Regex AngleRegex = new Regex(
            @"^\s*(-?\d+(?:\.\d+)?)\s*([a-zA-Z°]*)\s*$",
            RegexOptions.Compiled);

        /// <summary>
        /// Parse a length string into Revit internal units (feet).
        /// Supported formats (case-insensitive, whitespace ignored):
        ///   "1000mm" "100cm" "1m" "1.5 m"
        ///   "36in"   "36 in" "36\""
        ///   "3ft" "3 ft" "3'" "3.5'"
        ///   "3'-6\"" "3'6\""   (feet-inches imperial)
        ///   "1000"             (bare number — assumed already in feet)
        /// Throws FormatException for unparseable input.
        /// </summary>
        public static double ParseLengthToFeet(string input)
        {
            if (input == null)
                throw new FormatException("Length string is null.");

            string trimmed = input.Trim();
            if (trimmed.Length == 0)
                throw new FormatException("Length string is empty.");

            // Try feet-inches imperial first (it contains an apostrophe, which the
            // simple regex would also match but interpret incorrectly).
            var fim = FeetInchesRegex.Match(trimmed);
            if (fim.Success)
            {
                double feet = double.Parse(fim.Groups[1].Value, CultureInfo.InvariantCulture);
                double inches = 0.0;
                if (fim.Groups[2].Success && fim.Groups[2].Length > 0)
                    inches = double.Parse(fim.Groups[2].Value, CultureInfo.InvariantCulture);
                // Preserve sign of feet for the inches part.
                double sign = feet < 0 ? -1.0 : 1.0;
                return feet + sign * inches * FeetPerInch;
            }

            var m = SimpleLengthRegex.Match(trimmed);
            if (!m.Success)
                throw new FormatException("Cannot parse length '" + input + "'. Expected formats like '1000mm', '3m', '36in', \"3'-6\\\"\".");

            double value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            string suffix = m.Groups[2].Value.Trim().ToLowerInvariant();

            switch (suffix)
            {
                case "":
                    // Bare number — already feet (backward compat).
                    return value;
                case "mm":
                case "millimeter":
                case "millimeters":
                    return value * FeetPerMillimeter;
                case "cm":
                case "centimeter":
                case "centimeters":
                    return value * FeetPerCentimeter;
                case "m":
                case "meter":
                case "meters":
                    return value * FeetPerMeter;
                case "in":
                case "inch":
                case "inches":
                case "\"":
                case "”":
                    return value * FeetPerInch;
                case "ft":
                case "foot":
                case "feet":
                case "'":
                    return value;
                default:
                    throw new FormatException("Unknown length unit '" + m.Groups[2].Value + "' in '" + input + "'. Supported: mm, cm, m, in, ft, ', \".");
            }
        }

        /// <summary>
        /// Parse an angle string into Revit internal units (radians).
        /// Supported: "90deg" "90°" "1.5708rad" "1.5708"  (bare = radians)
        /// </summary>
        public static double ParseAngleToRadians(string input)
        {
            if (input == null)
                throw new FormatException("Angle string is null.");

            string trimmed = input.Trim();
            if (trimmed.Length == 0)
                throw new FormatException("Angle string is empty.");

            var m = AngleRegex.Match(trimmed);
            if (!m.Success)
                throw new FormatException("Cannot parse angle '" + input + "'. Expected formats like '90deg', '90°', '1.5708rad'.");

            double value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            string suffix = m.Groups[2].Value.Trim().ToLowerInvariant();

            switch (suffix)
            {
                case "":
                case "rad":
                case "radian":
                case "radians":
                    return value;
                case "deg":
                case "degree":
                case "degrees":
                case "°":
                    return value * Math.PI / 180.0;
                default:
                    throw new FormatException("Unknown angle unit '" + m.Groups[2].Value + "' in '" + input + "'. Supported: deg, °, rad.");
            }
        }
    }
}

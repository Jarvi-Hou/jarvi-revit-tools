using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// Door-width screening only. A Revit family's nominal width or rough opening
    /// is not proof of the installed door's clear opening.
    /// </summary>
    public class CheckDoorClearWidthsTool : IRevitTool
    {
        private const double DefaultMinWidthInches = 32.0;
        private const double InchesPerFoot = 12.0;

        public string Name => "check_door_clear_widths";

        public string Description =>
            "门宽初筛（非规范合规判定）。优先读取明确的净宽参数；只有名义宽/洞口宽时仅作风险筛查，默认阈值 32 英寸。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["minWidthInches"] = new JObject
                {
                    ["type"]        = "number",
                    ["description"] = "Screening threshold in inches. Default 32. This does not establish code compliance.",
                    ["default"]     = DefaultMinWidthInches,
                    ["minimum"]     = 1
                }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc   = uidoc.Document       ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            double minWidthInches = DefaultMinWidthInches;
            if (input != null)
            {
                var t = input["minWidthInches"];
                if (t != null && t.Type != JTokenType.Null)
                {
                    try { minWidthInches = (double)t; }
                    catch (Exception ex) { throw new ArgumentException("'minWidthInches' must be a number.", ex); }
                    if (minWidthInches <= 0) throw new ArgumentException("'minWidthInches' must be > 0.");
                }
            }
            double minWidthFeet = minWidthInches / InchesPerFoot;

            var doors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .ToElements();

            int passScreen = 0;
            var failScreen = new JArray();
            var unknown = new JArray();

            foreach (var elem in doors)
            {
                if (elem == null) continue;

                DoorWidthMeasurement measurement = TryGetDoorWidthFeet(elem);
                if (measurement == null)
                {
                    unknown.Add(BuildDoorIdentity(doc, elem));
                    continue;
                }

                if (measurement.WidthFeet >= minWidthFeet) { passScreen++; continue; }

                double widthInches = measurement.WidthFeet * InchesPerFoot;
                double deficit     = minWidthInches - widthInches;

                var item = BuildDoorIdentity(doc, elem);
                item.Merge(new JObject
                {
                    ["widthFeet"]       = Math.Round(measurement.WidthFeet, 4),
                    ["widthInches"]     = Math.Round(widthInches, 2),
                    ["shortfallInches"] = Math.Round(deficit, 2),
                    ["measurementSource"] = measurement.Source,
                    ["isExplicitClearWidth"] = measurement.IsExplicitClearWidth
                });
                failScreen.Add(item);
            }

            var ordered = failScreen
                .OfType<JObject>()
                .OrderByDescending(j => (double)j["shortfallInches"])
                .ToList();
            var orderedArr = new JArray();
            foreach (var j in ordered) orderedArr.Add(j);

            return new JObject
            {
                ["resultType"]          = "screening_only",
                ["disclaimer"]          = "名义宽、洞口宽或族参数不等于安装后实际净开口；本结果不能作为规范合规证明。",
                ["thresholdInches"]     = minWidthInches,
                ["totalDoors"]          = doors.Count,
                ["passScreenCount"]     = passScreen,
                ["failScreenCount"]     = orderedArr.Count,
                ["unknownCount"]        = unknown.Count,
                ["failScreenDoors"]     = orderedArr,
                ["unknownDoors"]        = unknown
            };
        }

        // ---- helpers ----

        /// <summary>
        /// Look for a width-like parameter on the door, preferring built-in DOOR_WIDTH,
        /// then common display names. Returns null if nothing readable found.
        /// </summary>
        private sealed class DoorWidthMeasurement
        {
            public double WidthFeet;
            public string Source;
            public bool IsExplicitClearWidth;
        }

        private static DoorWidthMeasurement TryGetDoorWidthFeet(Element elem)
        {
            string[] clearWidthNames = { "Clear Width", "Clear Opening Width", "净宽", "有效净宽", "开启净宽" };
            DoorWidthMeasurement explicitWidth = TryNamedWidth(elem, clearWidthNames, true, "instance:");
            if (explicitWidth != null) return explicitWidth;

            var instance = elem as FamilyInstance;
            explicitWidth = TryNamedWidth(instance == null ? null : instance.Symbol, clearWidthNames, true, "type:");
            if (explicitWidth != null) return explicitWidth;

            // Built-in DOOR_WIDTH is a useful screening input but is normally nominal width.
            try
            {
                var p = elem.get_Parameter(BuiltInParameter.DOOR_WIDTH);
                if (p != null && p.StorageType == StorageType.Double && p.HasValue)
                    return new DoorWidthMeasurement
                    {
                        WidthFeet = p.AsDouble(),
                        Source = "BuiltInParameter.DOOR_WIDTH",
                        IsExplicitClearWidth = false
                    };
            }
            catch { /* fall through */ }

            string[] nominalNames = { "Width", "Rough Width", "宽度", "洞口宽" };
            DoorWidthMeasurement nominal = TryNamedWidth(elem, nominalNames, false, "instance:");
            if (nominal != null) return nominal;
            return TryNamedWidth(instance == null ? null : instance.Symbol, nominalNames, false, "type:");
        }

        private static DoorWidthMeasurement TryNamedWidth(Element element, IEnumerable<string> names,
                                                           bool explicitClearWidth, string sourcePrefix)
        {
            if (element == null) return null;
            foreach (var name in names)
            {
                try
                {
                    var p = element.LookupParameter(name);
                    if (p != null && p.StorageType == StorageType.Double && p.HasValue)
                        return new DoorWidthMeasurement
                        {
                            WidthFeet = p.AsDouble(),
                            Source = sourcePrefix + name,
                            IsExplicitClearWidth = explicitClearWidth
                        };
                }
                catch { /* try next */ }
            }
            return null;
        }

        private static JObject BuildDoorIdentity(Document doc, Element elem)
        {
            return new JObject
            {
                ["id"] = elem.Id.Value,
                ["uniqueId"] = elem.UniqueId,
                ["familyName"] = SafeFamilyName(elem),
                ["typeName"] = SafeTypeName(elem),
                ["levelName"] = SafeLevelName(doc, elem)
            };
        }

        private static string SafeFamilyName(Element elem)
        {
            try { return (elem as FamilyInstance)?.Symbol?.FamilyName; }
            catch { return null; }
        }

        private static string SafeTypeName(Element elem)
        {
            try
            {
                var typeId = elem.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId) return null;
                var type = elem.Document.GetElement(typeId);
                return type?.Name;
            }
            catch { return null; }
        }

        private static string SafeLevelName(Document doc, Element elem)
        {
            try
            {
                if (elem.LevelId == null || elem.LevelId == ElementId.InvalidElementId) return null;
                return doc.GetElement(elem.LevelId)?.Name;
            }
            catch { return null; }
        }
    }
}

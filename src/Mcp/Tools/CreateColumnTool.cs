using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 在项目中创建结构柱。输入坐标（单位米）和标高。
    /// Transaction 包裹。
    /// </summary>
    public class CreateColumnTool : IRevitTool
    {
        public string Name => "create_column";
        public string Description =>
            "在指定 XY 坐标（米）创建结构柱。可选 columnTypeId。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["x"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "X coordinate in meters."
                },
                ["y"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Y coordinate in meters."
                },
                ["levelId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Level ElementId to place the column on."
                },
                ["columnTypeId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Optional FamilySymbol ElementId. Defaults to first column type."
                }
            },
            ["required"] = new JArray { "x", "y", "levelId" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            double xM = (double)input["x"];
            double yM = (double)input["y"];
            var levelId = new ElementId((long)input["levelId"]);

            var level = doc.GetElement(levelId) as Level;
            if (level == null)
                throw new ArgumentException("levelId " + levelId.Value + " does not refer to a Level element.");

            // --- 找 FamilySymbol ---
            FamilySymbol symbol = null;
            var ctToken = input["columnTypeId"];
            if (ctToken != null && ctToken.Type != JTokenType.Null)
            {
                symbol = doc.GetElement(new ElementId((long)ctToken)) as FamilySymbol;
                if (symbol == null)
                    throw new ArgumentException("columnTypeId " + (long)ctToken + " does not refer to a valid FamilySymbol.");
            }
            else
            {
                symbol = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StructuralColumns)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .FirstOrDefault();
                if (symbol == null)
                {
                    symbol = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Columns)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .FirstOrDefault();
                }
                if (symbol == null)
                    throw new InvalidOperationException("No column FamilySymbol found in project.");
            }

            if (!symbol.IsActive)
            {
                using (var txActivate = new Transaction(doc, "Activate column symbol"))
                {
                    txActivate.Start();
                    symbol.Activate();
                    JarviTools.Core.TransactionSafety.Commit(txActivate, "Activate column type");
                }
            }

            // --- 创建（米 → 英尺）---
            var location = new XYZ(xM / 0.3048, yM / 0.3048, 0);
            FamilyInstance fi = null;

            using (var tx = new Transaction(doc, "Create column"))
            {
                tx.Start();
                try
                {
                    fi = doc.Create.NewFamilyInstance(location, symbol, level,
                        Autodesk.Revit.DB.Structure.StructuralType.Column);
                    if (fi == null)
                        throw new InvalidOperationException("NewFamilyInstance returned null.");
                    JarviTools.Core.TransactionSafety.Commit(tx, "Create column");
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }

            return new JObject
            {
                ["columnId"] = fi.Id.Value,
                ["levelName"] = level.Name ?? (JToken)JValue.CreateNull(),
                ["columnTypeName"] = symbol.Name ?? (JToken)JValue.CreateNull(),
                ["location"] = new JObject
                {
                    ["x"] = xM,
                    ["y"] = yM
                }
            };
        }
    }
}

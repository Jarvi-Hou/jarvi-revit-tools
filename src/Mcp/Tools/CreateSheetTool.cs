using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 创建图纸。设置图纸编号和名称。
    /// 可选 titleBlockId，默认取项目第一个图框。
    /// Transaction 包裹，图框 Activate 放在独立小 Transaction。
    /// </summary>
    public class CreateSheetTool : IRevitTool
    {
        public string Name => "create_sheet";
        public string Description =>
            "创建新图纸。可选的 titleBlockId 默认使用第一个图框族。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["sheetNumber"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Sheet number, e.g. 'A-101'."
                },
                ["sheetName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Sheet name, e.g. 'Floor Plan'."
                },
                ["titleBlockId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Optional title block FamilySymbol ElementId."
                }
            },
            ["required"] = new JArray { "sheetNumber", "sheetName" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            string sheetNumber = (string)input["sheetNumber"];
            string sheetName = (string)input["sheetName"];

            ElementId tbId = ElementId.InvalidElementId;
            var tbToken = input["titleBlockId"];
            if (tbToken != null && tbToken.Type != JTokenType.Null)
            {
                tbId = new ElementId((long)tbToken);
            }
            else
            {
                var firstTb = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .FirstOrDefault();
                if (firstTb != null)
                {
                    if (!firstTb.IsActive)
                    {
                        using (var txA = new Transaction(doc, "Activate titleblock"))
                        {
                            txA.Start();
                            try { firstTb.Activate(); JarviTools.Core.TransactionSafety.Commit(txA, "Activate title block"); }
                            catch { if (txA.HasStarted() && !txA.HasEnded()) txA.RollBack(); throw; }
                        }
                    }
                    tbId = firstTb.Id;
                }
            }

            if (tbId == ElementId.InvalidElementId)
                throw new InvalidOperationException("No title block available. Provide a 'titleBlockId' or load one first.");

            ViewSheet sheet = null;
            string tbName = null;

            using (var tx = new Transaction(doc, "Create sheet"))
            {
                tx.Start();
                try
                {
                    sheet = ViewSheet.Create(doc, tbId);
                    sheet.SheetNumber = sheetNumber;
                    sheet.Name = sheetName;

                    var tbSymbol = doc.GetElement(tbId) as FamilySymbol;
                    if (tbSymbol != null) tbName = tbSymbol.Name;

                    JarviTools.Core.TransactionSafety.Commit(tx, "Create sheet");
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }

            return new JObject
            {
                ["sheetId"] = sheet.Id.Value,
                ["sheetNumber"] = sheet.SheetNumber ?? (JToken)JValue.CreateNull(),
                ["sheetName"] = sheet.Name ?? (JToken)JValue.CreateNull(),
                ["titleBlockName"] = tbName ?? (JToken)JValue.CreateNull()
            };
        }
    }
}

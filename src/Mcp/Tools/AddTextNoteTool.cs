using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 在视图中添加文字注释。输入坐标（单位米）和文本。
    /// Transaction 包裹。
    /// </summary>
    public class AddTextNoteTool : IRevitTool
    {
        public string Name => "add_text_note";
        public string Description =>
            "在视图的指定坐标（米）处添加文字注释。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["viewId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "ElementId of the target view."
                },
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
                ["z"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Optional Z coordinate in meters (default 0)."
                },
                ["text"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Text content of the note."
                },
                ["textNoteTypeId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Optional TextNoteType ElementId."
                }
            },
            ["required"] = new JArray { "viewId", "x", "y", "text" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            long viewIdLong = (long)input["viewId"];
            var viewId = new ElementId(viewIdLong);
            double xM = (double)input["x"];
            double yM = (double)input["y"];
            double zM = 0;
            var zToken = input["z"];
            if (zToken != null && zToken.Type != JTokenType.Null)
                zM = (double)zToken;

            string text = (string)input["text"];
            if (string.IsNullOrEmpty(text))
                throw new ArgumentException("'text' is required and must be non-empty.");

            // 找 TextNoteType
            ElementId typeId;
            var tntToken = input["textNoteTypeId"];
            if (tntToken != null && tntToken.Type != JTokenType.Null)
            {
                typeId = new ElementId((long)tntToken);
            }
            else
            {
                var firstType = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .FirstOrDefault();
                if (firstType == null)
                    throw new InvalidOperationException("No TextNoteType found in project.");
                typeId = firstType.Id;
            }

            var pt = new XYZ(xM / 0.3048, yM / 0.3048, zM / 0.3048);
            TextNote note = null;
            string viewName = null;

            using (var tx = new Transaction(doc, "Add text note"))
            {
                tx.Start();
                try
                {
                    note = TextNote.Create(doc, viewId, pt, text, typeId);
                    var view = doc.GetElement(viewId) as View;
                    if (view != null) viewName = view.Name;
                    JarviTools.Core.TransactionSafety.Commit(tx, "Add text note");
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }

            return new JObject
            {
                ["textNoteId"] = note.Id.Value,
                ["viewName"] = viewName ?? (JToken)JValue.CreateNull(),
                ["location"] = new JObject
                {
                    ["x"] = xM,
                    ["y"] = yM,
                    ["z"] = zM
                },
                ["text"] = text
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 列出所有修订（Revision）信息及其应用的图纸。
    /// 可选 sheetNumber 过滤到单张图纸。
    /// </summary>
    public class GetSheetRevisionsTool : IRevitTool
    {
        public string Name => "get_sheet_revisions";
        public string Description =>
            "列出所有修订及应用该修订的图纸。可选 sheetNumber 过滤。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["sheetNumber"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional sheet number filter, e.g. 'A-101'."
                }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            string filterSheet = null;
            if (input != null)
            {
                var token = input["sheetNumber"];
                if (token != null && token.Type != JTokenType.Null)
                    filterSheet = (string)token;
            }

            // 读取所有修订
            var revisions = new FilteredElementCollector(doc)
                .OfClass(typeof(Revision))
                .Cast<Revision>()
                .OrderBy(r => r.SequenceNumber)
                .ToList();

            // 读取所有图纸，建立 sheetNumber → revisionIds 反向映射
            var revisionSheets = new Dictionary<ElementId, List<JObject>>();
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>();

            foreach (var sheet in sheets)
            {
                // 如果传了 sheetNumber 过滤
                if (!string.IsNullOrEmpty(filterSheet) &&
                    !string.Equals(sheet.SheetNumber, filterSheet, StringComparison.OrdinalIgnoreCase))
                    continue;

                var revIds = sheet.GetAllRevisionIds();
                foreach (var rid in revIds)
                {
                    if (!revisionSheets.ContainsKey(rid))
                        revisionSheets[rid] = new List<JObject>();
                    revisionSheets[rid].Add(new JObject
                    {
                        ["sheetNumber"] = sheet.SheetNumber ?? (JToken)JValue.CreateNull(),
                        ["sheetName"] = sheet.Name ?? (JToken)JValue.CreateNull()
                    });
                }
            }

            var revisionsArr = new JArray();
            foreach (var rev in revisions)
            {
                string revDesc = null;
                try { revDesc = rev.Description; } catch { }
                string revDate = null;
                try { revDate = rev.RevisionDate; } catch { }

                var sheetsForRev = revisionSheets.TryGetValue(rev.Id, out var sList) ? sList : new List<JObject>();

                revisionsArr.Add(new JObject
                {
                    ["revisionId"] = rev.Id.Value,
                    ["sequenceNumber"] = rev.SequenceNumber,
                    ["revisionDate"] = revDate ?? (JToken)JValue.CreateNull(),
                    ["description"] = revDesc ?? (JToken)JValue.CreateNull(),
                    ["appliedToSheets"] = new JArray(sheetsForRev)
                });
            }

            return new JObject
            {
                ["revisions"] = revisionsArr,
                ["total"] = revisions.Count
            };
        }
    }
}

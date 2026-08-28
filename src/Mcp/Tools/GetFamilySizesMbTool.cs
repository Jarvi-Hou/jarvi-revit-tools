using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 获取族保存为 RFA 后的文件大小（MB）。SLOW: 逐个打开内嵌族文档，
    /// 保存到本机临时目录测量后立即删除。用 maxFamilies 限制扫描数量。
    /// </summary>
    public class GetFamilySizesMbTool : IRevitTool
    {
        public string Name => "get_family_sizes_mb";
        public string Description =>
            "逐个打开可编辑族，将副本短暂保存到系统临时目录后测量 RFA 文件大小（MB）。默认最多扫描 50 个，最多 100 个。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["maxFamilies"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Maximum families to scan (default 50). Set higher carefully.",
                    ["default"] = 50
                }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            int maxFamilies = 50;
            if (input != null)
            {
                var token = input["maxFamilies"];
                if (token != null && token.Type != JTokenType.Null)
                    maxFamilies = (int)token;
            }
            if (maxFamilies < 1 || maxFamilies > 100)
                throw new ArgumentOutOfRangeException("maxFamilies", "maxFamilies must be between 1 and 100.");

            var families = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .OrderBy(f =>
                {
                    try { return f.Name; } catch { return ""; }
                })
                .ToList();

            int total = families.Count;
            int scanned = 0;
            int skipped = 0;
            var results = new JArray();

            foreach (var f in families)
            {
                if (scanned >= maxFamilies)
                {
                    skipped = total - scanned;
                    break;
                }

                string catName = null;
                try { catName = f.Category?.Name; } catch { }

                bool isInPlace = false;
                try { isInPlace = f.IsInPlace; } catch { }

                if (isInPlace)
                {
                    results.Add(new JObject
                    {
                        ["id"] = f.Id.Value,
                        ["name"] = f.Name ?? (JToken)JValue.CreateNull(),
                        ["category"] = catName ?? (JToken)JValue.CreateNull(),
                        ["sizeMB"] = JValue.CreateNull(),
                        ["reason"] = "in-place"
                    });
                    scanned++;
                    continue;
                }

                string reason;
                double? sizeMB = MeasureSavedFamilySize(doc, f, out reason);

                results.Add(new JObject
                {
                    ["id"] = f.Id.Value,
                    ["name"] = f.Name ?? (JToken)JValue.CreateNull(),
                    ["category"] = catName ?? (JToken)JValue.CreateNull(),
                    ["sizeMB"] = sizeMB.HasValue ? (JToken)(Math.Round(sizeMB.Value, 2)) : JValue.CreateNull(),
                    ["reason"] = reason ?? (JToken)JValue.CreateNull()
                });
                scanned++;
            }

            return new JObject
            {
                ["families"] = results,
                ["scanned"] = scanned,
                ["skipped"] = skipped,
                ["total"] = total
            };
        }

        /// <summary>供 F2 get_family_sizes_top_n 复用的扫描方法。</summary>
        internal static List<FamilySizeInfo> ScanFamilySizes(Document doc, int maxFamilies)
        {
            var families = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .OrderBy(f =>
                {
                    try { return f.Name; } catch { return ""; }
                })
                .ToList();

            var results = new List<FamilySizeInfo>();
            int scanned = 0;

            foreach (var f in families)
            {
                if (scanned >= maxFamilies) break;

                bool isInPlace = false;
                try { isInPlace = f.IsInPlace; } catch { }
                if (isInPlace) { scanned++; continue; }

                string catName = null;
                try { catName = f.Category?.Name; } catch { }

                string ignoredReason;
                double? sizeMB = MeasureSavedFamilySize(doc, f, out ignoredReason);

                results.Add(new FamilySizeInfo
                {
                    Id = f.Id.Value,
                    Name = f.Name,
                    Category = catName,
                    SizeMB = sizeMB
                });
                scanned++;
            }

            return results;
        }

        private static double? MeasureSavedFamilySize(
            Document projectDocument,
            Family family,
            out string reason)
        {
            reason = null;
            Document familyDocument = null;
            string tempDirectory = null;
            try
            {
                familyDocument = projectDocument.EditFamily(family);
                tempDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "OpenRevit-FamilySize-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDirectory);
                string safeName = string.Join("_", (family.Name ?? "Family")
                    .Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "Family";
                string tempPath = Path.Combine(tempDirectory, safeName + ".rfa");
                var options = new SaveAsOptions { OverwriteExistingFile = true };
                familyDocument.SaveAs(tempPath, options);
                if (!File.Exists(tempPath))
                {
                    reason = "temporary RFA was not created";
                    return null;
                }
                return new FileInfo(tempPath).Length / 1024.0 / 1024.0;
            }
            catch (Exception ex)
            {
                reason = "family measurement failed: " + ex.Message;
                return null;
            }
            finally
            {
                if (familyDocument != null)
                {
                    try { familyDocument.Close(false); } catch { }
                }
                if (!string.IsNullOrWhiteSpace(tempDirectory) && Directory.Exists(tempDirectory))
                {
                    try { Directory.Delete(tempDirectory, true); } catch { }
                }
            }
        }
    }

    /// <summary>F1/F2 共用内部数据结构。</summary>
    internal class FamilySizeInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public double? SizeMB { get; set; }
    }
}

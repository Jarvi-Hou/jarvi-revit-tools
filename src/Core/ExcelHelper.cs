using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace JarviTools.Core
{
    /// <summary>
    /// 写真正的 .xlsx (OOXML SpreadsheetML)。
    ///
    /// 零外部依赖,仅用 .NET 4.8 自带的 System.IO.Compression。
    /// 替代了之前生成 SpreadsheetML 2003 (XML) 再起 .xls 扩展名的做法,
    /// 后者会让 Excel 双击时弹"文件格式和扩展名不匹配"警告。
    /// </summary>
    public static class ExcelHelper
    {
        // ==================== 公共入口 ====================

        /// <summary>
        /// 一次性写一个完整的 .xlsx 文件。
        /// 工作表顺序:汇总在最前,其后按 sheets 字典的字典序;
        /// 如果 sheets 中包含 SHEET_EXCLUDED,它会被保留在末尾。
        /// </summary>
        /// <param name="filePath">绝对路径,后缀建议 .xlsx</param>
        /// <param name="summaryCounts">汇总表行 (类别名 → 数量)</param>
        /// <param name="sheets">数据工作表 (sheetName → 元素列表)</param>
        /// <param name="customHeaders">可选:在标准 6 列之后插入的自定义列名 (对应 ElementData 的 MajorName/Subcontractor/ShouldExport)</param>
        public static void Write(string filePath,
                                 Dictionary<string, int> summaryCounts,
                                 Dictionary<string, List<ElementData>> sheets,
                                 List<string> customHeaders = null)
        {
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentNullException(nameof(filePath));
            if (summaryCounts == null) summaryCounts = new Dictionary<string, int>();
            if (sheets == null) sheets = new Dictionary<string, List<ElementData>>();

            // 排序:除已排除构件外按字典序,已排除放最后
            var orderedDataSheets = sheets
                .Where(kv => kv.Key != Constants.SHEET_EXCLUDED)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();
            if (sheets.ContainsKey(Constants.SHEET_EXCLUDED))
                orderedDataSheets.Add(new KeyValuePair<string, List<ElementData>>(
                    Constants.SHEET_EXCLUDED, sheets[Constants.SHEET_EXCLUDED]));

            var sheetSpecs = new List<SheetSpec>();
            sheetSpecs.Add(BuildSummarySheet(summaryCounts));
            foreach (var kv in orderedDataSheets)
                sheetSpecs.Add(BuildDataSheet(kv.Key, kv.Value, customHeaders));
            EnsureUniqueSheetNames(sheetSpecs);

            using (var stream = File.Create(filePath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "[Content_Types].xml", BuildContentTypes(sheetSpecs.Count));
                WriteEntry(archive, "_rels/.rels",          BuildRootRels());
                WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRels(sheetSpecs.Count));
                WriteEntry(archive, "xl/workbook.xml",      BuildWorkbook(sheetSpecs));
                WriteEntry(archive, "xl/styles.xml",        BuildStyles());
                for (int i = 0; i < sheetSpecs.Count; i++)
                    WriteEntry(archive, "xl/worksheets/sheet" + (i + 1) + ".xml", sheetSpecs[i].Xml);
            }
        }

        // ==================== XML 转义 (保留旧 API) ====================

        /// <summary>
        /// XML 转义:处理 5 个特殊字符 + 剥离 XML 1.0 不允许的控制字符
        /// (0x00-0x08, 0x0B, 0x0C, 0x0E-0x1F)。Revit 参数值里偶尔混入这些字符
        /// 会导致整份 .xlsx 文件被 Excel 拒绝打开。
        /// </summary>
        public static string XmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '&') { sb.Append("&amp;"); continue; }
                if (c == '<') { sb.Append("&lt;"); continue; }
                if (c == '>') { sb.Append("&gt;"); continue; }
                if (c == '"') { sb.Append("&quot;"); continue; }
                if (c == '\'') { sb.Append("&apos;"); continue; }
                if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>旧名保留,内部转发到 XmlEscape。</summary>
        public static string EscapeXml(string value) => XmlEscape(value);

        // ==================== 内部:Sheet XML 生成 ====================

        private sealed class SheetSpec
        {
            public string Name;
            public string Xml;
        }

        private static SheetSpec BuildSummarySheet(Dictionary<string, int> counts)
        {
            StringBuilder body = new StringBuilder();
            body.Append("<row r=\"1\">");
            body.Append(HeaderCell("A1", "类别名称"));
            body.Append(HeaderCell("B1", "图元数量"));
            body.Append("</row>");

            int row = 2;
            int total = 0;
            foreach (var kv in counts.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                body.Append("<row r=\"" + row + "\">");
                body.Append(InlineStringCell("A" + row, kv.Key));
                body.Append(NumberCell("B" + row, kv.Value));
                body.Append("</row>");
                total += kv.Value;
                row++;
            }

            body.Append("<row r=\"" + row + "\">");
            body.Append(HeaderCell("A" + row, "总计"));
            body.Append(HeaderNumberCell("B" + row, total));
            body.Append("</row>");

            return new SheetSpec
            {
                Name = SafeSheetName(Constants.SHEET_SUMMARY),
                Xml  = WrapWorksheet(body.ToString()),
            };
        }

        private static SheetSpec BuildDataSheet(string sheetName,
                                                List<ElementData> elements,
                                                List<string> customHeaders)
        {
            if (elements == null) elements = new List<ElementData>();

            // 收集参数列(在元素中出现过的参数名,排序后作为列头)
            var paramHeaders = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var item in elements)
                if (item.Parameters != null)
                    foreach (var key in item.Parameters.Keys)
                        paramHeaders.Add(key);
            var sortedParams = paramHeaders.ToList();

            // 列计划: [序号, 图元ID, 类别, 族, 类型, 标高] + customHeaders + sortedParams
            var headerNames = new List<string>
            {
                Constants.HEADER_INDEX,
                Constants.HEADER_ELEMENT_ID,
                Constants.HEADER_CATEGORY,
                Constants.HEADER_FAMILY,
                Constants.HEADER_TYPE,
                Constants.HEADER_LEVEL,
            };
            if (customHeaders != null) headerNames.AddRange(customHeaders);
            headerNames.AddRange(sortedParams);

            StringBuilder body = new StringBuilder();

            // 表头
            body.Append("<row r=\"1\">");
            for (int c = 0; c < headerNames.Count; c++)
                body.Append(HeaderCell(ColumnLetter(c + 1) + "1", headerNames[c]));
            body.Append("</row>");

            // 数据行
            int row = 2;
            int idx = 1;
            foreach (var data in elements)
            {
                body.Append("<row r=\"" + row + "\">");
                int col = 1;
                body.Append(NumberCell      (ColumnLetter(col++) + row, idx));
                body.Append(InlineStringCell(ColumnLetter(col++) + row, data.ElementId));
                body.Append(InlineStringCell(ColumnLetter(col++) + row, data.Category));
                body.Append(InlineStringCell(ColumnLetter(col++) + row, data.FamilyName));
                body.Append(InlineStringCell(ColumnLetter(col++) + row, data.TypeName));
                body.Append(InlineStringCell(ColumnLetter(col++) + row, data.Level));

                if (customHeaders != null)
                {
                    foreach (var h in customHeaders)
                    {
                        string v = Constants.DEFAULT_VALUE;
                        if      (h == Constants.PARAM_MAJOR_NAME)    v = data.MajorName;
                        else if (h == Constants.PARAM_SUBCONTRACTOR) v = data.Subcontractor;
                        else if (h == Constants.PARAM_SHOULD_EXPORT) v = data.ShouldExport;
                        body.Append(InlineStringCell(ColumnLetter(col++) + row, v));
                    }
                }

                foreach (var p in sortedParams)
                {
                    string v = Constants.DEFAULT_VALUE;
                    if (data.Parameters != null && data.Parameters.TryGetValue(p, out var raw)
                        && !string.IsNullOrEmpty(raw))
                        v = raw;
                    body.Append(InlineStringCell(ColumnLetter(col++) + row, v));
                }

                body.Append("</row>");
                row++;
                idx++;
            }

            return new SheetSpec
            {
                Name = SafeSheetName(sheetName),
                Xml  = WrapWorksheet(body.ToString()),
            };
        }

        private static string WrapWorksheet(string sheetDataInner)
        {
            return
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                  "<sheetData>" + sheetDataInner + "</sheetData>" +
                "</worksheet>";
        }

        // ==================== 单元格 ====================

        // OOXML cell styles 索引 (与 BuildStyles 里 cellXfs 顺序对齐):
        //   0 = 默认 (无样式)
        //   1 = Header (宋体加粗 + 灰底 + 边框 + 居中)
        private const string HEADER_STYLE = " s=\"1\"";

        private static string InlineStringCell(string @ref, string value)
        {
            return "<c r=\"" + @ref + "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">"
                   + XmlEscape(value ?? "") + "</t></is></c>";
        }

        private static string HeaderCell(string @ref, string value)
        {
            return "<c r=\"" + @ref + "\"" + HEADER_STYLE + " t=\"inlineStr\"><is><t xml:space=\"preserve\">"
                   + XmlEscape(value ?? "") + "</t></is></c>";
        }

        private static string NumberCell(string @ref, int value)
        {
            return "<c r=\"" + @ref + "\"><v>" + value + "</v></c>";
        }

        private static string HeaderNumberCell(string @ref, int value)
        {
            return "<c r=\"" + @ref + "\"" + HEADER_STYLE + "><v>" + value + "</v></c>";
        }

        private static string ColumnLetter(int col1Based)
        {
            // 1→A, 26→Z, 27→AA, 28→AB ...
            StringBuilder sb = new StringBuilder();
            int n = col1Based;
            while (n > 0)
            {
                int rem = (n - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                n = (n - 1) / 26;
            }
            return sb.ToString();
        }

        private static string SafeSheetName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Sheet";
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (c == ':' || c == '\\' || c == '/' || c == '?' || c == '*' || c == '[' || c == ']')
                    sb.Append('_');
                else
                    sb.Append(c);
            }
            string safe = sb.ToString().Trim().Trim('\'');
            if (safe.Length == 0) safe = "Sheet";
            if (safe.Length > Constants.MAX_SHEET_NAME_LENGTH)
                safe = safe.Substring(0, Constants.MAX_SHEET_NAME_LENGTH);
            return safe;
        }

        private static void EnsureUniqueSheetNames(IList<SheetSpec> sheets)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sheet in sheets)
            {
                var baseName = SafeSheetName(sheet.Name);
                var candidate = baseName;
                var suffixNumber = 2;

                while (!used.Add(candidate))
                {
                    var suffix = " (" + suffixNumber + ")";
                    var prefixLength = Constants.MAX_SHEET_NAME_LENGTH - suffix.Length;
                    var prefix = baseName.Length > prefixLength
                        ? baseName.Substring(0, prefixLength)
                        : baseName;
                    candidate = prefix + suffix;
                    suffixNumber++;
                }

                sheet.Name = candidate;
            }
        }

        // ==================== Package parts ====================

        private static string BuildContentTypes(int sheetCount)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\"  ContentType=\"application/xml\"/>");
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            for (int i = 1; i <= sheetCount; i++)
                sb.Append("<Override PartName=\"/xl/worksheets/sheet" + i + ".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            sb.Append("</Types>");
            return sb.ToString();
        }

        private static string BuildRootRels()
        {
            return
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                  "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>";
        }

        private static string BuildWorkbookRels(int sheetCount)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 1; i <= sheetCount; i++)
                sb.Append("<Relationship Id=\"rId" + i + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet" + i + ".xml\"/>");
            int stylesId = sheetCount + 1;
            sb.Append("<Relationship Id=\"rId" + stylesId + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private static string BuildWorkbook(List<SheetSpec> sheets)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\""
                      + " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            sb.Append("<sheets>");
            for (int i = 0; i < sheets.Count; i++)
            {
                sb.Append("<sheet name=\"" + XmlEscape(sheets[i].Name) + "\""
                          + " sheetId=\"" + (i + 1) + "\""
                          + " r:id=\"rId" + (i + 1) + "\"/>");
            }
            sb.Append("</sheets>");
            sb.Append("</workbook>");
            return sb.ToString();
        }

        /// <summary>
        /// 最小可用 styles.xml:
        ///   fonts[0] = 宋体 11
        ///   fonts[1] = 宋体 11 加粗
        ///   fills[0] = 无填充       (OOXML 规定 index 0 必须是 none)
        ///   fills[1] = gray125      (OOXML 规定 index 1 必须是 gray125)
        ///   fills[2] = 灰色 #C0C0C0 (Header 用)
        ///   borders[0] = 无边框
        ///   borders[1] = 四面细黑边
        ///   cellXfs[0] = 默认
        ///   cellXfs[1] = Header (font 1, fill 2, border 1, 水平/垂直居中)
        /// </summary>
        private static string BuildStyles()
        {
            return
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                  "<fonts count=\"2\">" +
                    "<font><sz val=\"11\"/><name val=\"宋体\"/><charset val=\"134\"/></font>" +
                    "<font><b/><sz val=\"11\"/><name val=\"宋体\"/><charset val=\"134\"/></font>" +
                  "</fonts>" +
                  "<fills count=\"3\">" +
                    "<fill><patternFill patternType=\"none\"/></fill>" +
                    "<fill><patternFill patternType=\"gray125\"/></fill>" +
                    "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFC0C0C0\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                  "</fills>" +
                  "<borders count=\"2\">" +
                    "<border><left/><right/><top/><bottom/><diagonal/></border>" +
                    "<border>" +
                      "<left style=\"thin\"><color rgb=\"FF000000\"/></left>" +
                      "<right style=\"thin\"><color rgb=\"FF000000\"/></right>" +
                      "<top style=\"thin\"><color rgb=\"FF000000\"/></top>" +
                      "<bottom style=\"thin\"><color rgb=\"FF000000\"/></bottom>" +
                      "<diagonal/>" +
                    "</border>" +
                  "</borders>" +
                  "<cellStyleXfs count=\"1\">" +
                    "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/>" +
                  "</cellStyleXfs>" +
                  "<cellXfs count=\"2\">" +
                    "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                    "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\"" +
                      " applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\">" +
                      "<alignment horizontal=\"center\" vertical=\"center\"/>" +
                    "</xf>" +
                  "</cellXfs>" +
                "</styleSheet>";
        }

        // ==================== zip helpers ====================

        private static void WriteEntry(ZipArchive archive, string path, string content)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
            // OOXML 标准要求 UTF-8。带 BOM 与否都行,Excel 都接受;
            // 这里不带 BOM 与现有项目风格一致,且少几个字节。
            using (var sw = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                sw.Write(content);
        }
    }
}

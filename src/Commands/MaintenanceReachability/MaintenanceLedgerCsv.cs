using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace JarviTools.Commands.MaintenanceReachability
{
    /// <summary>
    /// Small, dependency-free CSV and snapshot helper used by the maintenance-ledger
    /// bridge.  Keeping this code free of Revit API references lets it be tested by a
    /// normal .NET Framework console compiler.
    /// </summary>
    internal static class MaintenanceLedgerCsv
    {
        internal static string Serialize(
            IList<string> headers,
            IEnumerable<IDictionary<string, string>> rows)
        {
            if (headers == null) throw new ArgumentNullException("headers");
            if (headers.Count == 0) throw new ArgumentException("表头不能为空。", "headers");
            if (headers.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("表头不能包含空名称。", "headers");
            if (headers.Distinct(StringComparer.Ordinal).Count() != headers.Count)
                throw new ArgumentException("表头不能包含重复名称。", "headers");

            var builder = new StringBuilder();
            AppendRow(builder, headers);
            foreach (IDictionary<string, string> row in rows ??
                     Enumerable.Empty<IDictionary<string, string>>())
            {
                AppendRow(builder, headers.Select(header =>
                {
                    string value;
                    return row != null && row.TryGetValue(header, out value)
                        ? value ?? string.Empty
                        : string.Empty;
                }));
            }
            return builder.ToString();
        }

        internal static List<string> FindOrphanManualKeys(
            IEnumerable<string> currentRowKeys,
            IEnumerable<KeyValuePair<string, bool>> priorManualRows)
        {
            var current = new HashSet<string>(
                (currentRowKeys ?? Enumerable.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.Ordinal);
            return (priorManualRows ?? Enumerable.Empty<KeyValuePair<string, bool>>())
                .Where(x => x.Value && !string.IsNullOrWhiteSpace(x.Key) &&
                            !current.Contains(x.Key))
                .Select(x => x.Key)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
        }

        internal static List<Dictionary<string, string>> Parse(string csv)
        {
            var rawRows = ParseRawRows(csv ?? string.Empty);
            if (rawRows.Count == 0)
                return new List<Dictionary<string, string>>();

            List<string> headers = rawRows[0];
            if (headers.Count == 0 || headers.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("CSV 表头不完整。");
            if (headers.Distinct(StringComparer.Ordinal).Count() != headers.Count)
                throw new InvalidDataException("CSV 表头存在重复列。");

            var result = new List<Dictionary<string, string>>();
            for (int rowIndex = 1; rowIndex < rawRows.Count; rowIndex++)
            {
                List<string> source = rawRows[rowIndex];
                if (source.Count == 1 && string.IsNullOrEmpty(source[0])) continue;
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int columnIndex = 0; columnIndex < headers.Count; columnIndex++)
                {
                    row[headers[columnIndex]] = columnIndex < source.Count
                        ? source[columnIndex]
                        : string.Empty;
                }
                result.Add(row);
            }
            return result;
        }

        internal static List<string> ParseHeaders(string csv)
        {
            List<List<string>> rawRows = ParseRawRows(csv ?? string.Empty);
            if (rawRows.Count == 0) return new List<string>();
            List<string> headers = rawRows[0];
            if (headers.Count == 0 || headers.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("CSV 表头不完整。");
            if (headers.Distinct(StringComparer.Ordinal).Count() != headers.Count)
                throw new InvalidDataException("CSV 表头存在重复列。");
            return headers;
        }

        internal static string ReadAllTextShared(string path)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                return reader.ReadToEnd();
        }

        internal static void WriteAllTextAtomic(string path, string content)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("输出路径不能为空。", "path");

            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                throw new DirectoryNotFoundException("输出文件夹不存在：" + directory);

            string tempPath = Path.Combine(
                directory,
                "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(tempPath, content ?? string.Empty, new UTF8Encoding(true));
                if (File.Exists(path))
                    File.Replace(tempPath, path, null, true);
                else
                    File.Move(tempPath, path);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        internal static string Sha256Hex(string value)
        {
            return Sha256Hex(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        internal static string Sha256HexUtf8BomFile(string value)
        {
            var encoding = new UTF8Encoding(true);
            byte[] preamble = encoding.GetPreamble();
            byte[] content = encoding.GetBytes(value ?? string.Empty);
            var bytes = new byte[preamble.Length + content.Length];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Buffer.BlockCopy(content, 0, bytes, preamble.Length, content.Length);
            return Sha256Hex(bytes);
        }

        internal static string Sha256Hex(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            byte[] hash;
            using (SHA256 algorithm = SHA256.Create())
                hash = algorithm.ComputeHash(bytes);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (byte item in hash) builder.Append(item.ToString("x2"));
            return builder.ToString();
        }

        private static void AppendRow(StringBuilder builder, IEnumerable<string> values)
        {
            bool first = true;
            foreach (string value in values)
            {
                if (!first) builder.Append(',');
                builder.Append(Escape(value));
                first = false;
            }
            builder.Append("\r\n");
        }

        private static string Escape(string value)
        {
            string safe = value ?? string.Empty;
            if (safe.Length > 0)
            {
                char first = safe[0];
                if (first == '=' || first == '+' || first == '-' || first == '@' || first == '\t' || first == '\r')
                    safe = "'" + safe;
            }
            if (safe.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return safe;
            return "\"" + safe.Replace("\"", "\"\"") + "\"";
        }

        private static List<List<string>> ParseRawRows(string csv)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;

            for (int index = 0; index < csv.Length; index++)
            {
                char current = csv[index];
                if (quoted)
                {
                    if (current == '"')
                    {
                        if (index + 1 < csv.Length && csv[index + 1] == '"')
                        {
                            field.Append('"');
                            index++;
                        }
                        else
                        {
                            quoted = false;
                        }
                    }
                    else
                    {
                        field.Append(current);
                    }
                    continue;
                }

                if (current == '"' && field.Length == 0)
                {
                    quoted = true;
                }
                else if (current == ',')
                {
                    row.Add(field.ToString());
                    field.Clear();
                }
                else if (current == '\r' || current == '\n')
                {
                    if (current == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n')
                        index++;
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = new List<string>();
                }
                else
                {
                    field.Append(current);
                }
            }

            if (quoted) throw new InvalidDataException("CSV 存在未闭合的引号。");
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add(row);
            }
            return rows;
        }
    }
}

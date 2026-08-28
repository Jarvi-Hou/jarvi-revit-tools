using System;
using System.IO;
using System.Text;

namespace JarviTools.Mcp.Tools
{
    internal static class CsvExportSafety
    {
        internal static string PreparePath(string path, bool overwrite)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("outputPath is required.");
            string fullPath = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(fullPath), ".csv", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("outputPath must end with .csv.");
            string directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                throw new DirectoryNotFoundException("Output directory does not exist: " + directory);
            if (File.Exists(fullPath) && !overwrite)
                throw new IOException("Output file already exists. Set overwrite=true only after confirming the target: " + fullPath);
            return fullPath;
        }

        internal static string EscapeField(string value)
        {
            string safe = NeutralizeFormula(value ?? string.Empty);
            if (safe.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                return "\"" + safe.Replace("\"", "\"\"") + "\"";
            return safe;
        }

        internal static string NeutralizeFormula(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            char first = value[0];
            return first == '=' || first == '+' || first == '-' || first == '@' || first == '\t' || first == '\r'
                ? "'" + value
                : value;
        }

        internal static void WriteAllTextAtomic(string path, string content)
        {
            string directory = Path.GetDirectoryName(path);
            string temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, content ?? string.Empty, new UTF8Encoding(true));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}

using System;
using System.IO;
using System.Text;

namespace JarviTools.Mcp.Server
{
    internal static class Logger
    {
        private static readonly object _lock = new object();
        private static string _logDir;

        public static void Init()
        {
            _logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "JarviTools.Mcp", "logs");
            Directory.CreateDirectory(_logDir);
            DeleteExpiredLogs(14);
        }

        private static void DeleteExpiredLogs(int retentionDays)
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, retentionDays));
                foreach (string file in Directory.GetFiles(_logDir, "*.log"))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
            }
            catch { }
        }

        private static string CurrentLogFile =>
            Path.Combine(_logDir ?? Path.GetTempPath(),
                $"{DateTime.Now:yyyy-MM-dd}.log");

        public static void Info(string msg)  => Write("INFO ", msg, null);
        public static void Warn(string msg)  => Write("WARN ", msg, null);
        public static void Error(string msg, Exception ex = null) => Write("ERROR", msg, ex);

        private static void Write(string level, string msg, Exception ex)
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ')
                .Append(level).Append(' ')
                .Append(msg);
            if (ex != null)
                line.AppendLine().Append(ex.ToString());

            lock (_lock)
            {
                try
                {
                    File.AppendAllText(CurrentLogFile, line.ToString() + Environment.NewLine, Encoding.UTF8);
                }
                catch { /* swallow logging errors */ }
            }
        }
    }
}

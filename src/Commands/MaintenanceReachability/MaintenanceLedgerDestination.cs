using System;
using System.IO;

namespace JarviTools.Commands.MaintenanceReachability
{
    internal sealed class MaintenanceLedgerDestination
    {
        public string OutputDirectory;
        public string FilePrefix;
        public string LegacyArchiveRoot;

        /// <summary>
        /// Pure, no-write normalization shared by configuration lookup and exporters.
        /// A false result means callers must report "not configured"/invalid and must
        /// not try a fallback directory.
        /// </summary>
        internal bool TryNormalize(
            string defaultFilePrefix,
            out MaintenanceLedgerDestination normalized,
            out string errorCode,
            out string errorMessage)
        {
            normalized = null;
            errorCode = string.Empty;
            errorMessage = string.Empty;
            if (string.IsNullOrWhiteSpace(OutputDirectory))
            {
                errorCode = "destination_not_configured";
                errorMessage = "HandReach 台账目录未配置；未写入任何文件。";
                return false;
            }
            if (!Path.IsPathRooted(OutputDirectory))
            {
                errorCode = "directory_not_absolute";
                errorMessage = "HandReach 台账目录必须是绝对路径；未写入任何文件。";
                return false;
            }

            string fullPath;
            try { fullPath = Path.GetFullPath(OutputDirectory.Trim()); }
            catch (Exception exception)
            {
                errorCode = "directory_invalid";
                errorMessage = "HandReach 台账目录无效；未写入任何文件：" + exception.Message;
                return false;
            }
            if (!Directory.Exists(fullPath))
            {
                errorCode = "directory_missing";
                errorMessage = "HandReach 台账输出文件夹不存在；未写入任何文件：" + fullPath;
                return false;
            }

            string prefix = string.IsNullOrWhiteSpace(FilePrefix)
                ? (defaultFilePrefix ?? string.Empty).Trim()
                : FilePrefix.Trim();
            if (string.IsNullOrWhiteSpace(prefix) || prefix.Length > 80 ||
                prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !string.Equals(prefix, Path.GetFileName(prefix), StringComparison.Ordinal))
            {
                errorCode = "file_prefix_invalid";
                errorMessage = "HandReach 台账文件前缀无效；未写入任何文件。";
                return false;
            }

            string legacyArchiveRoot = string.Empty;
            if (!string.IsNullOrWhiteSpace(LegacyArchiveRoot))
            {
                if (!Path.IsPathRooted(LegacyArchiveRoot))
                {
                    errorCode = "legacy_archive_root_not_absolute";
                    errorMessage = "HandReach 旧台账归档根目录必须是绝对路径；未写入任何文件。";
                    return false;
                }
                try { legacyArchiveRoot = Path.GetFullPath(LegacyArchiveRoot.Trim()); }
                catch (Exception exception)
                {
                    errorCode = "legacy_archive_root_invalid";
                    errorMessage = "HandReach 旧台账归档根目录无效；未写入任何文件：" +
                                   exception.Message;
                    return false;
                }
            }

            normalized = new MaintenanceLedgerDestination
            {
                OutputDirectory = fullPath,
                FilePrefix = prefix,
                LegacyArchiveRoot = legacyArchiveRoot
            };
            return true;
        }
    }
}

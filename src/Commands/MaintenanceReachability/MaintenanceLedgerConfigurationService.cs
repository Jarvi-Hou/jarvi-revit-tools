using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Autodesk.Revit.DB;

namespace JarviTools.Commands.MaintenanceReachability
{
    /// <summary>
    /// Remembers a ledger destination by model fingerprint.  No Revit model path is
    /// persisted.  Once an AI has explicitly synchronized a project, later supported
    /// Show/Clear operations and HandReach analysis can refresh its CSV evidence.
    /// </summary>
    internal static class MaintenanceLedgerConfigurationService
    {
        private const string ConfigurationSchema = "OpenRevit.MaintenanceLedgerDestination.v1";
        private const int MaxRememberedModels = 32;

        private sealed class ConfigurationFile
        {
            public string Schema = ConfigurationSchema;
            public List<ConfigurationRow> Rows = new List<ConfigurationRow>();
        }

        private sealed class ConfigurationRow
        {
            public string ModelFingerprint;
            public string OutputDirectory;
            public string FilePrefix;
            public string UpdatedAtUtc;
        }

        internal static void Remember(
            Document document,
            string outputDirectory,
            string filePrefix)
        {
            if (document == null) throw new ArgumentNullException("document");
            string directory = ValidateDirectory(outputDirectory);
            string fingerprint = MaintenanceLedgerSyncService.GetModelFingerprint(document);
            string prefix = ValidateFilePrefix(filePrefix);

            ConfigurationFile configuration = ReadConfiguration();
            configuration.Rows.RemoveAll(x => x == null ||
                string.Equals(x.ModelFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
            configuration.Rows.Add(new ConfigurationRow
            {
                ModelFingerprint = fingerprint,
                OutputDirectory = directory,
                FilePrefix = prefix,
                UpdatedAtUtc = DateTime.UtcNow.ToString("o")
            });
            configuration.Rows = configuration.Rows
                .OrderByDescending(x => x.UpdatedAtUtc, StringComparer.Ordinal)
                .Take(MaxRememberedModels)
                .ToList();

            string path = GetConfigurationPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string json = JsonConvert.SerializeObject(configuration, Formatting.Indented) +
                          Environment.NewLine;
            MaintenanceLedgerCsv.WriteAllTextAtomic(path, json);
        }

        internal static bool TryResolve(
            Document document,
            out MaintenanceLedgerDestination destination)
        {
            destination = null;
            if (document == null) return false;
            string fingerprint = MaintenanceLedgerSyncService.GetModelFingerprint(document);
            ConfigurationRow row = ReadConfiguration().Rows
                .Where(x => x != null &&
                    string.Equals(x.ModelFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.UpdatedAtUtc, StringComparer.Ordinal)
                .FirstOrDefault();
            if (row == null) return false;

            var configured = new MaintenanceLedgerDestination
            {
                OutputDirectory = row.OutputDirectory,
                FilePrefix = row.FilePrefix
            };
            string errorCode;
            string errorMessage;
            return configured.TryNormalize(
                MaintenanceLedgerSyncService.DefaultFilePrefix,
                out destination,
                out errorCode,
                out errorMessage);
        }

        private static ConfigurationFile ReadConfiguration()
        {
            string path = GetConfigurationPath();
            if (!File.Exists(path)) return new ConfigurationFile();
            try
            {
                string json = MaintenanceLedgerCsv.ReadAllTextShared(path);
                ConfigurationFile result = JsonConvert.DeserializeObject<ConfigurationFile>(json);
                if (result == null ||
                    !string.Equals(result.Schema, ConfigurationSchema, StringComparison.Ordinal) ||
                    result.Rows == null)
                    return new ConfigurationFile();
                return result;
            }
            catch
            {
                // A damaged optional destination cache must never make analysis fail.
                // The caller will report that no automatic ledger destination exists.
                return new ConfigurationFile();
            }
        }

        private static string ValidateDirectory(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
                throw new ArgumentException("台账输出文件夹必须是已存在的绝对路径。", "outputDirectory");
            string fullPath = Path.GetFullPath(value.Trim());
            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException("台账输出文件夹不存在：" + fullPath);
            return fullPath;
        }

        private static string ValidateFilePrefix(string value)
        {
            string prefix = string.IsNullOrWhiteSpace(value)
                ? MaintenanceLedgerSyncService.DefaultFilePrefix
                : value.Trim();
            if (prefix.Length > 80 ||
                prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                prefix.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                prefix.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
                throw new ArgumentException("台账文件前缀无效。", "filePrefix");
            return prefix;
        }

        private static string GetConfigurationPath()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(
                local,
                "OpenRevit Tools",
                "MaintenanceLedger",
                "destinations.json");
        }
    }
}

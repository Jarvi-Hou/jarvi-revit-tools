using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace JarviTools.Commands.MaintenanceReachability
{
    internal sealed class MaintenanceHandReachManualState
    {
        public string Conclusion;
        public string Note;
        public string EvidenceFingerprint;
        public string ResultFingerprint;
        public string DecisionReason;
    }

    internal sealed class MaintenanceHandReachLegacyMigrationResult
    {
        public readonly Dictionary<string, MaintenanceHandReachManualState> ManualStates =
            new Dictionary<string, MaintenanceHandReachManualState>(StringComparer.Ordinal);
        public string Status = "not_needed";
        public string ArchiveDirectory = string.Empty;
        public int MappedManualRowCount;
        public string Warning = string.Empty;
    }

    /// <summary>
    /// One-time, fail-safe migration for pre-v2 HandReach ledgers.  Exact legacy
    /// bytes are archived and hash-verified before an exporter may replace the live
    /// summary.  The ambiguous legacy column named "结论" is deliberately never
    /// interpreted as a manual confirmation.
    /// </summary>
    internal static class MaintenanceHandReachLegacyMigrationService
    {
        internal static readonly string DefaultArchiveRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenRevit Tools",
            "Archives");

        private static readonly string[] ManualConclusionAliases =
        {
            "台账人工确认", "人工确认", "人工结论"
        };

        private static readonly string[] ManualNoteAliases =
        {
            "台账人工备注", "人工备注", "专业备注"
        };

        private sealed class TargetIdentity
        {
            public string RowKey;
            public string Group;
            public string DeviceNo;
            public string DisplayName;
            public double ProxyX;
            public double ProxyY;
            public double ProxyZ;
        }

        private sealed class ArchivedFile
        {
            public string SourcePath;
            public string ArchivedName;
            public long Length;
            public string Sha256;
            public string LastWriteTimeUtc;
            public byte[] Bytes;
        }

        internal static MaintenanceHandReachLegacyMigrationResult ArchiveAndMigrate(
            string summaryPath,
            string candidatePath,
            string manifestPath,
            HandReachAnalysisResult result,
            string requestedArchiveRoot)
        {
            if (string.IsNullOrWhiteSpace(summaryPath))
                throw new ArgumentException("旧 HandReach 汇总路径不能为空。", "summaryPath");
            if (result == null) throw new ArgumentNullException("result");
            if (!File.Exists(summaryPath))
                return new MaintenanceHandReachLegacyMigrationResult();

            string archiveRoot = string.IsNullOrWhiteSpace(requestedArchiveRoot)
                ? DefaultArchiveRoot
                : requestedArchiveRoot.Trim();
            if (!Path.IsPathRooted(archiveRoot))
                throw new InvalidDataException(
                    "HandReach 旧台账归档根目录必须是绝对路径；旧文件未覆盖。");
            archiveRoot = Path.GetFullPath(archiveRoot);
            Directory.CreateDirectory(archiveRoot);

            string timestamp = DateTime.Now.ToString(
                "yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
            string archiveDirectory = CreateUniqueArchiveDirectory(
                archiveRoot,
                timestamp + "-HandReach台账旧版归档");

            List<string> exactLegacyPaths = new[]
            {
                summaryPath,
                candidatePath,
                manifestPath
            }
                .Where(x => !string.IsNullOrWhiteSpace(x) && File.Exists(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var archived = new List<ArchivedFile>();
            try
            {
                foreach (string sourcePath in exactLegacyPaths)
                    archived.Add(ArchiveExactFile(sourcePath, archiveDirectory));
                if (!archived.Any(x => string.Equals(
                    x.SourcePath, summaryPath, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException(
                        "HandReach 旧汇总在归档期间消失；未写入新版台账。");

                string archiveManifestPath = Path.Combine(
                    archiveDirectory,
                    timestamp + "-HandReach台账旧版归档清单.json");
                string archiveManifest = JsonConvert.SerializeObject(new
                {
                    schemaVersion = "OpenRevit.HandReachLegacyArchive.v1",
                    archivedAtLocal = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                    reason = "pre-v2 HandReach ledger lacked stable row/manual columns",
                    sourceSummary = summaryPath,
                    files = archived.Select(x => new
                    {
                        sourcePath = x.SourcePath,
                        archivedName = x.ArchivedName,
                        length = x.Length,
                        sha256 = x.Sha256,
                        lastWriteTimeUtc = x.LastWriteTimeUtc
                    }).ToArray(),
                    safety = "Every archived file is an exact byte copy verified by SHA-256 before live replacement."
                }, Formatting.Indented) + Environment.NewLine;
                MaintenanceLedgerCsv.WriteAllTextAtomic(
                    archiveManifestPath,
                    archiveManifest);
                if (!File.Exists(archiveManifestPath))
                    throw new IOException("HandReach 旧台账归档清单未生成。旧文件未覆盖。");

                foreach (ArchivedFile file in archived)
                    EnsureSourceStillMatchesArchive(file);
            }
            catch
            {
                // A partial archive is intentionally retained for forensic recovery.
                // The caller receives the exception before any live ledger write.
                throw;
            }

            var migration = new MaintenanceHandReachLegacyMigrationResult
            {
                ArchiveDirectory = archiveDirectory,
                Status = "archived_legacy_started_clean",
                Warning = "旧 HandReach 汇总已按原字节归档并校验。旧列“结论”属于旧算法输出，未迁入人工确认；新版人工列从空值开始。"
            };

            ArchivedFile archivedSummary = archived.First(x => string.Equals(
                x.SourcePath, summaryPath, StringComparison.OrdinalIgnoreCase));
            List<Dictionary<string, string>> rows;
            try
            {
                rows = MaintenanceLedgerCsv.Parse(DecodeUtf8(archivedSummary.Bytes));
            }
            catch (Exception exception)
            {
                migration.Status = "archived_legacy_parse_failed_started_clean";
                migration.Warning =
                    "旧 HandReach 汇总无法可靠解析，已完整归档后新建新版；任何旧人工信息仅保留在归档中，需人工复核：" +
                    exception.Message;
                return migration;
            }

            string conclusionColumn = FindFirstColumn(rows, ManualConclusionAliases);
            string noteColumn = FindFirstColumn(rows, ManualNoteAliases);
            bool hasExplicitManualColumn = !string.IsNullOrWhiteSpace(conclusionColumn) ||
                                           !string.IsNullOrWhiteSpace(noteColumn);
            if (!hasExplicitManualColumn) return migration;

            List<Dictionary<string, string>> manualRows = rows
                .Where(x => !string.IsNullOrWhiteSpace(Read(x, conclusionColumn)) ||
                            !string.IsNullOrWhiteSpace(Read(x, noteColumn)))
                .ToList();
            if (manualRows.Count == 0) return migration;

            List<TargetIdentity> targets = BuildTargetIdentities(result);
            var matched = new Dictionary<string, MaintenanceHandReachManualState>(
                StringComparer.Ordinal);
            foreach (Dictionary<string, string> row in manualRows)
            {
                TargetIdentity target;
                if (!TryMatchExactlyOne(row, targets, out target) ||
                    matched.ContainsKey(target.RowKey))
                {
                    migration.Status = "archived_legacy_ambiguous_started_clean";
                    migration.Warning =
                        "旧 HandReach 汇总含明确人工列，但至少一行无法唯一映射到当前稳定 TargetKey；为避免错配，人工值未自动迁移，仅保留在已校验归档中，需人工复核。";
                    return migration;
                }
                matched[target.RowKey] = new MaintenanceHandReachManualState
                {
                    Conclusion = Read(row, conclusionColumn),
                    Note = Read(row, noteColumn)
                };
            }

            foreach (KeyValuePair<string, MaintenanceHandReachManualState> item in matched)
                migration.ManualStates[item.Key] = item.Value;
            migration.MappedManualRowCount = matched.Count;
            migration.Status = "archived_legacy_manual_migrated";
            migration.Warning = "旧 HandReach 汇总已按原字节归档；" + matched.Count +
                                " 行明确人工确认/备注已通过稳定一对一身份迁入新版。旧列“结论”未作为人工值迁移。";
            return migration;
        }

        internal static void EnsureArchiveHashMatches(
            string expectedSha256,
            byte[] archivedBytes)
        {
            string actual = MaintenanceLedgerCsv.Sha256Hex(archivedBytes ?? new byte[0]);
            if (!string.Equals(expectedSha256, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "HandReach 旧台账归档副本哈希校验失败；旧文件未覆盖。");
        }

        private static ArchivedFile ArchiveExactFile(
            string sourcePath,
            string archiveDirectory)
        {
            byte[] sourceBytes = ReadAllBytesShared(sourcePath);
            string sourceHash = MaintenanceLedgerCsv.Sha256Hex(sourceBytes);
            string archivedName = Path.GetFileName(sourcePath);
            string archivedPath = Path.Combine(archiveDirectory, archivedName);
            if (File.Exists(archivedPath))
                throw new IOException("HandReach 归档目标已存在，拒绝覆盖：" + archivedPath);
            File.WriteAllBytes(archivedPath, sourceBytes);
            byte[] copiedBytes = File.ReadAllBytes(archivedPath);
            EnsureArchiveHashMatches(sourceHash, copiedBytes);
            if (copiedBytes.LongLength != sourceBytes.LongLength)
                throw new InvalidDataException(
                    "HandReach 旧台账归档副本长度不一致；旧文件未覆盖。");

            DateTime lastWriteUtc = File.GetLastWriteTimeUtc(sourcePath);
            try { File.SetLastWriteTimeUtc(archivedPath, lastWriteUtc); }
            catch { /* timestamp is also recorded in the archive manifest */ }
            return new ArchivedFile
            {
                SourcePath = sourcePath,
                ArchivedName = archivedName,
                Length = sourceBytes.LongLength,
                Sha256 = sourceHash,
                LastWriteTimeUtc = lastWriteUtc.ToString("o", CultureInfo.InvariantCulture),
                Bytes = sourceBytes
            };
        }

        private static void EnsureSourceStillMatchesArchive(ArchivedFile archived)
        {
            if (archived == null || !File.Exists(archived.SourcePath))
                throw new InvalidDataException(
                    "HandReach 旧台账在归档后发生变化或消失；未写入新版台账。");
            byte[] currentBytes = ReadAllBytesShared(archived.SourcePath);
            string currentHash = MaintenanceLedgerCsv.Sha256Hex(currentBytes);
            if (!string.Equals(currentHash, archived.Sha256,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "HandReach 旧台账在归档后发生变化；为避免覆盖并发人工编辑，未写入新版台账：" +
                    archived.SourcePath);
        }

        private static byte[] ReadAllBytesShared(string path)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                var bytes = new byte[stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0) throw new EndOfStreamException("读取旧台账时提前结束。");
                    offset += read;
                }
                return bytes;
            }
        }

        private static string DecodeUtf8(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes ?? new byte[0]))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                return reader.ReadToEnd();
        }

        private static string CreateUniqueArchiveDirectory(
            string archiveRoot,
            string baseName)
        {
            for (int index = 0; index <= 999; index++)
            {
                string name = index == 0
                    ? baseName
                    : baseName + "-" + index.ToString("D2", CultureInfo.InvariantCulture);
                string path = Path.Combine(archiveRoot, name);
                if (Directory.Exists(path)) continue;
                Directory.CreateDirectory(path);
                return path;
            }
            throw new IOException("无法为 HandReach 旧台账分配唯一归档目录。");
        }

        private static List<TargetIdentity> BuildTargetIdentities(
            HandReachAnalysisResult result)
        {
            return result.TargetResults
                .Where(x => x != null && x.Target != null)
                .Select(x => x.Target)
                .Select(x =>
                {
                    string group = !string.IsNullOrWhiteSpace(x.GroupKey)
                        ? x.GroupKey.Trim()
                        : (result.GroupKey ?? string.Empty).Trim();
                    return new TargetIdentity
                    {
                        RowKey = group + "|" + (x.TargetKey ?? string.Empty) + "|HandReach",
                        Group = group,
                        DeviceNo = NormalizeDeviceNo(x.DeviceNo),
                        DisplayName = NormalizeIdentityText(x.GetDisplayName()),
                        ProxyX = x.ServiceFaceProxyX,
                        ProxyY = x.ServiceFaceProxyY,
                        ProxyZ = x.ServiceFaceProxyZ
                    };
                })
                .ToList();
        }

        private static bool TryMatchExactlyOne(
            IDictionary<string, string> row,
            IList<TargetIdentity> targets,
            out TargetIdentity target)
        {
            target = null;
            string priorRowKey = Read(row, "行键");
            if (!string.IsNullOrWhiteSpace(priorRowKey))
            {
                List<TargetIdentity> rowKeyMatches = targets
                    .Where(x => string.Equals(x.RowKey, priorRowKey.Trim(),
                        StringComparison.Ordinal))
                    .ToList();
                if (rowKeyMatches.Count == 1)
                {
                    target = rowKeyMatches[0];
                    return true;
                }
                if (rowKeyMatches.Count > 1) return false;
            }

            string deviceNo = NormalizeDeviceNo(Read(row, "设备编号"));
            string displayName = NormalizeIdentityText(
                FirstValue(row, "维修对象", "设备"));
            string group = FirstValue(row, "逻辑组", "天花分组").Trim();
            double proxyX;
            double proxyY;
            double proxyZ;
            if (string.IsNullOrWhiteSpace(deviceNo) ||
                string.IsNullOrWhiteSpace(displayName) ||
                !TryReadProxy(row, out proxyX, out proxyY, out proxyZ))
                return false;

            List<TargetIdentity> matches = targets
                .Where(x => string.Equals(x.DeviceNo, deviceNo,
                    StringComparison.Ordinal))
                .Where(x => string.Equals(x.DisplayName, displayName,
                    StringComparison.Ordinal))
                .Where(x => string.IsNullOrWhiteSpace(group) ||
                            string.Equals(x.Group, group, StringComparison.Ordinal))
                .Where(x => Math.Abs(x.ProxyX - proxyX) <= 0.11 &&
                            Math.Abs(x.ProxyY - proxyY) <= 0.11 &&
                            Math.Abs(x.ProxyZ - proxyZ) <= 0.11)
                .ToList();
            if (matches.Count != 1) return false;
            target = matches[0];
            return true;
        }

        private static bool TryReadProxy(
            IDictionary<string, string> row,
            out double x,
            out double y,
            out double z)
        {
            x = 0.0;
            y = 0.0;
            z = 0.0;
            if (TryParseInvariant(Read(row, "检修面代理点Xmm"), out x) &&
                TryParseInvariant(Read(row, "检修面代理点Ymm"), out y) &&
                TryParseInvariant(Read(row, "检修面代理点Zmm"), out z))
                return true;
            string combined = Read(row, "检修面代理点mm");
            string[] parts = (combined ?? string.Empty)
                .Split(new[] { ';', '；', ',' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 3 &&
                   TryParseInvariant(parts[0], out x) &&
                   TryParseInvariant(parts[1], out y) &&
                   TryParseInvariant(parts[2], out z);
        }

        private static bool TryParseInvariant(string value, out double parsed)
        {
            string text = (value ?? string.Empty).Trim();
            if (text.StartsWith("'", StringComparison.Ordinal)) text = text.Substring(1);
            return double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out parsed);
        }

        private static string FindFirstColumn(
            IList<Dictionary<string, string>> rows,
            IEnumerable<string> candidates)
        {
            Dictionary<string, string> first = rows == null
                ? null
                : rows.FirstOrDefault();
            if (first == null) return string.Empty;
            return candidates.FirstOrDefault(first.ContainsKey) ?? string.Empty;
        }

        private static string FirstValue(
            IDictionary<string, string> row,
            params string[] columns)
        {
            foreach (string column in columns ?? new string[0])
            {
                string value = Read(row, column);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return string.Empty;
        }

        private static string Read(
            IDictionary<string, string> row,
            string column)
        {
            if (row == null || string.IsNullOrWhiteSpace(column)) return string.Empty;
            string value;
            return row.TryGetValue(column, out value) ? value ?? string.Empty : string.Empty;
        }

        private static string NormalizeDeviceNo(string value)
        {
            string text = (value ?? string.Empty).Trim();
            int number;
            return int.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out number) && number >= 0
                ? number.ToString("D2", CultureInfo.InvariantCulture)
                : text;
        }

        private static string NormalizeIdentityText(string value)
        {
            return string.Join(" ", (value ?? string.Empty)
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}

using System;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JarviTools.Commands.MaintenanceReachability;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// Explicit MCP bridge between Revit maintenance parameters and an AI-managed XLSX.
    /// It only reads the model and writes deterministic JSON/CSV bridge files.
    /// </summary>
    public sealed class MaintenanceLedgerSyncTool : IRevitTool
    {
        public string Name => "sync_maintenance_ledger_bridge";

        public string Description =>
            "从 Revit 中正式路线、HandReach 与侧墙备选三类 OpenRevit DirectShape 读取 8 个 CODEX 参数，生成用户台账 CSV、CODEX 证据 CSV 和带模型指纹/哈希的 manifest，并记住本模型后续受支持操作的自动同步目录。完整快照可重复运行而不累加重复行，并保留桥接 CSV 的人工确认/备注。本工具不直接改 XLSX，由 AI 核验 manifest 后同步项目台账。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["outputDirectory"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "已存在的绝对输出文件夹。建议使用项目 CODEX 交接资料下的台账同步桥接子文件夹。"
                },
                ["filePrefix"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "可选的稳定文件前缀，默认 maintenance-ledger；不得包含路径。"
                },
                ["expectedModelFingerprint"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "可选。AI 已知的模型指纹；不匹配时拒绝写文件，防止串项目。"
                },
                ["dryRun"] = new JObject
                {
                    ["type"] = "boolean",
                    ["description"] = "仅统计并返回将生成的路径/指纹，不写文件。默认 false。",
                    ["default"] = false
                }
            },
            ["required"] = new JArray { "outputDirectory" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null || uiapp.ActiveUIDocument == null)
                throw new InvalidOperationException("Revit 没有活动项目文档。");
            Document doc = uiapp.ActiveUIDocument.Document;
            input = input ?? new JObject();

            string outputDirectory = ReadString(input, "outputDirectory");
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("outputDirectory 不能为空。");
            if (!Path.IsPathRooted(outputDirectory))
                throw new ArgumentException("outputDirectory 必须是绝对路径。");

            var options = new MaintenanceLedgerSyncOptions
            {
                OutputDirectory = outputDirectory,
                FilePrefix = ReadString(input, "filePrefix"),
                ExpectedModelFingerprint = ReadString(input, "expectedModelFingerprint"),
                DryRun = input["dryRun"] != null && input["dryRun"].Value<bool>()
            };
            MaintenanceLedgerSyncResult result;
            try
            {
                result = MaintenanceLedgerSyncService.Export(doc, options);
            }
            catch (InvalidDataException exception)
            {
                return new JObject
                {
                    ["status"] = "blocked_manual_data_preservation",
                    ["filesWritten"] = false,
                    ["existingManifestCurrentForModel"] = false,
                    ["error"] = exception.Message,
                    ["requiredAction"] =
                        "先迁移或确认孤儿人工行，再重新同步；旧 manifest 只描述旧快照，不能代表当前模型。"
                };
            }
            catch (IOException exception)
            {
                return new JObject
                {
                    ["status"] = "write_failed_snapshot_unusable_verify_manifest_hashes",
                    ["filesWritten"] = false,
                    ["filesKnownConsistent"] = false,
                    ["existingManifestCurrentForModel"] = false,
                    ["error"] = exception.Message,
                    ["requiredAction"] =
                        "重新同步并逐文件核对 manifest SHA-256；核对前不要使用当前桥接快照。"
                };
            }

            return new JObject
            {
                ["status"] = result.FilesWritten ? "written" : "dry_run",
                ["schemaVersion"] = result.SchemaVersion,
                ["modelFingerprint"] = result.ModelFingerprint,
                ["generatedAtUtc"] = result.GeneratedAtUtc,
                ["ownedDirectShapeCount"] = result.OwnedDirectShapeCount,
                ["userLedgerRowCount"] = result.UserLedgerRowCount,
                ["preservedManualRowCount"] = result.PreservedManualRowCount,
                ["snapshotHashSha256"] = result.SnapshotHashSha256,
                ["filesWritten"] = result.FilesWritten,
                ["userLedgerCsvPath"] = result.UserLedgerCsvPath,
                ["codexEvidenceCsvPath"] = result.CodexEvidenceCsvPath,
                ["manifestJsonPath"] = result.ManifestJsonPath,
                ["warnings"] = new JArray(result.Warnings)
            };
        }

        private static string ReadString(JObject input, string name)
        {
            JToken token = input[name];
            return token == null || token.Type == JTokenType.Null
                ? null
                : token.Value<string>();
        }
    }
}

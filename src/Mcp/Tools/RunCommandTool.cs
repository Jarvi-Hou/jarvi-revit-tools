using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 执行 Revit 内置命令。类似 Rhino MCP 的 run_command。
    /// 通过 PostableCommand 枚举触发 Revit 内置命令（如 Wall, Door, Window, Level 等）。
    /// 注意：Revit API 限制——只能触发已注册的内置或插件命令，
    /// 不支持像 Rhino 那样直接执行"任意命令行字符串"。
    /// </summary>
    public class RunCommandTool : IRevitTool
    {
        private static readonly HashSet<string> AllowedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Wall", "Door", "Window", "Level", "Section", "Dimension", "TextNote", "Room", "Area",
            "Floor", "Roof", "Stairs", "Grid", "DetailLine", "ReferencePlane", "StructuralColumn",
            "StructuralBeam", "Pipe", "Duct", "CableTray", "Conduit"
        };

        public string Name => "run_command";

        public string Description =>
            "执行 Revit 内置命令。输入 PostableCommand 名称（如 \"Wall\"、\"Door\"、\"Level\"、\"Section\"、\"Dimension\"），命令会被发送到 Revit 交互式执行。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["command"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "PostableCommand 名称。常见值：Wall, Door, Window, Level, Section, Dimension, TextNote, Room, Floor, Roof, Stairs, Grid, DetailLine, ReferencePlane, StructuralColumn, StructuralBeam, Pipe, Duct, CableTray, Conduit。"
                }
            },
            ["required"] = new JArray { "command" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            if (input == null) throw new ArgumentException("Input is required.");

            string commandStr = (string)input["command"];
            if (string.IsNullOrWhiteSpace(commandStr))
                throw new ArgumentException("'command' is required and must be non-empty.");
            if (!AllowedCommands.Contains(commandStr.Trim()))
                throw new InvalidOperationException(
                    "Command is not in the safe interactive allowlist: " + commandStr +
                    ". Save/close/delete/purge/synchronize/import/export/link/undo commands are intentionally blocked.");

            // 解析 PostableCommand 枚举
            if (!Enum.TryParse<PostableCommand>(commandStr, true, out var postableCmd))
            {
                // 没找到——列出相近的枚举值帮助调试
                var allPostable = Enum.GetValues(typeof(PostableCommand))
                    .Cast<PostableCommand>()
                    .Select(e => e.ToString())
                    .Where(n => n.IndexOf(commandStr, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Take(20)
                    .ToList();

                throw new ArgumentException(
                    "未找到命令 '" + commandStr + "'，它不是有效的 PostableCommand 枚举值。" +
                    (allPostable.Any()
                        ? "\n相近命令: " + string.Join(", ", allPostable)
                        : "") +
                    "\n\n提示：Revit 不能像 Rhino 那样执行任意命令字符串，只能通过 PostableCommand 枚举调用内置命令。" +
                    "\n常见命令：Wall, Door, Window, Level, Section, Dimension, TextNote, Room, Area, Floor, Grid, DetailLine, ReferencePlane");
            }

            var cmdId = RevitCommandId.LookupPostableCommandId(postableCmd);
            if (cmdId == null)
                throw new InvalidOperationException("无法获取命令 ID: " + commandStr);

            if (!uiapp.CanPostCommand(cmdId))
                throw new InvalidOperationException(
                    "Revit cannot post command '" + commandStr + "' in the current UI state.");

            uiapp.PostCommand(cmdId);

            return new JObject
            {
                ["status"]  = "pending_user_interaction",
                ["command"] = commandStr,
                ["message"] = "命令 '" + commandStr + "' 已发送到 Revit，请在 Revit 界面中交互操作。"
            };
        }
    }
}

using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JarviTools.Core;

namespace JarviTools.Mcp.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class StatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Revit MCP Server v" + Constants.PLUGIN_VERSION + " — OpenRevit Tools");
            sb.AppendLine();

            if (McpHost.Server != null && McpHost.Server.IsRunning)
                sb.AppendLine($"状态：运行中 http://127.0.0.1:{McpHost.Server.Port}/");
            else
                sb.AppendLine("状态：已停止");

            sb.AppendLine();
            sb.AppendLine($"已注册工具 ({McpHost.Tools.All().Count()} 个):");
            foreach (var t in McpHost.Tools.All().OrderBy(t => t.Name))
                sb.AppendLine("  • " + t.Name + " — " + t.Description);

            TaskDialog.Show("Revit MCP — 状态", sb.ToString());
            return Result.Succeeded;
        }
    }
}

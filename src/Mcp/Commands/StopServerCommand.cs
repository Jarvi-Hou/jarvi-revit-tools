using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace JarviTools.Mcp.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class StopServerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (McpHost.Server == null || !McpHost.Server.IsRunning)
            {
                TaskDialog.Show("Revit MCP", "Server is not running.");
                return Result.Succeeded;
            }

            McpHost.Server.Stop();
            int cancelled = McpHost.Server.LastCancelledQueuedRequests;
            McpHost.Server = null;
            TaskDialog.Show("Revit MCP", "Server stopped.\nCancelled queued requests: " + cancelled +
                "\nAn operation already running on the Revit thread cannot be interrupted safely.");
            return Result.Succeeded;
        }
    }
}

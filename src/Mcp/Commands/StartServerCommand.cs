using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JarviTools.Mcp.Server;

namespace JarviTools.Mcp.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class StartServerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                if (McpHost.Server != null && McpHost.Server.IsRunning)
                {
                    TaskDialog.Show("OpenRevit MCP",
                        "Server already running on 127.0.0.1:" + McpHost.Server.Port + ".");
                    return Result.Succeeded;
                }

                McpHost.Server = new HttpServer(7800);
                var uidoc = data == null || data.Application == null
                    ? null
                    : data.Application.ActiveUIDocument;
                McpHost.CaptureActiveContext(
                    uidoc == null ? null : uidoc.Document,
                    uidoc == null || uidoc.Document == null ? null : uidoc.Document.ActiveView);
                McpHost.Server.Start();

                TaskDialog.Show("OpenRevit MCP",
                    "Server started on http://127.0.0.1:" + McpHost.Server.Port + "\n\n" +
                    "Registered tools: " + McpHost.Tools.All().Count() + "\n\n" +
                    "Connect a trusted Codex session or another MCP client to query the active Revit model.");
                return Result.Succeeded;
            }
            catch (System.Exception exception)
            {
                message = exception.Message;
                Logger.Error("StartServerCommand failed", exception);
                TaskDialog.Show("OpenRevit MCP — Error", "Failed to start server:\n" + exception.Message);
                return Result.Failed;
            }
        }
    }
}

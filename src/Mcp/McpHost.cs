using System;
using Autodesk.Revit.UI;
using JarviTools.Mcp.Async;
using JarviTools.Mcp.Server;

namespace JarviTools.Mcp
{
    /// <summary>
    /// 替代原 RevitMcpPlugin.App 里的静态成员。
    /// JarviTools.Application 在 OnStartup 时调用 Initialize() 把 MCP 子系统挂起来,
    /// OnShutdown 时调用 Shutdown() 把 HTTP server 关掉。
    ///
    /// 这些静态字段被 HTTP server / Command 类引用,所以必须在调用任何
    /// MCP command 之前就完成 Initialize。
    /// </summary>
    public static class McpHost
    {
        public static RevitExternalEventHandler EventHandler { get; private set; }
        public static ExternalEvent              ExternalEvt  { get; private set; }
        public static HttpServer                 Server       { get; set; }
        public static ToolRegistry               Tools        { get; private set; }
        public static string ActiveDocumentKey { get; private set; }
        public static string ActiveViewUniqueId { get; private set; }

        public static bool IsInitialized => Tools != null;

        public static void Initialize()
        {
            if (IsInitialized) return;

            Logger.Init();
            Logger.Info("McpHost initializing...");

            EventHandler = new RevitExternalEventHandler();
            ExternalEvt  = ExternalEvent.Create(EventHandler);

            Tools = new ToolRegistry();
            Tools.RegisterAll();

            Logger.Info("McpHost ready.");
        }

        public static void CaptureActiveContext(Autodesk.Revit.DB.Document document,
                                                Autodesk.Revit.DB.View view)
        {
            ActiveDocumentKey = RevitExternalEventHandler.BuildDocumentKey(document);
            ActiveViewUniqueId = view == null ? null : view.UniqueId;
        }

        public static void ClearActiveContext()
        {
            ActiveDocumentKey = null;
            ActiveViewUniqueId = null;
        }

        public static void Shutdown()
        {
            try
            {
                if (EventHandler != null) EventHandler.StopAcceptingRequests();
                Server?.Stop();
                Server = null;
                int cancelled = EventHandler == null ? 0 : EventHandler.CancelAllQueued();
                if (cancelled > 0) Logger.Warn("Cancelled " + cancelled + " queued MCP request(s) during shutdown.");
                Logger.Info("McpHost shut down.");
            }
            catch (Exception ex)
            {
                try { Logger.Error("Shutdown error", ex); } catch { }
            }
        }
    }
}

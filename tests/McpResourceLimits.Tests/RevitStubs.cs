using System;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Autodesk.Revit.DB
{
    public class Document
    {
        public string PathName { get; set; }
        public string Title { get; set; }
        public View ActiveView { get; set; }
    }

    public class View
    {
        public string UniqueId { get; set; }
    }
}

namespace Autodesk.Revit.UI
{
    public enum ExternalEventRequest
    {
        Accepted,
        Pending,
        Denied,
        TimedOut
    }

    public interface IExternalEventHandler
    {
        void Execute(UIApplication app);
        string GetName();
    }

    public class ExternalEvent
    {
        public ExternalEventRequest Raise() { return ExternalEventRequest.Accepted; }
    }

    public class UIApplication
    {
        public UIDocument ActiveUIDocument { get; set; }
    }

    public class UIDocument
    {
        public Autodesk.Revit.DB.Document Document { get; set; }
    }
}

namespace JarviTools.Mcp
{
    internal static class McpHost
    {
        public static StubToolRegistry Tools { get; set; }
        public static Autodesk.Revit.UI.ExternalEvent ExternalEvt { get; set; }
    }

    internal sealed class StubToolRegistry
    {
        public StubTool Tool { get; set; }
        public StubTool Get(string name) { return Tool; }
    }

    internal sealed class StubTool
    {
        public int DelayMilliseconds { get; set; }
        public JObject Execute(Autodesk.Revit.UI.UIApplication app, JObject input)
        {
            if (DelayMilliseconds > 0) Thread.Sleep(DelayMilliseconds);
            return new JObject();
        }
    }
}

namespace JarviTools.Mcp.Server
{
    internal static class Logger
    {
        public static void Warn(string message) { }
        public static void Error(string message, Exception exception) { }
    }
}

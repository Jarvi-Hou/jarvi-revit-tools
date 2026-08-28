namespace JarviTools.Mcp.Server
{
    /// <summary>
    /// Hard process-local limits for the loopback MCP endpoint. These are deliberately
    /// conservative because every accepted tool call can ultimately occupy Revit's UI thread.
    /// </summary>
    internal static class McpResourceLimits
    {
        public const int MaxRequestBodyBytes = 1024 * 1024;
        public const int MaxConcurrentHttpRequests = 8;
        public const int MaxQueuedRevitRequests = 64;
        public const int MaxRequestsPerExternalEvent = 4;
        public const int MaxExternalEventSliceMilliseconds = 100;
    }
}

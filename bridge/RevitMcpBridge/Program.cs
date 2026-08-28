using System;
using System.Text;

namespace OpenRevit.McpBridge
{
    internal static class Program
    {
        private const string DefaultBaseUrl = "http://127.0.0.1:7800/";

        private static int Main(string[] args)
        {
            TryConfigureConsoleEncoding();

            string baseUrl = ResolveBaseUrl(args);
            string token = Environment.GetEnvironmentVariable("OPENREVIT_MCP_TOKEN");
            Log("starting (target: " + baseUrl + ")");

            try
            {
                using (var revit = new RevitHttpClient(baseUrl, token, new Uri(baseUrl).Port))
                {
                    return new McpServer(revit).Run();
                }
            }
            catch (Exception ex)
            {
                Log("fatal: " + ex);
                return 1;
            }
        }

        private static string ResolveBaseUrl(string[] args)
        {
            if (args != null && args.Length == 2 &&
                string.Equals(args[0], "--url", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeBaseUrl(args[1]);
            }

            string environmentUrl = Environment.GetEnvironmentVariable("OPENREVIT_MCP_URL");
            return NormalizeBaseUrl(string.IsNullOrWhiteSpace(environmentUrl) ? DefaultBaseUrl : environmentUrl);
        }

        private static string NormalizeBaseUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("OPENREVIT_MCP_URL must be an absolute HTTP(S) URL.");
            }
            if (!uri.IsLoopback)
            {
                throw new ArgumentException("OPENREVIT_MCP_URL must target localhost/loopback. Remote MCP endpoints are not supported.");
            }

            string normalized = uri.AbsoluteUri;
            return normalized.EndsWith("/", StringComparison.Ordinal) ? normalized : normalized + "/";
        }

        private static void TryConfigureConsoleEncoding()
        {
            try
            {
                Console.InputEncoding = Encoding.UTF8;
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch
            {
                // Some MCP hosts own the console encoding. JSON itself remains valid ASCII/UTF-8.
            }
        }

        internal static void Log(string message)
        {
            try
            {
                Console.Error.WriteLine("[openrevit-bridge] " + message);
            }
            catch
            {
                // Logging must never break the JSON-RPC stream.
            }
        }
    }
}

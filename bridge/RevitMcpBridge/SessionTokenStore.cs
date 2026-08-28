using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OpenRevit.McpBridge
{
    internal static class SessionTokenStore
    {
        internal static string Read(int port)
        {
            string environmentToken = Environment.GetEnvironmentVariable("OPENREVIT_MCP_TOKEN");
            if (!string.IsNullOrWhiteSpace(environmentToken))
            {
                return environmentToken.Trim();
            }

            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string path = Path.Combine(local, "OpenRevit Tools", "Mcp", "session-" + port + ".token");
                if (!File.Exists(path))
                {
                    return null;
                }

                byte[] protectedBytes = File.ReadAllBytes(path);
                byte[] clearBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(clearBytes);
            }
            catch
            {
                return null;
            }
        }
    }
}

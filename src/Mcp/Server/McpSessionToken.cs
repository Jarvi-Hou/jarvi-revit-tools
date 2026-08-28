using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace JarviTools.Mcp.Server
{
    /// <summary>
    /// Creates a per-server-start bearer token and stores it encrypted for the
    /// current Windows user. The stdio MCP bridge reads the same file, so users
    /// do not have to copy credentials manually.
    /// </summary>
    internal static class McpSessionToken
    {
        internal static string CreateAndStore(int port)
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(bytes);

            var token = Convert.ToBase64String(bytes);
            var clearBytes = Encoding.UTF8.GetBytes(token);
            var protectedBytes = ProtectedData.Protect(clearBytes, null, DataProtectionScope.CurrentUser);

            var path = GetTokenPath(port);
            var directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var tempPath = path + ".tmp";
            File.WriteAllBytes(tempPath, protectedBytes);
            if (File.Exists(path))
                File.Delete(path);
            File.Move(tempPath, path);

            try { File.SetAttributes(path, FileAttributes.Hidden); } catch { }
            return token;
        }

        internal static void Clear(int port, string expectedToken)
        {
            try
            {
                var path = GetTokenPath(port);
                if (!File.Exists(path)) return;

                var stored = Read(port);
                if (!string.IsNullOrEmpty(expectedToken) && SecureEquals(stored, expectedToken))
                    File.Delete(path);
            }
            catch
            {
                // A stale encrypted token is harmless and will be replaced on next start.
            }
        }

        internal static string Read(int port)
        {
            var path = GetTokenPath(port);
            if (!File.Exists(path)) return null;

            var protectedBytes = File.ReadAllBytes(path);
            var clearBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clearBytes);
        }

        internal static bool SecureEquals(string left, string right)
        {
            if (left == null || right == null) return false;

            var leftBytes = Encoding.UTF8.GetBytes(left);
            var rightBytes = Encoding.UTF8.GetBytes(right);
            var difference = leftBytes.Length ^ rightBytes.Length;
            var count = Math.Max(leftBytes.Length, rightBytes.Length);

            for (var i = 0; i < count; i++)
            {
                var a = i < leftBytes.Length ? leftBytes[i] : (byte)0;
                var b = i < rightBytes.Length ? rightBytes[i] : (byte)0;
                difference |= a ^ b;
            }

            return difference == 0;
        }

        private static string GetTokenPath(int port)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "OpenRevit Tools", "Mcp", "session-" + port + ".token");
        }
    }
}

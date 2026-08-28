using System;
using System.IO;
using System.Text;

namespace JarviTools.Mcp.Server
{
    internal sealed class McpRequestBodyTooLargeException : Exception
    {
        public McpRequestBodyTooLargeException(int maximumBytes)
            : base("request_body_too_large: maximum is " + maximumBytes + " bytes")
        {
        }
    }

    internal static class McpRequestBodyReader
    {
        public static string Read(Stream input, Encoding encoding, long declaredLength, int maximumBytes)
        {
            if (input == null) return string.Empty;
            if (maximumBytes <= 0) throw new ArgumentOutOfRangeException("maximumBytes");
            if (declaredLength > maximumBytes)
                throw new McpRequestBodyTooLargeException(maximumBytes);

            byte[] buffer = new byte[8192];
            int total = 0;
            using (var body = new MemoryStream())
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (read > maximumBytes - total)
                        throw new McpRequestBodyTooLargeException(maximumBytes);
                    body.Write(buffer, 0, read);
                    total += read;
                }

                return (encoding ?? Encoding.UTF8).GetString(body.ToArray());
            }
        }
    }
}

using System;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>Shared offset pagination contract for read-only MCP list tools.</summary>
    internal sealed class PaginationOptions
    {
        public const int DefaultLimit = 100;
        public const int MaxLimit = 1000;

        private PaginationOptions(int limit, int offset)
        {
            Limit = limit;
            Offset = offset;
        }

        public int Limit { get; }
        public int Offset { get; }

        public static PaginationOptions Parse(JObject input)
        {
            int limit = ReadInteger(input, "limit", DefaultLimit);
            int offset = ReadInteger(input, "offset", 0);

            if (limit < 1 || limit > MaxLimit)
                throw new ArgumentOutOfRangeException("limit", "'limit' must be between 1 and " + MaxLimit + ".");
            if (offset < 0)
                throw new ArgumentOutOfRangeException("offset", "'offset' must be zero or greater.");

            return new PaginationOptions(limit, offset);
        }

        public JObject CreateMetadata(int total, int returned)
        {
            bool truncated = Offset + returned < total;
            JToken nextOffset = truncated
                ? (JToken)new JValue(Offset + returned)
                : JValue.CreateNull();

            return new JObject
            {
                ["total"] = total,
                ["returned"] = returned,
                ["limit"] = Limit,
                ["offset"] = Offset,
                ["truncated"] = truncated,
                ["nextOffset"] = nextOffset
            };
        }

        private static int ReadInteger(JObject input, string name, int defaultValue)
        {
            if (input == null)
                return defaultValue;

            JToken token = input[name];
            if (token == null || token.Type == JTokenType.Null)
                return defaultValue;
            if (token.Type != JTokenType.Integer)
                throw new ArgumentException("'" + name + "' must be an integer.");

            try
            {
                return token.Value<int>();
            }
            catch (Exception ex)
            {
                throw new ArgumentException("'" + name + "' is outside the supported integer range.", ex);
            }
        }
    }
}

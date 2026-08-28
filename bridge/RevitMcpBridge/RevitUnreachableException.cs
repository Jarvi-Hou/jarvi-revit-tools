using System;

namespace OpenRevit.McpBridge
{
    internal sealed class RevitUnreachableException : Exception
    {
        public RevitUnreachableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}

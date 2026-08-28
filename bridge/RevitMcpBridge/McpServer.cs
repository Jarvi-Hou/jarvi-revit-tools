using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OpenRevit.McpBridge
{
    internal sealed class McpServer
    {
        private const string ProtocolVersion = "2024-11-05";
        private const string ServerName = "openrevit-mcp-bridge";
        private const string ServerVersion = "0.5.0";
        private const int RevitUnreachableCode = -32000;
        private const string RevitUnreachableMessage =
            "OpenRevit MCP is not reachable. Open Revit and click 'Start MCP Server' on the OpenRevit ribbon.";

        private readonly RevitHttpClient _revit;
        private bool _stopRequested;

        public McpServer(RevitHttpClient revit)
        {
            _revit = revit ?? throw new ArgumentNullException(nameof(revit));
        }

        public int Run()
        {
            TextReader input = Console.In;
            TextWriter output = Console.Out;

            while (!_stopRequested)
            {
                string line;
                try
                {
                    line = input.ReadLine();
                }
                catch (IOException ex)
                {
                    Program.Log("stdin read error: " + ex.Message);
                    break;
                }

                if (line == null)
                {
                    Program.Log("stdin closed; exiting");
                    break;
                }

                if (line.Length > 0)
                {
                    HandleLine(line, output);
                }
            }

            return 0;
        }

        private void HandleLine(string line, TextWriter writer)
        {
            JObject request;
            try
            {
                request = JObject.Parse(line);
            }
            catch (JsonException ex)
            {
                WriteJson(writer, BuildError(JValue.CreateNull(), -32700, "Parse error: " + ex.Message));
                return;
            }

            JToken id = request["id"];
            string method = (string)request["method"];
            JObject parameters = request["params"] as JObject;
            bool isNotification = id == null;

            if (string.IsNullOrWhiteSpace(method))
            {
                if (!isNotification)
                {
                    WriteJson(writer, BuildError(id, -32600, "Invalid request: missing 'method'"));
                }
                return;
            }

            try
            {
                switch (method)
                {
                    case "initialize":
                        if (!isNotification)
                        {
                            WriteJson(writer, BuildResult(id, HandleInitialize()));
                        }
                        break;

                    case "notifications/initialized":
                    case "initialized":
                        Program.Log("client initialized");
                        break;

                    case "tools/list":
                        if (!isNotification)
                        {
                            WriteJson(writer, HandleToolsList(id));
                        }
                        break;

                    case "tools/call":
                        if (!isNotification)
                        {
                            WriteJson(writer, HandleToolsCall(id, parameters));
                        }
                        break;

                    case "ping":
                        if (!isNotification)
                        {
                            WriteJson(writer, BuildResult(id, new JObject()));
                        }
                        break;

                    case "shutdown":
                        _stopRequested = true;
                        if (!isNotification)
                        {
                            WriteJson(writer, BuildResult(id, JValue.CreateNull()));
                        }
                        break;

                    case "exit":
                        _stopRequested = true;
                        break;

                    default:
                        if (!isNotification)
                        {
                            WriteJson(writer, BuildError(id, -32601, "Method not found: " + method));
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Program.Log("handler exception for " + method + ": " + ex);
                if (!isNotification)
                {
                    WriteJson(writer, BuildError(id, -32603, "Internal error: " + ex.Message));
                }
            }
        }

        private static JObject HandleInitialize()
        {
            return new JObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JObject { ["tools"] = new JObject() },
                ["serverInfo"] = new JObject
                {
                    ["name"] = ServerName,
                    ["version"] = ServerVersion
                }
            };
        }

        private JObject HandleToolsList(JToken id)
        {
            JObject payload;
            try
            {
                payload = _revit.GetTools();
            }
            catch (RevitUnreachableException ex)
            {
                Program.Log("tools/list: " + ex.Message);
                return BuildError(id, RevitUnreachableCode, RevitUnreachableMessage);
            }

            if (!((bool?)payload["ok"]).GetValueOrDefault())
            {
                return BuildError(id, -32603, "Plugin error from /tools: " + ((string)payload["error"] ?? "unknown error"));
            }

            JArray tools = payload["tools"] as JArray ?? new JArray();
            tools.Add(new JObject
            {
                ["name"] = "get_revit_operation_status",
                ["description"] = "Query a long-running Revit operation by operationId after an HTTP timeout. This call does not enter the Revit API queue.",
                ["inputSchema"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["operationId"] = new JObject { ["type"] = "string" }
                    },
                    ["required"] = new JArray { "operationId" },
                    ["additionalProperties"] = false
                }
            });
            return BuildResult(id, new JObject
            {
                ["tools"] = tools
            });
        }

        private JObject HandleToolsCall(JToken id, JObject parameters)
        {
            string name = parameters == null ? null : (string)parameters["name"];
            if (string.IsNullOrWhiteSpace(name))
            {
                return BuildError(id, -32602, "Invalid params: 'name' is required");
            }

            JObject arguments = parameters["arguments"] as JObject ?? new JObject();
            JObject payload;
            try
            {
                payload = string.Equals(name, "get_revit_operation_status", StringComparison.Ordinal)
                    ? _revit.GetOperationStatus((string)arguments["operationId"])
                    : _revit.CallTool(name, arguments);
            }
            catch (RevitUnreachableException ex)
            {
                Program.Log("tools/call " + name + ": " + ex.Message);
                return BuildError(id, RevitUnreachableCode, RevitUnreachableMessage);
            }

            bool ok = ((bool?)payload["ok"]).GetValueOrDefault();
            string text = ok
                ? (payload["data"] ?? JValue.CreateNull()).ToString(Formatting.None)
                : "Error: " + ((string)payload["error"] ?? "unknown error");

            return BuildResult(id, new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = text
                    }
                },
                ["isError"] = !ok
            });
        }

        private static JObject BuildResult(JToken id, JToken result)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["result"] = result ?? JValue.CreateNull()
            };
        }

        private static JObject BuildError(JToken id, int code, string message)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message
                }
            };
        }

        private static void WriteJson(TextWriter writer, JObject message)
        {
            writer.WriteLine(message.ToString(Formatting.None));
            writer.Flush();
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using JarviTools.Mcp.Async;
using JarviTools.Core;

namespace JarviTools.Mcp.Server
{
    /// <summary>
    /// Tiny HttpListener-based server.
    /// Routes:
    ///   GET  /health           — quick liveness check
    ///   GET  /tools            — list tools + schemas (for MCP tools/list)
    ///   POST /tools/{name}     — invoke a tool; body = JSON input
    /// </summary>
    public class HttpServer
    {
        public int Port { get; }
        public bool IsRunning { get; private set; }
        public int LastCancelledQueuedRequests { get; private set; }

        private HttpListener _listener;
        private Thread _acceptThread;
        private volatile bool _stop;
        private string _sessionToken;
        private readonly SemaphoreSlim _requestSlots;

        public HttpServer(int port = 7800)
        {
            Port = port;
            _requestSlots = new SemaphoreSlim(
                McpResourceLimits.MaxConcurrentHttpRequests,
                McpResourceLimits.MaxConcurrentHttpRequests);
        }

        public void Start()
        {
            if (IsRunning) return;

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");

            try
            {
                _listener.Start();
                _sessionToken = McpSessionToken.CreateAndStore(Port);
            }
            catch (HttpListenerException ex)
            {
                McpSessionToken.Clear(Port, _sessionToken);
                _sessionToken = null;
                throw new InvalidOperationException(
                    $"Failed to bind http://127.0.0.1:{Port}/. " +
                    "Either the port is in use, or an administrator must grant URL ACL to the current Windows user. " +
                    $"Example: netsh http add urlacl url=http://127.0.0.1:{Port}/ user=\"%USERDOMAIN%\\%USERNAME%\"\n" +
                    "Underlying: " + ex.Message, ex);
            }
            catch
            {
                try { _listener.Stop(); } catch { }
                try { _listener.Close(); } catch { }
                _listener = null;
                IsRunning = false;
                McpSessionToken.Clear(Port, _sessionToken);
                _sessionToken = null;
                throw;
            }

            _stop = false;
            IsRunning = true;
            LastCancelledQueuedRequests = 0;
            if (McpHost.EventHandler != null)
                McpHost.EventHandler.StartAcceptingRequests();
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "RevitMcp-Accept" };
            _acceptThread.Start();

            Logger.Info($"HTTP server listening on 127.0.0.1:{Port}");
        }

        public void Stop()
        {
            _stop = true;
            if (McpHost.EventHandler != null)
                McpHost.EventHandler.StopAcceptingRequests();
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
            IsRunning = false;
            int cancelled = McpHost.EventHandler == null ? 0 : McpHost.EventHandler.CancelAllQueued();
            LastCancelledQueuedRequests = cancelled;
            McpSessionToken.Clear(Port, _sessionToken);
            _sessionToken = null;
            if (cancelled > 0)
                Logger.Warn("Cancelled " + cancelled + " queued MCP request(s) while stopping the server.");
            Logger.Info("HTTP server stopped.");
        }

        private void AcceptLoop()
        {
            while (!_stop)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { return; }  // listener was stopped

                if (!_requestSlots.Wait(0))
                {
                    try
                    {
                        ctx.Response.Headers["Retry-After"] = "1";
                        WriteJson(ctx.Response, 429, new JObject
                        {
                            ["ok"] = false,
                            ["error"] = "too_many_concurrent_requests",
                            ["maximum"] = McpResourceLimits.MaxConcurrentHttpRequests
                        });
                    }
                    catch { }
                    continue;
                }

                bool queued = ThreadPool.QueueUserWorkItem(delegate
                {
                    try { Handle(ctx); }
                    catch (Exception ex) { Logger.Error("Unhandled in HandleRequest", ex); }
                    finally { _requestSlots.Release(); }
                });
                if (!queued)
                {
                    _requestSlots.Release();
                    try
                    {
                        WriteJson(ctx.Response, 503, new JObject
                        {
                            ["ok"] = false,
                            ["error"] = "request_dispatch_unavailable"
                        });
                    }
                    catch { }
                }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var req  = ctx.Request;
            var resp = ctx.Response;
            var path = req.Url.AbsolutePath.TrimEnd('/');

            try
            {
                if (path == "/health" || path == "")
                {
                    WriteJson(resp, 200, new JObject
                    {
                        ["ok"]      = true,
                        ["version"] = Constants.PLUGIN_VERSION,
                        ["server"]  = "OpenRevitMcpPlugin",
                        ["tools"]   = McpHost.Tools.All().Count(),
                        ["queuedRevitRequests"] = McpHost.EventHandler == null ? 0 : McpHost.EventHandler.QueuedCount,
                        ["limits"] = new JObject
                        {
                            ["requestBodyBytes"] = McpResourceLimits.MaxRequestBodyBytes,
                            ["concurrentHttpRequests"] = McpResourceLimits.MaxConcurrentHttpRequests,
                            ["queuedRevitRequests"] = McpResourceLimits.MaxQueuedRevitRequests,
                            ["requestsPerExternalEvent"] = McpResourceLimits.MaxRequestsPerExternalEvent,
                            ["externalEventSliceMilliseconds"] = McpResourceLimits.MaxExternalEventSliceMilliseconds
                        }
                    });
                    return;
                }

                if (!IsAuthorized(req))
                {
                    // HttpListener forbids setting WWW-Authenticate manually via
                    // Headers. Authentication is an application-level bearer token,
                    // so return a clean forbidden response instead of throwing a 500.
                    WriteJson(resp, 403, new JObject
                    {
                        ["ok"] = false,
                        ["error"] = "forbidden: use the bundled OpenRevit MCP bridge"
                    });
                    return;
                }

                if (path == "/tools" && req.HttpMethod == "GET")
                {
                    WriteJson(resp, 200, new JObject
                    {
                        ["ok"]    = true,
                        ["tools"] = McpHost.Tools.Describe()
                    });
                    return;
                }

                if (path.StartsWith("/operations/", StringComparison.Ordinal) && req.HttpMethod == "GET")
                {
                    string operationId = path.Substring("/operations/".Length);
                    WriteJson(resp, 200, new JObject
                    {
                        ["ok"] = true,
                        ["data"] = McpHost.EventHandler.GetOperationStatus(operationId)
                    });
                    return;
                }

                if (path.StartsWith("/tools/") && req.HttpMethod == "POST")
                {
                    var toolName = path.Substring("/tools/".Length);
                    var body     = ReadBody(req);
                    var input    = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);

                    var result = InvokeTool(toolName, input);
                    WriteJson(resp, 200, result);
                    return;
                }

                WriteJson(resp, 404, new JObject
                {
                    ["ok"] = false, ["error"] = $"not_found: {req.HttpMethod} {path}"
                });
            }
            catch (McpRequestBodyTooLargeException ex)
            {
                WriteJson(resp, 413, new JObject
                {
                    ["ok"] = false,
                    ["error"] = ex.Message
                });
            }
            catch (JsonException ex)
            {
                WriteJson(resp, 400, new JObject
                {
                    ["ok"] = false,
                    ["error"] = "invalid_json: " + ex.Message
                });
            }
            catch (Exception ex)
            {
                Logger.Error("Request failed: " + path, ex);
                try
                {
                    WriteJson(resp, 500, new JObject
                    {
                        ["ok"] = false, ["error"] = ex.Message
                    });
                }
                catch { /* response may already be closed */ }
            }
        }

        private bool IsAuthorized(HttpListenerRequest request)
        {
            var header = request.Headers["Authorization"];
            const string prefix = "Bearer ";
            if (string.IsNullOrWhiteSpace(header) || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            var supplied = header.Substring(prefix.Length).Trim();
            return McpSessionToken.SecureEquals(_sessionToken, supplied);
        }

        private JObject InvokeTool(string toolName, JObject input)
        {
            if (_stop || !IsRunning)
            {
                return new JObject
                {
                    ["ok"] = false,
                    ["error"] = "mcp_server_stopping"
                };
            }

            var pending = new PendingRequest
            {
                ToolName = toolName,
                Input = input,
                ExpectedDocumentKey = McpHost.ActiveDocumentKey,
                ExpectedViewUniqueId = McpHost.ActiveViewUniqueId,
                OperationId = Guid.NewGuid().ToString("N")
            };
            string rejection;
            if (!McpHost.EventHandler.TryEnqueue(pending, out rejection))
            {
                return new JObject
                {
                    ["ok"] = false,
                    ["error"] = rejection
                };
            }

            var raiseResult = McpHost.ExternalEvt.Raise();
            if (raiseResult != Autodesk.Revit.UI.ExternalEventRequest.Accepted &&
                raiseResult != Autodesk.Revit.UI.ExternalEventRequest.Pending)
            {
                if (pending.TryCancel())
                {
                    pending.Error = new InvalidOperationException("revit_event_not_accepted: " + raiseResult);
                    pending.Done.Set();
                }
                Logger.Warn("ExternalEvent.Raise() returned " + raiseResult + " for tool " + toolName);
                return new JObject
                {
                    ["ok"] = false,
                    ["operationId"] = pending.OperationId,
                    ["error"] = "revit_event_not_accepted: " + raiseResult
                };
            }

            var finished = pending.Done.Wait(TimeSpan.FromMinutes(5));
            if (!finished)
            {
                if (pending.TryCancel())
                {
                    pending.Error = new TimeoutException("revit_queue_timeout (300s); request cancelled before execution");
                    pending.Done.Set();
                    return new JObject
                    {
                        ["ok"] = false,
                        ["operationId"] = pending.OperationId,
                        ["error"] = "revit_queue_timeout (300s); request cancelled before execution"
                    };
                }
                // Completion and timeout can race by a few instructions. Give Done.Set() one final chance.
                finished = pending.Done.Wait(TimeSpan.FromSeconds(1));
            }
            if (!finished)
            {
                return new JObject
                {
                    ["ok"] = false,
                    ["operationId"] = pending.OperationId,
                    ["error"] = "revit_operation_still_running_after_300s; do not retry blindly"
                };
            }

            if (pending.Error != null)
                return new JObject { ["ok"] = false, ["error"] = pending.Error.Message };

            return new JObject { ["ok"] = true, ["data"] = pending.Result };
        }

        private static string ReadBody(HttpListenerRequest req)
        {
            return McpRequestBodyReader.Read(
                req.InputStream,
                req.ContentEncoding ?? Encoding.UTF8,
                req.ContentLength64,
                McpResourceLimits.MaxRequestBodyBytes);
        }

        private static void WriteJson(HttpListenerResponse resp, int statusCode, JToken payload)
        {
            var json  = payload.ToString(Formatting.None);
            var bytes = Encoding.UTF8.GetBytes(json);
            resp.StatusCode      = statusCode;
            resp.ContentType     = "application/json; charset=utf-8";
            resp.ContentLength64 = bytes.Length;
            resp.OutputStream.Write(bytes, 0, bytes.Length);
            resp.OutputStream.Close();
        }
    }
}

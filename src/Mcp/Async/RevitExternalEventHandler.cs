using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Linq;
using System.Diagnostics;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using JarviTools.Mcp.Server;

namespace JarviTools.Mcp.Async
{
    /// <summary>One request queued from HTTP thread to Revit main thread.</summary>
    public class PendingRequest
    {
        public string ToolName;
        public JObject Input;
        public JObject Result;
        public Exception Error;
        public string ExpectedDocumentKey;
        public string ExpectedViewUniqueId;
        public string OperationId;
        // 0=queued, 1=started, 2=cancelled, 3=completed.
        private int _state;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        public DateTime CreatedUtc = DateTime.UtcNow;
        public DateTime? StartedUtc;
        public DateTime? CompletedUtc;

        public bool TryStart()
        {
            bool started = Interlocked.CompareExchange(ref _state, 1, 0) == 0;
            if (started) StartedUtc = DateTime.UtcNow;
            return started;
        }
        public bool TryCancel()
        {
            bool cancelled = Interlocked.CompareExchange(ref _state, 2, 0) == 0;
            if (cancelled) CompletedUtc = DateTime.UtcNow;
            return cancelled;
        }
        public bool IsStarted => Volatile.Read(ref _state) == 1;
        public bool IsCompleted => Volatile.Read(ref _state) == 3;
        public bool IsCancelled => Volatile.Read(ref _state) == 2;
        public string StateName
        {
            get
            {
                switch (Volatile.Read(ref _state))
                {
                    case 0: return "queued";
                    case 1: return "running";
                    case 2: return "cancelled";
                    case 3: return Error == null ? "completed" : "failed";
                    default: return "unknown";
                }
            }
        }
        public void MarkCompleted()
        {
            CompletedUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref _state, 3);
        }
    }

    /// <summary>
    /// Drains the request queue on the Revit main thread.
    /// HTTP threads enqueue requests and Raise() the external event;
    /// Revit invokes Execute() on its main thread; we run each tool and signal completion.
    /// </summary>
    public class RevitExternalEventHandler : IExternalEventHandler
    {
        private readonly ConcurrentQueue<PendingRequest> _queue = new ConcurrentQueue<PendingRequest>();
        private readonly ConcurrentDictionary<string, PendingRequest> _operations =
            new ConcurrentDictionary<string, PendingRequest>(StringComparer.Ordinal);
        private readonly object _enqueueGate = new object();
        private int _queuedCount;
        private bool _acceptingRequests;
        private int _continuationScheduled;

        public int QueuedCount { get { return Volatile.Read(ref _queuedCount); } }
        public bool IsAcceptingRequests
        {
            get { lock (_enqueueGate) return _acceptingRequests; }
        }

        public void StartAcceptingRequests()
        {
            lock (_enqueueGate)
                _acceptingRequests = true;
        }

        public void StopAcceptingRequests()
        {
            lock (_enqueueGate)
                _acceptingRequests = false;
        }

        public bool TryEnqueue(PendingRequest req, out string rejection)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.OperationId))
                throw new ArgumentException("OperationId is required.");

            lock (_enqueueGate)
            {
                if (!_acceptingRequests)
                {
                    rejection = "mcp_server_stopping";
                    return false;
                }
                if (_queuedCount >= McpResourceLimits.MaxQueuedRevitRequests)
                {
                    rejection = "revit_queue_full: maximum is " +
                        McpResourceLimits.MaxQueuedRevitRequests + " queued requests";
                    return false;
                }

                _operations[req.OperationId] = req;
                _queue.Enqueue(req);
                Interlocked.Increment(ref _queuedCount);
            }
            TrimOperations();
            rejection = null;
            return true;
        }

        public JObject GetOperationStatus(string operationId)
        {
            PendingRequest request;
            if (string.IsNullOrWhiteSpace(operationId) || !_operations.TryGetValue(operationId, out request))
                return new JObject { ["found"] = false, ["operationId"] = operationId };

            var response = new JObject
            {
                ["found"] = true,
                ["operationId"] = request.OperationId,
                ["toolName"] = request.ToolName,
                ["status"] = request.StateName,
                ["createdAtUtc"] = request.CreatedUtc.ToString("o"),
                ["startedAtUtc"] = request.StartedUtc.HasValue ? (JToken)request.StartedUtc.Value.ToString("o") : JValue.CreateNull(),
                ["completedAtUtc"] = request.CompletedUtc.HasValue ? (JToken)request.CompletedUtc.Value.ToString("o") : JValue.CreateNull()
            };
            if (request.IsCompleted)
            {
                response["ok"] = request.Error == null;
                if (request.Error == null) response["data"] = request.Result;
                else response["error"] = request.Error.Message;
            }
            else if (request.IsCancelled)
            {
                response["ok"] = false;
                response["error"] = request.Error == null ? "cancelled" : request.Error.Message;
            }
            return response;
        }

        private void TrimOperations()
        {
            DateTime cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var pair in _operations.Where(x =>
                x.Value.CompletedUtc.HasValue && x.Value.CompletedUtc.Value < cutoff).ToList())
            {
                PendingRequest ignored;
                _operations.TryRemove(pair.Key, out ignored);
            }
            if (_operations.Count <= 256) return;
            foreach (var pair in _operations.OrderBy(x => x.Value.CreatedUtc).Take(_operations.Count - 256).ToList())
            {
                PendingRequest ignored;
                _operations.TryRemove(pair.Key, out ignored);
            }
        }

        public int CancelAllQueued()
        {
            return CancelAllQueued("MCP server stopped before request execution.");
        }

        private int CancelAllQueued(string reason)
        {
            int cancelled = 0;
            PendingRequest request;
            while (TryDequeue(out request))
            {
                if (request.TryCancel())
                {
                    request.Error = new OperationCanceledException(reason);
                    request.Done.Set();
                    cancelled++;
                }
                else if (request.IsCancelled)
                {
                    request.Done.Set();
                }
            }
            return cancelled;
        }

        public void Execute(UIApplication app)
        {
            var stopwatch = Stopwatch.StartNew();
            int processed = 0;
            PendingRequest req;
            while (processed < McpResourceLimits.MaxRequestsPerExternalEvent
                && stopwatch.ElapsedMilliseconds < McpResourceLimits.MaxExternalEventSliceMilliseconds
                && TryDequeue(out req))
            {
                if (!req.TryStart())
                {
                    req.Done.Set();
                    continue;
                }

                processed++;
                try
                {
                    var tool = McpHost.Tools.Get(req.ToolName);
                    if (tool == null)
                        throw new InvalidOperationException("Unknown tool: " + req.ToolName);

                    if (app.ActiveUIDocument == null)
                        throw new InvalidOperationException("No active document in Revit.");

                    var activeDocument = app.ActiveUIDocument.Document;
                    var activeView = activeDocument.ActiveView;
                    if (!string.IsNullOrEmpty(req.ExpectedDocumentKey)
                        && !string.Equals(req.ExpectedDocumentKey, BuildDocumentKey(activeDocument), StringComparison.Ordinal))
                        throw new OperationCanceledException("Active Revit document changed before the MCP request could execute.");
                    if (!string.IsNullOrEmpty(req.ExpectedViewUniqueId)
                        && (activeView == null || !string.Equals(req.ExpectedViewUniqueId, activeView.UniqueId, StringComparison.Ordinal)))
                        throw new OperationCanceledException("Active Revit view changed before the MCP request could execute.");

                    req.Result = tool.Execute(app, req.Input ?? new JObject());
                }
                catch (Exception ex)
                {
                    Logger.Error("Tool '" + req.ToolName + "' failed", ex);
                    req.Error = ex;
                }
                finally
                {
                    req.MarkCompleted();
                    req.Done.Set();
                }
            }

            if (QueuedCount > 0 && IsAcceptingRequests)
                ScheduleContinuation();
        }

        private bool TryDequeue(out PendingRequest request)
        {
            if (!_queue.TryDequeue(out request)) return false;
            Interlocked.Decrement(ref _queuedCount);
            return true;
        }

        private void ScheduleContinuation()
        {
            if (Interlocked.CompareExchange(ref _continuationScheduled, 1, 0) != 0)
                return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    // Let the current ExternalEvent execution return before raising the next slice.
                    Thread.Sleep(25);
                    if (QueuedCount <= 0 || !IsAcceptingRequests) return;

                    ExternalEvent externalEvent = McpHost.ExternalEvt;
                    if (externalEvent == null)
                    {
                        CancelAllQueued("Revit ExternalEvent is unavailable.");
                        return;
                    }

                    ExternalEventRequest raiseResult = externalEvent.Raise();
                    if (raiseResult != ExternalEventRequest.Accepted &&
                        raiseResult != ExternalEventRequest.Pending)
                    {
                        Logger.Warn("Unable to continue the bounded MCP queue: " + raiseResult);
                        CancelAllQueued("Revit did not accept the continuation ExternalEvent: " + raiseResult);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to schedule the next bounded MCP queue slice", ex);
                    CancelAllQueued("Failed to schedule the next Revit ExternalEvent.");
                }
                finally
                {
                    Interlocked.Exchange(ref _continuationScheduled, 0);
                }
            });
        }

        internal static string BuildDocumentKey(Autodesk.Revit.DB.Document document)
        {
            if (document == null) return null;
            string path = null;
            try { path = document.PathName; } catch { }
            return (path ?? string.Empty) + "|" + (document.Title ?? string.Empty) + "|" + document.GetHashCode();
        }

        public string GetName() => "RevitMcp External Event Handler";
    }
}

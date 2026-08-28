using System;
using System.IO;
using System.Linq;
using System.Text;
using JarviTools.Mcp.Server;
using JarviTools.Mcp.Async;
using JarviTools.Mcp;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        Run("release limits are conservative", ReleaseLimitsAreConservative);
        Run("body at exact limit is accepted", ExactLimitIsAccepted);
        Run("declared oversize body is rejected before reading", DeclaredOversizeIsRejected);
        Run("chunked oversize body is rejected while reading", ChunkedOversizeIsRejected);
        Run("Revit queue rejects the sixty-fifth request", RevitQueueIsBounded);
        Run("stopping admission cancels all queued requests", StopCancelsQueuedRequests);
        Run("ExternalEvent handler yields at item budget", HandlerYieldsAtItemBudget);
        Run("ExternalEvent handler yields at time boundary", HandlerYieldsAtTimeBoundary);

        Console.WriteLine("TOTAL passed=" + _passed + " failed=" + _failed);
        return _failed == 0 ? 0 : 1;
    }

    private static void ReleaseLimitsAreConservative()
    {
        Assert(McpResourceLimits.MaxRequestBodyBytes == 1024 * 1024, "Body limit changed unexpectedly.");
        Assert(McpResourceLimits.MaxConcurrentHttpRequests > 0 &&
               McpResourceLimits.MaxConcurrentHttpRequests <= 16, "HTTP concurrency is not bounded conservatively.");
        Assert(McpResourceLimits.MaxQueuedRevitRequests > 0 &&
               McpResourceLimits.MaxQueuedRevitRequests <= 128, "Revit queue is not bounded conservatively.");
        Assert(McpResourceLimits.MaxRequestsPerExternalEvent > 0 &&
               McpResourceLimits.MaxRequestsPerExternalEvent <= 8, "ExternalEvent batch is too large.");
        Assert(McpResourceLimits.MaxExternalEventSliceMilliseconds > 0 &&
               McpResourceLimits.MaxExternalEventSliceMilliseconds <= 250, "ExternalEvent time slice is too large.");
    }

    private static void ExactLimitIsAccepted()
    {
        byte[] bytes = new byte[McpResourceLimits.MaxRequestBodyBytes];
        string result = McpRequestBodyReader.Read(
            new MemoryStream(bytes), Encoding.UTF8, bytes.Length, McpResourceLimits.MaxRequestBodyBytes);
        Assert(result.Length == bytes.Length, "Exact-limit body was truncated.");
    }

    private static void DeclaredOversizeIsRejected()
    {
        var stream = new ThrowIfReadStream();
        AssertThrowsTooLarge(delegate
        {
            McpRequestBodyReader.Read(
                stream,
                Encoding.UTF8,
                McpResourceLimits.MaxRequestBodyBytes + 1L,
                McpResourceLimits.MaxRequestBodyBytes);
        });
        Assert(!stream.WasRead, "Oversize declared body should be rejected before reading the stream.");
    }

    private static void ChunkedOversizeIsRejected()
    {
        byte[] bytes = new byte[McpResourceLimits.MaxRequestBodyBytes + 1];
        AssertThrowsTooLarge(delegate
        {
            McpRequestBodyReader.Read(
                new MemoryStream(bytes), Encoding.UTF8, -1, McpResourceLimits.MaxRequestBodyBytes);
        });
    }

    private static void RevitQueueIsBounded()
    {
        var handler = new RevitExternalEventHandler();
        handler.StartAcceptingRequests();
        for (int i = 0; i < McpResourceLimits.MaxQueuedRevitRequests; i++)
        {
            string rejection;
            Assert(handler.TryEnqueue(NewRequest(i), out rejection), "Request " + i + " was rejected: " + rejection);
        }

        string fullRejection;
        Assert(!handler.TryEnqueue(NewRequest(999), out fullRejection), "Queue accepted more than its hard limit.");
        Assert(fullRejection.StartsWith("revit_queue_full", StringComparison.Ordinal), "Wrong queue-full reason.");
        Assert(handler.QueuedCount == McpResourceLimits.MaxQueuedRevitRequests, "Queue count exceeded its limit.");
    }

    private static void StopCancelsQueuedRequests()
    {
        var handler = new RevitExternalEventHandler();
        handler.StartAcceptingRequests();
        var requests = new PendingRequest[3];
        for (int i = 0; i < requests.Length; i++)
        {
            requests[i] = NewRequest(i);
            string rejection;
            Assert(handler.TryEnqueue(requests[i], out rejection), "Setup enqueue failed: " + rejection);
        }

        handler.StopAcceptingRequests();
        int cancelled = handler.CancelAllQueued();
        Assert(cancelled == requests.Length, "Not every queued request was cancelled.");
        Assert(handler.QueuedCount == 0, "Cancelled requests remained in the queue.");
        for (int i = 0; i < requests.Length; i++)
        {
            Assert(requests[i].IsCancelled, "Request did not retain cancelled state.");
            Assert(requests[i].Done.IsSet, "Cancelled waiter was not released.");
        }

        string rejectionAfterStop;
        Assert(!handler.TryEnqueue(NewRequest(9), out rejectionAfterStop), "Stopped handler accepted a new request.");
        Assert(rejectionAfterStop == "mcp_server_stopping", "Wrong stopped-handler rejection reason.");
    }

    private static PendingRequest NewRequest(int number)
    {
        return new PendingRequest
        {
            OperationId = "operation-" + number,
            ToolName = "test"
        };
    }

    private static void HandlerYieldsAtItemBudget()
    {
        var handler = CreateExecutableHandler(0);
        var requests = EnqueueRequests(handler, McpResourceLimits.MaxRequestsPerExternalEvent + 1);
        handler.Execute(CreateApplication());

        Assert(requests.Take(McpResourceLimits.MaxRequestsPerExternalEvent).All(x => x.IsCompleted),
            "The first bounded batch did not complete.");
        Assert(!requests[requests.Length - 1].IsCompleted, "Handler drained beyond its item budget.");
        Assert(handler.QueuedCount == 1, "Expected one request to remain queued after item-budget yield.");
        handler.StopAcceptingRequests();
        handler.CancelAllQueued();
    }

    private static void HandlerYieldsAtTimeBoundary()
    {
        var handler = CreateExecutableHandler(60);
        var requests = EnqueueRequests(handler, McpResourceLimits.MaxRequestsPerExternalEvent);
        handler.Execute(CreateApplication());

        int completed = requests.Count(x => x.IsCompleted);
        Assert(completed >= 1 && completed < requests.Length,
            "Handler did not yield between requests after crossing its time budget; completed=" + completed);
        Assert(handler.QueuedCount == requests.Length - completed, "Time-budget yield left an inconsistent queue count.");
        handler.StopAcceptingRequests();
        handler.CancelAllQueued();
    }

    private static RevitExternalEventHandler CreateExecutableHandler(int delayMilliseconds)
    {
        McpHost.Tools = new StubToolRegistry
        {
            Tool = new StubTool { DelayMilliseconds = delayMilliseconds }
        };
        McpHost.ExternalEvt = new Autodesk.Revit.UI.ExternalEvent();
        var handler = new RevitExternalEventHandler();
        handler.StartAcceptingRequests();
        return handler;
    }

    private static PendingRequest[] EnqueueRequests(RevitExternalEventHandler handler, int count)
    {
        var requests = Enumerable.Range(0, count).Select(NewRequest).ToArray();
        foreach (PendingRequest request in requests)
        {
            string rejection;
            Assert(handler.TryEnqueue(request, out rejection), "Setup enqueue failed: " + rejection);
        }
        return requests;
    }

    private static Autodesk.Revit.UI.UIApplication CreateApplication()
    {
        return new Autodesk.Revit.UI.UIApplication
        {
            ActiveUIDocument = new Autodesk.Revit.UI.UIDocument
            {
                Document = new Autodesk.Revit.DB.Document
                {
                    Title = "test",
                    ActiveView = new Autodesk.Revit.DB.View { UniqueId = "view" }
                }
            }
        };
    }

    private static void AssertThrowsTooLarge(Action action)
    {
        try
        {
            action();
            throw new InvalidOperationException("Expected McpRequestBodyTooLargeException.");
        }
        catch (McpRequestBodyTooLargeException)
        {
        }
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("FAIL " + name + " :: " + ex.Message);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ThrowIfReadStream : MemoryStream
    {
        public bool WasRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            WasRead = true;
            throw new InvalidOperationException("Stream should not have been read.");
        }
    }
}

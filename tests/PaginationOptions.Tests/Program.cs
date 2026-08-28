using System;
using JarviTools.Mcp.Tools;
using Newtonsoft.Json.Linq;

internal static class Program
{
    private static int Main()
    {
        try
        {
            DefaultsAreStable();
            NextOffsetIsReported();
            LastPageHasNoNextOffset();
            InvalidInputsAreRejected();
            Console.WriteLine("PASS pagination contract (4 checks)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL pagination contract :: " + ex.Message);
            return 1;
        }
    }

    private static void DefaultsAreStable()
    {
        PaginationOptions paging = PaginationOptions.Parse(null);
        Assert(paging.Limit == 100, "Default limit changed.");
        Assert(paging.Offset == 0, "Default offset changed.");
    }

    private static void NextOffsetIsReported()
    {
        PaginationOptions paging = PaginationOptions.Parse(new JObject
        {
            ["limit"] = 25,
            ["offset"] = 50
        });
        JObject metadata = paging.CreateMetadata(120, 25);
        Assert((int)metadata["total"] == 120, "Wrong total.");
        Assert((int)metadata["returned"] == 25, "Wrong returned count.");
        Assert((bool)metadata["truncated"], "Intermediate page must be truncated.");
        Assert((int)metadata["nextOffset"] == 75, "Wrong nextOffset.");
    }

    private static void LastPageHasNoNextOffset()
    {
        PaginationOptions paging = PaginationOptions.Parse(new JObject
        {
            ["limit"] = 1000,
            ["offset"] = 100
        });
        JObject metadata = paging.CreateMetadata(120, 20);
        Assert(!(bool)metadata["truncated"], "Last page must not be truncated.");
        Assert(metadata["nextOffset"].Type == JTokenType.Null, "Last page must have null nextOffset.");
    }

    private static void InvalidInputsAreRejected()
    {
        AssertThrows(() => PaginationOptions.Parse(new JObject { ["limit"] = 0 }));
        AssertThrows(() => PaginationOptions.Parse(new JObject { ["limit"] = 1001 }));
        AssertThrows(() => PaginationOptions.Parse(new JObject { ["limit"] = "100" }));
        AssertThrows(() => PaginationOptions.Parse(new JObject { ["offset"] = -1 }));
    }

    private static void AssertThrows(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException("Expected an argument validation failure.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

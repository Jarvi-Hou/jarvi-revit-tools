using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using JarviTools.Commands.MaintenanceReachability;

internal static class MaintenanceLedgerCsvTests
{
    private static int _assertions;

    public static int Main(string[] args)
    {
        try
        {
            RoundTripQuotedChineseAndMultilineFields();
            FormulaLikeValuesAreNeutralized();
            AtomicReplacementDoesNotAccumulateRows(args[0]);
            ManualOrphansAreDetectedBeforeReplacement();
            InvalidCsvIsRejected();
            Console.WriteLine("PASS MaintenanceLedgerCsvTests assertions=" + _assertions);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL " + exception);
            return 1;
        }
    }

    private static void RoundTripQuotedChineseAndMultilineFields()
    {
        var headers = new[] { "行键", "结论", "备注" };
        var sourceRow = new Dictionary<string, string>();
        sourceRow["行键"] = "MR1-a";
        sourceRow["结论"] = "可维修";
        sourceRow["备注"] = "施工单位说：\"手,工具\"\r\n均可进入";
        var rows = new List<IDictionary<string, string>> { sourceRow };

        string csv = MaintenanceLedgerCsv.Serialize(headers, rows);
        List<Dictionary<string, string>> parsed = MaintenanceLedgerCsv.Parse(csv);
        Assert(parsed.Count == 1, "round-trip row count");
        Assert(parsed[0]["行键"] == "MR1-a", "stable row key");
        Assert(parsed[0]["备注"] == rows[0]["备注"], "quoted multiline note");
    }

    private static void AtomicReplacementDoesNotAccumulateRows(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, "maintenance-ledger-test.user.csv");
        var headers = new[] { "行键", "台账人工备注" };
        var firstA = new Dictionary<string, string>();
        firstA["行键"] = "A";
        firstA["台账人工备注"] = "first";
        var firstB = new Dictionary<string, string>();
        firstB["行键"] = "B";
        firstB["台账人工备注"] = "second";
        var first = new List<IDictionary<string, string>> { firstA, firstB };
        MaintenanceLedgerCsv.WriteAllTextAtomic(path,
            MaintenanceLedgerCsv.Serialize(headers, first));

        var secondA = new Dictionary<string, string>();
        secondA["行键"] = "A";
        secondA["台账人工备注"] = "kept";
        var second = new List<IDictionary<string, string>> { secondA };
        MaintenanceLedgerCsv.WriteAllTextAtomic(path,
            MaintenanceLedgerCsv.Serialize(headers, second));

        byte[] bytes = File.ReadAllBytes(path);
        Assert(bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf,
            "UTF-8 BOM");
        List<Dictionary<string, string>> parsed = MaintenanceLedgerCsv.Parse(
            MaintenanceLedgerCsv.ReadAllTextShared(path));
        Assert(parsed.Count == 1, "full snapshot replacement");
        Assert(parsed[0]["行键"] == "A", "replacement key");
        Assert(parsed[0]["台账人工备注"] == "kept", "replacement value");
        Assert(MaintenanceLedgerCsv.Sha256Hex(bytes) ==
               MaintenanceLedgerCsv.Sha256HexUtf8BomFile(
                   MaintenanceLedgerCsv.Serialize(headers, second)),
            "manifest hash covers exact UTF-8 BOM file bytes");
    }

    private static void FormulaLikeValuesAreNeutralized()
    {
        var row = new Dictionary<string, string>();
        row["key"] = "=HYPERLINK(\"https://example.invalid\")";
        string csv = MaintenanceLedgerCsv.Serialize(
            new[] { "key" },
            new List<IDictionary<string, string>> { row });
        var parsed = MaintenanceLedgerCsv.Parse(csv);
        Assert(parsed[0]["key"].StartsWith("'=", StringComparison.Ordinal),
            "ledger CSV formula injection neutralized");
    }

    private static void InvalidCsvIsRejected()
    {
        bool rejected = false;
        try { MaintenanceLedgerCsv.Parse("行键,备注\r\nA,\"not closed"); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "unclosed quote rejected");
    }

    private static void ManualOrphansAreDetectedBeforeReplacement()
    {
        var prior = new List<KeyValuePair<string, bool>>
        {
            new KeyValuePair<string, bool>("kept", true),
            new KeyValuePair<string, bool>("removed-manual", true),
            new KeyValuePair<string, bool>("removed-empty", false)
        };
        List<string> orphanKeys = MaintenanceLedgerCsv.FindOrphanManualKeys(
            new[] { "kept", "new" }, prior);
        Assert(orphanKeys.Count == 1 && orphanKeys[0] == "removed-manual",
            "only removed rows with manual data are protected as orphans");
    }

    private static void Assert(bool condition, string name)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException("Assertion failed: " + name);
    }
}

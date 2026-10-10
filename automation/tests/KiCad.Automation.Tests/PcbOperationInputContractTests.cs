using System.Text.Json;
using Google.Protobuf;
using Kiapi.Board.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbOperationInputContractTests
{
    [TestMethod]
    public void TypedNetlistInputPreservesEveryNativeField()
    {
        string project = Path.Combine(Path.GetTempPath(), "pcb-netlist-input-contract");
        var input = new PcbImportNetlistInput
        {
            Board = Board(project),
            NetlistPath = "export.net",
            DryRun = true,
            MatchMode = PcbNetlistMatchModeInput.Reference,
            DeleteExtraFootprints = true,
            UpdateFootprints = true,
            TransferGroups = true,
            OverrideLocks = true
        };

        ImportNetlist native = input.ToNative("11111111-1111-1111-1111-111111111111");
        Assert.AreEqual(DocumentType.DoctypePcb, native.Board.Type);
        Assert.AreEqual(project, native.Board.Project.Path);
        Assert.AreEqual("export.net", native.NetlistPath);
        Assert.IsTrue(native.DryRun);
        Assert.AreEqual(NetlistMatchMode.NmmReference, native.MatchMode);
        Assert.IsTrue(native.DeleteExtraFootprints);
        Assert.IsTrue(native.UpdateFootprints);
        Assert.IsTrue(native.TransferGroups);
        Assert.IsTrue(native.OverrideLocks);
        Assert.AreEqual("11111111-1111-1111-1111-111111111111", native.OperationId);

        ImportNetlist roundTrip = BoardJson.Parser.Parse<ImportNetlist>(BoardJson.Formatter.Format(native));
        Assert.AreEqual(native, roundTrip);
    }

    [TestMethod]
    public void TypedRefillInputPreservesOrderedZonesAndSupportsAllZones()
    {
        string project = Path.Combine(Path.GetTempPath(), "pcb-refill-input-contract");
        var first = new NativeUuidInput { Value = "11111111-1111-1111-1111-111111111111" };
        var second = new NativeUuidInput { Value = "22222222-2222-2222-2222-222222222222" };
        var input = new PcbRefillZonesInput { Board = Board(project), Zones = [first, second] };
        RefillZones native = input.ToNative("33333333-3333-3333-3333-333333333333");

        CollectionAssert.AreEqual(new[] { first.Value, second.Value }, native.Zones.Select(zone => zone.Value).ToArray());
        Assert.AreEqual("33333333-3333-3333-3333-333333333333", native.OperationId);

        var allZones = new PcbRefillZonesInput { Board = Board(project) };
        Assert.AreEqual(0, allZones.ToNative("44444444-4444-4444-4444-444444444444").Zones.Count);
    }

    [TestMethod]
    public void TypedInputsRejectUnknownDuplicateAndWrongDocumentMembers()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<PcbImportNetlistInput>(
            "{\"netlistPath\":\"a\",\"netlistPath\":\"b\"}", options));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<PcbRefillZonesInput>(
            "{\"unexpected\":true}", options));

        var wrongType = new PcbRefillZonesInput
        {
            Board = new NativeDocumentInput
            {
                Type = NativeDocumentType.Schematic,
                Project = new() { Name = "wrong", Path = Path.GetTempPath() },
                SheetPath = new() { Path = [new() { Value = "55555555-5555-5555-5555-555555555555" }] }
            }
        };
        var error = Assert.ThrowsExactly<AutomationException>(() => wrongType.ToNative(
            "66666666-6666-6666-6666-666666666666"));
        Assert.AreEqual("invalid_pcb_refill_target", error.Code);

        var invalidZone = new PcbRefillZonesInput
        {
            Board = Board(Path.GetTempPath()),
            Zones = [new NativeUuidInput { Value = "not-a-uuid" }]
        };
        var invalid = Assert.ThrowsExactly<AutomationException>(() => invalidZone.ToNative(
            "77777777-7777-7777-7777-777777777777"));
        Assert.AreEqual("invalid_pcb_refill_request", invalid.Code);
    }

    [TestMethod]
    public async Task CompiledStdioExposesTypedFormsAndRefusesConflictingOrEmptyLegacyInputs()
    {
        string root = Directory.CreateTempSubdirectory("kicad-pcb-operation-input-schema-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(
                SyncHarnessProcessTests.ProductionStartInfo(), Path.Combine(root, "state"),
                Path.Combine(root, "stderr.log"), deadline.Token);
            JsonElement listed = await mcp.ListTools(null);
            JsonElement[] tools = listed.GetProperty("tools").EnumerateArray()
                .Where(tool => tool.GetProperty("name").GetString() is "kicad_pcb_import_netlist" or "kicad_pcb_refill_zones")
                .ToArray();
            Assert.AreEqual(2, tools.Length);
            foreach (JsonElement tool in tools)
            {
                string name = tool.GetProperty("name").GetString()!;
                JsonElement schema = tool.GetProperty("inputSchema");
                string[] required = schema.GetProperty("required").EnumerateArray()
                    .Select(item => item.GetString()!).ToArray();
                CollectionAssert.IsSubsetOf(new[] { "instanceId", "operationId" }, required, name);
                JsonElement properties = schema.GetProperty("properties");
                foreach (string property in new[] { "requestJson", "request", "expectedStateJson", "expectedState" })
                    Assert.IsTrue(properties.TryGetProperty(property, out _), $"{name}: {property}");
                Assert.IsTrue(properties.GetProperty("request").GetProperty("type").EnumerateArray()
                    .Any(item => item.GetString() == "object"), name);
                Assert.IsTrue(properties.GetProperty("expectedState").GetProperty("type").EnumerateArray()
                    .Any(item => item.GetString() == "object"), name);

                var conflict = await mcp.Tool(name, new
                {
                    instanceId = Guid.NewGuid().ToString("D"),
                    operationId = Guid.NewGuid().ToString("D"),
                    requestJson = "{}",
                    request = new { },
                    expectedStateJson = "{}"
                });
                Assert.IsTrue(conflict.GetProperty("isError").GetBoolean(), conflict.GetRawText());
                Assert.AreEqual("invalid_pcb_operation_input", conflict.GetProperty("structuredContent")
                    .GetProperty("errorCode").GetString(), conflict.GetRawText());

                var empty = await mcp.Tool(name, new
                {
                    instanceId = Guid.NewGuid().ToString("D"),
                    operationId = Guid.NewGuid().ToString("D"),
                    requestJson = "",
                    expectedStateJson = "{}"
                });
                Assert.IsTrue(empty.GetProperty("isError").GetBoolean(), empty.GetRawText());
                Assert.AreEqual("invalid_pcb_operation_input", empty.GetProperty("structuredContent")
                    .GetProperty("errorCode").GetString(), empty.GetRawText());
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static NativeDocumentInput Board(string project) => new()
    {
        Type = NativeDocumentType.Pcb,
        Project = new() { Name = "pcb-input-contract", Path = project },
        BoardFilename = "fixture.kicad_pcb"
    };
}

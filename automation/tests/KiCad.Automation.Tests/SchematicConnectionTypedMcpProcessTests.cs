using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicConnectionTypedMcpProcessTests
{
    [TestMethod]
    public void TypedConnectionInputRejectsUnknownMutationKind()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        SchematicConnectionMutationInput input = JsonSerializer.Deserialize<SchematicConnectionMutationInput>("""
            { "mutation": "create", "kind": 1, "itemId": "11111111-1111-1111-1111-111111111111" }
            """, options)!;
        Assert.AreEqual(SchematicConnectionMutationKind.Create, input.Mutation);
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<SchematicConnectionMutationInput>(
            "{\"mutation\":\"not-a-connection-operation\"}", options));
    }

    [TestMethod]
    public void TypedConnectionInputRetainsCompleteHierarchyAndAnyPayloads()
    {
        string root = Directory.CreateTempSubdirectory("kicad-schematic-connection-input-").FullName;
        try
        {
            var request = CheckedSchematicToolTests.Request(root, Guid.NewGuid().ToString("D"));
            var checkedState = new CheckedSchematicState
            {
                State = request.ExpectedState.Clone(),
                Electrical = new SchematicElectricalState
                {
                    Hierarchy = new SchematicHierarchyDataSnapshot
                    {
                        Data = new SchematicHierarchyData { Document = request.Batch.Document.Clone() },
                        Revision = request.Batch.ExpectedRevision.Clone()
                    }
                }
            };
            string itemId = Guid.NewGuid().ToString("D");
            var screen = new SchematicScreenData
            {
                Metadata = new() { Document = request.Batch.Document.Clone(), ScreenId = new() { Value = Guid.NewGuid().ToString("D") } }
            };
            screen.Items.Add(Any.Pack(new LocalLabel
            {
                Id = new() { Value = itemId }, Position = new() { XNm = 1_000, YNm = 2_000 }, Text = new() { Text_ = "DATA" }
            }));
            checkedState.Electrical.Hierarchy.Data.Instances.Add(screen);

            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var input = JsonSerializer.Deserialize<SchematicConnectionMutationInput>(JsonSerializer.Serialize(new
            {
                document = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(request.Batch.Document)),
                expectedState = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(checkedState)),
                mutation = "read", kind = (int)SchematicConnectionItemKind.LocalLabel, itemId
            }, options), options)!;
            var payload = input.ToPayload();
            var retained = SchematicJson.Parser.Parse<CheckedSchematicState>(payload.ExpectedState);
            var packed = retained.Electrical.Hierarchy.Data.Instances.Single().Items.Single();
            Assert.IsTrue(packed.Is(LocalLabel.Descriptor), payload.ExpectedState);
            Assert.AreEqual("DATA", packed.Unpack<LocalLabel>().Text.Text_);

            var incomplete = JsonSerializer.Deserialize<SchematicConnectionMutationInput>(JsonSerializer.Serialize(new
            {
                document = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(request.Batch.Document)),
                expectedState = new { state = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(checkedState.State)) },
                mutation = "read", kind = (int)SchematicConnectionItemKind.LocalLabel, itemId
            }, options), options)!;
            var refusal = Assert.ThrowsExactly<AutomationException>(() => incomplete.ToPayload());
            Assert.AreEqual("invalid_schematic_connection_typed_input", refusal.Code);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task TypedConnectionMutationPublishesStructuredSchemaAndRejectsIncompleteInput()
    {
        string root = Directory.CreateTempSubdirectory("kicad-schematic-connection-typed-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(
                SyncHarnessProcessTests.ProductionStartInfo(),
                Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), timeout.Token);
            JsonElement listed = await mcp.ListTools(null);
            JsonElement tool = listed.GetProperty("tools").EnumerateArray()
                .Single(candidate => candidate.GetProperty("name").GetString() == "kicad_schematic_connection_mutation_typed");
            JsonElement properties = tool.GetProperty("inputSchema").GetProperty("properties");
            JsonElement input = properties.GetProperty("input");
            foreach (string property in new[] { "document", "expectedState", "mutation", "kind", "itemId", "operationId", "xNm", "yNm" })
                Assert.IsTrue(input.GetProperty("properties").TryGetProperty(property, out _), property);

            JsonElement refusal = await mcp.Tool("kicad_schematic_connection_mutation_typed", new
            {
                instanceId = Guid.NewGuid().ToString("D"), input = new { }
            });
            Assert.IsTrue(refusal.GetProperty("isError").GetBoolean(), refusal.GetRawText());
            Assert.AreEqual("invalid_schematic_connection_typed_input",
                refusal.GetProperty("structuredContent").GetProperty("errorCode").GetString(), refusal.GetRawText());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}

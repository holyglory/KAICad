using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicMetadataMutationTests
{
    [TestMethod]
    public async Task MalformedTypedMetadataIsRejectedBeforeNativeDispatch()
    {
        string root = Directory.CreateTempSubdirectory("schematic-metadata-invalid-").FullName;
        try
        {
            string epoch = Guid.NewGuid().ToString("D");
            var request = CheckedSchematicToolTests.Request(root, epoch);
            string document = SchematicJson.Formatter.Format(request.Batch.Document);
            var checkedState = new CheckedSchematicState
            {
                State = request.ExpectedState.Clone(),
                Electrical = new SchematicElectricalState
                {
                    Hierarchy = new SchematicHierarchyDataSnapshot
                    {
                        Data = new SchematicHierarchyData { Document = request.Batch.Document.Clone() },
                        Revision = request.ExpectedState.Revision.Clone()
                    }
                }
            };
            var registry = new InstanceRegistry(new NativeClientTests.FixtureTransport { Epoch = epoch }, root);
            var tools = new SchematicMetadataMutationTools(registry);

            var aliases = await tools.ReplaceBusAliases("missing", document,
                SchematicJson.Formatter.Format(checkedState),
                [new(" DATA", ["D0"])], Guid.NewGuid().ToString("D"), default);
            AssertError(aliases, "invalid_bus_aliases");

            var variants = await tools.ReplaceVariantRegistry("missing", document,
                SchematicJson.Formatter.Format(checkedState),
                [new("Assembly", "contains\0NUL")], Guid.NewGuid().ToString("D"), default);
            AssertError(variants, "invalid_variant_registry");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task TypedMetadataUsesCheckedReceiptReplayAndKeepsSaveExplicit()
    {
        using var fixture = await MetadataFixture.CreateAsync();
        string operation = Guid.NewGuid().ToString("D");
        string before = fixture.ExpectedJson;
        var aliases = await fixture.Tools.ReplaceBusAliases(fixture.InstanceId, fixture.DocumentJson,
            before, [new("DATA", ["D0", "D1"])], operation, default);
        Assert.IsFalse(aliases.IsError ?? false, Data(aliases).GetRawText());
        Assert.AreEqual(1, fixture.Mutations);
        Assert.AreEqual("DATA", fixture.State.Electrical.Hierarchy.Data.Instances.Single().Metadata.BusAliases.Single().Name);
        Assert.IsFalse(Data(aliases).GetProperty("designFilesSaved").GetBoolean());

        int captures = fixture.Captures;
        var replay = await fixture.Tools.ReplaceBusAliases(fixture.InstanceId, fixture.DocumentJson,
            before, [new("DATA", ["D0", "D1"])], operation, default);
        Assert.IsFalse(replay.IsError ?? false, Data(replay).GetRawText());
        Assert.IsTrue(Data(replay).GetProperty("replayed").GetBoolean());
        Assert.IsFalse(Data(replay).GetProperty("mutationSubmitted").GetBoolean());
        Assert.AreEqual(1, fixture.Mutations);
        Assert.AreEqual(captures, fixture.Captures, "Replay must inspect the receipt before recapturing state.");

        string variantOperation = Guid.NewGuid().ToString("D");
        var variants = await fixture.Tools.ReplaceVariantRegistry(fixture.InstanceId,
            fixture.DocumentJson, fixture.ExpectedJson,
            [new("Assembly", "Place near heat spreader"), new("tooling", "")],
            variantOperation, default);
        Assert.IsFalse(variants.IsError ?? false, Data(variants).GetRawText());
        Assert.AreEqual("Place near heat spreader",
            fixture.State.Electrical.Hierarchy.Data.Instances.Single().Metadata.VariantDescriptions["Assembly"]);
        Assert.AreEqual(2, fixture.Mutations);
    }

    private static JsonElement Data(CallToolResult result) =>
        JsonSerializer.SerializeToElement(result.StructuredContent);

    private static void AssertError(CallToolResult result, string code)
    {
        Assert.IsTrue(result.IsError, Data(result).GetRawText());
        Assert.AreEqual(code, Data(result).GetProperty("errorCode").GetString(), Data(result).GetRawText());
        Assert.IsFalse(Data(result).GetProperty("mutationSubmitted").GetBoolean());
    }

    private sealed class MetadataFixture : INativeTransport, IDisposable
    {
        private readonly string root = Directory.CreateTempSubdirectory("schematic-metadata-fixture-").FullName;
        private readonly Dictionary<string, (CheckedSchematicBatch Request, CheckedSchematicBatchReceipt Receipt)> receipts = [];
        internal NativeClientTests.FixtureTransport Session { get; } = new() { Epoch = Guid.NewGuid().ToString("D") };
        internal CheckedSchematicState State { get; }
        internal SchematicMetadataMutationTools Tools { get; }
        internal string InstanceId => Session.InstanceId;
        internal string DocumentJson => SchematicJson.Formatter.Format(State.State.Document);
        internal string ExpectedJson => SchematicJson.Formatter.Format(State);
        internal int Mutations { get; private set; }
        internal int Captures { get; private set; }
        private InstanceRegistry Registry { get; }

        private MetadataFixture()
        {
            Session.ProjectPath = Path.Combine(root, "fixture.kicad_pro");
            Registry = new(this, root);
            Tools = new(Registry);
            var request = CheckedSchematicToolTests.Request(root, Session.Epoch);
            State = new CheckedSchematicState
            {
                State = request.ExpectedState.Clone(),
                Electrical = new SchematicElectricalState
                {
                    Hierarchy = new SchematicHierarchyDataSnapshot
                    {
                        Data = new SchematicHierarchyData { Document = request.Batch.Document.Clone() },
                        Revision = request.ExpectedState.Revision.Clone(),
                        TrackingComplete = true
                    }
                }
            };
            var screen = new SchematicScreenData
            {
                Metadata = new SchematicMetadata
                {
                    Document = request.Batch.Document.Clone(),
                    ScreenId = new KIID { Value = Guid.NewGuid().ToString("D") }
                }
            };
            State.Electrical.Hierarchy.Data.Instances.Add(screen);
            State.Electrical.Hierarchy.Revision = request.ExpectedState.Revision.Clone();
        }

        internal static async Task<MetadataFixture> CreateAsync()
        {
            var fixture = new MetadataFixture();
            try
            {
                await fixture.Registry.AttachAsync(
                    NativeIpcEndpoint.FromSocketPath(Path.Combine(fixture.root, "native.sock")),
                    fixture.InstanceId);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public Task<byte[]> ExchangeAsync(string endpoint, byte[] requestBytes, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var request = ApiRequest.Parser.ParseFrom(requestBytes);
            IMessage payload;
            if (request.Message.Is(ReadCheckedSchematicState.Descriptor))
            {
                Captures++;
                payload = State.Clone();
            }
            else if (request.Message.Is(ReadCheckedSchematicBatchReceipt.Descriptor))
            {
                var query = request.Message.Unpack<ReadCheckedSchematicBatchReceipt>();
                if (receipts.TryGetValue(query.OperationId, out var saved))
                {
                    if (!saved.Request.Equals(query.ExpectedRequest))
                        return Reply(new CheckedSchematicBatchReceipt(), 3, "operation_reused");
                    payload = saved.Receipt.Clone();
                }
                else
                {
                    var notFound = new CheckedSchematicBatchReceipt
                    {
                        Document = query.Document.Clone(), ProcessEpoch = Session.Epoch,
                        OperationId = query.OperationId,
                        Status = CheckedSchematicBatchStatus.CsbsNotFound
                    };
                    payload = notFound;
                }
            }
            else if (request.Message.Is(CheckedSchematicBatch.Descriptor))
            {
                var batch = request.Message.Unpack<CheckedSchematicBatch>();
                Mutations++;
                CheckedSchematicContract.ValidateRequest(batch, Session.Epoch);
                var before = State.State.Clone();
                var operation = batch.Batch.Operations.Single();
                if (operation.ReplaceBusAliases is { } aliasState)
                {
                    State.Electrical.Hierarchy.Data.Instances.Single().Metadata.BusAliases.Clear();
                    State.Electrical.Hierarchy.Data.Instances.Single().Metadata.BusAliases.Add(aliasState.Aliases.Select(a => a.Clone()));
                }
                else if (operation.ReplaceVariantRegistry is { } variantState)
                {
                    var metadata = State.Electrical.Hierarchy.Data.Instances.Single().Metadata;
                    metadata.VariantDescriptions.Clear(); metadata.VariantDescriptions.Add(variantState.Descriptions);
                }
                State.State.Revision.Sequence++;
                State.Electrical.Hierarchy.Revision = State.State.Revision.Clone();
                State.State.StateSha256 = new string('d', 64);
                var result = new CheckedSchematicBatchReceipt
                {
                    Document = batch.Batch.Document.Clone(), ProcessEpoch = Session.Epoch,
                    OperationId = batch.Batch.OperationId, ExpectedRequestVerified = true,
                    Status = CheckedSchematicBatchStatus.CsbsCompleted,
                    ObservedBefore = before,
                    ObservedAfter = State.State.Clone(),
                    Result = new SchematicItemBatchResult { Revision = State.State.Revision.Clone(),
                        BusAliasesChanged = operation.ReplaceBusAliases is not null,
                        VariantRegistryChanged = operation.ReplaceVariantRegistry is not null }
                };
                receipts[batch.Batch.OperationId] = (batch.Clone(), result.Clone());
                payload = result;
            }
            else return Session.ExchangeAsync(endpoint, requestBytes, timeout, cancellationToken);
            return Reply(payload);
        }

        private Task<byte[]> Reply(IMessage payload, int status = 1, string error = "") =>
            Task.FromResult(new ApiResponse
            {
                Header = new() { KicadToken = Session.Epoch },
                Status = new() { Status = (ApiStatusCode)status, ErrorMessage = error },
                Message = Any.Pack(payload)
            }.ToByteArray());

        public void Dispose()
        {
            try { Directory.Delete(root, true); }
            catch { }
        }
    }
}

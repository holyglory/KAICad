using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Commands;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

// Explicit protocol fixtures, not evidence of a running KiCad process.
[TestClass]
public sealed class NativeClientTests
{
    [TestMethod]
    public async Task HandshakePinsEpochOnSubsequentCalls()
    {
        var fixture = new FixtureTransport();
        var client = new NativeClient(fixture, "ipc:///tmp/test.sock");
        await client.HandshakeAsync();
        await client.GetVersionAsync();
        Assert.AreEqual("fixture-epoch", fixture.LastRequest!.Header.KicadToken);
        Assert.AreEqual("fixture-epoch", client.Epoch);
    }

    [TestMethod]
    public async Task RecycledSocketCannotBecomeAnotherInstance()
    {
        var fixture = new FixtureTransport();
        var client = new NativeClient(fixture, "ipc:///tmp/test.sock");
        await client.HandshakeAsync();
        fixture.Epoch = "replacement-process";
        AutomationException error = await Assert.ThrowsExactlyAsync<AutomationException>(() => client.GetVersionAsync());
        Assert.AreEqual("instance_changed", error.Code);
    }

    [TestMethod]
    public async Task NativeFailureIsNotASuccessfulEmptyResponse()
    {
        var fixture = new FixtureTransport { Status = 7 };
        var client = new NativeClient(fixture, "ipc:///tmp/test.sock");
        NativeApiException error = await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.HandshakeAsync());
        Assert.AreEqual(7, error.Status);
    }

    [TestMethod]
    public async Task WrongResponseTypeIsRejected()
    {
        var fixture = new FixtureTransport { WrongType = true };
        var client = new NativeClient(fixture, "ipc:///tmp/test.sock");
        AutomationException error = await Assert.ThrowsExactlyAsync<AutomationException>(() => client.HandshakeAsync());
        Assert.AreEqual("invalid_response", error.Code);
    }

    [TestMethod]
    public void RemoteOrImplicitEndpointsAreRejected()
    {
        foreach (string endpoint in new[] { "tcp://localhost:9000", "ipc://relative", "", "ipc:///tmp/a\0b" })
            Assert.ThrowsExactly<ArgumentException>(() => NngTransport.ValidateEndpoint(endpoint));
        NngTransport.ValidateEndpoint("ipc:///tmp/a.sock");
    }

    [TestMethod]
    public void SchematicOpenRequiresAnExplicitNativePath()
    {
        var fixture = new FixtureTransport();
        var client = new NativeClient(fixture, "ipc:///tmp/test.sock");
        foreach (string path in new[] { "relative.kicad_sch", "/tmp/legacy.sch", "" })
        {
            Assert.ThrowsExactly<AutomationException>(() => client.OpenRootSchematicAsync(path));
            Assert.ThrowsExactly<AutomationException>(() => client.CreateRootSchematicAsync(path));
        }
        Assert.IsNull(fixture.LastRequest, "Invalid paths must fail before transport.");
    }

    [TestMethod]
    public async Task SnapshotRequestsOptIntoCurrentFieldsWithoutChangingCallerMessages()
    {
        IMessage[] requests = [new ReadSchematicMetadata(), new ReadSchematicScreenData(),
            new ReadSchematicHierarchyData(), new ReadSchematicElectricalState(), new CaptureSchematicObservation(), new RenderSchematicViews()];
        foreach (var request in requests)
        {
            var field = request.Descriptor.FindFieldByName("schema_version");
            var current = NativeClient.CurrentSnapshotRequest(request);
            Assert.AreNotSame(request, current);
            Assert.AreEqual(0U, field.Accessor.GetValue(request));
            Assert.AreEqual(5U, field.Accessor.GetValue(current));
            foreach (uint explicitVersion in new[] { 1U, 2U, 3U, 4U, 5U })
            {
                field.Accessor.SetValue(request, explicitVersion);
                Assert.AreSame(request, NativeClient.CurrentSnapshotRequest(request));
                Assert.AreEqual(explicitVersion, field.Accessor.GetValue(request));
            }
        }
        var other = new ReadSchematicSaveState();
        Assert.AreSame(other, NativeClient.CurrentSnapshotRequest(other));
        var transport = new FixtureTransport();
        var client = new NativeClient(transport, "ipc:///tmp/schema-fixture.sock");
        var query = new ReadSchematicMetadata();
        // The transport is deliberately a protocol fixture, not a native peer.
        await client.InvokeAsync<ReadSchematicMetadata, GetVersionResponse>(query);
        Assert.AreEqual(5U, transport.LastRequest!.Message.Unpack<ReadSchematicMetadata>().SchemaVersion);
        Assert.AreEqual(0U, query.SchemaVersion);
    }

    [TestMethod]
    [DataRow(1U)]
    [DataRow(2U)]
    [DataRow(3U)]
    [DataRow(4U)]
    [DataRow(5U)]
    public async Task AutomaticSnapshotNegotiationIsBoundedPinnedAndCachedPerPeer(uint maximum)
    {
        var payload = new SchematicMetadataSnapshot { Metadata = new(), TrackingComplete = false };
        payload.Metadata.UnrepresentedState.Add("fixture-unrepresented-feature");
        var transport = new ScriptTransport((request, _) => SnapshotReply(payload,
            request.Message.Unpack<ReadSchematicMetadata>().SchemaVersion > maximum ? 3 : 1));
        var client = new NativeClient(transport, "ipc:///tmp/schema-negotiation.sock");
        var query = new ReadSchematicMetadata();
        var result = await client.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(query);
        Assert.AreEqual(payload, result); // Coverage limitations are not discarded or invented.
        CollectionAssert.AreEqual(Enumerable.Range((int)maximum, 6 - (int)maximum).Reverse().Select(x => (uint)x).ToArray(),
            transport.Requests.Select(x => x.Message.Unpack<ReadSchematicMetadata>().SchemaVersion).ToArray());
        Assert.AreEqual(0U, query.SchemaVersion);
        Assert.AreEqual("schema-peer", client.Epoch);
        foreach (var request in transport.Requests.Skip(1)) Assert.AreEqual("schema-peer", request.Header.KicadToken);
        int count = transport.Requests.Count;
        await client.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(query);
        Assert.HasCount(count + 1, transport.Requests);
        Assert.AreEqual(maximum, transport.Requests[^1].Message.Unpack<ReadSchematicMetadata>().SchemaVersion);
        var independent = new NativeClient(transport, "ipc:///tmp/another-schema-peer.sock");
        await independent.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(query);
        Assert.AreEqual(5U, transport.Requests[count + 1].Message.Unpack<ReadSchematicMetadata>().SchemaVersion);
    }

    [TestMethod]
    public async Task ExplicitSchemasAndMutationsNeverNegotiateOrRetry()
    {
        foreach (uint version in new[] { 1U, 3U, 4U, 5U })
        {
            var transport = new ScriptTransport((_, _) => SnapshotReply(new SchematicMetadataSnapshot(), 3));
            var client = new NativeClient(transport, "ipc:///tmp/explicit-schema.sock");
            await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(
                new() { SchemaVersion = version }));
            Assert.HasCount(1, transport.Requests);
            Assert.AreEqual(version, transport.Requests[0].Message.Unpack<ReadSchematicMetadata>().SchemaVersion);
        }
        var mutationTransport = new ScriptTransport((_, _) => SnapshotReply(new SchematicItemBatchResult(), 3));
        var mutationClient = new NativeClient(mutationTransport, "ipc:///tmp/mutation-no-retry.sock");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => mutationClient.InvokeAsync<ApplySchematicItemBatch, SchematicItemBatchResult>(new()));
        Assert.HasCount(1, mutationTransport.Requests);
    }

    [TestMethod]
    [DataRow(7, "Unsupported schematic snapshot schema version")]
    [DataRow(3, "Wrong document")]
    [DataRow(8, "Unsupported schematic snapshot schema version")]
    public async Task UnrelatedFailuresCannotTriggerSchemaDowngrade(int status, string message)
    {
        var transport = new ScriptTransport((_, _) => SnapshotReply(new SchematicMetadataSnapshot(), status, message));
        var client = new NativeClient(transport, "ipc:///tmp/unrelated-schema-error.sock");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(new()));
        Assert.HasCount(1, transport.Requests);
    }

    [TestMethod]
    public async Task SchemaNegotiationStopsAtOneAndRejectsEpochChangesCancellationAndTimeout()
    {
        var rejected = new ScriptTransport((_, _) => SnapshotReply(new SchematicMetadataSnapshot(), 3));
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => new NativeClient(rejected, "ipc:///tmp/rejected-schema.sock")
            .InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(new()));
        Assert.HasCount(5, rejected.Requests);

        var replaced = new ScriptTransport((_, attempt) =>
        {
            var reply = SnapshotReply(new SchematicMetadataSnapshot(), attempt == 1 ? 3 : 1);
            if (attempt > 1) reply.Header.KicadToken = "replacement-peer";
            return reply;
        });
        var changed = await Assert.ThrowsExactlyAsync<AutomationException>(() => new NativeClient(replaced, "ipc:///tmp/replaced-schema.sock")
            .InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(new()));
        Assert.AreEqual("instance_changed", changed.Code);
        Assert.HasCount(2, replaced.Requests);

        using var cancellation = new CancellationTokenSource();
        var cancelled = new ScriptTransport((_, _) => { cancellation.Cancel(); return SnapshotReply(new SchematicMetadataSnapshot(), 3); });
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new NativeClient(cancelled, "ipc:///tmp/cancelled-schema.sock")
            .InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(new(), cancellation.Token));
        Assert.HasCount(1, cancelled.Requests);
        var timedOut = new ScriptTransport((_, _) => throw new TimeoutException("fixture timeout"));
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => new NativeClient(timedOut, "ipc:///tmp/timed-out-schema.sock")
            .InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(new()));
        Assert.HasCount(1, timedOut.Requests);
    }

    private static ApiResponse SnapshotReply(IMessage payload, int status,
        string error = "Unsupported schematic snapshot schema version") => new()
    {
        Header = new() { KicadToken = "schema-peer" },
        Status = new() { Status = (ApiStatusCode)status, ErrorMessage = status == 1 ? "" : error },
        Message = Any.Pack(payload)
    };

    private sealed class ScriptTransport(Func<ApiRequest, int, ApiResponse> reply) : INativeTransport
    {
        public List<ApiRequest> Requests { get; } = new();
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Requests.Add(ApiRequest.Parser.ParseFrom(request));
            return Task.FromResult(reply(Requests[^1], Requests.Count).ToByteArray());
        }
    }

    internal sealed class FixtureTransport : INativeTransport
    {
        public string Epoch { get; set; } = "fixture-epoch";
        public int Status { get; set; } = 1;
        public bool WrongType { get; set; }
        public string InstanceId { get; } = Guid.NewGuid().ToString("D");
        public string ProjectPath { get; set; } = "/fixture/test.kicad_pro";
        public ApiRequest? LastRequest { get; private set; }

        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            LastRequest = ApiRequest.Parser.ParseFrom(request);
            IMessage payload = LastRequest.Message.Is(GetAutomationSession.Descriptor) && !WrongType
                ? new AutomationSession { ProtocolVersion = 1, InstanceId = InstanceId, ProjectPath = ProjectPath, Epoch = Epoch }
                : new GetVersionResponse { Version = new Kiapi.Common.Types.KiCadVersion { FullVersion = "isolated-protocol-fixture" } };
            return Task.FromResult(new ApiResponse
            {
                Header = new ApiResponseHeader { KicadToken = Epoch },
                Status = new ApiResponseStatus { Status = (ApiStatusCode)Status, ErrorMessage = Status == 1 ? "" : "fixture busy" },
                Message = Any.Pack(payload)
            }.ToByteArray());
        }
    }
}

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Commands;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

public sealed class NativeApiException(int status, string message) : IOException(message)
{
    public int Status { get; } = status;
}

/// <summary>One explicit peer, pinned to a native process epoch after handshake.</summary>
public sealed class NativeClient(INativeTransport transport, string endpoint, string? expectedEpoch = null)
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly string clientName = $"kicad-automation-{Guid.NewGuid():N}";
    private string? epoch = expectedEpoch;
    private uint snapshotSchema = 5;
    public string Endpoint { get; } = endpoint;
    public string Epoch => epoch ?? throw new InvalidOperationException("Handshake has not completed.");

    public async Task<AutomationSession> HandshakeAsync(CancellationToken cancellationToken = default)
    {
        AutomationSession session = await InvokeAsync<GetAutomationSession, AutomationSession>(
            new GetAutomationSession(), cancellationToken);
        if (session.ProtocolVersion != 1 || string.IsNullOrWhiteSpace(session.InstanceId) || session.Epoch != Epoch)
            throw new AutomationException("incompatible_native", "The native peer does not implement the required automation session contract.");
        return session;
    }

    public Task<GetVersionResponse> GetVersionAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<GetVersion, GetVersionResponse>(new GetVersion(), cancellationToken);

    public Task<OpenDocumentResponse> OpenRootSchematicAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetExtension(path) != ".kicad_sch")
            throw new AutomationException("invalid_document_path", "An absolute native schematic path is required.");
        return InvokeAsync<OpenDocument, OpenDocumentResponse>(
            new OpenDocument { Type = (Kiapi.Common.Types.DocumentType)1, Path = path }, cancellationToken);
    }

    public Task<OpenDocumentResponse> CreateRootSchematicAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetExtension(path) != ".kicad_sch")
            throw new AutomationException("invalid_document_path", "An absolute native schematic path is required.");
        return InvokeAsync<OpenDocument, OpenDocumentResponse>(
            new OpenDocument { Type = (Kiapi.Common.Types.DocumentType)1, Path = path, CreateIfMissing = true }, cancellationToken);
    }

    public async Task<TResponse> InvokeAsync<TRequest, TResponse>(TRequest request,
        CancellationToken cancellationToken = default)
        where TRequest : IMessage<TRequest>
        where TResponse : IMessage<TResponse>, new()
    {
        await serial.WaitAsync(cancellationToken);
        try
        {
            bool negotiate = !ReferenceEquals(request, CurrentSnapshotRequest(request));
            uint requestedSchema = snapshotSchema;
            string? observedEpoch = epoch;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var envelope = new ApiRequest
                {
                    Header = new ApiRequestHeader { KicadToken = observedEpoch ?? "", ClientName = clientName },
                    Message = Any.Pack(CurrentSnapshotRequest(request, requestedSchema))
                };
                byte[] bytes = await transport.ExchangeAsync(Endpoint, envelope.ToByteArray(), TimeSpan.FromSeconds(15), cancellationToken);
                ApiResponse response = ApiResponse.Parser.ParseFrom(bytes);
                string? peerEpoch = response.Header?.KicadToken;
                if (string.IsNullOrWhiteSpace(peerEpoch))
                    throw new AutomationException("invalid_response", "Native response did not identify its process epoch.");
                // SA-04 applies between negotiation replies too, including before a first handshake.
                if (observedEpoch is not null && observedEpoch != peerEpoch)
                    throw new AutomationException("instance_changed", "The native process changed; reattach explicitly before continuing.");
                observedEpoch = peerEpoch;
                if (negotiate && requestedSchema > 1 && (int?)response.Status?.Status == 3
                    && response.Status.ErrorMessage == "Unsupported schematic snapshot schema version")
                {
                    --requestedSchema;
                    continue; // Only bounded, automatically versioned read requests reach this path.
                }
                if (response.Status is null || (int)response.Status.Status != 1)
                    throw new NativeApiException((int)(response.Status?.Status ?? 0),
                                                 response.Status?.ErrorMessage ?? "Native response has no status.");
                var result = new TResponse();
                if (response.Message is null || !response.Message.Is(result.Descriptor))
                    throw new AutomationException("invalid_response", $"Expected a {result.Descriptor.FullName} response.");
                result.MergeFrom(response.Message.Value);
                epoch = peerEpoch;
                if (negotiate) snapshotSchema = requestedSchema;
                return result;
            }
        }
        finally { serial.Release(); }
    }

    // New clients opt into all current schematic fields. Explicit legacy or
    // unknown versions remain untouched for negotiation/error handling. Clone
    // read requests so invoking a client never changes caller-owned messages.
    internal static IMessage CurrentSnapshotRequest(IMessage request, uint schemaVersion = 5) => request switch
    {
        ReadSchematicMetadata { SchemaVersion: 0 } value => new ReadSchematicMetadata(value) { SchemaVersion = schemaVersion },
        ReadSchematicScreenData { SchemaVersion: 0 } value => new ReadSchematicScreenData(value) { SchemaVersion = schemaVersion },
        ReadSchematicHierarchyData { SchemaVersion: 0 } value => new ReadSchematicHierarchyData(value) { SchemaVersion = schemaVersion },
        ReadSchematicElectricalState { SchemaVersion: 0 } value => new ReadSchematicElectricalState(value) { SchemaVersion = schemaVersion },
        CaptureSchematicObservation { SchemaVersion: 0 } value => new CaptureSchematicObservation(value) { SchemaVersion = schemaVersion },
        RenderSchematicViews { SchemaVersion: 0 } value => new RenderSchematicViews(value) { SchemaVersion = schemaVersion },
        _ => request
    };
}

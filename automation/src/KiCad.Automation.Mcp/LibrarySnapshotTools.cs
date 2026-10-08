using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>Bounded read-only lifecycle evidence for one explicit library entry.</summary>
[McpServerToolType]
public sealed class LibrarySnapshotTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_library_snapshot_read", ReadOnly = true),
     Description("Read compact, project-bound lifecycle evidence for one explicitly identified symbol or footprint library entry. The native owner forces a disk refresh, captures the complete direct KiCad checkpoint, revision and dependency baselines, and returns the state envelope required by the matching checked writer. Unsupported providers remain explicit refusals."),
     KiCadCapability("library-snapshot", "native-symbol-library-adapter", "explicit project/document identity, direct provider row, checked revision/state digest and dependency baselines"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Read(string instanceId, string expectedProcessEpoch, string documentJson,
        string libraryNickname, string entryName, CancellationToken cancellationToken, bool forcedRefresh = true,
        bool allowMissingEntry = false)
        => Execute(async () =>
        {
            ValidateEpoch(expectedProcessEpoch);
            DocumentSpecifier document = ParseDocument(documentJson);
            if (document.Project is null || document.LibId is null)
                throw new AutomationException("invalid_library_document", "documentJson must carry project and library identity.");
            if (document.Type is not (DocumentType.DoctypeSymbol or DocumentType.DoctypeFootprint))
                throw new AutomationException("invalid_library_document", "documentJson must identify a symbol or footprint library entry.");
            if (!string.Equals(document.LibId.LibraryNickname, libraryNickname, StringComparison.Ordinal)
                || !string.Equals(document.LibId.EntryName, entryName, StringComparison.Ordinal))
                throw new AutomationException("library_document_mismatch", "libraryNickname and entryName must match documentJson.");

            NativeClient client = registry.Client(instanceId);
            AutomationSession session = await client.HandshakeAsync(cancellationToken);
            if (!string.Equals(session.Epoch, expectedProcessEpoch, StringComparison.Ordinal))
                throw new AutomationException("stale_process_epoch", "The expected process epoch is stale; inspect the instance again.");

            NativeCapabilityLibrarySnapshotReadResponse response = await client.InvokeAsync<
                NativeCapabilityLibrarySnapshotRead, NativeCapabilityLibrarySnapshotReadResponse>(new()
                {
                    Document = document,
                    ProcessEpoch = expectedProcessEpoch,
                    LibraryNickname = libraryNickname,
                    EntryName = entryName,
                    ForcedRefresh = forcedRefresh,
                    AllowMissingEntry = allowMissingEntry
                }, cancellationToken);

            if (!string.Equals(response.Status, "complete", StringComparison.Ordinal))
                throw new AutomationException(
                    string.IsNullOrWhiteSpace(response.ErrorCode) ? "library_snapshot_failed" : response.ErrorCode,
                    string.IsNullOrWhiteSpace(response.ErrorMessage) ? "The native owner did not complete the library snapshot." : response.ErrorMessage);

            JsonElement checkedState = document.Type == DocumentType.DoctypeFootprint
                ? JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(response.FootprintCheckedState))
                : JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(response.CheckedState));
            bool mutationAvailable = document.Type == DocumentType.DoctypeFootprint
                ? response.FootprintCheckedState.StateComplete
                : response.CheckedState.StateComplete;
            return Data(new
            {
                instanceId,
                processEpoch = response.ProcessEpoch,
                status = response.Status,
                document = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(response.Document)),
                tablePath = response.TablePath,
                tableScope = response.TableScope,
                nickname = response.Nickname,
                rawUri = response.RawUri,
                expandedUri = response.ExpandedUri,
                provider = response.Provider,
                providerOptions = response.ProviderOptions,
                detachedDefinitionSha256 = response.DetachedDefinitionSha256,
                detachedDefinitionBytes = response.DetachedDefinitionBytes,
                dependencyBaselines = response.DependencyBaselines.Select(baseline => new
                {
                    path = baseline.Path,
                    exists = baseline.Exists,
                    known = baseline.Known,
                    sha256 = baseline.Sha256,
                    bytes = baseline.Bytes
                }).ToArray(),
                forcedRefresh = response.ForcedRefresh,
                providerObservable = response.ProviderObservable,
                contentSnapshotComplete = response.ContentSnapshotComplete,
                diskBaselineChecked = response.DiskBaselineChecked,
                completeChangeTracking = response.CompleteChangeTracking,
                postverify = response.Postverify,
                checkedState,
                readOnly = true,
                mutationAvailable
            });
        });

    private static DocumentSpecifier ParseDocument(string json)
    {
        try { return SchematicJson.Parser.Parse<DocumentSpecifier>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException("invalid_library_document", "documentJson is not valid DocumentSpecifier protobuf JSON: " + error.Message); }
    }

    private static void ValidateEpoch(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid epoch) || epoch == Guid.Empty)
            throw new AutomationException("invalid_process_epoch", "Provide a canonical native process epoch.");
    }

    private static CallToolResult Data(object value)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(value);
        return new() { StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> operation)
    {
        try { return await operation(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException or InvalidJsonException or IOException)
        {
            string code = error is AutomationException known ? known.Code : "library_snapshot_failed";
            JsonElement structured = JsonSerializer.SerializeToElement(new { errorCode = code, errorMessage = error.Message });
            return new() { IsError = true, StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }
}

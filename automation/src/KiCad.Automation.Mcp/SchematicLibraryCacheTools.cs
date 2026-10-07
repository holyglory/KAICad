using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Typed access to the schematic's embedded library cache.  This is the native
/// cache owned by a schematic screen, not a global .kicad_sym library editor;
/// the latter still needs a separate native command family.
/// </summary>
[McpServerToolType]
public sealed class SchematicLibraryCacheTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_schematic_library_cache_replace", ReadOnly = false, UseStructuredContent = true),
     Description("Replace one schematic screen's complete embedded symbol-cache definitions as one checked native undo step. documentJson must identify the schematic root; expectedStateJson must be the complete structuredContent returned by kicad_schematic_checked_state, including lifecycle state and electrical hierarchy; screenId is resolved from that hierarchy and repeated instances of a shared screen must agree on cache definitions; definitionsJson is a JSON array of complete SchematicCachedSymbol protobuf values; operationId is a canonical UUID. The operation is revision-bound, atomic and receipt-backed, and does not save files or edit a global .kicad_sym library.")]
    [KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root document, exact screen identity, complete cache definitions, checked revision and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Replace(string instanceId, string documentJson, string expectedStateJson,
        string screenId, string definitionsJson, string operationId, CancellationToken cancellationToken)
        => Execute(instanceId, screenId, operationId, async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_checked_document");
        ValidateRoot(document);
        CheckedSchematicState expected = ParseExpectedObservation(expectedStateJson, document);
        ValidateIdentity(screenId, "screenId");
        ValidateIdentity(operationId, "operationId");
        SchematicCachedSymbol[] definitions = ParseDefinitions(definitionsJson);
        if (definitions.Length > 4096)
            throw new AutomationException("invalid_library_cache", "A cache replacement may contain at most 4096 definitions.");
        DocumentSpecifier target = ResolveTargetDocument(expected, document, screenId);
        var cache = new SchematicLibraryCacheState { ScreenId = new() { Value = screenId } };
        cache.Definitions.Add(definitions);
        var batch = new ApplySchematicItemBatch
        {
            Document = document.Clone(), DocumentEpoch = expected.State.Revision.Epoch,
            ExpectedRevision = expected.State.Revision.Clone(), OperationId = operationId,
            Description = "Replace typed schematic library cache"
        };
        batch.Operations.Add(new SchematicItemOperation { TargetDocument = target.Clone(), ReplaceLibraryCache = cache });
        var request = new CheckedSchematicBatch { Batch = batch, ExpectedState = expected.State.Clone() };
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        CheckedSchematicContract.ValidateObservation(expected, document, session.Epoch);
        CheckedSchematicTools.ValidateRequest(request, session.Epoch);
        var prior = await client.InvokeAsync<ReadCheckedSchematicBatchReceipt, CheckedSchematicBatchReceipt>(new()
        {
            Document = document.Clone(), ProcessEpoch = session.Epoch,
            OperationId = operationId, ExpectedRequest = request.Clone()
        }, cancellationToken);
        CheckedSchematicTools.ValidateResult(request, prior, inspect: true);
        if (prior.Status != CheckedSchematicBatchStatus.CsbsNotFound)
            return MutationReply(instanceId, session.Epoch, document, target, screenId, definitions.Length,
                operationId, prior, mutationSubmitted: false, replayed: true);
        CheckedSchematicState live = await ReadExpected(client, document, expected, cancellationToken);
        DocumentSpecifier liveTarget = ResolveTargetDocument(live, document, screenId);
        if (!liveTarget.Equals(target))
            throw new AutomationException("stale_document_state", "The target screen changed after the checked observation; inspect a fresh checked state and retry.");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, cancellationToken);
            CheckedSchematicTools.ValidateResult(request, receipt, inspect: false);
            return MutationReply(instanceId, session.Epoch, document, target, screenId, definitions.Length,
                operationId, receipt, mutationSubmitted: true, replayed: false);
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException or IOException
            or OperationCanceledException)
        {
            return Failure(instanceId, operationId, screenId, target, true, error);
        }
    });

    private static CheckedSchematicState ParseExpectedObservation(string json, DocumentSpecifier document)
    {
        try
        {
            using JsonDocument root = JsonDocument.Parse(json);
            if (!root.RootElement.TryGetProperty("state", out _))
                throw new AutomationException("invalid_checked_state",
                    "expectedStateJson must be the complete structuredContent returned by kicad_schematic_checked_state.");
            CheckedSchematicState result = SchematicJson.Parser.Parse<CheckedSchematicState>(json);
            CheckedSchematicContract.ValidateObservation(result, document, result.State?.ProcessEpoch ?? string.Empty);
            return result;
        }
        catch (AutomationException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
        { throw new AutomationException("invalid_checked_state", "expectedStateJson is not a complete checked schematic observation: " + error.Message); }
    }

    private static DocumentSpecifier ResolveTargetDocument(CheckedSchematicState state, DocumentSpecifier root, string screenId)
    {
        var matches = state.Electrical.Hierarchy.Data.Instances
            .Where(screen => screen.Metadata?.ScreenId?.Value == screenId && screen.Metadata.Document is not null)
            .OrderBy(screen => PathKey(screen.Metadata!.Document), StringComparer.Ordinal).ToArray();
        if (matches.Length == 0)
            throw new AutomationException("screen_not_found", "screenId is not present in the checked schematic hierarchy.");

        var first = matches[0];
        foreach (var screen in matches)
        {
            var owner = screen.Metadata!.Document;
            try { DocumentStateTools.ValidateTarget(owner); }
            catch (AutomationException error) { throw new AutomationException("invalid_target_document", error.Message); }
            if (owner.Type != DocumentType.DoctypeSchematic || !Equals(owner.Project, root.Project)
                || owner.SheetPath?.Path.Count < 1 || !Equals(owner.SheetPath!.Path[0], root.SheetPath.Path[0]))
                throw new AutomationException("invalid_target_document", "Every instance of screenId must belong to the observed schematic root and project.");
        }
        foreach (var screen in matches.Skip(1))
        {
            if (screen.CachedSymbols.Count != first.CachedSymbols.Count
                || screen.CachedSymbols.Where((symbol, index) => !symbol.Equals(first.CachedSymbols[index])).Any())
                throw new AutomationException("inconsistent_shared_screen_cache",
                    "Repeated instances of screenId report different embedded library-cache definitions; no mutation was sent.");
        }
        var target = first.Metadata!.Document.Clone();
        return target;
    }

    private static string PathKey(DocumentSpecifier document) =>
        string.Join('/', document.SheetPath?.Path.Select(id => id.Value) ?? []);

    private static async Task<CheckedSchematicState> ReadExpected(NativeClient client, DocumentSpecifier document,
        CheckedSchematicState expected, CancellationToken token)
    {
        var observed = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
        CheckedSchematicContract.ValidateObservation(observed, document, client.Epoch);
        if (!observed.State.Equals(expected.State) || !observed.Electrical.Equals(expected.Electrical))
            throw new AutomationException("stale_document_state", "The schematic changed after the checked observation; inspect a fresh checked state and retry.");
        return observed;
    }

    private static CallToolResult MutationReply(string instanceId, string processEpoch, DocumentSpecifier root,
        DocumentSpecifier target, string screenId, int definitionCount, string operationId,
        CheckedSchematicBatchReceipt receipt, bool mutationSubmitted, bool replayed)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(new
        {
            instanceId, processEpoch, operationId, screenId, definitionCount,
            document = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(root)),
            targetDocument = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(target)),
            receipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(receipt)),
            mutationSubmitted, replayed,
            outcome = receipt.Status switch
            {
                CheckedSchematicBatchStatus.CsbsCompleted => "completed",
                CheckedSchematicBatchStatus.CsbsRejected => "rejected",
                CheckedSchematicBatchStatus.CsbsIndeterminate => "indeterminate",
                _ => "not_found"
            },
            designFilesSaved = false, globalLibraryEdited = false, nativeReceipt = true
        });
        return new CallToolResult { IsError = receipt.Status != CheckedSchematicBatchStatus.CsbsCompleted,
            StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static CallToolResult Failure(string instanceId, string operationId, string screenId,
        DocumentSpecifier target, bool mutationSubmitted, Exception error)
    {
        string code = error switch
        {
            AutomationException known => known.Code,
            NativeApiException native => "native_status_" + native.Status,
            NngException transport => transport.RequestDelivered ? "operation_outcome_unknown" : "native_not_reached",
            OperationCanceledException => "operation_outcome_unknown",
            _ => "schematic_library_cache_failed"
        };
        string outcome = error is NngException nng
            ? (nng.RequestDelivered ? "indeterminate" : "not_submitted")
            : error is OperationCanceledException ? "indeterminate"
            : mutationSubmitted ? "not_confirmed" : "not_submitted";
        if (error is NngException { RequestDelivered: false }) mutationSubmitted = false;
        JsonElement structured = JsonSerializer.SerializeToElement(new
        {
            instanceId, operationId, screenId,
            targetDocument = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(target)),
            mutationSubmitted, outcome, errorCode = code, errorMessage = error.Message
        });
        return new CallToolResult { IsError = true, StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static SchematicCachedSymbol[] ParseDefinitions(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new AutomationException("invalid_library_cache", "definitionsJson must be a JSON array.");
            var definitions = new List<SchematicCachedSymbol>();
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                SchematicCachedSymbol definition = SchematicJson.Parser.Parse<SchematicCachedSymbol>(element.GetRawText());
                if (string.IsNullOrWhiteSpace(definition.CacheKey) || definition.Definition?.Id is null
                    || string.IsNullOrWhiteSpace(definition.Definition.Id.LibraryNickname)
                    || string.IsNullOrWhiteSpace(definition.Definition.Id.EntryName))
                    throw new AutomationException("invalid_library_cache", "Each cached symbol needs a cacheKey and complete library identifier.");
                if (definition.CacheKey.Length > 512 || definition.Definition.Items.Count > 16384)
                    throw new AutomationException("invalid_library_cache", "Cache keys and symbol definitions exceed bounded limits.");
                definitions.Add(definition);
            }
            return definitions.ToArray();
        }
        catch (AutomationException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException("invalid_library_cache", "definitionsJson is not a valid SchematicCachedSymbol array: " + error.Message); }
    }

    private static T Parse<T>(string json, string code) where T : class, IMessage<T>, new()
    {
        try { return SchematicJson.Parser.Parse<T>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException(code, "Invalid protobuf JSON: " + error.Message); }
    }

    private static void ValidateRoot(DocumentSpecifier document)
    {
        try { DocumentStateTools.ValidateTarget(document); }
        catch (AutomationException error) { throw new AutomationException("invalid_checked_document", error.Message); }
        if (document.Type != DocumentType.DoctypeSchematic || document.SheetPath?.Path.Count != 1)
            throw new AutomationException("invalid_checked_document", "Library-cache replacement requires an explicit schematic root document.");
    }

    private static void ValidateIdentity(string value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out Guid id) || id == Guid.Empty || id.ToString("D") != value)
            throw new AutomationException("invalid_identity", name + " must be a canonical nonempty UUID.");
    }

    private static async Task<CallToolResult> Execute(string instanceId, string screenId, string operationId,
        Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException or InvalidJsonException or JsonException or IOException)
        {
            string code = error switch
            {
                AutomationException known => known.Code,
                NativeApiException native => "native_status_" + native.Status,
                NngException transport => "transport_status_" + transport.ErrorCode,
                _ => "schematic_library_cache_failed"
            };
            JsonElement structured = JsonSerializer.SerializeToElement(new
            {
                instanceId, screenId, operationId, mutationSubmitted = false, outcome = "not_submitted",
                errorCode = code, errorMessage = error.Message
            });
            return new CallToolResult { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }
}

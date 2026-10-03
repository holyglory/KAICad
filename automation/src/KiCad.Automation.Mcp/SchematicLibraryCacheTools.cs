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
     Description("Replace one schematic screen's complete embedded symbol-cache definitions as one checked native undo step. documentJson must identify the schematic root; expectedStateJson must be the matching checked lifecycle state; screenId is the native screen UUID; definitionsJson is a JSON array of complete SchematicCachedSymbol protobuf values; operationId is a new canonical UUID. The operation is revision-bound, atomic and receipt-backed, and does not save files. This edits the schematic's embedded cache only; it does not create or edit a global .kicad_sym library.")]
    [KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root document, exact screen identity, complete cache definitions, checked revision and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Replace(string instanceId, string documentJson, string expectedStateJson,
        string screenId, string definitionsJson, string operationId, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_checked_document");
        try { DocumentStateTools.ValidateTarget(document); }
        catch (AutomationException error) { throw new AutomationException("invalid_checked_document", error.Message); }
        if (document.Type != DocumentType.DoctypeSchematic || document.SheetPath?.Path.Count != 1)
            throw new AutomationException("invalid_checked_document", "Library-cache replacement requires an explicit schematic root document.");
        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_checked_state");
        if (expected.Scope != DocumentLifecycleScope.DlsSchematicHierarchy || !expected.Document.Equals(document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_checked_state", "expectedStateJson must identify the same schematic root and native process epoch.");
        ValidateIdentity(screenId, "screenId");
        ValidateIdentity(operationId, "operationId");
        SchematicCachedSymbol[] definitions = ParseDefinitions(definitionsJson);
        if (definitions.Length > 4096)
            throw new AutomationException("invalid_library_cache", "A cache replacement may contain at most 4096 definitions.");
        var cache = new SchematicLibraryCacheState { ScreenId = new() { Value = screenId } };
        cache.Definitions.Add(definitions);
        var batch = new ApplySchematicItemBatch
        {
            Document = document.Clone(), DocumentEpoch = expected.Revision.Epoch,
            ExpectedRevision = expected.Revision.Clone(), OperationId = operationId,
            Description = "Replace typed schematic library cache"
        };
        batch.Operations.Add(new SchematicItemOperation { ReplaceLibraryCache = cache });
        var request = new CheckedSchematicBatch { Batch = batch, ExpectedState = expected.Clone() };
        NativeClient client = registry.Client(instanceId);
        CheckedSchematicTools.ValidateRequest(request, client.Epoch);
        var receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, cancellationToken);
        CheckedSchematicTools.ValidateResult(request, receipt, inspect: false);
        string receiptJson = SchematicJson.Formatter.Format(receipt);
        JsonElement structured = JsonSerializer.SerializeToElement(new
        {
            instanceId, operationId, screenId, definitionCount = definitions.Length,
            receipt = JsonSerializer.Deserialize<JsonElement>(receiptJson),
            mutationSubmitted = true, designFilesSaved = false,
            globalLibraryEdited = false, nativeReceipt = true
        });
        return new CallToolResult { IsError = receipt.Status != CheckedSchematicBatchStatus.CsbsCompleted,
            StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    });

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

    private static void ValidateIdentity(string value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out Guid id) || id == Guid.Empty || id.ToString("D") != value)
            throw new AutomationException("invalid_identity", name + " must be a canonical nonempty UUID.");
    }

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException or InvalidJsonException or JsonException or IOException)
        {
            string code = error is AutomationException known ? known.Code : "schematic_library_cache_failed";
            JsonElement structured = JsonSerializer.SerializeToElement(new { errorCode = code, errorMessage = error.Message });
            return new CallToolResult { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }
}

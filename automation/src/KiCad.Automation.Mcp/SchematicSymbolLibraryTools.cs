using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>Typed global symbol-library access through KiCad's native adapter.</summary>
[McpServerToolType]
public sealed partial class SchematicSymbolLibraryTools(InstanceRegistry registry)
{
    private const int MaximumDefinitionBytes = 4 * 1024 * 1024;
    private const int MaximumItems = 4096;
    private const int MaximumFields = 512;
    private const int MaximumPins = 2048;
    private const int MaximumGraphics = 4096;
    private const int MaximumTextLength = 16 * 1024;
    private const int MaximumMetadataEntries = 512;
    private const int MaximumPinMapEntries = 4096;

    [McpServerTool(Name = "kicad_symbol_library_validate", ReadOnly = true),
     Description("Validate one complete SchematicSymbol protobuf-JSON definition without contacting KiCad. libraryNickname is the explicit library identity and must match definition.id.libraryNickname. The deterministic result reports bounded item/field counts, pin identity/name/number/length/orientation checks, named pin maps and associated footprint references, supported graphics Any types, unit/body-style references and a SHA-256 of canonical protobuf JSON. Pin maps may be sparse, and an empty associated map name uses native identity mapping; repeated symbol pin numbers and bracketed multi-pad entries are preserved. This is a managed read-only check; it does not resolve a footprint library, compare actual pad inventories, mutate a file or claim native persistence."),
     KiCadCapability("symbol-library", "compiled-mcp", "managed SchematicSymbol structure, pins, graphics Any types, units/body styles and canonical content SHA-256"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public CallToolResult Validate(string libraryNickname, string definitionJson,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateLibraryIdentity(libraryNickname);
            if (string.IsNullOrWhiteSpace(definitionJson))
                throw new AutomationException("invalid_symbol_definition", "definitionJson is required.");
            byte[] source = Encoding.UTF8.GetBytes(definitionJson);
            if (source.Length > MaximumDefinitionBytes)
                throw new AutomationException("symbol_definition_too_large", $"definitionJson exceeds the {MaximumDefinitionBytes} byte limit.");

            SchematicSymbol definition = ParseDefinition(definitionJson);
            string canonical = SchematicJson.Formatter.Format(definition);
            string contentSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            var issues = new List<SymbolValidationIssue>();
            ValidateDefinition(libraryNickname, definition, issues, cancellationToken);
            var ordered = issues.OrderBy(issue => issue.Path, StringComparer.Ordinal)
                .ThenBy(issue => issue.Code, StringComparer.Ordinal)
                .ThenBy(issue => issue.Message, StringComparer.Ordinal).ToArray();
            return Data(new
            {
                libraryNickname,
                libraryId = definition.Id is null ? null : new { libraryNickname = definition.Id.LibraryNickname, entryName = definition.Id.EntryName },
                valid = ordered.Length == 0,
                issues = ordered,
                contentSha256,
                canonicalBytes = Encoding.UTF8.GetByteCount(canonical),
                itemCount = definition.Items.Count,
                fieldCount = CountFields(definition),
                pinCount = definition.Items.Count(child => child.Item?.Is(SchematicPin.Descriptor) == true),
                graphicCount = definition.Items.Count(child => IsGraphic(child.Item)),
                nativeContacted = false,
                nativeMutation = false
            });
        }
        catch (Exception error) when (error is AutomationException or InvalidProtocolBufferException or InvalidJsonException)
        {
            string code = error is AutomationException known ? known.Code : "invalid_symbol_definition";
            return Data(new { errorCode = code, errorMessage = error.Message, valid = false,
                issues = Array.Empty<SymbolValidationIssue>(), nativeContacted = false, nativeMutation = false }, true);
        }
    }

    [McpServerTool(Name = "kicad_symbol_library_clone", ReadOnly = false),
     Description("Clone one complete symbol definition into an explicitly named project symbol library through the native adapter. Supply definitionJson and pinDisplayJson from the same kicad_symbol_library_read result; complete pinDisplayJson is required because the definition body does not own pin-label visibility or spacing. targetLibraryNickname and targetSymbolName replace only its library identity and entry name. The clone is validated before the native write and verifies the supplied source display against the persisted clone, retaining the caller operation UUID and native receipt semantics of kicad_symbol_library_write."),
     KiCadCapability("symbol-library", "compiled-mcp plus native-symbol-library-adapter", "validated symbol clone, explicit target library/name, operation UUID and native modify hashes"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Clone(string instanceId, string expectedProcessEpoch, string definitionJson,
        string targetLibraryNickname, string targetSymbolName, string operationId,
        CancellationToken cancellationToken, bool overwrite = false, string pinDisplayJson = "") => Execute(async () =>
    {
        ValidateEpoch(expectedProcessEpoch);
        ValidateLibrary(targetLibraryNickname);
        ValidateName(targetSymbolName, "targetSymbolName");
        ValidateOperation(operationId);
        SchematicSymbol source = ParseDefinition(definitionJson);
        SchematicSymbol clone = source.Clone();
        clone.Id = new() { LibraryNickname = targetLibraryNickname, EntryName = targetSymbolName };
        List<SymbolValidationIssue> issues = [];
        ValidateDefinition(targetLibraryNickname, clone, issues, cancellationToken);
        if (issues.Count != 0)
            throw new AutomationException("invalid_symbol_definition", "The cloned definition is invalid: " + string.Join("; ", issues.OrderBy(i => i.Path, StringComparer.Ordinal).Select(i => i.Code)));
        NativeCapabilitySymbolLibraryPinDisplay display = ParsePinDisplay(pinDisplayJson, required: true)!;
        return await Write(instanceId, expectedProcessEpoch, targetLibraryNickname,
            SchematicJson.Formatter.Format(clone), operationId, cancellationToken, overwrite,
            pinDisplayJson: SchematicJson.Formatter.Format(display));
    });

    [McpServerTool(Name = "kicad_symbol_library_receipt", ReadOnly = true),
     Description("Read the process-owned receipt for a symbol-library write or delete operation ID. A found receipt can be returned after a lost reply without resubmitting the mutation; the receipt is retained only while the native process lives.")]
    [KiCadCapability("symbol-library", "native-symbol-library-adapter", "operation UUID and process-owned write/delete receipt"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativeSymbolLibraryReadbackUsesMcp")]
    public Task<CallToolResult> Receipt(string instanceId, string expectedProcessEpoch, string operationId,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        ValidateEpoch(expectedProcessEpoch); ValidateOperation(operationId);
        NativeClient client = registry.Client(instanceId); AutomationSession session = await Handshake(client, expectedProcessEpoch, cancellationToken);
        var response = await client.InvokeAsync<NativeCapabilitySymbolLibraryReadOperation, NativeCapabilitySymbolLibraryReadOperationResponse>(
            new() { OperationId = operationId }, cancellationToken);
        return Data(new { instanceId, processEpoch = session.Epoch, operationId, found = response.Found,
            writeResponse = response.Found && response.WriteResponse is { Completed: true } ? JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(response.WriteResponse)) : (JsonElement?)null,
            deleteResponse = response.Found && response.DeleteResponse is { Completed: true } ? JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(response.DeleteResponse)) : (JsonElement?)null,
            nativeReceipt = response.Found, revisionTracked = false });
    });

    [McpServerTool(Name = "kicad_symbol_library_checked_receipt", ReadOnly = true),
     Description("Read one exact checked direct-symbol-file receipt after a lost reply, MCP reconnect or native process restart. Supply the current process epoch and the original request JSON; after an epoch change the durable old-epoch record is returned without a native lookup or writer retry, and admitted or unknown outcomes remain fail-closed."),
     KiCadCapability("symbol-library", "checked-native-direct-symbol-file", "exact checked request, durable MCP receipt, native revision and rollback evidence"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativeSymbolLibraryReadbackUsesMcp")]
    public Task<CallToolResult> CheckedReceipt(string instanceId, string expectedProcessEpoch,
        string documentJson, string operationId, string expectedRequestJson,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        ValidateEpoch(expectedProcessEpoch); ValidateOperation(operationId);
        DocumentSpecifier document = ParseDocument(documentJson);
        NativeCapabilitySymbolLibraryCheckedWrite expectedRequest;
        try { expectedRequest = SchematicJson.Parser.Parse<NativeCapabilitySymbolLibraryCheckedWrite>(expectedRequestJson); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException("invalid_library_request", "expectedRequestJson is not valid checked symbol-library request: " + error.Message); }
        if (expectedRequest.Document is null || !expectedRequest.Document.Equals(document)
            || !SymbolLibraryCheckedReceiptRecord.CanonicalGuid(expectedRequest.ProcessEpoch)
            || expectedRequest.OperationId != operationId)
            throw new AutomationException("invalid_library_request", "expectedRequestJson must retain the same document, original process epoch and operation UUID.");
        var receiptStore = new SymbolLibraryCheckedReceiptStore(registry.StateDirectory);
        bool sameProcessEpoch = expectedRequest.ProcessEpoch == expectedProcessEpoch;
        SymbolLibraryCheckedReceiptRecord? durableBeforeNative = sameProcessEpoch
            ? receiptStore.ReadBound(instanceId, expectedRequest)
            : receiptStore.ReadBoundAfterNativeRestart(instanceId, expectedRequest, expectedProcessEpoch);

        // A changed epoch cannot answer the original checked request: the
        // native receipt table belongs to the old process.  Confirm that the
        // replacement process is the one named by the caller, then return only
        // the exact durable MCP record.  This branch never invokes a native
        // lookup or a writer, so an admitted/unknown result cannot be replayed
        // accidentally after a restart.
        if (!sameProcessEpoch)
        {
            NativeClient replacement = registry.Client(instanceId);
            AutomationSession replacementSession = await replacement.HandshakeAsync(cancellationToken);
            if (replacementSession.InstanceId != instanceId || replacementSession.Epoch != expectedProcessEpoch)
                throw new AutomationException("stale_process_epoch", "The current native process epoch does not match the recovery request.");
            if (durableBeforeNative is null)
                throw new AutomationException("stale_process_epoch", "The original checked symbol request has no durable receipt; inspect the library before retrying.");

            if (durableBeforeNative.Outcome == SymbolLibraryCheckedReceiptRecord.Terminal)
            {
                NativeCapabilitySymbolLibraryCheckedWriteResponse retained =
                    SymbolLibraryCheckedReceiptStore.ParseResponse(durableBeforeNative.ResponseJson);
                SymbolLibraryCheckedContract.ValidateResponse(retained, expectedRequest);
                retained.Replayed = true;
                return Data(new
                {
                    instanceId,
                    processEpoch = expectedProcessEpoch,
                    durableProcessEpoch = durableBeforeNative.ProcessEpoch,
                    operationId,
                    found = true,
                    receipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(retained)),
                    nativeReceiptAvailable = false,
                    durableRecovery = true,
                    retryDispatched = false,
                    retrySafety = "terminal-receipt"
                });
            }

            return Data(new
            {
                instanceId,
                processEpoch = expectedProcessEpoch,
                durableProcessEpoch = durableBeforeNative.ProcessEpoch,
                operationId,
                found = true,
                receipt = (JsonElement?)null,
                nativeReceiptAvailable = false,
                durableRecovery = true,
                retryDispatched = false,
                outcome = durableBeforeNative.Outcome,
                retrySafety = "unknown-native-outcome",
                errorCode = "operation_outcome_unknown",
                errorMessage = durableBeforeNative.ErrorMessage.Length == 0
                    ? "The checked symbol write has no terminal receipt; inspect the native library before retrying."
                    : durableBeforeNative.ErrorMessage
            }, error: true);
        }
        NativeCapabilitySymbolLibraryCheckedReadOperationResponse? nativeResponse = null;
        AutomationException? nativeUnavailable = null;
        try
        {
            NativeClient client = registry.Client(instanceId);
            AutomationSession session = await Handshake(client, expectedProcessEpoch, cancellationToken);
            nativeResponse = await client.InvokeAsync<
                NativeCapabilitySymbolLibraryCheckedReadOperation, NativeCapabilitySymbolLibraryCheckedReadOperationResponse>(new()
                {
                    Document = document, ProcessEpoch = session.Epoch, OperationId = operationId,
                    ExpectedRequest = expectedRequest
                }, cancellationToken);
            if (nativeResponse.Found)
            {
                SymbolLibraryCheckedContract.ValidateResponse(nativeResponse.Receipt, expectedRequest);
                // A live native read is authoritative, but an existing durable
                // terminal record is immutable and remains the reconnect proof.
                if (durableBeforeNative?.Outcome != SymbolLibraryCheckedReceiptRecord.Terminal)
                    receiptStore.WriteTerminal(instanceId, expectedRequest, nativeResponse.Receipt);
                return Data(new
                {
                    instanceId, processEpoch = expectedProcessEpoch, operationId, found = true,
                    receipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(nativeResponse.Receipt)),
                    nativeReceiptAvailable = true, durableRecovery = false, retryDispatched = false
                });
            }
        }
        catch (NngException) { }
        catch (AutomationException error) when (error.Code is "stale_process_epoch" or "instance_exited"
            or "instance_changed" or "unknown_instance")
        {
            // A native replacement discards its process-owned receipt table.  The
            // exact request-bound MCP record remains authoritative for recovery;
            // preserve this failure only when no durable record exists.
            nativeUnavailable = error;
        }

        SymbolLibraryCheckedReceiptRecord? durable = receiptStore.Read(instanceId, operationId);
        if (durable is not null)
        {
            NativeCapabilitySymbolLibraryCheckedWrite storedRequest =
                SymbolLibraryCheckedReceiptStore.ParseRequest(durable);
            if (!storedRequest.Equals(expectedRequest))
                throw new AutomationException("operation_id_conflict", "The operation ID is already bound to another checked symbol request.");
            if (durable.Outcome == SymbolLibraryCheckedReceiptRecord.Terminal)
            {
                NativeCapabilitySymbolLibraryCheckedWriteResponse storedResponse =
                    SymbolLibraryCheckedReceiptStore.ParseResponse(durable.ResponseJson);
                SymbolLibraryCheckedContract.ValidateResponse(storedResponse, expectedRequest);
                return Data(new
                {
                    instanceId, processEpoch = expectedProcessEpoch, operationId, found = true,
                    receipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(storedResponse)),
                    nativeReceiptAvailable = false, durableRecovery = true, retryDispatched = false
                });
            }

            throw new AutomationException("operation_outcome_unknown",
                durable.ErrorMessage.Length == 0
                    ? "The checked symbol write has no terminal receipt; inspect the native library before retrying."
                    : durable.ErrorMessage);
        }

        if (nativeUnavailable is not null)
            throw nativeUnavailable;

        return Data(new
        {
            instanceId,
            processEpoch = expectedProcessEpoch,
            operationId,
            found = nativeResponse?.Found == true,
            receipt = nativeResponse?.Found == true
                ? JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(nativeResponse.Receipt))
                : (JsonElement?)null,
            nativeReceiptAvailable = false,
            durableRecovery = false,
            retryDispatched = false
        });
    });

    [McpServerTool(Name = "kicad_symbol_library_list", ReadOnly = true),
     Description("List symbols in an explicitly named project symbol library through KiCad's native symbol-library adapter. The result includes the adapter modify hash; this is an observation, not a revision admission.")]
    [KiCadCapability("symbol-library", "native-symbol-library-adapter", "library nickname and native modify hash"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativeSymbolLibraryReadbackUsesMcp")]
    public Task<CallToolResult> List(string instanceId, string expectedProcessEpoch, string libraryNickname,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        ValidateEpoch(expectedProcessEpoch); ValidateLibrary(libraryNickname);
        NativeClient client = registry.Client(instanceId); AutomationSession session = await Handshake(client, expectedProcessEpoch, cancellationToken);
        var response = await client.InvokeAsync<NativeCapabilitySymbolLibraryReadNames, NativeCapabilitySymbolLibraryReadNamesResponse>(
            new() { LibraryNickname = libraryNickname }, cancellationToken);
        return Data(new { instanceId, processEpoch = session.Epoch, libraryNickname,
            symbolNames = response.SymbolNames.ToArray(), modifyHash = response.ModifyHash,
            revisionTracked = false, nativeReceiptAvailable = false });
    });

    [McpServerTool(Name = "kicad_symbol_library_read", ReadOnly = true),
     Description("Read one complete typed symbol definition from an explicitly named project symbol library. The native definition body is returned as SchematicSymbol protobuf JSON, alongside a separate complete pinDisplay object with pin-name visibility, pin-number visibility and exact spacing in nanometers. Preserve both objects when cloning or editing. The result also carries the library modify hash.")]
    [KiCadCapability("symbol-library", "native-symbol-library-adapter", "library nickname, symbol name and native modify hash"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativeSymbolLibraryReadbackUsesMcp")]
    public Task<CallToolResult> Read(string instanceId, string expectedProcessEpoch, string libraryNickname,
        string symbolName, CancellationToken cancellationToken) => Execute(async () =>
    {
        ValidateEpoch(expectedProcessEpoch); ValidateLibrary(libraryNickname); ValidateName(symbolName, "symbolName");
        NativeClient client = registry.Client(instanceId); AutomationSession session = await Handshake(client, expectedProcessEpoch, cancellationToken);
        var response = await client.InvokeAsync<NativeCapabilitySymbolLibraryReadSymbol, NativeCapabilitySymbolLibraryReadSymbolResponse>(
            new() { LibraryNickname = libraryNickname, SymbolName = symbolName }, cancellationToken);
        ValidatePinDisplay(response.PinDisplay);
        return Data(new { instanceId, processEpoch = session.Epoch, libraryNickname, symbolName,
            definition = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(response.Definition)),
            pinDisplay = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(response.PinDisplay)),
            modifyHash = response.ModifyHash, revisionTracked = false, nativeReceiptAvailable = false });
    });

    [McpServerTool(Name = "kicad_symbol_library_write", ReadOnly = false),
     Description("Create or replace one complete symbol definition in an explicitly named project symbol library through KiCad's native adapter. Optional pinDisplayJson supplies all of showPinNames, showPinNumbers and pinNameOffsetNm; false and zero are explicit values, and native-valid negative spacing is supported. When omitted, updates preserve the existing library display and new entries use native defaults. When expectedModifyHash is supplied, the write is admitted only if the current native modify hash matches; an already recorded operation ID is replayed before that check. The process-owned receipt and persisted readback verify the supplied display as well as the definition. The library still has no durable lifecycle revision sequence.")]
    [KiCadCapability("symbol-library", "native-symbol-library-adapter", "library nickname, complete SchematicSymbol, overwrite flag, operation UUID and native modify hashes"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativeSymbolLibraryReadbackUsesMcp")]
    public Task<CallToolResult> Write(string instanceId, string expectedProcessEpoch, string libraryNickname,
        string definitionJson, string operationId, CancellationToken cancellationToken, bool overwrite = true,
        string expectedModifyHash = "", string pinDisplayJson = "") => Execute(async () =>
    {
        ValidateEpoch(expectedProcessEpoch); ValidateLibrary(libraryNickname); ValidateOperation(operationId);
        SchematicSymbol definition = ParseDefinition(definitionJson);
        if (definition.Id is null || definition.Id.LibraryNickname != libraryNickname || string.IsNullOrWhiteSpace(definition.Id.EntryName))
            throw new AutomationException("symbol_library_definition_mismatch", "definitionJson must carry the same library nickname and a nonempty entry name.");
        List<SymbolValidationIssue> issues = [];
        ValidateDefinition(libraryNickname, definition, issues, cancellationToken);
        if (issues.Count != 0)
            throw new AutomationException("invalid_symbol_definition", "The symbol definition is invalid: "
                + string.Join("; ", issues.OrderBy(issue => issue.Path, StringComparer.Ordinal).Select(issue => issue.Code)));
        NativeCapabilitySymbolLibraryPinDisplay? display = ParsePinDisplay(pinDisplayJson, required: false);
        NativeClient client = registry.Client(instanceId); AutomationSession session = await Handshake(client, expectedProcessEpoch, cancellationToken);
        string revisionAdmission = await EnsureModifyHash(client, libraryNickname, operationId, expectedModifyHash, cancellationToken);
        var response = await client.InvokeAsync<NativeCapabilitySymbolLibraryWriteSymbol, NativeCapabilitySymbolLibraryWriteSymbolResponse>(
            new() { LibraryNickname = libraryNickname, Definition = definition, Overwrite = overwrite, OperationId = operationId,
                PinDisplay = display }, cancellationToken);
        SymbolLibraryWriteReadback readback = response.Completed
            ? await VerifyWriteReadback(client, libraryNickname, response, display, cancellationToken)
            : new(false, null, null);
        return Data(new { instanceId, processEpoch = session.Epoch, libraryNickname,
            symbolName = response.SymbolName, operationId, operationOutcome = response.Completed ? "completed" : "skipped",
            beforeModifyHash = response.BeforeModifyHash, afterModifyHash = response.AfterModifyHash,
            definition = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(response.Definition)),
            pinDisplay = response.PinDisplay is null ? (JsonElement?)null
                : JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(response.PinDisplay)),
            pinDisplayInputVerified = display is not null && readback.Verified,
            nativeCommandSucceeded = response.Completed, nativeReceiptAvailable = true,
            operationReceipt = true, revisionTracked = false, persistenceVerified = readback.Verified,
            persistedContentSha256 = readback.ContentSha256, persistedModifyHash = readback.ModifyHash,
            persistenceSource = readback.Verified ? "native-symbol-library-readback" : null, transportRetrySafe = true,
            expectedModifyHash = string.IsNullOrWhiteSpace(expectedModifyHash) ? null : expectedModifyHash,
            revisionAdmission });
    });

    [McpServerTool(Name = "kicad_symbol_library_write_checked", ReadOnly = false),
     Description("Checked direct-file symbol-library write. Pass the exact checkedStateJson returned by kicad_library_snapshot_read together with its state digest. The native owner admits only an existing direct KiCad .kicad_sym file, holds one complete dependency lock through save/readback/rollback, verifies the target and unrelated entries, and the MCP journal retains the exact receipt for replay after a lost reply or MCP replacement. Legacy, folder, database, HTTP and incomplete providers return an honest refusal without reaching a writer."),
     KiCadCapability("symbol-library", "checked-native-direct-symbol-file", "project-bound symbol document, process epoch, complete checked state/revision, operation UUID, definition and typed receipt"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> WriteChecked(string instanceId, string expectedProcessEpoch, string documentJson,
        string definitionJson, string operationId, string expectedSnapshotSha256, CancellationToken cancellationToken,
        bool overwrite = true, string pinDisplayJson = "", string expectedStateJson = "") => Execute(async () =>
    {
        ValidateEpoch(expectedProcessEpoch); ValidateOperation(operationId);
        if (expectedSnapshotSha256.Length != 64 || !expectedSnapshotSha256.All(char.IsAsciiHexDigitLower))
            throw new AutomationException("invalid_library_snapshot", "expectedSnapshotSha256 must be a lowercase complete snapshot digest.");
        DocumentSpecifier document;
        try { document = SchematicJson.Parser.Parse<DocumentSpecifier>(documentJson); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException("invalid_library_document", "documentJson is not a valid symbol document: " + error.Message); }
        if (document.Type != DocumentType.DoctypeSymbol || document.Project is null || document.LibId is null)
            throw new AutomationException("invalid_library_document", "A project-bound symbol document is required.");
        SchematicSymbol definition = ParseDefinition(definitionJson);
        if (definition.Id is null || definition.Id.LibraryNickname != document.LibId.LibraryNickname
            || string.IsNullOrWhiteSpace(definition.Id.EntryName))
            throw new AutomationException("symbol_library_definition_mismatch", "The definition identity must match the symbol document.");
        List<SymbolValidationIssue> issues = [];
        ValidateDefinition(document.LibId.LibraryNickname, definition, issues, cancellationToken);
        if (issues.Count != 0)
            throw new AutomationException("invalid_symbol_definition", "The symbol definition is invalid: "
                + string.Join("; ", issues.OrderBy(issue => issue.Path, StringComparer.Ordinal).Select(issue => issue.Code)));
        NativeCapabilitySymbolLibraryPinDisplay? display = ParsePinDisplay(pinDisplayJson, required: true);
        NativeCapabilitySymbolLibraryCheckedState? expectedState = null;
        if (!string.IsNullOrWhiteSpace(expectedStateJson))
        {
            try { expectedState = SchematicJson.Parser.Parse<NativeCapabilitySymbolLibraryCheckedState>(expectedStateJson); }
            catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
            { throw new AutomationException("invalid_library_snapshot", "expectedStateJson is not valid checked symbol-library state: " + error.Message); }
        }
        if (expectedState is null)
            throw new AutomationException("invalid_library_snapshot", "expectedStateJson is required for checked direct-file mutation.");
        if (!string.Equals(expectedState.StateSha256, expectedSnapshotSha256, StringComparison.Ordinal))
            throw new AutomationException("stale_library_state", "expectedSnapshotSha256 must match the complete checked state digest.");
        if (expectedState.Document is null || !expectedState.Document.Equals(document)
            || expectedState.ProcessEpoch != expectedProcessEpoch
            || expectedState.Revision is null || expectedState.Revision.Epoch != expectedProcessEpoch)
            throw new AutomationException("invalid_library_snapshot",
                "expectedStateJson must retain the exact symbol document and native process epoch.");
        var expectedRequest = new NativeCapabilitySymbolLibraryCheckedWrite
        {
            Document = document.Clone(), ProcessEpoch = expectedProcessEpoch, OperationId = operationId,
            ExpectedSnapshotSha256 = expectedSnapshotSha256, Definition = definition.Clone(),
            Overwrite = overwrite, ExpectedState = expectedState.Clone()
        };
        if (display is not null)
            expectedRequest.PinDisplay = display.Clone();

        // Persist the exact admission before contacting KiCad. A replacement
        // MCP process must never redispatch an operation whose native outcome
        // may already exist.
        var receiptStore = new SymbolLibraryCheckedReceiptStore(registry.StateDirectory);
        var reservation = receiptStore.Reserve(instanceId, expectedRequest);
        if (!reservation.Created)
        {
            if (reservation.Receipt.Outcome == SymbolLibraryCheckedReceiptRecord.Terminal)
            {
                NativeCapabilitySymbolLibraryCheckedWriteResponse replay =
                    SymbolLibraryCheckedReceiptStore.ParseResponse(reservation.Receipt.ResponseJson);
                replay.Replayed = true;
                SymbolLibraryCheckedContract.ValidateResponse(replay, expectedRequest);
                return Data(new
                {
                    instanceId, processEpoch = expectedProcessEpoch, document, operationId,
                    status = replay.Status, committed = replay.Committed, replayed = true,
                    rollbackCompleted = replay.RollbackCompleted, revisionTracked = replay.RevisionTracked,
                    completeLifecycle = replay.CompleteLifecycle, beforeSnapshotSha256 = replay.BeforeSnapshotSha256,
                    afterSnapshotSha256 = replay.AfterSnapshotSha256, errorCode = replay.ErrorCode,
                    errorMessage = replay.ErrorMessage, nativeReceiptAvailable = false,
                    receiptVerified = true, persistenceVerified = replay.Committed,
                    requestFingerprintSha256 = replay.RequestFingerprintSha256,
                    expectedRequestVerified = replay.ExpectedRequestVerified,
                    nativeWriteStarted = replay.NativeWriteStarted, postverified = replay.Postverified,
                    targetVerified = replay.TargetVerified, unrelatedEntriesVerified = replay.UnrelatedEntriesVerified,
                    rollbackAttempted = replay.RollbackAttempted,
                    receiptStatus = replay.ReceiptStatus.ToString(), scope = replay.Scope,
                    beforeRevision = replay.ObservedBefore?.Revision, afterRevision = replay.ObservedAfter?.Revision,
                    rollbackRevision = replay.ObservedRollback?.Revision,
                    persistenceSource = replay.Committed ? "durable-symbol-library-receipt" : null,
                    durableRecovery = true,
                    transportRetrySafe = true
                }, !replay.Committed || !string.IsNullOrEmpty(replay.ErrorCode));
            }

            throw new AutomationException("operation_outcome_unknown",
                reservation.Receipt.Outcome == SymbolLibraryCheckedReceiptRecord.Unknown
                    ? "The checked symbol write was delivered previously but its native result is unresolved; inspect the library before starting a new operation."
                    : "Another MCP process has admitted this checked symbol write; reconcile its outcome before retrying.");
        }

        NativeClient client;
        AutomationSession session;
        try
        {
            client = registry.Client(instanceId);
            session = await Handshake(client, expectedProcessEpoch, cancellationToken);
        }
        catch
        {
            // No native mutation has been submitted before the handshake;
            // release only this still-admitted reservation.
            try { receiptStore.RemoveAdmission(instanceId, expectedRequest); }
            catch (AutomationException) { }
            throw;
        }

        NativeCapabilitySymbolLibraryCheckedWriteResponse response;
        try
        {
            response = await client.InvokeAsync<
                NativeCapabilitySymbolLibraryCheckedWrite, NativeCapabilitySymbolLibraryCheckedWriteResponse>(expectedRequest, cancellationToken);
            SymbolLibraryCheckedContract.ValidateResponse(response, expectedRequest);
            receiptStore.WriteTerminal(instanceId, expectedRequest, response);
        }
        catch (NngException error)
        {
            if (error.RequestDelivered)
            {
                receiptStore.WriteUnknown(instanceId, expectedRequest, "operation_outcome_unknown",
                    $"KiCad received checked symbol write {operationId} but did not answer; inspect the library before retrying.");
            }
            else
            {
                receiptStore.RemoveAdmission(instanceId, expectedRequest);
            }
            throw new AutomationException(error.RequestDelivered ? "operation_outcome_unknown" : "native_not_reached",
                error.RequestDelivered
                    ? $"KiCad received checked symbol write {operationId} but did not answer; inspect the library before retrying."
                    : $"KiCad did not receive checked symbol write {operationId}; inspect the instance before retrying.");
        }
        catch (OperationCanceledException)
        {
            receiptStore.WriteUnknown(instanceId, expectedRequest, "operation_outcome_unknown",
                $"Checked symbol write {operationId} was cancelled after admission; inspect the library before retrying.");
            throw new AutomationException("operation_outcome_unknown",
                $"Checked symbol write {operationId} was cancelled after admission; inspect the library before retrying.");
        }
        catch (AutomationException error)
        {
            receiptStore.WriteUnknown(instanceId, expectedRequest, "invalid_native_receipt",
                $"The checked symbol write returned an untrusted native result: {error.Message}");
            throw;
        }
        var data = new
        {
            instanceId, processEpoch = session.Epoch, document, operationId,
            status = response.Status, committed = response.Committed, replayed = response.Replayed,
            rollbackCompleted = response.RollbackCompleted, revisionTracked = response.RevisionTracked,
            completeLifecycle = response.CompleteLifecycle, beforeSnapshotSha256 = response.BeforeSnapshotSha256,
            afterSnapshotSha256 = response.AfterSnapshotSha256, errorCode = response.ErrorCode,
            errorMessage = response.ErrorMessage, nativeReceiptAvailable = true,
            durableRecovery = false,
            receiptVerified = true, persistenceVerified = response.Committed,
            requestFingerprintSha256 = response.RequestFingerprintSha256,
            expectedRequestVerified = response.ExpectedRequestVerified,
            nativeWriteStarted = response.NativeWriteStarted,
            postverified = response.Postverified,
            targetVerified = response.TargetVerified,
            unrelatedEntriesVerified = response.UnrelatedEntriesVerified,
            rollbackAttempted = response.RollbackAttempted,
            receiptStatus = response.ReceiptStatus.ToString(),
            scope = response.Scope,
            beforeRevision = response.ObservedBefore?.Revision,
            afterRevision = response.ObservedAfter?.Revision,
            rollbackRevision = response.ObservedRollback?.Revision,
            persistenceSource = response.Committed ? "native-lifecycle-receipt" : null,
            transportRetrySafe = !response.Committed || response.Replayed
        };
        return Data(data, !response.Committed || !string.IsNullOrEmpty(response.ErrorCode));
    });

    [McpServerTool(Name = "kicad_symbol_library_delete", ReadOnly = false),
     Description("Delete one named symbol from an explicitly identified project symbol library through KiCad's native adapter. When expectedModifyHash is supplied, deletion is admitted only if the library's current native modify hash still matches; an already recorded operation ID is replayed before that check. operationId binds the request to a process-owned native receipt, and the completed delete is verified by re-enumerating the persisted library through the native adapter. The library still has no durable lifecycle revision sequence.")]
    [KiCadCapability("symbol-library", "native-symbol-library-adapter", "library nickname, symbol name, operation UUID and native modify hashes"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativeSymbolLibraryReadbackUsesMcp")]
    public Task<CallToolResult> Delete(string instanceId, string expectedProcessEpoch, string libraryNickname,
        string symbolName, string operationId, CancellationToken cancellationToken, string expectedModifyHash = "") => Execute(async () =>
    {
        ValidateEpoch(expectedProcessEpoch); ValidateLibrary(libraryNickname); ValidateName(symbolName, "symbolName"); ValidateOperation(operationId);
        NativeClient client = registry.Client(instanceId); AutomationSession session = await Handshake(client, expectedProcessEpoch, cancellationToken);
        string revisionAdmission = await EnsureModifyHash(client, libraryNickname, operationId, expectedModifyHash, cancellationToken);
        var response = await client.InvokeAsync<NativeCapabilitySymbolLibraryDeleteSymbol, NativeCapabilitySymbolLibraryDeleteSymbolResponse>(
            new() { LibraryNickname = libraryNickname, SymbolName = symbolName, OperationId = operationId }, cancellationToken);
        SymbolLibraryDeleteReadback readback = response.Completed
            ? await VerifyDeleteReadback(client, libraryNickname, symbolName, response, cancellationToken)
            : new(false, null);
        return Data(new { instanceId, processEpoch = session.Epoch, libraryNickname, symbolName, operationId,
            operationOutcome = response.Completed ? "completed" : "skipped", beforeModifyHash = response.BeforeModifyHash,
            afterModifyHash = response.AfterModifyHash, nativeCommandSucceeded = response.Completed,
            nativeReceiptAvailable = true, operationReceipt = true, revisionTracked = false,
            persistenceVerified = readback.Verified, persistedModifyHash = readback.ModifyHash,
            persistenceSource = readback.Verified ? "native-symbol-library-readback" : null,
            transportRetrySafe = true,
            expectedModifyHash = string.IsNullOrWhiteSpace(expectedModifyHash) ? null : expectedModifyHash,
            revisionAdmission });
    });

    private static async Task<SymbolLibraryWriteReadback> VerifyWriteReadback(NativeClient client,
        string libraryNickname, NativeCapabilitySymbolLibraryWriteSymbolResponse response,
        NativeCapabilitySymbolLibraryPinDisplay? requestedDisplay,
        CancellationToken cancellationToken)
    {
        ValidatePinDisplay(response.PinDisplay);
        if (requestedDisplay is not null && !requestedDisplay.Equals(response.PinDisplay))
            throw new AutomationException("symbol_library_persistence_mismatch",
                $"KiCad's receipt for {response.OperationId} changed the supplied source pin display.");
        NativeCapabilitySymbolLibraryReadSymbolResponse persisted = await client.InvokeAsync<
            NativeCapabilitySymbolLibraryReadSymbol, NativeCapabilitySymbolLibraryReadSymbolResponse>(
                new() { LibraryNickname = libraryNickname, SymbolName = response.SymbolName }, cancellationToken);
        ValidatePinDisplay(persisted.PinDisplay);
        string canonical = SchematicJson.Formatter.Format(persisted.Definition);
        string expectedCanonical = SchematicJson.Formatter.Format(response.Definition);
        if (!string.Equals(canonical, expectedCanonical, StringComparison.Ordinal))
            throw new AutomationException("symbol_library_persistence_mismatch",
                $"KiCad accepted symbol-library write {response.OperationId}, but native readback of {libraryNickname}:{response.SymbolName} differed from the written definition.");
        if (persisted.ModifyHash != response.AfterModifyHash)
            throw new AutomationException("symbol_library_persistence_mismatch",
                $"KiCad accepted symbol-library write {response.OperationId}, but native readback reported modify hash {persisted.ModifyHash} instead of {response.AfterModifyHash}.");
        NativeCapabilitySymbolLibraryPinDisplay expectedDisplay = requestedDisplay ?? response.PinDisplay;
        if (!expectedDisplay.Equals(persisted.PinDisplay))
            throw new AutomationException("symbol_library_persistence_mismatch",
                $"KiCad accepted symbol-library write {response.OperationId}, but the persisted pin display differed from the supplied source or retained receipt.");

        return new(true,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                canonical + "\0" + SchematicJson.Formatter.Format(persisted.PinDisplay)))), persisted.ModifyHash);
    }

    private static NativeCapabilitySymbolLibraryPinDisplay? ParsePinDisplay(string json, bool required)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            if (required)
                throw new AutomationException("symbol_library_pin_display_required",
                    "Cloning requires complete pinDisplayJson from the same source library read as definitionJson.");
            return null;
        }
        if (Encoding.UTF8.GetByteCount(json) > 4096)
            throw new AutomationException("invalid_symbol_pin_display", "pinDisplayJson exceeds the bounded settings size.");
        NativeCapabilitySymbolLibraryPinDisplay display;
        try { display = SchematicJson.Parser.Parse<NativeCapabilitySymbolLibraryPinDisplay>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException("invalid_symbol_pin_display", "pinDisplayJson is not valid pin-display protobuf JSON: " + error.Message); }
        ValidatePinDisplay(display);
        return display;
    }

    private static void ValidatePinDisplay(NativeCapabilitySymbolLibraryPinDisplay? display)
    {
        if (display is null || !display.HasShowPinNames || !display.HasShowPinNumbers || !display.HasPinNameOffsetNm)
            throw new AutomationException("invalid_symbol_pin_display",
                "Pin display must explicitly contain showPinNames, showPinNumbers and pinNameOffsetNm.");
        // Match the existing native schematic range and 100 nm grid contract.
        long offset = display.PinNameOffsetNm;
        if (offset % 100 != 0 || offset < (long)int.MinValue * 100 || offset > (long)int.MaxValue * 100)
            throw new AutomationException("invalid_symbol_pin_display",
                "pinNameOffsetNm must lie on the native 100 nm grid and within the native coordinate range.");
    }

    private static async Task<SymbolLibraryDeleteReadback> VerifyDeleteReadback(NativeClient client,
        string libraryNickname, string symbolName, NativeCapabilitySymbolLibraryDeleteSymbolResponse response,
        CancellationToken cancellationToken)
    {
        NativeCapabilitySymbolLibraryReadNamesResponse persisted = await client.InvokeAsync<
            NativeCapabilitySymbolLibraryReadNames, NativeCapabilitySymbolLibraryReadNamesResponse>(
                new() { LibraryNickname = libraryNickname }, cancellationToken);
        if (persisted.SymbolNames.Contains(symbolName, StringComparer.Ordinal))
            throw new AutomationException("symbol_library_persistence_mismatch",
                $"KiCad accepted symbol-library delete {response.OperationId}, but native readback still listed {libraryNickname}:{symbolName}.");
        if (persisted.ModifyHash != response.AfterModifyHash)
            throw new AutomationException("symbol_library_persistence_mismatch",
                $"KiCad accepted symbol-library delete {response.OperationId}, but native readback reported modify hash {persisted.ModifyHash} instead of {response.AfterModifyHash}.");

        return new(true, persisted.ModifyHash);
    }

    private static async Task<AutomationSession> Handshake(NativeClient client, string expectedEpoch, CancellationToken token)
    {
        AutomationSession session = await client.HandshakeAsync(token);
        if (session.Epoch != expectedEpoch) throw new AutomationException("stale_process_epoch", "The expected process epoch is stale; inspect the instance again.");
        return session;
    }

    private static async Task<string> EnsureModifyHash(NativeClient client, string libraryNickname,
        string operationId, string expectedModifyHash, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedModifyHash)) return "not-requested";
        if (!ulong.TryParse(expectedModifyHash, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out ulong expected))
            throw new AutomationException("invalid_symbol_library_revision", "expectedModifyHash must be an unsigned decimal native modify hash.");

        NativeCapabilitySymbolLibraryReadOperationResponse prior = await client.InvokeAsync<NativeCapabilitySymbolLibraryReadOperation,
            NativeCapabilitySymbolLibraryReadOperationResponse>(new() { OperationId = operationId }, cancellationToken);
        // A native receipt proves that this exact operation was previously admitted;
        // keep the outward result deterministic so replay is byte-for-byte identical.
        if (prior.Found) return "modify-hash-matched";

        NativeCapabilitySymbolLibraryReadNamesResponse current = await client.InvokeAsync<NativeCapabilitySymbolLibraryReadNames,
            NativeCapabilitySymbolLibraryReadNamesResponse>(new() { LibraryNickname = libraryNickname }, cancellationToken);
        if (current.ModifyHash != expected)
            throw new AutomationException("symbol_library_changed",
                $"The symbol library modify hash changed from {expectedModifyHash} to {current.ModifyHash}; inspect it and retry with a fresh operation ID.");
        return "modify-hash-matched";
    }

    private static SchematicSymbol ParseDefinition(string json)
    {
        try { return SchematicJson.Parser.Parse<SchematicSymbol>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException("invalid_symbol_definition", "definitionJson is not a valid SchematicSymbol protobuf JSON: " + error.Message); }
    }

    private static void ValidateDefinition(string libraryNickname, SchematicSymbol definition,
        List<SymbolValidationIssue> issues, CancellationToken token)
    {
        if (definition.Id is null)
            Issue(issues, "missing_library_id", "id", "A complete symbol definition requires an explicit library identifier.");
        else
        {
            if (!string.Equals(definition.Id.LibraryNickname, libraryNickname, StringComparison.Ordinal))
                Issue(issues, "library_id_mismatch", "id.libraryNickname", "The definition library nickname must match the explicit libraryNickname argument.");
            if (string.IsNullOrWhiteSpace(definition.Id.EntryName) || definition.Id.EntryName.Length > 255
                || definition.Id.EntryName.Contains(':') || HasControl(definition.Id.EntryName))
                Issue(issues, "invalid_entry_name", "id.entryName", "entryName must be a bounded, nonempty symbol name without separators or control characters.");
            if (string.IsNullOrWhiteSpace(definition.Id.LibraryNickname) || definition.Id.LibraryNickname.Length > 255
                || definition.Id.LibraryNickname.Contains(':') || HasControl(definition.Id.LibraryNickname))
                Issue(issues, "invalid_library_nickname", "id.libraryNickname", "libraryNickname must be a bounded name without separators or control characters.");
        }

        if ((int)definition.Type is < (int)SchematicSymbolType.SstNormal or > (int)SchematicSymbolType.SstLocalPower)
            Issue(issues, "invalid_symbol_type", "type", "type must be a supported schematic symbol type.");
        if (definition.UnitCount is < 1 or > 64)
            Issue(issues, "invalid_unit_count", "unitCount", "unitCount must be between 1 and 64.");
        if (definition.BodyStyle.Count is < 1 or > 16)
            Issue(issues, "invalid_body_style_count", "bodyStyle", "bodyStyle must contain between 1 and 16 styles.");
        var bodyStyles = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < definition.BodyStyle.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            string path = $"bodyStyle[{i}]";
            string name = definition.BodyStyle[i].Name;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || !bodyStyles.Add(name))
                Issue(issues, "invalid_body_style", path, "Each body style requires a bounded, distinct nonempty name.");
        }
        if (definition.DemorganBodyStyles && (definition.BodyStyle.Count < 2
            || definition.BodyStyle[0].Name != "Standard" || definition.BodyStyle[1].Name != "Alternate"))
            Issue(issues, "invalid_demorgan_body_styles", "demorganBodyStyles", "De Morgan styles require Standard followed by Alternate.");

        ValidateStandardField(definition.ReferenceField, "Reference", "referenceField", issues);
        ValidateStandardField(definition.ValueField, "Value", "valueField", issues);
        ValidateStandardField(definition.FootprintField, "Footprint", "footprintField", issues);
        ValidateStandardField(definition.DatasheetField, "Datasheet", "datasheetField", issues);
        ValidateStandardField(definition.DescriptionField, "Description", "descriptionField", issues);
        int fields = CountFields(definition);
        if (fields > MaximumFields)
            Issue(issues, "too_many_fields", "fields", $"The definition contains {fields} fields; the limit is {MaximumFields}.");
        if (definition.Items.Count > MaximumItems)
            Issue(issues, "too_many_items", "items", $"The definition contains {definition.Items.Count} items; the limit is {MaximumItems}.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var pinIds = new HashSet<string>(StringComparer.Ordinal);
        var pinNumbers = new HashSet<string>(StringComparer.Ordinal);
        int pins = 0, graphics = 0;
        for (int i = 0; i < definition.Items.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            SchematicSymbolChild child = definition.Items[i];
            string path = $"items[{i}]";
            int unit = child.Unit?.Unit ?? 0;
            int style = child.BodyStyle?.Style ?? 0;
            if (unit < 0 || unit > definition.UnitCount)
                Issue(issues, "invalid_child_unit", path + ".unit.unit", "Child unit must be zero (all units) or reference an existing unit.");
            if (style < 0 || style > definition.BodyStyle.Count)
                Issue(issues, "invalid_child_body_style", path + ".bodyStyle.style", "Child body style must be zero (all styles) or reference an existing body style.");
            if (child.Item is null || string.IsNullOrWhiteSpace(child.Item.TypeUrl) || child.Item.Value.Length == 0)
            {
                Issue(issues, "missing_child_type", path + ".item", "Every child requires a typed, nonempty protobuf Any value.");
                continue;
            }
            if (!IsSupportedChild(child.Item))
            {
                Issue(issues, "unsupported_child_type", path + ".item.@type", "Symbol children must use a supported schematic pin, field or graphics Any type.");
                continue;
            }
            string objectId = ChildId(child.Item);
            if (!string.IsNullOrEmpty(objectId))
            {
                if (!Guid.TryParseExact(objectId, "D", out var guid) || guid == Guid.Empty || objectId != guid.ToString("D"))
                    Issue(issues, "invalid_child_id", path + ".item.id", "Graphic and pin children require canonical nonempty UUID identities.");
                else if (!ids.Add(objectId))
                    Issue(issues, "duplicate_child_id", path + ".item.id", "Child UUIDs must be distinct.");
            }
            if (child.Item.Is(SchematicPin.Descriptor))
            {
                pins++;
                var pin = child.Item.Unpack<SchematicPin>();
                if (!string.IsNullOrWhiteSpace(pin.Number)) pinNumbers.Add(pin.Number);
                ValidatePin(pin, path, pinIds, issues);
            }
            else if (child.Item.Is(SchematicField.Descriptor))
                ValidateUserField(child.Item.Unpack<SchematicField>(), path, issues);
            else if (IsGraphic(child.Item))
            {
                graphics++;
                ValidateGraphic(child.Item, path, issues);
            }
        }
        if (pins > MaximumPins)
            Issue(issues, "too_many_pins", "items", $"The definition contains {pins} pins; the limit is {MaximumPins}.");
        if (graphics > MaximumGraphics)
            Issue(issues, "too_many_graphics", "items", $"The definition contains {graphics} graphics; the limit is {MaximumGraphics}.");
        if (pins == 0)
            Issue(issues, "missing_pins", "items", "A complete symbol definition requires at least one pin child.");

        for (int i = 0; i < definition.UnitDisplayNames.Count; i++)
        {
            var name = definition.UnitDisplayNames[i];
            if (name.Unit < 1 || name.Unit > definition.UnitCount || string.IsNullOrWhiteSpace(name.Name) || name.Name.Length > 255)
                Issue(issues, "invalid_unit_display_name", $"unitDisplayNames[{i}]", "Unit display names must reference an existing unit and contain bounded text.");
        }

        ValidatePinMapMetadata(definition.PinMaps, pinNumbers, issues, token);
    }

    private static void ValidatePinMapMetadata(SymbolPinMaps? maps,
        HashSet<string> pinNumbers, List<SymbolValidationIssue> issues, CancellationToken token)
    {
        if (maps is null) return;
            if (maps.PinMaps.Count > MaximumMetadataEntries)
                Issue(issues, "too_many_pin_maps", "pinMaps.pinMaps", $"A symbol may contain at most {MaximumMetadataEntries} named pin maps.");
            if (maps.AssociatedFootprints.Count > MaximumMetadataEntries)
                Issue(issues, "too_many_associated_footprints", "pinMaps.associatedFootprints", $"A symbol may contain at most {MaximumMetadataEntries} associated footprints.");

            var names = new HashSet<string>(StringComparer.Ordinal);
            var declaredMaps = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < maps.PinMaps.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                PinMap map = maps.PinMaps[i];
                string mapPath = $"pinMaps.pinMaps[{i}]";
                if (string.IsNullOrWhiteSpace(map.Name) || map.Name.Length > 255 || HasControl(map.Name))
                    Issue(issues, "invalid_pin_map_name", mapPath + ".name", "Pin-map names must be bounded, nonempty text without control characters.");
                else if (!names.Add(map.Name))
                    Issue(issues, "duplicate_pin_map_name", mapPath + ".name", "Pin-map names must be distinct within one symbol.");
                else
                    declaredMaps.Add(map.Name);
                if (map.Entries.Count > MaximumPinMapEntries)
                    Issue(issues, "too_many_pin_map_entries", mapPath + ".entries", $"A pin map may contain at most {MaximumPinMapEntries} entries.");
                var mapPins = new HashSet<string>(StringComparer.Ordinal);
                for (int j = 0; j < map.Entries.Count; j++)
                {
                    token.ThrowIfCancellationRequested();
                    PinMapEntry entry = map.Entries[j];
                    string entryPath = $"{mapPath}.entries[{j}]";
                    if (string.IsNullOrWhiteSpace(entry.PinNumber) || entry.PinNumber.Length > 255 || HasControl(entry.PinNumber))
                        Issue(issues, "invalid_pin_map_pin", entryPath + ".pinNumber", "Pin-map entries require bounded, nonempty pin numbers without control characters.");
                    else if (!mapPins.Add(entry.PinNumber))
                        Issue(issues, "duplicate_pin_map_pin", entryPath + ".pinNumber", "A pin number may occur only once in one pin map.");
                    else if (!pinNumbers.Contains(entry.PinNumber))
                        Issue(issues, "unknown_pin_map_pin", entryPath + ".pinNumber", "Pin-map entries must reference a pin number declared by the symbol.");
                    if (string.IsNullOrWhiteSpace(entry.PadNumber) || entry.PadNumber.Length > 255 || HasControl(entry.PadNumber))
                        Issue(issues, "invalid_pin_map_pad", entryPath + ".padNumber", "Pin-map entries require bounded, nonempty pad numbers without control characters.");
                }
            }

            var associated = new HashSet<(string Library, string Entry, string Map)>();
            for (int i = 0; i < maps.AssociatedFootprints.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                AssociatedFootprint item = maps.AssociatedFootprints[i];
                string path = $"pinMaps.associatedFootprints[{i}]";
                if (item.Footprint is null || string.IsNullOrWhiteSpace(item.Footprint.LibraryNickname)
                    || string.IsNullOrWhiteSpace(item.Footprint.EntryName) || item.Footprint.LibraryNickname.Length > 255
                    || item.Footprint.EntryName.Length > 255 || item.Footprint.LibraryNickname.Contains(':')
                    || item.Footprint.EntryName.Contains(':') || HasControl(item.Footprint.LibraryNickname)
                    || HasControl(item.Footprint.EntryName))
                    Issue(issues, "invalid_associated_footprint", path + ".footprint", "Associated footprints require an explicit bounded library and entry identity without separators or control characters.");
                string mapName = item.MapName;
                if (mapName.Length != 0 && !declaredMaps.Contains(mapName))
                    Issue(issues, "unknown_associated_pin_map", path + ".mapName", "An associated footprint mapName must reference a declared pin map.");
                var key = (item.Footprint?.LibraryNickname ?? "", item.Footprint?.EntryName ?? "", mapName);
                if (!associated.Add(key))
                    Issue(issues, "duplicate_associated_footprint", path, "Associated footprint/library-map pairs must be distinct.");
            }
    }

    private static void ValidatePin(SchematicPin pin, string path,
        HashSet<string> pinIds, List<SymbolValidationIssue> issues)
    {
        if (pin.Id is null || !Guid.TryParseExact(pin.Id.Value, "D", out var id) || id == Guid.Empty || pin.Id.Value != id.ToString("D"))
            Issue(issues, "invalid_pin_id", path + ".item.id", "Pins require canonical nonempty UUID identities.");
        else if (!pinIds.Add(pin.Id.Value))
            Issue(issues, "duplicate_pin_id", path + ".item.id", "Pin UUIDs must be distinct.");
        if (pin.LibraryPinId is not null || pin.HasActiveAlternate)
            Issue(issues, "instance_only_pin_state", path + ".item",
                "A library definition owns its pin UUIDs and alternate functions; placed-pin links and active alternate selections belong to instances.");
        if (string.IsNullOrWhiteSpace(pin.Name) || pin.Name.Length > 255 || HasControl(pin.Name) || pin.Name.Contains(' '))
            Issue(issues, "invalid_pin_name", path + ".item.name", "Pins require bounded names without spaces or control characters.");
        if (string.IsNullOrWhiteSpace(pin.Number) || pin.Number.Length > 255 || HasControl(pin.Number) || pin.Number.Contains(' '))
            Issue(issues, "invalid_pin_number", path + ".item.number", "Pins require bounded numbers without spaces or control characters.");
        // A pin number identifies a logical contact, not a unique drawing
        // object. Native KiCad supports repeated occurrences across units and
        // styles, stacked pins and multiple locations in the same unit/style.
        if (pin.Length is null || pin.Length.ValueNm <= 0)
            Issue(issues, "invalid_pin_length", path + ".item.length.valueNm", "Pin length must be a positive distance.");
        if (pin.Position is null)
            Issue(issues, "missing_pin_position", path + ".item.position", "Pins require an explicit local position.");
        if (pin.Orientation is SchematicPinOrientation.SpoUnknown or < SchematicPinOrientation.SpoRight or > SchematicPinOrientation.SpoDown)
            Issue(issues, "invalid_pin_orientation", path + ".item.orientation", "Pin orientation must be Right, Left, Up or Down.");
        var alternateNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < pin.Alternates.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(pin.Alternates[i].Name) || pin.Alternates[i].Name.Length > 255)
                Issue(issues, "invalid_pin_alternate", $"{path}.item.alternates[{i}]", "Alternate pin names must be bounded and nonempty.");
            else if (!alternateNames.Add(pin.Alternates[i].Name))
                Issue(issues, "duplicate_pin_alternate", $"{path}.item.alternates[{i}].name",
                    "Alternate function names must be distinct within each pin; the native map would otherwise discard a declaration.");
        }
    }

    private static void ValidateStandardField(SchematicField? field, string expectedName, string path, List<SymbolValidationIssue> issues)
    {
        if (field is null)
        {
            Issue(issues, "missing_standard_field", path, $"The complete symbol requires the {expectedName} field.");
            return;
        }
        if (field.Name != expectedName)
            Issue(issues, "invalid_standard_field_name", path + ".name", $"The standard field must be named {expectedName}.");
        ValidateUserField(field, path, issues, checkName: false);
    }

    private static void ValidateUserField(SchematicField field, string path, List<SymbolValidationIssue> issues, bool checkName = true)
    {
        if (checkName && (string.IsNullOrWhiteSpace(field.Name) || field.Name.Length > 255 || HasControl(field.Name)))
            Issue(issues, "invalid_field_name", path + ".item.name", "Field names must be bounded and nonempty.");
        if (field.Text is null || field.Text.Text_.Length > MaximumTextLength)
            Issue(issues, "field_text_too_large", path + ".item.text.text", $"Field text must be at most {MaximumTextLength} characters.");
        if (field.CustomProperties.Count > 256)
            Issue(issues, "too_many_field_properties", path + ".item.customProperties", "A field may contain at most 256 custom properties.");
    }

    private static void ValidateGraphic(Any item, string path, List<SymbolValidationIssue> issues)
    {
        if (item.Is(SchematicLine.Descriptor) && item.Unpack<SchematicLine>().Type != SchematicLineType.SltGraphic)
            Issue(issues, "invalid_symbol_line_type", path + ".item.type", "Symbol line children must be graphic lines, not wires or buses.");
        if (item.Is(SchematicGraphicShape.Descriptor) && item.Unpack<SchematicGraphicShape>().Shape is null)
            Issue(issues, "missing_graphic_shape", path + ".item.shape", "Graphic-shape children require a geometry payload.");
        if (item.Is(SchematicText.Descriptor) && item.Unpack<SchematicText>().Text is null)
            Issue(issues, "missing_graphic_text", path + ".item.text", "Text children require a text payload.");
        if (item.Is(SchematicTextBox.Descriptor) && item.Unpack<SchematicTextBox>().Textbox is null)
            Issue(issues, "missing_graphic_textbox", path + ".item.textbox", "Text-box children require a text-box payload.");
    }

    private static bool IsSupportedChild(Any item) => item.Is(SchematicPin.Descriptor) || item.Is(SchematicField.Descriptor)
        || item.Is(SchematicLine.Descriptor) || item.Is(SchematicText.Descriptor) || item.Is(SchematicTextBox.Descriptor)
        || item.Is(SchematicGraphicShape.Descriptor) || item.Is(SchematicImage.Descriptor);

    private static bool IsGraphic(Any? item) => item is not null && (item.Is(SchematicLine.Descriptor)
        || item.Is(SchematicText.Descriptor) || item.Is(SchematicTextBox.Descriptor)
        || item.Is(SchematicGraphicShape.Descriptor) || item.Is(SchematicImage.Descriptor));

    private static string ChildId(Any item) => item.Is(SchematicPin.Descriptor) ? item.Unpack<SchematicPin>().Id?.Value ?? ""
        : item.Is(SchematicLine.Descriptor) ? item.Unpack<SchematicLine>().Id?.Value ?? ""
        : item.Is(SchematicText.Descriptor) ? item.Unpack<SchematicText>().Id?.Value ?? ""
        : item.Is(SchematicTextBox.Descriptor) ? item.Unpack<SchematicTextBox>().Id?.Value ?? ""
        : item.Is(SchematicGraphicShape.Descriptor) ? item.Unpack<SchematicGraphicShape>().Id?.Value ?? ""
        : item.Is(SchematicImage.Descriptor) ? item.Unpack<SchematicImage>().Id?.Value ?? "" : "";

    private static int CountFields(SchematicSymbol definition) =>
        (definition.ReferenceField is null ? 0 : 1) + (definition.ValueField is null ? 0 : 1)
        + (definition.FootprintField is null ? 0 : 1) + (definition.DatasheetField is null ? 0 : 1)
        + (definition.DescriptionField is null ? 0 : 1)
        + definition.Items.Count(child => child.Item?.Is(SchematicField.Descriptor) == true);

    private static void Issue(List<SymbolValidationIssue> issues, string code, string path, string message) =>
        issues.Add(new(code, path, message));

    private static bool HasControl(string value) => value.Any(char.IsControl);
    private static DocumentSpecifier ParseDocument(string json)
    {
        try { return SchematicJson.Parser.Parse<DocumentSpecifier>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException("invalid_library_document", "documentJson is not valid DocumentSpecifier protobuf JSON: " + error.Message); }
    }

    private static void ValidateLibraryIdentity(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 || value.Contains(':') || HasControl(value))
            throw new AutomationException("invalid_symbol_library", "libraryNickname must be a bounded name without separators or control characters.");
    }

    private sealed record SymbolLibraryWriteReadback(bool Verified, string? ContentSha256, ulong? ModifyHash);
    private sealed record SymbolLibraryDeleteReadback(bool Verified, ulong? ModifyHash);
    private sealed record SymbolValidationIssue(string Code, string Path, string Message);

    private static void ValidateEpoch(string value) { if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty) throw new AutomationException("invalid_process_epoch", "Provide a canonical process epoch."); }
    private static void ValidateOperation(string value) { if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty) throw new AutomationException("invalid_operation_id", "Provide a canonical operation UUID."); }
    private static void ValidateLibrary(string value) { if (string.IsNullOrWhiteSpace(value) || value.Contains('\0')) throw new AutomationException("invalid_symbol_library", "Provide a nonempty library nickname."); }
    private static void ValidateName(string value, string name) { if (string.IsNullOrWhiteSpace(value) || value.Contains('\0')) throw new AutomationException("invalid_symbol_name", name + " must be nonempty."); }

    private static CallToolResult Data(object value, bool error = false)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(value);
        return new() { IsError = error, StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> operation)
    {
        try { return await operation(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException or InvalidJsonException or IOException)
        {
            string code = error is AutomationException known ? known.Code : "symbol_library_failed";
            JsonElement structured = JsonSerializer.SerializeToElement(new { errorCode = code, errorMessage = error.Message });
            return new() { IsError = true, StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }
}

using System.ComponentModel;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Task-oriented access to the native footprint editor's library/document commands.
///
/// The footprint editor currently has the legacy editor command handlers but does not
/// participate in <see cref="DocumentLifecycleState"/>. These wrappers therefore pin
/// every call to the process epoch and an explicit footprint identity. Save, copy, revert
/// and library delete retain native process receipts while KiCad remains alive; the MCP
/// replay cache is an additional same-server guard, and a fresh MCP process can reconcile
/// the native receipt through kicad_footprint_receipt. A lost reply remains indeterminate
/// until that receipt or the editor state is inspected. This is deliberately kept separate
/// from the checked schematic/PCB lifecycle tools until the native protocol grows a footprint
/// lifecycle state.
/// </summary>
[McpServerToolType]
public sealed class FootprintLibraryTools(InstanceRegistry registry)
{
    private static readonly SemaphoreSlim MutationGate = new(1, 1);
    private static readonly Dictionary<string, StoredReceipt> Receipts = new(StringComparer.Ordinal);

    [McpServerTool(Name = "kicad_footprint_library_list", ReadOnly = true),
     Description("List footprint names in an explicitly named native footprint library. expectedProcessEpoch pins the read to one KiCad process; the response includes the native library timestamp and writable status. This is an observation and does not claim a durable library revision sequence."),
     KiCadCapability("footprint-library", "compiled-mcp plus native-footprint-api", "native footprint library enumeration, timestamp and writable status"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> ListLibrary(string instanceId, string expectedProcessEpoch, string libraryNickname,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        ValidateEpoch(expectedProcessEpoch); ValidateLibraryId(libraryNickname, "entry");
        NativeContext context = await Admit(instanceId, expectedProcessEpoch, cancellationToken);
        NativeCapabilityFootprintLibraryReadNamesResponse response = await context.Client.InvokeAsync<
            NativeCapabilityFootprintLibraryReadNames, NativeCapabilityFootprintLibraryReadNamesResponse>(
                new() { LibraryNickname = libraryNickname, BestEfforts = true }, cancellationToken);
        return Data(new
        {
            instanceId, processEpoch = context.Session.Epoch, libraryNickname,
            footprintNames = response.FootprintNames.ToArray(), modifyHash = response.ModifyHash,
            nativeQuery = true, revisionTracked = false
        });
    });

    [McpServerTool(Name = "kicad_footprint_library_delete"),
     Description("Delete one explicitly named footprint from a native footprint library. operationId binds exact replay and the native editor retains a receipt while alive; callers should inspect kicad_footprint_receipt after a lost reply. This operation is irreversible in the library unless the footprint is restored through a separate save-copy/write workflow."),
     KiCadCapability("footprint-library", "compiled-mcp plus native-footprint-api", "explicit library/name, native delete, operation UUID and native receipt"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> DeleteLibrary(string instanceId, string expectedProcessEpoch,
        string libraryNickname, string footprintName, string operationId, CancellationToken cancellationToken,
    string expectedModifyHash = "") =>
        Execute(() => WithReceipt(operationId, Fingerprint(instanceId, "library-delete", expectedProcessEpoch,
            libraryNickname + "\n" + footprintName + "\n" + expectedModifyHash), async () =>
        {
            ValidateOperationId(operationId); ValidateEpoch(expectedProcessEpoch);
            ValidateLibraryId(libraryNickname, footprintName);
            NativeContext context = await Admit(instanceId, expectedProcessEpoch, cancellationToken);
            ulong? expectedHash = string.IsNullOrWhiteSpace(expectedModifyHash) ? null
                : ulong.TryParse(expectedModifyHash, out ulong parsed) ? parsed
                : throw new AutomationException("invalid_footprint_library_revision", "expectedModifyHash must be an unsigned decimal modify hash.");
            NativeCapabilityFootprintLibraryDelete deleteRequest = new()
            { LibraryNickname = libraryNickname, FootprintName = footprintName, OperationId = operationId };
            if (expectedHash.HasValue) deleteRequest.ExpectedModifyHash = expectedHash.Value;
            NativeCapabilityFootprintLibraryDeleteResponse response = await context.Client.InvokeAsync<
                NativeCapabilityFootprintLibraryDelete, NativeCapabilityFootprintLibraryDeleteResponse>(
                    deleteRequest, cancellationToken);
            NativeCapabilityFootprintLibraryReadOperationResponse receipt = await ReadNativeLibraryReceipt(context, operationId, cancellationToken);
            FootprintLibraryContract.ValidateDeleteResponse(response, operationId, libraryNickname, footprintName);
            FootprintLibraryContract.ValidateLibraryReceipt(receipt, operationId, libraryNickname, footprintName);
            LibraryReadback readback = await VerifyDeletedLibraryEntry(context, libraryNickname, footprintName,
                response.AfterModifyHash, operationId, "library-delete", cancellationToken);
            return Data(new
            {
                instanceId, processEpoch = context.Session.Epoch, operationId, libraryNickname, footprintName,
                operation = "library-delete", operationOutcome = "completed",
                nativeCommandSucceeded = true, operationReceipt = true,
                nativeReceiptAvailable = receipt.Found, receiptScope = receipt.Found ? "native-process" : "mcp-process",
                nativeReceipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(receipt)),
                beforeModifyHash = response.BeforeModifyHash, afterModifyHash = response.AfterModifyHash,
                expectedModifyHash = string.IsNullOrWhiteSpace(expectedModifyHash) ? null : expectedModifyHash,
                revisionAdmission = expectedHash.HasValue ? "modify-hash-matched" : "not-requested", revisionTracked = false,
                libraryEntryAbsent = true, persistenceVerified = true,
                persistedModifyHash = readback.ModifyHash, persistenceSource = "native-library-list-readback",
                transportRetrySafe = false
            });
        }));

    [McpServerTool(Name = "kicad_footprint_open_library_item"),
     Description("Open one explicitly named footprint library item in the attached native footprint editor. libraryNickname and entryName identify the item; expectedProcessEpoch pins the request to the attached KiCad process; operationId is a caller-owned correlation UUID. The native command has no revision snapshot or retained receipt, so a lost reply is indeterminate: inspect kicad_footprint_open_documents before retrying. This does not edit the library or claim that the item was persisted."),
     KiCadCapability("footprint-library", "compiled-mcp plus native-footprint-api", "explicit library item, process epoch, operation ID; native revision/receipt unavailable"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> OpenLibraryItem(string instanceId, string expectedProcessEpoch,
        string libraryNickname, string entryName, string operationId, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            ValidateOperationId(operationId);
            ValidateEpoch(expectedProcessEpoch);
            ValidateLibraryId(libraryNickname, entryName);
            NativeContext context = await Admit(instanceId, expectedProcessEpoch, cancellationToken);
            var request = new OpenLibraryItem
            {
                Type = DocumentType.DoctypeFootprint,
                Identifier = new LibraryIdentifier { LibraryNickname = libraryNickname, EntryName = entryName }
            };
            try
            {
                await context.Client.InvokeAsync<OpenLibraryItem, Empty>(request, cancellationToken);
            }
            catch (NngException error)
            {
                throw MutationTransport(error, "footprint open", operationId);
            }

            GetOpenDocumentsResponse documents;
            try
            {
                documents = await context.Client.InvokeAsync<GetOpenDocuments, GetOpenDocumentsResponse>(
                    new() { Type = DocumentType.DoctypeFootprint }, cancellationToken);
            }
            catch (NngException error)
            {
                throw MutationTransport(error, "footprint open verification", operationId);
            }

            DocumentSpecifier? opened = documents.Documents.SingleOrDefault(document =>
                document.Type == DocumentType.DoctypeFootprint
                && document.LibId is not null
                && document.LibId.LibraryNickname == libraryNickname
                && document.LibId.EntryName == entryName);
            if (opened is null)
                throw new AutomationException("footprint_open_not_verified",
                    $"KiCad accepted footprint open operation {operationId}, but the requested {libraryNickname}:{entryName} was not present in the follow-up open-document query. Inspect the editor before retrying.");

            string snapshotSha256 = await SnapshotHash(context, opened, cancellationToken);
            return Data(new
            {
                instanceId, processEpoch = context.Session.Epoch, operationId,
                operation = "open-library-item", operationOutcome = "completed",
                operationReceipt = false, nativeReceiptAvailable = false,
                revisionTracked = false, transportRetrySafe = false,
                document = opened, libraryNickname, entryName,
                snapshotSha256, snapshotSource = "native-footprint-items",
                nativeVerification = "open-document-query"
            });
        });

    [McpServerTool(Name = "kicad_footprint_open_documents", ReadOnly = true),
     Description("List the footprint documents reported open by one attached native footprint editor. expectedProcessEpoch pins the read to a verified KiCad process. This is an observation only; the legacy footprint handler does not expose a revision token, and an empty or incomplete descriptor means no usable footprint document is available."),
     KiCadCapability("footprint-library", "compiled-mcp plus native-footprint-api", "process epoch; native footprint document identity, no revision token"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> OpenDocuments(string instanceId, string expectedProcessEpoch,
        CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            ValidateEpoch(expectedProcessEpoch);
            NativeContext context = await Admit(instanceId, expectedProcessEpoch, cancellationToken);
            GetOpenDocumentsResponse response;
            try
            {
                response = await context.Client.InvokeAsync<GetOpenDocuments, GetOpenDocumentsResponse>(
                    new() { Type = DocumentType.DoctypeFootprint }, cancellationToken);
            }
            catch (NngException error)
            {
                throw ReadTransport(error, "footprint open-document query");
            }

            string responseJson = SchematicJson.Formatter.Format(response);
            using JsonDocument parsed = JsonDocument.Parse(responseJson);
            string? snapshotSha256 = response.Documents.Count == 1 && IsUsableFootprintDocument(response.Documents[0])
                ? await SnapshotHash(context, response.Documents[0], cancellationToken) : null;
            return Data(new
            {
                instanceId, processEpoch = context.Session.Epoch,
                query = "open-footprint-documents", nativeQuery = true,
                revisionTracked = false, response = parsed.RootElement.Clone(),
                usableDocumentCount = response.Documents.Count(IsUsableFootprintDocument),
                snapshotSha256, snapshotSource = snapshotSha256 is null ? null : "native-footprint-items",
                nativeRevisionContract = "unavailable"
            });
        });

    [McpServerTool(Name = "kicad_footprint_definition_read", ReadOnly = true),
     Description("Read one complete footprint definition from the native library or the currently loaded editor. source is persisted_library or editor. The persisted path forces a native library refresh even when size and modification time are unchanged. An editor read is a detached projection; canonicalTargetJson may rename that projection without editing the target library. The response includes the complete nested Footprint, canonical writer hash, provider identity hash, file baseline and explicit limitations; incomplete provider coverage remains incomplete."),
     KiCadCapability("footprint-library", "compiled-mcp plus native-footprint-api", "forced-refresh complete footprint definition readback and detached editor projection"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "NativeFootprintLibraryJourney.FootprintLibraryListDeleteUsesLiveEditorAndNativeReceipts")]
    public Task<CallToolResult> ReadDefinition(string instanceId, string expectedProcessEpoch,
        string documentJson, string source, CancellationToken cancellationToken, string canonicalTargetJson = "") =>
        Execute(async () =>
        {
            ValidateEpoch(expectedProcessEpoch);
            DocumentSpecifier document = ParseFootprintDocument(documentJson);
            NativeFootprintDefinitionSource sourceValue = ParseDefinitionSource(source);
            LibraryIdentifier? canonicalTarget = ParseCanonicalTarget(canonicalTargetJson, sourceValue);
            NativeContext context = await Admit(instanceId, expectedProcessEpoch, cancellationToken);
            NativeCapabilityFootprintReadDefinitionResponse response = await ReadNativeDefinition(
                context, document, sourceValue, canonicalTarget, cancellationToken);
            FootprintLibraryContract.FootprintDefinitionCoverage coverage =
                FootprintLibraryContract.ValidateDefinitionResponse(response, document,
                    context.Session.Epoch, sourceValue, canonicalTarget);
            return Data(new
            {
                instanceId, processEpoch = context.Session.Epoch, document, source,
                nativeQuery = true, revisionTracked = false,
                definitionComplete = response.DefinitionComplete,
                canonicalSha256 = response.CanonicalSha256,
                providerSha256 = response.ProviderSha256,
                footprint = JsonSerializer.Deserialize<JsonElement>(BoardJson.Formatter.Format(response.Footprint)),
                fileBaseline = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(response.FileBaseline)),
                limitations = response.Limitations.ToArray(),
                canonicalTarget = response.CanonicalTarget,
                definitionCoverage = coverage
            });
        });

    [McpServerTool(Name = "kicad_footprint_definition_preview", ReadOnly = true),
     Description("Preview the complete definition currently loaded in the native footprint editor without navigating, selecting or mutating it. canonicalTargetJson optionally names the detached library identity used for save-copy planning. This is an editor projection and carries the same canonical writer hash and completeness limits as kicad_footprint_definition_read with source editor."),
     KiCadCapability("footprint-library", "compiled-mcp plus native-footprint-api", "detached complete editor definition projection"),
     KiCadVerification(KiCadVerificationLevel.InProcess, "CapabilityCatalogTests.CompiledServerAdvertisesExactlyItsRegisteredTools")]
    public Task<CallToolResult> PreviewDefinition(string instanceId, string expectedProcessEpoch,
        string documentJson, CancellationToken cancellationToken, string canonicalTargetJson = "") =>
        ReadDefinition(instanceId, expectedProcessEpoch, documentJson, "editor", cancellationToken, canonicalTargetJson);

    [McpServerTool(Name = "kicad_footprint_receipt", ReadOnly = true),
     Description("Read a footprint mutation receipt after a lost reply. The tool first checks the MCP replay cache, then asks the attached native footprint editor for its process-owned save, save-copy, revert or library-delete receipt, so a new MCP process can reconcile an operation while KiCad remains alive; it never resubmits the mutation."),
     KiCadCapability("footprint-library", "compiled-mcp plus native-footprint-api", "operation UUID and process-owned footprint mutation receipt"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Receipt(string instanceId, string expectedProcessEpoch, string operationId,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        ValidateOperationId(operationId);
        ValidateEpoch(expectedProcessEpoch);
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new AutomationException("invalid_instance_id", "instanceId is required to inspect a footprint receipt.");
        await MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (Receipts.TryGetValue(operationId, out StoredReceipt? stored))
            {
                JsonElement payload = stored.Result.StructuredContent ?? JsonSerializer.SerializeToElement(new { });
                string? processEpoch = payload.TryGetProperty("processEpoch", out JsonElement epoch) ? epoch.GetString() : null;
                if (!string.Equals(processEpoch, expectedProcessEpoch, StringComparison.Ordinal))
                    throw new AutomationException("stale_process_epoch", "The footprint receipt belongs to a different native process epoch.");
                string? receiptInstance = payload.TryGetProperty("instanceId", out JsonElement instance) ? instance.GetString() : null;
                if (!string.Equals(receiptInstance, instanceId, StringComparison.Ordinal))
                    throw new AutomationException("wrong_instance_receipt", "The footprint receipt belongs to a different MCP instance.");
                return Data(new { instanceId, processEpoch, operationId, found = true,
                    receiptScope = "mcp-process", nativeReceiptAvailable = payload.TryGetProperty("nativeReceiptAvailable", out var available) && available.GetBoolean(), receipt = payload });
            }

            NativeContext context;
            try { context = await Admit(instanceId, expectedProcessEpoch, cancellationToken); }
            catch (AutomationException error) when (error.Code == "unknown_instance")
            {
                // Keep receipt reads useful for callers that only have the MCP
                // operation identity; an unattached instance has no native
                // reconciliation source, so report an honest miss.
                return Data(new { instanceId, processEpoch = expectedProcessEpoch, operationId, found = false,
                    receiptScope = "native-process", nativeReceiptAvailable = false });
            }
            NativeCapabilityFootprintReadOperationResponse nativeReceipt = await ReadNativeReceipt(context, operationId, cancellationToken);
            NativeCapabilityFootprintLibraryReadOperationResponse libraryReceipt = await ReadNativeLibraryReceipt(context, operationId, cancellationToken);
            FootprintLibraryContract.ValidateEditorReceiptIdentity(nativeReceipt, operationId);
            FootprintLibraryContract.ValidateLibraryReceiptIdentity(libraryReceipt, operationId);
            bool nativeReceiptAvailable = FootprintLibraryContract.HasNativeReceipt(nativeReceipt, libraryReceipt);
            if (nativeReceiptAvailable)
                return Data(new
                {
                    instanceId, processEpoch = context.Session.Epoch, operationId, found = true,
                    receiptScope = "native-process", nativeReceiptAvailable,
                    nativeReceipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(nativeReceipt)),
                    nativeLibraryReceipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(libraryReceipt))
                });

            return Data(new { instanceId, processEpoch = context.Session.Epoch, operationId, found = false,
                receiptScope = "native-process", nativeReceiptAvailable,
                nativeReceipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(nativeReceipt)),
                nativeLibraryReceipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(libraryReceipt)) });
        }
        finally { MutationGate.Release(); }
    });

    [McpServerTool(Name = "kicad_footprint_save"),
     Description("Save the explicitly identified footprint currently loaded in the attached native footprint editor. Before mutation this captures a complete detached editor definition; after native save it forces a persisted library read and compares the full native-writer canonical hash and file baseline. Optional expectedModifyHash rejects a changed library before the native save and is bound to operation replay. A successful reply therefore proves complete definition persistence for this entry. operationId and the existing native/MCP receipts retain the lost-reply reconciliation behavior."),
     KiCadCapability("footprint-library", "compiled-mcp plus native-footprint-api", "explicit footprint document, process epoch, operation ID, optional native library modify hash and native process receipt; no durable lifecycle sequence"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Save(string instanceId, string expectedProcessEpoch, string documentJson,
        string operationId, CancellationToken cancellationToken, string expectedSnapshotSha256 = "",
        string expectedModifyHash = "") =>
        Execute(() => WithReceipt(operationId, Fingerprint(instanceId, "save", expectedProcessEpoch, documentJson + "\n" + expectedSnapshotSha256 + "\n" + expectedModifyHash), async () =>
        {
            ValidateOperationId(operationId);
            ValidateEpoch(expectedProcessEpoch);
            DocumentSpecifier document = ParseFootprintDocument(documentJson);
            NativeContext context = await Admit(instanceId, expectedProcessEpoch, cancellationToken);
            ulong? expectedLibraryHash = ParseExpectedModifyHash(expectedModifyHash);
            ulong beforeModifyHash = await EnsureLibraryModifyHash(context, document.LibId.LibraryNickname,
                expectedLibraryHash, cancellationToken);
            await EnsureSnapshot(context, document, expectedSnapshotSha256, cancellationToken);
            NativeCapabilityFootprintReadDefinitionResponse expectedDefinition = await ReadNativeDefinition(
                context, document, (NativeFootprintDefinitionSource)2, document.LibId, cancellationToken);
            FootprintLibraryContract.ValidateDefinitionResponse(expectedDefinition, document,
                context.Session.Epoch, (NativeFootprintDefinitionSource)2, document.LibId);
            RequireCompleteDefinition(expectedDefinition, "footprint save");
            try
            {
                await context.Client.InvokeAsync<SaveDocument, Empty>(new() { Document = document, OperationId = operationId }, cancellationToken);
            }
            catch (NngException error)
            {
                throw MutationTransport(error, "footprint save", operationId);
            }

            NativeCapabilityFootprintReadOperationResponse nativeReceipt = await ReadNativeReceipt(context, operationId, cancellationToken);
            FootprintLibraryContract.ValidateEditorReceipt(nativeReceipt, operationId, "save", document);
            string snapshotSha256 = await SnapshotHash(context, document, cancellationToken);
            NativeCapabilityFootprintReadDefinitionResponse persistedDefinition = await ReadPersistedDefinitionForVerification(
                context, document, operationId, "footprint save", cancellationToken);
            FootprintLibraryContract.FootprintDefinitionCoverage persistedCoverage =
                VerifyCanonicalPersistence(expectedDefinition, persistedDefinition, document, document,
                    context.Session.Epoch, "footprint save", operationId, document.LibId);
            LibraryReadback readback = await VerifyLibraryEntry(context, document.LibId.LibraryNickname,
                document.LibId.EntryName, operationId, "footprint save", cancellationToken);
            return Data(new
            {
                instanceId, processEpoch = context.Session.Epoch, operationId,
                operation = "save", document, operationOutcome = "completed",
                operationReceipt = true, nativeReceiptAvailable = nativeReceipt.Found,
                revisionTracked = false, nativeCommandSucceeded = true,
                libraryEntryObserved = true, geometryPersistenceVerified = true,
                persistenceVerified = true, persistenceSource = "native-forced-refresh-definition-readback",
                canonicalSha256 = persistedDefinition.CanonicalSha256,
                providerSha256 = persistedDefinition.ProviderSha256,
                definitionComplete = persistedDefinition.DefinitionComplete,
                definitionCoverage = persistedCoverage,
                fileBaseline = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(persistedDefinition.FileBaseline)),
                persistedLibraryNickname = document.LibId.LibraryNickname,
                persistedFootprintName = document.LibId.EntryName,
                beforeModifyHash,
                persistedModifyHash = readback.ModifyHash,
                expectedModifyHash = string.IsNullOrWhiteSpace(expectedModifyHash) ? null : expectedModifyHash,
                revisionAdmission = expectedLibraryHash.HasValue ? "modify-hash-matched" : "not-requested",
                transportRetrySafe = false,
                replayableWithinProcess = true, receiptScope = nativeReceipt.Found ? "native-process" : "mcp-process",
                nativeReceipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(nativeReceipt)),
                snapshotSha256, snapshotSource = "native-footprint-items", snapshotAdmission = string.IsNullOrWhiteSpace(expectedSnapshotSha256) ? "not-requested" : "matched",
                verification = "native-success-plus-forced-refresh-complete-definition-readback"
            });
        }));

    [McpServerTool(Name = "kicad_footprint_save_copy"),
     Description("Save a detached copy of the explicitly identified footprint to a library entry without opening the copy. Before mutation this captures the complete editor definition under the intended destination identity and, when expectedModifyHash is supplied, rejects a changed destination library before the native save-copy. The expected hash is bound to operation replay. After native save it forces a persisted read of that destination and compares the full native-writer canonical hash and file baseline. A successful reply proves complete save-copy persistence; operationId and the existing receipts retain lost-reply reconciliation."),
     KiCadCapability("footprint-library", "compiled-mcp plus native-footprint-api", "explicit footprint document, process epoch, operation ID, optional native destination-library modify hash and native process receipt; no lifecycle revision token"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> SaveCopy(string instanceId, string expectedProcessEpoch, string documentJson,
        string path, string operationId, CancellationToken cancellationToken, bool overwrite = false,
        bool includeProject = false, string expectedModifyHash = "") =>
        Execute(() => WithReceipt(operationId, Fingerprint(instanceId, "save-copy", expectedProcessEpoch,
            documentJson + "\n" + path + "\n" + overwrite + "\n" + includeProject + "\n" + expectedModifyHash), async () =>
        {
            ValidateOperationId(operationId);
            ValidateEpoch(expectedProcessEpoch);
            DocumentSpecifier document = ParseFootprintDocument(documentJson);
            ValidateCopyPath(path);
            (string targetLibrary, string targetName) = ResolveCopyTarget(document, path);
            ulong? expectedLibraryHash = ParseExpectedModifyHash(expectedModifyHash);
            NativeContext context = await Admit(instanceId, expectedProcessEpoch, cancellationToken);
            ulong beforeModifyHash = await EnsureLibraryModifyHash(context, targetLibrary,
                expectedLibraryHash, cancellationToken);
            LibraryIdentifier canonicalTarget = new() { LibraryNickname = targetLibrary, EntryName = targetName };
            NativeCapabilityFootprintReadDefinitionResponse expectedDefinition = await ReadNativeDefinition(
                context, document, (NativeFootprintDefinitionSource)2, canonicalTarget, cancellationToken);
            FootprintLibraryContract.ValidateDefinitionResponse(expectedDefinition, document,
                context.Session.Epoch, (NativeFootprintDefinitionSource)2, canonicalTarget);
            RequireCompleteDefinition(expectedDefinition, "footprint save-copy");
            try
            {
                await context.Client.InvokeAsync<SaveCopyOfDocument, Empty>(new()
                {
                    Document = document,
                    Path = path,
                    Options = new SaveOptions { Overwrite = overwrite, IncludeProject = includeProject }, OperationId = operationId
                }, cancellationToken);
            }
            catch (NngException error)
            {
                throw MutationTransport(error, "footprint save-copy", operationId);
            }

            NativeCapabilityFootprintReadOperationResponse nativeReceipt = await ReadNativeReceipt(context, operationId, cancellationToken);
            FootprintLibraryContract.ValidateEditorReceipt(nativeReceipt, operationId, "save-copy", document);
            string snapshotSha256 = await SnapshotHash(context, document, cancellationToken);
            DocumentSpecifier persistedDocument = document.Clone();
            persistedDocument.LibId = canonicalTarget;
            NativeCapabilityFootprintReadDefinitionResponse persistedDefinition = await ReadPersistedDefinitionForVerification(
                context, persistedDocument, operationId, "footprint save-copy", cancellationToken);
            FootprintLibraryContract.FootprintDefinitionCoverage persistedCoverage =
                VerifyCanonicalPersistence(expectedDefinition, persistedDefinition, document, persistedDocument,
                    context.Session.Epoch, "footprint save-copy", operationId, canonicalTarget);
            LibraryReadback readback = await VerifyLibraryEntry(context, targetLibrary, targetName, operationId,
                "footprint save-copy", cancellationToken);
            return Data(new
            {
                instanceId, processEpoch = context.Session.Epoch, operationId,
                operation = "save-copy", document, path, overwrite, includeProject,
                operationOutcome = "completed", operationReceipt = true,
                nativeReceiptAvailable = nativeReceipt.Found, revisionTracked = false,
                nativeCommandSucceeded = true, libraryEntryObserved = true,
                geometryPersistenceVerified = true, persistenceVerified = true,
                persistenceSource = "native-forced-refresh-definition-readback",
                canonicalSha256 = persistedDefinition.CanonicalSha256,
                providerSha256 = persistedDefinition.ProviderSha256,
                definitionComplete = persistedDefinition.DefinitionComplete,
                definitionCoverage = persistedCoverage,
                fileBaseline = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(persistedDefinition.FileBaseline)),
                persistedLibraryNickname = targetLibrary, persistedFootprintName = targetName,
                beforeModifyHash,
                expectedModifyHash = string.IsNullOrWhiteSpace(expectedModifyHash) ? null : expectedModifyHash,
                persistedModifyHash = readback.ModifyHash,
                revisionAdmission = expectedLibraryHash.HasValue ? "modify-hash-matched" : "not-requested",
                copyOpened = false, transportRetrySafe = false,
                replayableWithinProcess = true, receiptScope = nativeReceipt.Found ? "native-process" : "mcp-process",
                nativeReceipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(nativeReceipt)), snapshotSha256,
                verification = "native-success-plus-forced-refresh-complete-definition-readback"
            });
        }));

    [McpServerTool(Name = "kicad_footprint_library_write_checked", ReadOnly = false),
     Description("Write one complete FootprintInstance into an explicitly identified direct KiCad .pretty library entry. expectedStateJson and expectedSnapshotSha256 must come from kicad_library_snapshot_read; the native owner locks the table and every sibling .kicad_mod baseline, verifies persisted readback and retains rollback evidence. The operation UUID is exact-replay safe; unsupported providers or incomplete definitions are refused before the writer."),
     KiCadCapability("footprint-library", "checked-native-direct-footprint-file", "project-bound footprint document, process epoch, complete checked state/revision, operation UUID, definition and typed receipt"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> WriteChecked(string instanceId, string expectedProcessEpoch,
        string documentJson, string footprintJson, string operationId, string expectedSnapshotSha256,
        string expectedStateJson, CancellationToken cancellationToken, bool overwrite = true) =>
        Execute(async () =>
        {
            ValidateOperationId(operationId);
            ValidateEpoch(expectedProcessEpoch);
            if (expectedSnapshotSha256.Length != 64 || !expectedSnapshotSha256.All(char.IsAsciiHexDigitLower))
                throw new AutomationException("invalid_library_snapshot", "expectedSnapshotSha256 must be a lowercase complete snapshot digest.");
            DocumentSpecifier document = ParseFootprintDocument(documentJson);
            if (string.IsNullOrWhiteSpace(expectedStateJson))
                throw new AutomationException("invalid_library_snapshot", "expectedStateJson is required for checked footprint mutation.");
            NativeCapabilityFootprintLibraryCheckedState expectedState;
            try { expectedState = SchematicJson.Parser.Parse<NativeCapabilityFootprintLibraryCheckedState>(expectedStateJson); }
            catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
            { throw new AutomationException("invalid_library_snapshot", "expectedStateJson is not valid checked footprint-library state: " + error.Message); }
            if (expectedState.StateSha256 != expectedSnapshotSha256)
                throw new AutomationException("stale_library_state", "expectedSnapshotSha256 must match the complete checked state digest.");
            FootprintInstance footprint;
            try { footprint = BoardJson.Parser.Parse<FootprintInstance>(footprintJson); }
            catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
            { throw new AutomationException("invalid_footprint_definition", "footprintJson is not valid FootprintInstance protobuf JSON: " + error.Message); }
            if (footprint.Definition?.Id is null
                || footprint.Definition.Id.LibraryNickname != document.LibId.LibraryNickname
                || footprint.Definition.Id.EntryName != document.LibId.EntryName)
                throw new AutomationException("footprint_library_definition_mismatch", "The footprint definition identity must match the requested document.");
            IReadOnlyList<string> structuralIssues = FootprintLibraryCheckedContract.ValidateCandidate(
                footprint, cancellationToken);
            if (structuralIssues.Count != 0)
                throw new AutomationException("invalid_footprint_definition",
                    "The complete footprint definition is structurally invalid: "
                    + string.Join(", ", structuralIssues.Take(16)));

            NativeCapabilityFootprintLibraryCheckedWrite expectedRequest = new()
            {
                Document = document.Clone(), ProcessEpoch = expectedProcessEpoch, OperationId = operationId,
                ExpectedSnapshotSha256 = expectedSnapshotSha256, Footprint = footprint.Clone(),
                Overwrite = overwrite, ExpectedState = expectedState.Clone()
            };

            // Reserve the exact request before touching the native writer. The
            // reservation is durable, so a second MCP process cannot dispatch
            // an operation whose first outcome is still unknown.
            var receiptStore = new FootprintLibraryCheckedReceiptStore(registry.StateDirectory);
            var reservation = receiptStore.Reserve(instanceId, expectedRequest);
            if (!reservation.Created)
            {
                if (reservation.Receipt.Outcome == FootprintLibraryCheckedReceiptRecord.Terminal)
                {
                    NativeCapabilityFootprintLibraryCheckedWriteResponse replay =
                        FootprintLibraryCheckedReceiptStore.ParseResponse(reservation.Receipt.ResponseJson);
                    replay.Replayed = true;
                    FootprintLibraryCheckedContract.ValidateResponse(replay, expectedRequest);
                    return PublishCheckedResponse(instanceId, expectedProcessEpoch, document, operationId,
                        replay, nativeReceiptAvailable: false, durableRecovery: true);
                }

                throw new AutomationException("operation_outcome_unknown",
                    reservation.Receipt.Outcome == FootprintLibraryCheckedReceiptRecord.Unknown
                        ? "The checked footprint write was delivered previously but its native result is unresolved; inspect the native library before starting a new operation."
                        : "Another MCP process has admitted this checked footprint write; reconcile its outcome before retrying.");
            }

            NativeClient client;
            AutomationSession session;
            try
            {
                client = registry.Client(instanceId);
                session = await client.HandshakeAsync(cancellationToken);
                if (session.Epoch != expectedProcessEpoch)
                {
                    receiptStore.RemoveAdmission(instanceId, expectedRequest);
                    throw new AutomationException("stale_process_epoch", "The expected process epoch is stale; inspect the instance again.");
                }
            }
            catch
            {
                // No native mutation has been submitted before the handshake;
                // release only this still-admitted reservation so a fresh
                // epoch can make a new admission.
                try { receiptStore.RemoveAdmission(instanceId, expectedRequest); }
                catch (AutomationException) { }
                throw;
            }

            NativeCapabilityFootprintLibraryCheckedWriteResponse response;
            try
            {
                response = await client.InvokeAsync<
                    NativeCapabilityFootprintLibraryCheckedWrite, NativeCapabilityFootprintLibraryCheckedWriteResponse>(new()
                    {
                        Document = document, ProcessEpoch = session.Epoch, OperationId = operationId,
                        ExpectedSnapshotSha256 = expectedSnapshotSha256, Footprint = footprint,
                        Overwrite = overwrite, ExpectedState = expectedState
                    }, cancellationToken);
                FootprintLibraryCheckedContract.ValidateResponse(response, expectedRequest);
                receiptStore.WriteTerminal(instanceId, expectedRequest, response);
            }
            catch (NngException error)
            {
                if (error.RequestDelivered)
                {
                    receiptStore.WriteUnknown(instanceId, expectedRequest, "operation_outcome_unknown",
                        $"KiCad received checked footprint write {operationId} but did not answer; inspect the library before retrying.");
                }
                else
                {
                    receiptStore.RemoveAdmission(instanceId, expectedRequest);
                }
                throw new AutomationException(error.RequestDelivered ? "operation_outcome_unknown" : "native_not_reached",
                    error.RequestDelivered
                        ? $"KiCad received checked footprint write {operationId} but did not answer; inspect the library before retrying."
                        : $"KiCad did not receive checked footprint write {operationId}; inspect the instance before retrying.");
            }
            catch (OperationCanceledException)
            {
                receiptStore.WriteUnknown(instanceId, expectedRequest, "operation_outcome_unknown",
                    $"Checked footprint write {operationId} was cancelled after admission; inspect the library before retrying.");
                throw new AutomationException("operation_outcome_unknown",
                    $"Checked footprint write {operationId} was cancelled after admission; inspect the library before retrying.");
            }
            catch (AutomationException error)
            {
                receiptStore.WriteUnknown(instanceId, expectedRequest, "invalid_native_receipt",
                    $"The checked footprint write returned an untrusted native result: {error.Message}");
                throw;
            }

            return PublishCheckedResponse(instanceId, session.Epoch, document, operationId,
                response, nativeReceiptAvailable: true, durableRecovery: false);
        });

    [McpServerTool(Name = "kicad_footprint_library_checked_receipt", ReadOnly = true),
     Description("Read one exact process-owned checked footprint-library receipt after a lost reply or MCP reconnect. The original request, document, process epoch and operation identity must match; lookup never dispatches a writer."),
     KiCadCapability("footprint-library", "checked-native-direct-footprint-file", "exact checked request, process-owned receipt, revision and rollback evidence"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> CheckedReceipt(string instanceId, string expectedProcessEpoch,
        string documentJson, string operationId, string expectedRequestJson,
        CancellationToken cancellationToken) => Execute(async () =>
        {
            ValidateOperationId(operationId); ValidateEpoch(expectedProcessEpoch);
            DocumentSpecifier document = ParseFootprintDocument(documentJson);
            NativeCapabilityFootprintLibraryCheckedWrite expectedRequest;
            try { expectedRequest = SchematicJson.Parser.Parse<NativeCapabilityFootprintLibraryCheckedWrite>(expectedRequestJson); }
            catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
            { throw new AutomationException("invalid_library_request", "expectedRequestJson is not valid checked footprint request: " + error.Message); }
            if (!expectedRequest.Document.Equals(document) || expectedRequest.ProcessEpoch != expectedProcessEpoch
                || expectedRequest.OperationId != operationId)
                throw new AutomationException("invalid_library_request", "expectedRequestJson must retain the same document, process epoch and operation UUID.");
            var receiptStore = new FootprintLibraryCheckedReceiptStore(registry.StateDirectory);
            NativeCapabilityFootprintLibraryCheckedReadOperationResponse? nativeResponse = null;
            try
            {
                NativeClient client = registry.Client(instanceId);
                AutomationSession session = await client.HandshakeAsync(cancellationToken);
                if (session.Epoch != expectedProcessEpoch)
                    throw new AutomationException("stale_process_epoch", "The expected process epoch is stale; inspect the instance again.");
                nativeResponse = await client.InvokeAsync<NativeCapabilityFootprintLibraryCheckedReadOperation,
                    NativeCapabilityFootprintLibraryCheckedReadOperationResponse>(new()
                    {
                        Document = document, ProcessEpoch = session.Epoch, OperationId = operationId,
                        ExpectedRequest = expectedRequest
                    }, cancellationToken);
                if (nativeResponse.Found)
                {
                    FootprintLibraryCheckedContract.ValidateResponse(nativeResponse.Receipt, expectedRequest);
                    receiptStore.WriteTerminal(instanceId, expectedRequest, nativeResponse.Receipt);
                    return Data(new
                    {
                        instanceId, processEpoch = session.Epoch, operationId, found = true,
                        receipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(nativeResponse.Receipt)),
                        nativeReceiptAvailable = true, durableRecovery = false, retryDispatched = false
                    });
                }
            }
            catch (NngException) { }

            FootprintLibraryCheckedReceiptRecord? durable = receiptStore.Read(instanceId, operationId);
            if (durable is not null)
            {
                NativeCapabilityFootprintLibraryCheckedWrite storedRequest =
                    FootprintLibraryCheckedReceiptStore.ParseRequest(durable);
                if (!storedRequest.Equals(expectedRequest))
                    throw new AutomationException("operation_id_conflict", "The operation ID is already bound to another checked footprint request.");
                if (durable.Outcome == FootprintLibraryCheckedReceiptRecord.Terminal)
                {
                    NativeCapabilityFootprintLibraryCheckedWriteResponse storedResponse =
                        FootprintLibraryCheckedReceiptStore.ParseResponse(durable.ResponseJson);
                    FootprintLibraryCheckedContract.ValidateResponse(storedResponse, expectedRequest);
                    return Data(new
                    {
                        instanceId, processEpoch = expectedProcessEpoch, operationId, found = true,
                        receipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(storedResponse)),
                        nativeReceiptAvailable = false, durableRecovery = true, retryDispatched = false
                    });
                }
                throw new AutomationException("operation_outcome_unknown",
                    durable.ErrorMessage.Length == 0
                        ? "The checked footprint write has no terminal receipt; inspect the native library before retrying."
                        : durable.ErrorMessage);
            }

            return Data(new
            {
                instanceId, processEpoch = expectedProcessEpoch, operationId,
                found = nativeResponse?.Found == true,
                receipt = nativeResponse?.Found == true
                    ? JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(nativeResponse.Receipt))
                    : (JsonElement?)null,
                nativeReceiptAvailable = false, durableRecovery = false, retryDispatched = false
            });
        });

    [McpServerTool(Name = "kicad_footprint_revert"),
     Description("Revert the explicitly identified footprint in the attached native footprint editor to its library copy. documentJson must identify the open footprint; expectedProcessEpoch pins the request; operationId correlates it. The MCP process retains an exact replay receipt and KiCad retains a process-owned receipt that kicad_footprint_receipt can read after an MCP reconnect. The native command has no lifecycle revision snapshot, so a lost reply is indeterminate until reconciled. This can discard unsaved editor changes."),
     KiCadCapability("footprint-library", "compiled-mcp plus native-footprint-api", "explicit footprint document, process epoch, operation ID, native process receipt; no lifecycle revision token"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Revert(string instanceId, string expectedProcessEpoch, string documentJson,
        string operationId, CancellationToken cancellationToken, string expectedSnapshotSha256 = "") =>
        Execute(() => WithReceipt(operationId, Fingerprint(instanceId, "revert", expectedProcessEpoch, documentJson + "\n" + expectedSnapshotSha256), async () =>
        {
            ValidateOperationId(operationId);
            ValidateEpoch(expectedProcessEpoch);
            DocumentSpecifier document = ParseFootprintDocument(documentJson);
            NativeContext context = await Admit(instanceId, expectedProcessEpoch, cancellationToken);
            await EnsureSnapshot(context, document, expectedSnapshotSha256, cancellationToken);
            try
            {
                await context.Client.InvokeAsync<RevertDocument, Empty>(new() { Document = document, OperationId = operationId }, cancellationToken);
            }
            catch (NngException error)
            {
                throw MutationTransport(error, "footprint revert", operationId);
            }

            NativeCapabilityFootprintReadOperationResponse nativeReceipt = await ReadNativeReceipt(context, operationId, cancellationToken);
            FootprintLibraryContract.ValidateEditorReceipt(nativeReceipt, operationId, "revert", document);
            string snapshotSha256 = await SnapshotHash(context, document, cancellationToken);
            return Data(new
            {
                instanceId, processEpoch = context.Session.Epoch, operationId,
                operation = "revert", document, operationOutcome = "completed",
                operationReceipt = true, nativeReceiptAvailable = nativeReceipt.Found,
                revisionTracked = false, discardedUnsavedChanges = true,
                transportRetrySafe = false, replayableWithinProcess = true,
                receiptScope = nativeReceipt.Found ? "native-process" : "mcp-process", nativeReceipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(nativeReceipt)), verification = "native-success-only"
                , snapshotSha256, snapshotSource = "native-footprint-items", snapshotAdmission = string.IsNullOrWhiteSpace(expectedSnapshotSha256) ? "not-requested" : "matched"
            });
        }));

    private async Task<NativeContext> Admit(string instanceId, string expectedProcessEpoch,
        CancellationToken cancellationToken)
    {
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (!string.Equals(session.Epoch, expectedProcessEpoch, StringComparison.Ordinal))
            throw new AutomationException("stale_process_epoch",
                "The expected process epoch does not match the attached KiCad process; inspect the instance and retry with a fresh epoch.");
        return new(client, session);
    }

    private static async Task<string> SnapshotHash(NativeContext context, DocumentSpecifier document,
        CancellationToken cancellationToken)
    {
        GetItemsResponse response = await context.Client.InvokeAsync<GetItems, GetItemsResponse>(new()
        {
            Header = new() { Document = document },
            Types_ = { KiCadObjectType.KotPcbPad, KiCadObjectType.KotPcbShape }
        }, cancellationToken);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            BoardJson.Formatter.Format(response))));
    }

    private static NativeFootprintDefinitionSource ParseDefinitionSource(string source)
    {
        return source.Trim().ToLowerInvariant() switch
        {
            "persisted" or "persisted_library" or "library" => (NativeFootprintDefinitionSource)1,
            "editor" or "preview" => (NativeFootprintDefinitionSource)2,
            _ => throw new AutomationException("invalid_footprint_definition_source",
                "source must be persisted_library or editor.")
        };
    }

    private static LibraryIdentifier? ParseCanonicalTarget(string json,
        NativeFootprintDefinitionSource source)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        if ((int)source != 2)
            throw new AutomationException("invalid_footprint_canonical_target",
                "canonicalTargetJson is only valid for an editor definition projection.");
        try
        {
            LibraryIdentifier target = SchematicJson.Parser.Parse<LibraryIdentifier>(json);
            if (string.IsNullOrWhiteSpace(target.LibraryNickname) || string.IsNullOrWhiteSpace(target.EntryName))
                throw new AutomationException("invalid_footprint_canonical_target",
                    "canonicalTargetJson must contain a library nickname and entry name.");
            ValidateLibraryId(target.LibraryNickname, target.EntryName);
            return target;
        }
        catch (AutomationException) { throw; }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
        {
            throw new AutomationException("invalid_footprint_canonical_target",
                "canonicalTargetJson is not valid LibraryIdentifier protobuf JSON: " + error.Message);
        }
    }

    private static Task<NativeCapabilityFootprintReadDefinitionResponse> ReadNativeDefinition(
        NativeContext context, DocumentSpecifier document, NativeFootprintDefinitionSource source,
        LibraryIdentifier? canonicalTarget, CancellationToken cancellationToken)
    {
        NativeCapabilityFootprintReadDefinition request = new()
        {
            Document = document,
            Source = source
        };
        if (canonicalTarget is not null) request.CanonicalTarget = canonicalTarget;
        return context.Client.InvokeAsync<NativeCapabilityFootprintReadDefinition,
            NativeCapabilityFootprintReadDefinitionResponse>(request, cancellationToken);
    }

    private static void RequireCompleteDefinition(
        NativeCapabilityFootprintReadDefinitionResponse response, string operation)
        => FootprintLibraryContract.ValidateCompleteDefinition(response, operation);

    private static async Task<NativeCapabilityFootprintReadDefinitionResponse>
        ReadPersistedDefinitionForVerification(NativeContext context, DocumentSpecifier document,
            string operationId, string operation, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadNativeDefinition(context, document,
                (NativeFootprintDefinitionSource)1, null, cancellationToken);
        }
        catch (NngException error)
        {
            throw new AutomationException("footprint_persistence_unverified",
                error.RequestDelivered
                    ? $"KiCad completed {operation} operation {operationId}, but the forced persisted definition read did not answer; inspect the library before retrying."
                    : $"KiCad completed {operation} operation {operationId}, but the forced persisted definition read was not reached; inspect the library before retrying.");
        }
        catch (NativeApiException error)
        {
            throw new AutomationException("footprint_persistence_unverified",
                $"KiCad completed {operation} operation {operationId}, but the forced persisted definition read failed: {error.Message}");
        }
    }

    private static FootprintLibraryContract.FootprintDefinitionCoverage VerifyCanonicalPersistence(
        NativeCapabilityFootprintReadDefinitionResponse before,
        NativeCapabilityFootprintReadDefinitionResponse after,
        DocumentSpecifier expectedBeforeDocument,
        DocumentSpecifier expectedAfterDocument,
        string expectedProcessEpoch,
        string operation, string operationId, LibraryIdentifier expectedBeforeTarget)
    {
        FootprintLibraryContract.ValidateDefinitionReadbackIdentity(before, after,
            expectedBeforeDocument, expectedAfterDocument, expectedProcessEpoch, operation, operationId);
        FootprintLibraryContract.FootprintDefinitionCoverage beforeCoverage =
            FootprintLibraryContract.ValidateDefinitionResponse(before, expectedBeforeDocument,
                expectedProcessEpoch, (NativeFootprintDefinitionSource)2, expectedBeforeTarget);
        FootprintLibraryContract.FootprintDefinitionCoverage afterCoverage =
            FootprintLibraryContract.ValidateDefinitionResponse(after, expectedAfterDocument,
                expectedProcessEpoch, (NativeFootprintDefinitionSource)1, null);
        if (!beforeCoverage.Complete || !afterCoverage.Complete
            || !string.Equals(before.ProviderSha256, after.ProviderSha256, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(after.CanonicalSha256)
            || !string.Equals(before.CanonicalSha256, after.CanonicalSha256, StringComparison.OrdinalIgnoreCase)
            || !after.FileBaseline.BaselineKnown || !after.FileBaseline.BaselineExists
            || !after.FileBaseline.CurrentKnown || !after.FileBaseline.CurrentExists
            || after.FileBaseline.Status != NativeFileBaselineStatus.NfbsUnchanged)
        {
            string limits = after.Limitations.Count == 0 ? "the forced persisted readback did not match the complete editor definition"
                : string.Join("; ", after.Limitations);
            throw new AutomationException("footprint_persistence_unverified",
                $"KiCad completed {operation} operation {operationId}, but complete native definition readback was not verified: {limits}");
        }
        return afterCoverage.WithPersistence(persistedReadbackVerified: true, canonicalHashMatched: true);
    }

    private static async Task EnsureSnapshot(NativeContext context, DocumentSpecifier document,
        string expectedSnapshotSha256, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedSnapshotSha256)) return;
        if (expectedSnapshotSha256.Length != 64 || expectedSnapshotSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new AutomationException("invalid_footprint_snapshot", "expectedSnapshotSha256 must be a lowercase or uppercase SHA-256 digest.");
        string current = await SnapshotHash(context, document, cancellationToken);
        if (!string.Equals(current, expectedSnapshotSha256, StringComparison.OrdinalIgnoreCase))
            throw new AutomationException("footprint_snapshot_changed", "The footprint's native pad/graphic snapshot changed before this mutation.");
    }

    private static async Task<LibraryReadback> VerifyLibraryEntry(NativeContext context,
        string libraryNickname, string footprintName, string operationId, string operation,
        CancellationToken cancellationToken)
    {
        NativeCapabilityFootprintLibraryReadNamesResponse response;
        try
        {
            response = await context.Client.InvokeAsync<NativeCapabilityFootprintLibraryReadNames,
                NativeCapabilityFootprintLibraryReadNamesResponse>(new()
                {
                    LibraryNickname = libraryNickname,
                    BestEfforts = false
                }, cancellationToken);
        }
        catch (NngException error)
        {
            throw MutationTransport(error, operation + " persistence verification", operationId);
        }
        catch (NativeApiException error)
        {
            throw new AutomationException("footprint_persistence_unverified",
                $"KiCad completed {operation} operation {operationId}, but the native library re-read failed: {error.Message}");
        }

        if (!response.FootprintNames.Contains(footprintName, StringComparer.Ordinal))
            throw new AutomationException("footprint_persistence_unverified",
                $"KiCad completed {operation} operation {operationId}, but a native library re-read did not contain {libraryNickname}:{footprintName}. Inspect the library before retrying.");

        return new(response.ModifyHash);
    }

    private static ulong? ParseExpectedModifyHash(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!ulong.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out ulong parsed))
            throw new AutomationException("invalid_footprint_library_revision",
                "expectedModifyHash must be an unsigned decimal native modify hash.");
        return parsed;
    }

    private static async Task<ulong> EnsureLibraryModifyHash(NativeContext context,
        string libraryNickname, ulong? expected, CancellationToken cancellationToken)
    {
        NativeCapabilityFootprintLibraryReadNamesResponse response =
            await context.Client.InvokeAsync<NativeCapabilityFootprintLibraryReadNames,
                NativeCapabilityFootprintLibraryReadNamesResponse>(new()
                {
                    LibraryNickname = libraryNickname,
                    BestEfforts = false
                }, cancellationToken);
        if (expected.HasValue && response.ModifyHash != expected.Value)
            throw new AutomationException("footprint_library_changed",
                $"The footprint library modify hash changed from {expected.Value} to {response.ModifyHash}; inspect it and retry with a fresh operation ID.");
        return response.ModifyHash;
    }

    private static async Task<LibraryReadback> VerifyDeletedLibraryEntry(NativeContext context,
        string libraryNickname, string footprintName, ulong expectedModifyHash,
        string operationId, string operation, CancellationToken cancellationToken)
    {
        NativeCapabilityFootprintLibraryReadNamesResponse response;
        try
        {
            response = await context.Client.InvokeAsync<NativeCapabilityFootprintLibraryReadNames,
                NativeCapabilityFootprintLibraryReadNamesResponse>(new()
                {
                    LibraryNickname = libraryNickname,
                    BestEfforts = false
                }, cancellationToken);
        }
        catch (NngException error)
        {
            throw MutationTransport(error, operation + " persistence verification", operationId);
        }
        catch (NativeApiException error)
        {
            throw new AutomationException("footprint_persistence_unverified",
                $"KiCad completed {operation} operation {operationId}, but the native library re-read failed: {error.Message}");
        }

        FootprintLibraryContract.ValidateDeletePersistence(response, operationId, libraryNickname,
            footprintName, expectedModifyHash);
        return new(response.ModifyHash);
    }

    private static (string LibraryNickname, string FootprintName) ResolveCopyTarget(
        DocumentSpecifier document, string path)
    {
        int separator = path.IndexOf(':');
        if (separator < 0)
            return (document.LibId!.LibraryNickname, path);
        return (path[..separator], path[(separator + 1)..]);
    }

    private static Task<NativeCapabilityFootprintReadOperationResponse> ReadNativeReceipt(
        NativeContext context, string operationId, CancellationToken cancellationToken)
        => context.Client.InvokeAsync<NativeCapabilityFootprintReadOperation, NativeCapabilityFootprintReadOperationResponse>(
            new() { OperationId = operationId }, cancellationToken);

    private static Task<NativeCapabilityFootprintLibraryReadOperationResponse> ReadNativeLibraryReceipt(
        NativeContext context, string operationId, CancellationToken cancellationToken)
        => context.Client.InvokeAsync<NativeCapabilityFootprintLibraryReadOperation,
            NativeCapabilityFootprintLibraryReadOperationResponse>(new() { OperationId = operationId }, cancellationToken);

    private static DocumentSpecifier ParseFootprintDocument(string json)
    {
        DocumentSpecifier document;
        try { document = SchematicJson.Parser.Parse<DocumentSpecifier>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
        { throw new AutomationException("invalid_footprint_document", "documentJson is not valid DocumentSpecifier protobuf JSON: " + error.Message); }

        if (document.Type != DocumentType.DoctypeFootprint || document.LibId is null)
            throw new AutomationException("invalid_footprint_document",
                "documentJson must identify a footprint document with a library nickname and entry name.");
        ValidateLibraryId(document.LibId.LibraryNickname, document.LibId.EntryName);
        return document;
    }

    private static void ValidateLibraryId(string libraryNickname, string entryName)
    {
        if (string.IsNullOrWhiteSpace(libraryNickname) || string.IsNullOrWhiteSpace(entryName)
            || libraryNickname.Contains('\0') || entryName.Contains('\0'))
            throw new AutomationException("invalid_footprint_library_id",
                "Provide nonempty libraryNickname and entryName values without NUL characters.");
    }

    private static void ValidateCopyPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')
            || path.Count(c => c == ':') > 1 || path.Split(':').Any(string.IsNullOrWhiteSpace)
            || path.Contains('/') || path.Contains('\\'))
            throw new AutomationException("invalid_footprint_copy_path",
                "path must be a footprint name or libNickname:entryName, without directory separators.");
    }

    private static void ValidateEpoch(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid epoch) || epoch == Guid.Empty)
            throw new AutomationException("invalid_process_epoch", "Provide the canonical process epoch from the attached KiCad session.");
    }

    private static void ValidateOperationId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid operation) || operation == Guid.Empty)
            throw new AutomationException("invalid_operation_id", "Provide a nonempty operation UUID in canonical D format.");
    }

    private static bool IsUsableFootprintDocument(DocumentSpecifier document) =>
        document.Type == DocumentType.DoctypeFootprint && document.LibId is not null
        && !string.IsNullOrWhiteSpace(document.LibId.LibraryNickname)
        && !string.IsNullOrWhiteSpace(document.LibId.EntryName);

    private static AutomationException MutationTransport(NngException error, string operation, string operationId) =>
        new(error.RequestDelivered ? "operation_outcome_unknown" : "native_not_reached",
            error.RequestDelivered
                ? $"KiCad received {operation} operation {operationId} but did not answer. The native footprint command has no retained receipt; inspect the footprint editor before retrying."
                : $"KiCad did not receive {operation} operation {operationId}; no native result was confirmed. Inspect the instance before retrying.");

    private static AutomationException ReadTransport(NngException error, string operation) =>
        new(error.RequestDelivered ? "read_outcome_unknown" : "native_not_reached",
            error.RequestDelivered
                ? $"KiCad received {operation} but did not answer; the footprint document list is unknown. Inspect the editor and retry the read."
                : $"KiCad did not receive {operation}; inspect the instance and retry the read.");

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (AutomationException error) { return Failure(error.Code, error.Message); }
        catch (NativeApiException error) { return Failure("native_status_" + error.Status, error.Message); }
        catch (NngException error) { return Failure("native_transport_unavailable", error.Message); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException
            or InvalidOperationException or ArgumentException or IOException)
        { return Failure("footprint_library_operation_failed", error.Message); }
    }

    private static CallToolResult Data(object value)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new() { StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static CallToolResult Data(object value, bool isError)
    {
        CallToolResult result = Data(value);
        result.IsError = isError;
        return result;
    }

    private static CallToolResult PublishCheckedResponse(string instanceId,
        string processEpoch, DocumentSpecifier document, string operationId,
        NativeCapabilityFootprintLibraryCheckedWriteResponse response,
        bool nativeReceiptAvailable, bool durableRecovery)
    {
        return Data(new
        {
            instanceId, processEpoch, document, operationId,
            status = response.Status, committed = response.Committed, replayed = response.Replayed,
            rollbackCompleted = response.RollbackCompleted, revisionTracked = response.RevisionTracked,
            completeLifecycle = response.CompleteLifecycle, beforeSnapshotSha256 = response.BeforeSnapshotSha256,
            afterSnapshotSha256 = response.AfterSnapshotSha256, errorCode = response.ErrorCode,
            errorMessage = response.ErrorMessage, nativeReceiptAvailable, receiptVerified = true,
            durableRecovery, persistenceVerified = response.Committed,
            requestFingerprintSha256 = response.RequestFingerprintSha256,
            expectedRequestVerified = response.ExpectedRequestVerified, nativeWriteStarted = response.NativeWriteStarted,
            postverified = response.Postverified, targetVerified = response.TargetVerified,
            unrelatedEntriesVerified = response.UnrelatedEntriesVerified, rollbackAttempted = response.RollbackAttempted,
            receiptStatus = response.ReceiptStatus.ToString(), scope = response.Scope,
            beforeRevision = response.ObservedBefore?.Revision, afterRevision = response.ObservedAfter?.Revision,
            rollbackRevision = response.ObservedRollback?.Revision,
            persistenceSource = response.Committed ? "native-footprint-lifecycle-receipt" : null,
            transportRetrySafe = !response.Committed || response.Replayed
        }, !response.Committed || !string.IsNullOrEmpty(response.ErrorCode));
    }

    private static CallToolResult Failure(string code, string message)
    {
        bool submitted = code is "operation_outcome_unknown" or "footprint_persistence_unverified";
        JsonElement structured = JsonSerializer.SerializeToElement(new { errorCode = code, errorMessage = message,
            mutationSubmitted = submitted,
            outcome = code == "operation_outcome_unknown" ? "indeterminate"
                : code == "footprint_persistence_unverified" ? "unverified" : "not_submitted" });
        return new() { IsError = true, StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static async Task<CallToolResult> WithReceipt(string operationId, string fingerprint,
        Func<Task<CallToolResult>> action)
    {
        await MutationGate.WaitAsync();
        try
        {
            if (Receipts.TryGetValue(operationId, out StoredReceipt? existing))
            {
                if (existing.Fingerprint != fingerprint)
                    throw new AutomationException("operation_id_reused", "The operation ID is already bound to a different footprint mutation.");
                return existing.Result;
            }
            CallToolResult result = await action();
            if (!(result.IsError ?? false)) Receipts[operationId] = new(fingerprint, result);
            return result;
        }
        finally { MutationGate.Release(); }
    }

    private static string Fingerprint(string instanceId, string operation, string epoch, string payload)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId + "\n" + operation + "\n" + epoch + "\n" + payload)));

    private sealed record LibraryReadback(ulong ModifyHash);
    private sealed record NativeContext(NativeClient Client, AutomationSession Session);
    private sealed record StoredReceipt(string Fingerprint, CallToolResult Result);
}

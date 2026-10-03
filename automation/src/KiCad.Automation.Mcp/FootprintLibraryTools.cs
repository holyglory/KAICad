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
            return Data(new
            {
                instanceId, processEpoch = context.Session.Epoch, operationId, libraryNickname, footprintName,
                operation = "library-delete", operationOutcome = response.Completed ? "completed" : "rejected",
                nativeCommandSucceeded = response.Completed, operationReceipt = true,
                nativeReceiptAvailable = receipt.Found, receiptScope = receipt.Found ? "native-process" : "mcp-process",
                nativeReceipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(receipt)),
                beforeModifyHash = response.BeforeModifyHash, afterModifyHash = response.AfterModifyHash,
                expectedModifyHash = string.IsNullOrWhiteSpace(expectedModifyHash) ? null : expectedModifyHash,
                revisionAdmission = expectedHash.HasValue ? "modify-hash-matched" : "not-requested", revisionTracked = false,
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
            if (nativeReceipt.Found || libraryReceipt.Found)
                return Data(new
                {
                    instanceId, processEpoch = context.Session.Epoch, operationId, found = true,
                    receiptScope = "native-process", nativeReceiptAvailable = true,
                    nativeReceipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(nativeReceipt)),
                    nativeLibraryReceipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(libraryReceipt))
                });

            return Data(new { instanceId, processEpoch = context.Session.Epoch, operationId, found = false,
                receiptScope = "native-process", nativeReceiptAvailable = true,
                nativeReceipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(nativeReceipt)),
                nativeLibraryReceipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(libraryReceipt)) });
        }
        finally { MutationGate.Release(); }
    });

    [McpServerTool(Name = "kicad_footprint_save"),
     Description("Save the explicitly identified footprint currently loaded in the attached native footprint editor. documentJson must be a DocumentSpecifier with type footprint and a library identifier; expectedProcessEpoch pins the request; operationId correlates the attempt. The MCP process retains an exact replay receipt and KiCad retains a process-owned receipt that kicad_footprint_receipt can read after an MCP reconnect. The native command has no lifecycle revision snapshot, so a lost reply remains indeterminate until reconciled. A successful reply confirms native command success and that the library lists the entry; saved geometry is not independently verified."),
     KiCadCapability("footprint-library", "compiled-mcp plus native-footprint-api", "explicit footprint document, process epoch, operation ID, native process receipt; no lifecycle revision token"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Save(string instanceId, string expectedProcessEpoch, string documentJson,
        string operationId, CancellationToken cancellationToken, string expectedSnapshotSha256 = "") =>
        Execute(() => WithReceipt(operationId, Fingerprint(instanceId, "save", expectedProcessEpoch, documentJson + "\n" + expectedSnapshotSha256), async () =>
        {
            ValidateOperationId(operationId);
            ValidateEpoch(expectedProcessEpoch);
            DocumentSpecifier document = ParseFootprintDocument(documentJson);
            NativeContext context = await Admit(instanceId, expectedProcessEpoch, cancellationToken);
            await EnsureSnapshot(context, document, expectedSnapshotSha256, cancellationToken);
            try
            {
                await context.Client.InvokeAsync<SaveDocument, Empty>(new() { Document = document, OperationId = operationId }, cancellationToken);
            }
            catch (NngException error)
            {
                throw MutationTransport(error, "footprint save", operationId);
            }

            NativeCapabilityFootprintReadOperationResponse nativeReceipt = await ReadNativeReceipt(context, operationId, cancellationToken);
            string snapshotSha256 = await SnapshotHash(context, document, cancellationToken);
            LibraryReadback readback = await VerifyLibraryEntry(context, document.LibId.LibraryNickname,
                document.LibId.EntryName, operationId, "footprint save", cancellationToken);
            return Data(new
            {
                instanceId, processEpoch = context.Session.Epoch, operationId,
                operation = "save", document, operationOutcome = "completed",
                operationReceipt = true, nativeReceiptAvailable = nativeReceipt.Found,
                revisionTracked = false, nativeCommandSucceeded = true,
                libraryEntryObserved = true, geometryPersistenceVerified = false,
                persistenceVerified = false, persistenceSource = "native-footprint-library-entry-enumeration",
                persistedLibraryNickname = document.LibId.LibraryNickname,
                persistedFootprintName = document.LibId.EntryName,
                persistedModifyHash = readback.ModifyHash,
                transportRetrySafe = false,
                replayableWithinProcess = true, receiptScope = nativeReceipt.Found ? "native-process" : "mcp-process",
                nativeReceipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(nativeReceipt)),
                snapshotSha256, snapshotSource = "native-footprint-items", snapshotAdmission = string.IsNullOrWhiteSpace(expectedSnapshotSha256) ? "not-requested" : "matched",
                verification = "native-success-plus-library-entry-enumeration"
            });
        }));

    [McpServerTool(Name = "kicad_footprint_save_copy"),
     Description("Save a copy of the explicitly identified footprint to a library entry without opening the copy. path is a footprint name or libNickname:entryName as accepted by KiCad's native footprint handler, not an arbitrary filesystem path; overwrite and includeProject are passed to the native command. expectedProcessEpoch and operationId pin and correlate the request. The MCP process retains an exact replay receipt and KiCad retains a process-owned receipt that kicad_footprint_receipt can read after an MCP reconnect. The native command has no lifecycle revision snapshot, so a lost reply remains indeterminate and must be reconciled before retrying. A successful reply confirms native command success and that the library lists the copied entry; saved geometry is not independently verified."),
     KiCadCapability("footprint-library", "compiled-mcp plus native-footprint-api", "explicit footprint document, process epoch, operation ID, native process receipt; no lifecycle revision token"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> SaveCopy(string instanceId, string expectedProcessEpoch, string documentJson,
        string path, string operationId, CancellationToken cancellationToken, bool overwrite = false,
        bool includeProject = false) =>
        Execute(() => WithReceipt(operationId, Fingerprint(instanceId, "save-copy", expectedProcessEpoch, documentJson + "\n" + path + "\n" + overwrite + "\n" + includeProject), async () =>
        {
            ValidateOperationId(operationId);
            ValidateEpoch(expectedProcessEpoch);
            DocumentSpecifier document = ParseFootprintDocument(documentJson);
            ValidateCopyPath(path);
            NativeContext context = await Admit(instanceId, expectedProcessEpoch, cancellationToken);
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
            string snapshotSha256 = await SnapshotHash(context, document, cancellationToken);
            (string targetLibrary, string targetName) = ResolveCopyTarget(document, path);
            LibraryReadback readback = await VerifyLibraryEntry(context, targetLibrary, targetName, operationId,
                "footprint save-copy", cancellationToken);
            return Data(new
            {
                instanceId, processEpoch = context.Session.Epoch, operationId,
                operation = "save-copy", document, path, overwrite, includeProject,
                operationOutcome = "completed", operationReceipt = true,
                nativeReceiptAvailable = nativeReceipt.Found, revisionTracked = false,
                nativeCommandSucceeded = true, libraryEntryObserved = true,
                geometryPersistenceVerified = false, persistenceVerified = false,
                persistenceSource = "native-footprint-library-entry-enumeration",
                persistedLibraryNickname = targetLibrary, persistedFootprintName = targetName,
                persistedModifyHash = readback.ModifyHash,
                copyOpened = false, transportRetrySafe = false,
                replayableWithinProcess = true, receiptScope = nativeReceipt.Found ? "native-process" : "mcp-process",
                nativeReceipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(nativeReceipt)), snapshotSha256,
                verification = "native-success-plus-library-entry-enumeration"
            });
        }));

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

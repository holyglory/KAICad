using System.ComponentModel;
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

[McpServerToolType]
public sealed class CheckedSchematicTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_schematic_checked_state", ReadOnly = true),
     Description("Capture whole-schematic electrical data, typed hierarchy and full native admission state together at one native checkpoint. Requires an explicit root documentJson and instance ID. The native reader rejects changed or pending captures. The returned state can guard a CheckedSchematicBatch planned from its paired electrical snapshot; it may become stale afterward. Reports existing file conflicts and incomplete tracking without authorizing mutation. No design edits, file writes, navigation or image capture occur; kicad_schematic_checked_view returns this same state together with the rendered sheet."),
     KiCadCapability("schematic-design", "native-api", "explicit instance ID and root document"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.CheckedBatchesRejectChangedStateAndPreserveNativeUndo")]
    public async Task<CallToolResult> Observe(string instanceId, string documentJson, CancellationToken cancellationToken)
    {
        CheckedSchematicState? result = null;
        var response = await InstanceToolBoundary.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = SchematicJson.Parser.Parse<Kiapi.Common.Types.DocumentSpecifier>(documentJson);
            DocumentStateTools.ValidateTarget(document);
            if ((int)document.Type != 1 || document.SheetPath?.Path.Count != 1)
                throw new AutomationException("invalid_checked_document", "Select the explicit schematic root for combined capture.");
            var client = registry.Client(instanceId);
            result = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
                { Document = document, ProcessEpoch = client.Epoch }, cancellationToken);
            CheckedSchematicContract.ValidateObservation(result, document, client.Epoch);
            return SchematicJson.Formatter.Format(result);
        });
        if (result is not null && !(response.IsError ?? false))
        {
            using var json = JsonDocument.Parse(SchematicJson.Formatter.Format(result));
            response.StructuredContent = json.RootElement.Clone();
        }
        return response;
    }

    [McpServerTool(Name = "kicad_schematic_checked_view", ReadOnly = true),
     Description("Look at a live schematic and get the exact state to edit it from, together: the displayed sheet as a PNG image and the whole-schematic checked state, captured in one native dispatch at one native checkpoint, so the image and the state always describe the same revision. Use it in an observe-then-apply loop while a person may be editing the same design: plan a CheckedSchematicBatch from structuredContent.checked (its state field is the batch's expectedState) and send it with kicad_schematic_apply_checked_batch; if anything changed in between, the batch is refused as stale_document_state and nothing changes, so observe again. documentJson is the explicit schematic root, as for kicad_schematic_checked_state. viewDocumentJson is the sheet instance the editor displays, the root when omitted; a sheet of another schematic or project is refused before KiCad is asked (invalid_checked_document), and a sheet the editor does not display is refused by KiCad (native_status_3). KiCad reads the checked state, renders the displayed sheet and reads the state again within one request on its GUI thread, so no editor event runs in between; a change seen by the closing read refuses the capture (native_status_4, observe again), and a busy editor or pending edit refuses it too (native_status_7). This server publishes a view only when the checked state, the image and the objects all belong to one checkpoint of this KiCad process, this root and the viewed sheet, and the image is the PNG of the size it claims; any other reply is refused as invalid_checked_view and carries no image. structuredContent is a NativeCapabilityCheckedView: checked {state, electrical} and view {snapshot: the displayed sheet's objects, preview: its revision, viewport and size}; the image is the first content block. No design edits, file writes, navigation or selection changes occur."),
     KiCadCapability("schematic-design", "native-api", "explicit instance ID, root document and displayed sheet instance"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.AgentAndPersonEditingTogetherNeverGetStaleOrPartialEdits")]
    public async Task<CallToolResult> ObserveView(string instanceId, string documentJson, CancellationToken cancellationToken,
        string? viewDocumentJson = null)
    {
        NativeCapabilityCheckedView? result = null;
        var response = await InstanceToolBoundary.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = ParseDocument(documentJson, "documentJson");
            var view = viewDocumentJson is null ? document.Clone() : ParseDocument(viewDocumentJson, "viewDocumentJson");
            DocumentStateTools.ValidateTarget(document);
            DocumentStateTools.ValidateTarget(view);
            if ((int)document.Type != 1 || document.SheetPath?.Path.Count != 1)
                throw new AutomationException("invalid_checked_document", "Select the explicit schematic root for the checked view.");
            if ((int)view.Type != 1 || view.SheetPath is null || view.SheetPath.Path.Count == 0
                || !Equals(view.SheetPath.Path[0], document.SheetPath.Path[0]) || !Equals(view.Project, document.Project))
                throw new AutomationException("invalid_checked_document", "The viewed sheet must be the root or a sheet below it in the same schematic.");
            var client = registry.Client(instanceId);
            result = await client.InvokeAsync<NativeCapabilityReadCheckedView, NativeCapabilityCheckedView>(new()
                { Document = document, ProcessEpoch = client.Epoch, View = view }, cancellationToken);
            ValidateView(result, document, view, client.Epoch);
            return "";
        });
        if (result is null || (response.IsError ?? false)) return response;
        // The image travels as its own content block; the structured view keeps every other field.
        var image = ImageContentBlock.FromBytes(result.View.Preview.Png.Memory, "image/png");
        var described = result.Clone();
        described.View.Preview.Png = ByteString.Empty;
        string json = SchematicJson.Formatter.Format(described);
        using var parsed = JsonDocument.Parse(json);
        return new() { Content = [image, new TextContentBlock { Text = json }], StructuredContent = parsed.RootElement.Clone() };
    }

    // The checked state, the rendered view and its objects all belong to one checkpoint of this exact process and target.
    // Every inconsistent reply is refused as invalid_checked_view, including a checked state of another root or process.
    internal static void ValidateView(NativeCapabilityCheckedView result, Kiapi.Common.Types.DocumentSpecifier document,
        Kiapi.Common.Types.DocumentSpecifier view, string processEpoch)
    {
        if (result.Checked is null || result.View?.Preview is null || result.View.Snapshot?.Data?.Metadata is null)
            throw new AutomationException("invalid_checked_view", "KiCad returned an incomplete checked view.");
        try { CheckedSchematicContract.ValidateObservation(result.Checked, document, processEpoch); }
        catch (AutomationException error)
        {
            throw new AutomationException("invalid_checked_view", "The checked state of the view is not one checkpoint of this process and root: " + error.Message);
        }
        var revision = result.Checked.State.Revision;
        var png = result.View.Preview.Png.Span;
        if (!Equals(result.View.Preview.Revision, revision) || !Equals(result.View.Snapshot.Revision, revision)
            || !Equals(result.View.Preview.Document, view) || !Equals(result.View.Snapshot.Data.Metadata.Document, view)
            || png.Length < 24 || !png[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png[16..20]) != result.View.Preview.WidthPixels
            || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png[20..24]) != result.View.Preview.HeightPixels)
            throw new AutomationException("invalid_checked_view", "The rendered view does not belong to the checked state's revision and sheet.");
    }

    private static Kiapi.Common.Types.DocumentSpecifier ParseDocument(string json, string name)
    {
        try { return SchematicJson.Parser.Parse<Kiapi.Common.Types.DocumentSpecifier>(json); }
        catch (Exception error) when (error is InvalidJsonException or InvalidProtocolBufferException or InvalidOperationException)
        {
            throw new AutomationException("invalid_checked_document", $"{name} could not be read as a DocumentSpecifier. {error.Message}");
        }
    }

    [McpServerTool(Name = "kicad_schematic_apply_checked_batch", ReadOnly = false),
     Description("Create, change and delete schematic objects in one step, only while KiCad still holds the exact observed native state (process epoch, native content digest, project settings and file baselines). requestJson is a CheckedSchematicBatch in protobuf JSON: batch {document: exactly the document expectedState was observed for (the schematic root when observed with kicad_schematic_checked_state), operations, operationId: a new UUID, documentEpoch and expectedRevision copied from the observed state, description} and expectedState: the state field of the kicad_schematic_checked_state result (checked.state of a kicad_schematic_checked_view result), or the kicad_document_state result for that same document. An operation edits the batch document unless its optional targetDocument names another loaded sheet instance of the same schematic. Operations include: create with a new object carrying a new UUID, or update with the complete changed object carrying its existing UUID, both as a typed Any, for example {\"create\":{\"@type\":\"type.googleapis.com/kiapi.schematic.types.LocalLabel\",...}} for a LocalLabel, SchematicLine (wire), Junction, SchematicText or SchematicSymbolInstance; remove with the object's UUID ({\"remove\":{\"value\":\"<uuid>\"}}); sheet and project settings and embedded files; connected symbol moves and transforms; symbol locks; library cache replacement; and one final assertConnectivity check, without targetDocument and alongside only create, update and library cache operations, that refuses the whole batch unless the result has the expected connections. Add, change or remove a symbol field by updating the symbol with its complete userFields list. The operations apply together as one native undo step, or not at all: a rejected receipt means KiCad is unchanged. A refused operation gives errorCode native_batch_rejected, usually with an errorMessage starting 'Atomic operation N rejected:' and the operation's zero-based index; a failed assertConnectivity check gives connectivity_postcondition_failed, and a batch planned from an out-of-date observation gives stale_document_state. A completed receipt lists every created or changed object as KiCad stored it (result.items, with created and updated objects in operation order) and every removed UUID (result.removed). Sending the identical request again, for example after a timeout, returns the same receipt and never edits again, even after the batch was undone; kicad_schematic_checked_batch_receipt reads it without sending. An operation ID stays bound to its request: after any refusal or change, observe again and send a new operation ID. Old peers reject this distinct command; no unchecked fallback exists. A rejected or indeterminate result is not success; cancellation or transport failure does not prove rollback. Does not save files or complete XML synchronization."),
     KiCadCapability("schematic-design", "native-api", "document state observation, process epoch, document revision, operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.CheckedBatchesRejectChangedStateAndPreserveNativeUndo",
         "NativeSessionTests.AgentAndPersonEditingTogetherNeverGetStaleOrPartialEdits")]
    public Task<CallToolResult> Apply(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run(instanceId, requestJson, inspect: false, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_replace_embedded_files"),
     Description("Replace the complete schematic-owned embedded-file set as one checked native undo step. documentJson identifies the explicit schematic root; expectedStateJson is the exact DocumentLifecycleState checkpoint from kicad_schematic_checked_state or kicad_document_state; embeddedFilesJson is a typed EmbeddedFiles protobuf object and embeddedFonts selects the native font archive flag. operationId is replay-safe and remains bound to the exact file payload and expected revision. The operation does not save the design file; inspect the receipt and call kicad_document_save separately."),
     KiCadCapability("schematic-design", "native-api", "explicit root document, lifecycle checkpoint, embedded-file payload and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> ReplaceEmbeddedFiles(string instanceId, string documentJson,
        string expectedStateJson, string embeddedFilesJson, string operationId, bool embeddedFonts,
        CancellationToken cancellationToken) => ExecuteEmbeddedReplacement(instanceId, documentJson,
            expectedStateJson, embeddedFilesJson, operationId, embeddedFonts, cancellationToken);

    private async Task<CallToolResult> ExecuteEmbeddedReplacement(string instanceId, string documentJson,
        string expectedStateJson, string embeddedFilesJson, string operationId, bool embeddedFonts,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentSpecifier document = ParseDocument(documentJson, "documentJson");
            DocumentLifecycleState expected = SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedStateJson);
            if (document.Type != DocumentType.DoctypeSchematic || document.SheetPath?.Path.Count != 1
                || !expected.Document.Equals(document) || expected.Scope != DocumentLifecycleScope.DlsSchematicHierarchy
                || string.IsNullOrWhiteSpace(expected.ProcessEpoch) || expected.Revision is null)
                throw new AutomationException("invalid_embedded_file_state",
                    "The embedded-file replacement requires the explicit schematic root and a matching lifecycle checkpoint.");
            if (!Guid.TryParseExact(operationId, "D", out var operation) || operation == Guid.Empty
                || !string.Equals(operationId, operation.ToString("D"), StringComparison.Ordinal))
                throw new AutomationException("invalid_operation_id", "operationId must be a canonical UUID.");
            EmbeddedFiles files = SchematicJson.Parser.Parse<EmbeddedFiles>(embeddedFilesJson);
            if (files.Files.Count > 4096 || files.Files.Any(file => string.IsNullOrWhiteSpace(file.Name)))
                throw new AutomationException("invalid_embedded_file_state", "embeddedFilesJson must contain at most 4096 named files.");

            CheckedSchematicBatch request = new()
            {
                Batch = new ApplySchematicItemBatch
                {
                    Document = document.Clone(),
                    DocumentEpoch = expected.Revision.Epoch,
                    ExpectedRevision = expected.Revision.Clone(),
                    OperationId = operationId,
                    Description = "MCP replace schematic embedded files"
                },
                ExpectedState = expected.Clone()
            };
            request.Batch.Operations.Add(new SchematicItemOperation
            {
                ReplaceEmbeddedFiles = new SchematicEmbeddedFileState
                {
                    Files = files,
                    EmbeddedFonts = embeddedFonts
                }
            });
        return await Run(instanceId, SchematicJson.Formatter.Format(request), inspect: false, cancellationToken);
        }
        catch (Exception error) when (error is AutomationException or InvalidProtocolBufferException
            or InvalidJsonException or InvalidOperationException or NngException or NativeApiException)
        {
            string code = error is AutomationException known ? known.Code
                : error is NativeApiException native ? "native_status_" + native.Status
                : error is NngException transport ? "transport_status_" + transport.ErrorCode
                : "invalid_embedded_file_state";
            JsonElement data = JsonSerializer.SerializeToElement(new
            {
                instanceId, operationId, mutationSubmitted = false,
                outcome = "not_submitted", errorCode = code, errorMessage = error.Message
            });
            return new CallToolResult
            {
                IsError = true,
                StructuredContent = data,
                Content = [new TextContentBlock { Text = data.GetRawText() }]
            };
        }
    }

    [McpServerTool(Name = "kicad_schematic_create_image"),
     Description("Create one typed schematic image through the checked native batch path. imageJson is a complete SchematicImage with a canonical UUID, position, positive scale and bounded PNG/JPEG bytes; documentJson and expectedStateJson bind the exact root revision, and operationId provides receipt replay. The edit is undoable and remains unsaved until kicad_document_save."),
     KiCadCapability("schematic-design", "native-api", "explicit root document, lifecycle checkpoint, complete image payload and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> CreateImage(string instanceId, string documentJson, string expectedStateJson,
        string imageJson, string operationId, CancellationToken cancellationToken) =>
        ExecuteImageMutation(instanceId, documentJson, expectedStateJson, imageJson, null, operationId,
            ImageMutation.Create, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_update_image"),
     Description("Replace one existing typed schematic image through the checked native batch path. imageJson must retain the existing canonical UUID and carry bounded image bytes; documentJson and expectedStateJson bind the exact root revision, and operationId provides receipt replay. The edit is undoable and remains unsaved until kicad_document_save."),
     KiCadCapability("schematic-design", "native-api", "explicit root document, lifecycle checkpoint, complete image payload and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> UpdateImage(string instanceId, string documentJson, string expectedStateJson,
        string imageJson, string operationId, CancellationToken cancellationToken) =>
        ExecuteImageMutation(instanceId, documentJson, expectedStateJson, imageJson, null, operationId,
            ImageMutation.Update, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_delete_image"),
     Description("Delete one explicitly identified schematic image through the checked native batch path. imageId must be the canonical UUID observed in the root schematic state; documentJson and expectedStateJson bind the exact revision, and operationId provides receipt replay. The edit is undoable and remains unsaved until kicad_document_save."),
     KiCadCapability("schematic-design", "native-api", "explicit root document, lifecycle checkpoint, image UUID and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> DeleteImage(string instanceId, string documentJson, string expectedStateJson,
        string imageId, string operationId, CancellationToken cancellationToken) =>
        ExecuteImageMutation(instanceId, documentJson, expectedStateJson, null, imageId, operationId,
            ImageMutation.Delete, cancellationToken);

    private enum ImageMutation { Create, Update, Delete }

    private async Task<CallToolResult> ExecuteImageMutation(string instanceId, string documentJson,
        string expectedStateJson, string? imageJson, string? imageId, string operationId,
        ImageMutation mutation, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentSpecifier document = ParseDocument(documentJson, "documentJson");
            DocumentLifecycleState expected = SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedStateJson);
            if (document.Type != DocumentType.DoctypeSchematic || document.SheetPath?.Path.Count != 1
                || !expected.Document.Equals(document) || expected.Scope != DocumentLifecycleScope.DlsSchematicHierarchy
                || string.IsNullOrWhiteSpace(expected.ProcessEpoch) || expected.Revision is null)
                throw new AutomationException("invalid_schematic_image_state", "Image mutation requires the explicit schematic root and a matching lifecycle checkpoint.");
            if (!Guid.TryParseExact(operationId, "D", out var operation) || operation == Guid.Empty
                || !string.Equals(operationId, operation.ToString("D"), StringComparison.Ordinal))
                throw new AutomationException("invalid_operation_id", "operationId must be a canonical UUID.");

            var batch = new ApplySchematicItemBatch
            {
                Document = document.Clone(), DocumentEpoch = expected.Revision.Epoch,
                ExpectedRevision = expected.Revision.Clone(), OperationId = operationId,
                Description = "MCP typed schematic image " + mutation.ToString().ToLowerInvariant()
            };
            if (mutation == ImageMutation.Delete)
            {
                if (!CanonicalUuid(imageId))
                    throw new AutomationException("invalid_schematic_image_id", "imageId must be a canonical UUID.");
                batch.Operations.Add(new SchematicItemOperation { Remove = new KIID { Value = imageId } });
            }
            else
            {
                SchematicImage image = SchematicJson.Parser.Parse<SchematicImage>(imageJson ?? "{}");
                ValidateImage(image);
                batch.Operations.Add(new SchematicItemOperation
                {
                    Create = mutation == ImageMutation.Create ? Any.Pack(image) : null,
                    Update = mutation == ImageMutation.Update ? Any.Pack(image) : null
                });
            }
            return await Run(instanceId, SchematicJson.Formatter.Format(new CheckedSchematicBatch
            {
                Batch = batch, ExpectedState = expected.Clone()
            }), inspect: false, cancellationToken);
        }
        catch (Exception error) when (error is AutomationException or InvalidProtocolBufferException
            or InvalidJsonException or InvalidOperationException or NngException or NativeApiException)
        {
            string code = error is AutomationException known ? known.Code
                : error is NativeApiException native ? "native_status_" + native.Status
                : error is NngException transport ? "transport_status_" + transport.ErrorCode
                : "invalid_schematic_image";
            JsonElement data = JsonSerializer.SerializeToElement(new
            {
                instanceId, operationId, mutationSubmitted = false,
                outcome = "not_submitted", errorCode = code, errorMessage = error.Message
            });
            return new CallToolResult { IsError = true, StructuredContent = data,
                Content = [new TextContentBlock { Text = data.GetRawText() }] };
        }
    }

    private static void ValidateImage(SchematicImage image)
    {
        if (!CanonicalUuid(image.Id?.Value))
            throw new AutomationException("invalid_schematic_image_id", "imageJson must carry a canonical image UUID.");
        if (image.ImageData.Length is < 1 or > 32 * 1024 * 1024)
            throw new AutomationException("invalid_schematic_image_data", "imageJson must carry between 1 byte and 32 MiB of image data.");
        if (image.ImageScale is null || !double.IsFinite(image.ImageScale.Value) || image.ImageScale.Value <= 0)
            throw new AutomationException("invalid_schematic_image_scale", "imageJson must carry a positive finite image scale.");
    }

    private static bool CanonicalUuid(string? value) => value is not null
        && Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
        && string.Equals(value, id.ToString("D"), StringComparison.Ordinal);

    [McpServerTool(Name = "kicad_schematic_checked_batch_receipt", ReadOnly = true),
     Description("Inspect the process-owned receipt for an exact CheckedSchematicBatch requestJson and instance ID. Does not submit or repeat a mutation. The original process, operation UUID, document and complete request must match; missing receipts remain not-found, and indeterminate results require reconciliation rather than a new operation ID. Receipts do not prove that the live document has not changed since the recorded operation."),
     KiCadCapability("schematic-design", "native-api", "exact checked request, process epoch, operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.CheckedBatchesRejectChangedStateAndPreserveNativeUndo")]
    public Task<CallToolResult> Inspect(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Run(instanceId, requestJson, inspect: true, cancellationToken);

    private async Task<CallToolResult> Run(string instanceId, string requestJson, bool inspect, CancellationToken token)
    {
        bool submitted = false;
        CheckedSchematicBatch? request = null;
        try
        {
            token.ThrowIfCancellationRequested();
            request = ParseRequest(requestJson);
            var client = registry.Client(instanceId);
            ValidateRequest(request, client.Epoch);
            token.ThrowIfCancellationRequested();
            submitted = !inspect;
            var result = inspect
                ? await client.InvokeAsync<ReadCheckedSchematicBatchReceipt, CheckedSchematicBatchReceipt>(new()
                {
                    Document = request.Batch.Document.Clone(), ProcessEpoch = client.Epoch,
                    OperationId = request.Batch.OperationId, ExpectedRequest = request.Clone()
                }, token)
                : await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, token);
            ValidateResult(request, result, inspect);
            using var json = JsonDocument.Parse(SchematicJson.Formatter.Format(result));
            var data = JsonSerializer.SerializeToElement(new { instanceId, receipt = json.RootElement.Clone(),
                mutationSubmitted = submitted, liveStateStillCurrent = false, designFilesSaved = false });
            return new() { IsError = !inspect && result.Status != CheckedSchematicBatchStatus.CsbsCompleted,
                Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or InvalidProtocolBufferException or InvalidJsonException or NngException)
        {
            string code = error switch { AutomationException known => known.Code,
                NativeApiException native => "native_status_" + native.Status,
                NngException transport => "transport_status_" + transport.ErrorCode, _ => "invalid_checked_batch" };
            var data = JsonSerializer.SerializeToElement(new { instanceId, operationId = request?.Batch?.OperationId,
                mutationSubmitted = submitted, outcome = submitted ? "not_confirmed" : "not_submitted", errorCode = code, errorMessage = error.Message });
            return new() { IsError = true, Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
        }
    }

    // An object whose "@type" this server cannot read fails as InvalidOperationException rather than as a JSON error;
    // it is still an unreadable request, refused before anything is sent, and the agent is told which type it was.
    private static CheckedSchematicBatch ParseRequest(string requestJson)
    {
        try { return SchematicJson.Parser.Parse<CheckedSchematicBatch>(requestJson); }
        catch (InvalidOperationException error)
        {
            throw new AutomationException("invalid_checked_batch",
                "requestJson could not be read; each object's @type must name a kiapi.schematic.types message. " + error.Message);
        }
    }

    internal static void ValidateRequest(CheckedSchematicBatch request, string processEpoch) =>
        CheckedSchematicContract.ValidateRequest(request, processEpoch);

    internal static void ValidateResult(CheckedSchematicBatch request, CheckedSchematicBatchReceipt result, bool inspect) =>
        CheckedSchematicContract.ValidateResult(request, result, inspect);
}

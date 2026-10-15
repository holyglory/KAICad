using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Commands;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProtocolRasterTraceResult = KiCad.Automation.Protocol.RasterTraceResult;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class RasterTraceProjectionTools(InstanceRegistry registry)
{
    private static readonly SemaphoreSlim BoardGate = new(1, 1);

    [McpServerTool(Name = "kicad_artwork_raster_project", ReadOnly = false),
     Description("Project one accepted KiCad raster trace proposal into an existing symbol library, a cloned PCB footprint, a non-copper PCB board graphic, or a checked schematic drawing. Filled pieces remain filled polygons; signed contour topology stays in a bounded manifest. Existing pins, pads, fields and metadata are retained. Human/generated source attribution remains compatible; imagegen requires a typed provenance envelope bound to the exact local bytes. Board source archives are preflighted with an atomic publish and readback hash before the item transaction, while board geometry and archival remain separate persistence boundaries; a verified compensating delete reports atomic recovery after a post-commit archive mismatch. Manifest validation does not authenticate an external ImageGen service."),
     KiCadCapability("generated-artwork-cad", "typed-projection-adapters", "accepted raster proposal, exact source SHA, deterministic filled polygons, target lifecycle admission and provenance"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp", "NativeSessionTests.NativeSymbolLibraryReadbackUsesMcp")]
    public Task<CallToolResult> Project(string instanceId, RasterTraceProjectionInput input,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProjectionContext context = await Prepare(instanceId, input, cancellationToken);
        return input.TargetKind switch
        {
            RasterTraceProjectionTargetKind.Symbol => await ProjectSymbol(instanceId, input, context, cancellationToken),
            RasterTraceProjectionTargetKind.Footprint => await ProjectFootprint(instanceId, input, context, cancellationToken),
            RasterTraceProjectionTargetKind.Board => await ProjectBoard(instanceId, input, context, cancellationToken),
            RasterTraceProjectionTargetKind.Schematic => await ProjectSchematic(instanceId, input, context, cancellationToken),
            _ => throw new AutomationException("invalid_raster_projection_target", "Choose exactly one supported projection target.")
        };
    });

    private async Task<ProjectionContext> Prepare(string instanceId, RasterTraceProjectionInput input,
        CancellationToken token)
    {
        input.Validate();
        ProtocolRasterTraceResult proposal = input.Proposal!.ToNative();
        ValidateProposal(proposal);
        byte[] source = await File.ReadAllBytesAsync(input.SourcePath!, token);
        if ((ulong)source.Length != proposal.SourceBytes)
            throw new AutomationException("raster_trace_source_changed", "Source bytes differ from the accepted proposal length.");
        string actual = Convert.ToHexStringLower(SHA256.HashData(source));
        if (!string.Equals(actual, proposal.SourceSha256, StringComparison.Ordinal)
            || !string.Equals(actual, input.Proposal.SourceSha256, StringComparison.Ordinal))
            throw new AutomationException("raster_trace_source_changed", "Source bytes do not match the accepted proposal SHA-256.");
        RasterTraceProvenanceInput? provenance = RasterTraceProvenanceInput.Validate(input.Provenance,
            input.SourceKind, actual, (ulong)source.Length);
        return new(proposal, source, actual, EmbeddedFileCodec.Name(actual), input.SourceKind!,
            input.SourceOrigin ?? "", input.OperationId!, provenance);
    }

    private async Task<CallToolResult> ProjectSymbol(string instanceId, RasterTraceProjectionInput input,
        ProjectionContext context, CancellationToken token)
    {
        RasterTraceSymbolTargetInput target = input.Symbol
            ?? throw new AutomationException("invalid_raster_projection_target", "Symbol target fields are required.");
        ValidateSymbolTarget(target);
        var reader = new SchematicSymbolLibraryTools(registry);
        CallToolResult read = await reader.Read(instanceId, input.ExpectedProcessEpoch!, target.LibraryNickname!, target.SymbolName!, token);
        EnsureSuccess(read, "symbol_library_read_failed");
        JsonElement definitionElement = Property(read.StructuredContent!.Value, "definition");
        SchematicSymbol definition = SchematicJson.Parser.Parse<SchematicSymbol>(definitionElement.GetRawText());
        if (target.ExpectedModifyHash is null)
            throw new AutomationException("invalid_raster_projection_state", "ExpectedModifyHash is required for symbol projection.");
        EmbeddedFile sourceFile = EmbeddedFileCodec.FromBytes(context.EmbeddedFileName, context.SourceBytes);
        definition.EmbeddedFiles ??= new();
        ExistingEmbeddedFile(definition.EmbeddedFiles, sourceFile.Name, sourceFile.DataHash);
        definition.EmbeddedFiles.Files.Add(sourceFile);
        List<string> created = AppendSymbolPieces(definition, context);
        definition.Items.Add(new SchematicSymbolChild { Item = Any.Pack(ManifestField(context, privateField: true)), IsPrivate = true });
        string operationId = context.OperationId;
        CallToolResult write = await new SchematicSymbolLibraryTools(registry).Write(instanceId,
            input.ExpectedProcessEpoch!, target.LibraryNickname!, SchematicJson.Formatter.Format(definition),
            operationId, token, overwrite: true, expectedModifyHash: target.ExpectedModifyHash);
        if (write.IsError ?? false) return Data(new
        {
            operationId, targetKind = "symbol", accepted = false, nativeResult = write.StructuredContent,
            geometrySha256 = context.Proposal.GeometrySha256, sourceSha256 = context.SourceSha256,
            provenance = context.Provenance, provenanceVerified = context.Provenance is not null,
            provenanceAtomic = true, electricalValidated = false, manufacturingValidated = false
        }, true);
        return Data(new
        {
            operationId, targetKind = "symbol", accepted = true, createdIds = created,
            nativeResult = write.StructuredContent, geometrySha256 = context.Proposal.GeometrySha256,
            sourceSha256 = context.SourceSha256, embeddedSourceVerified = true, provenanceAtomic = true,
            provenance = context.Provenance, provenanceVerified = context.Provenance is not null,
            electricalValidated = false, manufacturingValidated = false, receiptScope = "native-symbol-library"
        });
    }

    private async Task<CallToolResult> ProjectFootprint(string instanceId, RasterTraceProjectionInput input,
        ProjectionContext context, CancellationToken token)
    {
        RasterTraceFootprintTargetInput target = input.Footprint
            ?? throw new AutomationException("invalid_raster_projection_target", "Footprint target fields are required.");
        DocumentSpecifier document = NativeDocumentInput.ToNative(target.Document);
        DocumentLifecycleState expected = DocumentLifecycleInput.ToNative(target.ExpectedState);
        if (document.Type != DocumentType.DoctypePcb || !document.Equals(expected.Document)
            || expected.Scope != DocumentLifecycleScope.DlsPcb || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_raster_projection_state", "Footprint target state must identify the same PCB and process epoch.");
        ValidateUuid(target.SourceFootprintId, "SourceFootprintId");
        ValidateUuid(target.TargetFootprintId, "TargetFootprintId");
        if (target.SourceFootprintId == target.TargetFootprintId)
            throw new AutomationException("invalid_raster_projection_identity", "A footprint projection clone requires distinct source and target identities.");
        ValidateFootprintLibraryIdentity(target.LibraryNickname, target.FootprintName);
        if (target.Layer is not ("F.Cu" or "B.Cu"))
            throw new AutomationException("invalid_raster_projection_layer", "Footprint instances must use F.Cu or B.Cu.");
        BoardLayer targetLayer = ParseLayer(target.Layer!);
        BoardLayer artworkLayer = ParseLayer(target.ArtworkLayer);
        ValidateArtworkLayer(artworkLayer);
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(token);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("pcb_instance_changed", "The footprint checkpoint belongs to another native process epoch.");
        DocumentLifecycleState current = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, token);
        if (!current.Equals(expected))
            throw new AutomationException("pcb_state_changed", "The PCB changed before the footprint projection; inspect it again.");
        var query = new GetItemsById { Header = new() { Document = document } };
        query.Items.Add(new KIID { Value = target.SourceFootprintId });
        GetItemsResponse response = await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, token);
        if (response.Items.Count != 1 || !response.Items[0].Is(FootprintInstance.Descriptor))
            throw new AutomationException("raster_projection_source_footprint_missing", "The source footprint was not found on the admitted board.");
        FootprintInstance source = response.Items[0].Unpack<FootprintInstance>();
        FootprintInstance clone = source.Clone();
        clone.Id = new() { Value = target.TargetFootprintId };
        // Board readback may omit the library identifier for an inline or
        // imported footprint.  The projection target must supply the
        // authoritative identity explicitly; never invent one from the
        // source instance or silently pass an incomplete definition to the
        // placement planner.
        clone.Definition ??= new();
        clone.Definition.Id = new()
        {
            LibraryNickname = target.LibraryNickname!,
            EntryName = target.FootprintName!
        };
        clone.Position = new() { XNm = target.OriginXNm, YNm = target.OriginYNm };
        clone.Orientation = new() { ValueDegrees = target.OrientationDegrees };
        clone.Layer = targetLayer;
        clone.EmbeddedFiles ??= new();
        EmbeddedFile sourceFile = EmbeddedFileCodec.FromBytes(context.EmbeddedFileName, context.SourceBytes);
        ExistingEmbeddedFile(clone.EmbeddedFiles, sourceFile.Name, sourceFile.DataHash);
        clone.EmbeddedFiles.Files.Add(sourceFile);
        AddManifestProperties(clone.CustomProperties, context);
        List<string> created = AppendFootprintPieces(clone, context, artworkLayer);
        var candidate = PcbFootprintPlacementTools.BuildPlan(document, expected, clone);
        CallToolResult applied = await new PcbFootprintPlacementTools(registry).Apply(instanceId,
            candidate.CandidateRequestJson, SchematicJson.Formatter.Format(expected), context.OperationId, token);
        if (applied.IsError ?? false) return Data(new
        {
            operationId = context.OperationId, targetKind = "footprint", accepted = false,
            nativeResult = applied.StructuredContent, geometrySha256 = context.Proposal.GeometrySha256,
            sourceSha256 = context.SourceSha256, provenanceAtomic = true,
            provenance = context.Provenance, provenanceVerified = context.Provenance is not null,
            electricalValidated = false, manufacturingValidated = false
        }, true);
        return Data(new
        {
            operationId = context.OperationId, targetKind = "footprint", accepted = true, createdIds = created,
            targetFootprintId = target.TargetFootprintId, nativeResult = applied.StructuredContent,
            geometrySha256 = context.Proposal.GeometrySha256, sourceSha256 = context.SourceSha256,
            embeddedSourceVerified = true, provenanceAtomic = true, electricalValidated = false,
            provenance = context.Provenance, provenanceVerified = context.Provenance is not null,
            manufacturingValidated = false, receiptScope = "mcp-process"
        });
    }

    private async Task<CallToolResult> ProjectBoard(string instanceId, RasterTraceProjectionInput input,
        ProjectionContext context, CancellationToken token)
    {
        RasterTraceBoardTargetInput target = input.Board
            ?? throw new AutomationException("invalid_raster_projection_target", "Board target fields are required.");
        DocumentSpecifier document = NativeDocumentInput.ToNative(target.Document);
        DocumentLifecycleState expected = DocumentLifecycleInput.ToNative(target.ExpectedState);
        if (document.Type != DocumentType.DoctypePcb || !document.Equals(expected.Document)
            || expected.Scope != DocumentLifecycleScope.DlsPcb || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_raster_projection_state", "Board target state must identify the same PCB and process epoch.");
        BoardLayer layer = ParseLayer(target.Layer);
        ValidateArtworkLayer(layer);
        ArtworkSourceArchive.Receipt archive = ArchiveSource(target.RepositoryRoot!, target.SourceArchivePath!,
            context.SourceBytes, context.SourceSha256);
        string archiveBinding = ArtworkSourceArchive.TransactionBinding(archive);
        var create = new CreateItems { Header = new() { Document = document } };
        List<string> created = [];
        for (int index = 0; index < context.Proposal.FilledPieces.Count; index++)
        {
            string id = DeterministicId(context.Proposal.GeometrySha256, "board", index);
            BoardGraphicShape shape = NewBoardShape(id, context.Proposal.FilledPieces[index], context,
                layer, target.OriginXNm, target.OriginYNm, archive);
            create.Items.Add(Any.Pack(shape)); created.Add(id);
        }
        string requestJson = BoardJson.Formatter.Format(create);
        string expectedJson = SchematicJson.Formatter.Format(expected);
        string fingerprint = PcbArtworkReceiptStore.Fingerprint(
            "pcb-raster-artwork\n" + instanceId + "\n" + requestJson + "\n" + expectedJson
            + "\n" + context.OperationId + "\n" + archiveBinding);
        PcbArtworkReceiptStore receiptStore = new(registry.StateDirectory);
        CheckedPcbItemTransaction checkedRequest = PcbArtworkTools.BuildCheckedRequest(
            expected, create, context.OperationId, archive, "MCP raster trace PCB artwork");
        await BoardGate.WaitAsync(token);
        try
        {
            // The archive is a separate filesystem persistence boundary.  It
            // must still be readable and byte-identical immediately before a
            // native replay or first dispatch.
            ArtworkSourceArchive.Verify(target.RepositoryRoot!, archive,
                "invalid_raster_projection_archive", "raster_trace_archive_mismatch");
            PcbArtworkReceiptReservation reservation = receiptStore.Reserve(
                instanceId, expected.ProcessEpoch, context.OperationId, fingerprint, archive);
            if (!reservation.Created)
            {
                if (reservation.Receipt.Outcome == PcbArtworkReceiptRecord.Terminal)
                {
                    CallToolResult replay = PcbArtworkReceiptStore.ToResult(reservation.Receipt);
                    if (replay.StructuredContent is JsonElement payload)
                        PcbArtworkContract.ValidateReceiptIdentity(payload, instanceId, expected.ProcessEpoch);
                    return replay;
                }

                if (reservation.Receipt.Outcome == PcbArtworkReceiptRecord.Admitted)
                {
                    PcbCheckedItemDispatch.Outcome inspected = await PcbCheckedItemDispatch.InspectAsync(
                        registry, instanceId, checkedRequest, token);
                    if (inspected.Receipt is null
                        || inspected.Receipt.State == CheckedPcbItemTransactionState.CpbtsNotFound)
                    {
                        string message = inspected.ErrorMessage
                            ?? $"Raster PCB projection {context.OperationId} has an admitted durable boundary but no terminal native receipt; inspect the board before retrying.";
                        if (inspected.ErrorCode is null)
                        {
                            receiptStore.WriteUnknown(instanceId, expected.ProcessEpoch, context.OperationId,
                                fingerprint, archive, "operation_outcome_unknown", message);
                        }
                        throw new AutomationException("operation_outcome_unknown", message);
                    }

                    inspected.Receipt.Replayed = true;
                    bool reconciledArchiveVerified = true;
                    AutomationException? reconciledArchiveError = null;
                    try
                    {
                        ArtworkSourceArchive.Verify(target.RepositoryRoot!, archive,
                            "invalid_raster_projection_archive", "raster_trace_archive_mismatch");
                    }
                    catch (AutomationException error)
                    {
                        reconciledArchiveVerified = false;
                        reconciledArchiveError = error;
                    }
                    PcbArtworkTools.CompensatingRollback reconciledCompensation = !reconciledArchiveVerified
                        && inspected.Completed
                        ? await PcbArtworkTools.TryCompensatingRollbackAsync(registry, instanceId, expected,
                            inspected.Receipt!, created, context.OperationId, CancellationToken.None)
                        : PcbArtworkTools.CompensatingRollback.NotAttempted;
                    CallToolResult reconciled = BoardProjectionData(inspected, instanceId, document,
                        expected, context, archive, archiveBinding, layer, created,
                        reconciledArchiveVerified, reconciledArchiveError, reconciledCompensation,
                        durableReconciled: true);
                    if (inspected.UntrustedReceipt)
                    {
                        receiptStore.WriteUnknown(instanceId, expected.ProcessEpoch, context.OperationId,
                            fingerprint, archive, inspected.ErrorCode ?? "invalid_checked_pcb_receipt",
                            inspected.ErrorMessage ?? "The native raster PCB projection receipt was invalid and cannot be trusted.");
                    }
                    else if (!reconciledArchiveVerified && inspected.Completed && !reconciledCompensation.Confirmed)
                    {
                        receiptStore.WriteUnknown(instanceId, expected.ProcessEpoch, context.OperationId,
                            fingerprint, archive, reconciledCompensation.ErrorCode ?? reconciledArchiveError?.Code
                                ?? "raster_trace_archive_mismatch",
                            reconciledCompensation.ErrorMessage ?? reconciledArchiveError?.Message
                                ?? "The native raster PCB projection committed but compensating rollback was not confirmed; inspect the board before retrying.");
                    }
                    else if (inspected.NativeReceiptAvailable)
                    {
                        receiptStore.WriteTerminal(instanceId, expected.ProcessEpoch, context.OperationId,
                            fingerprint, archive, reconciledCompensation.Confirmed ? "compensated" : inspected.Receipt.State.ToString(),
                            reconciledArchiveVerified, reconciled);
                    }
                    return reconciled;
                }

                throw new AutomationException("operation_outcome_unknown",
                    reservation.Receipt.Outcome == PcbArtworkReceiptRecord.Unknown
                        ? "The raster PCB projection reached the native boundary but has no terminal receipt; inspect the board before retrying."
                        : "Another MCP process has admitted this raster PCB projection; reconcile its native outcome before retrying.");
            }
            try
            {
                // Keep the archive/request identity exact at the admission
                // boundary. If the source changed before native dispatch,
                // release only this still-admitted reservation.
                ArtworkSourceArchive.Verify(target.RepositoryRoot!, archive,
                    "invalid_raster_projection_archive", "raster_trace_archive_mismatch");
            }
            catch
            {
                try { receiptStore.RemoveAdmission(instanceId, expected.ProcessEpoch, context.OperationId, fingerprint, archive); }
                catch (AutomationException) { }
                throw;
            }
            PcbCheckedItemDispatch.Outcome outcome;
            try
            {
                outcome = await PcbCheckedItemDispatch.ApplyAsync(
                    registry, instanceId, checkedRequest, token);
            }
            catch (OperationCanceledException)
            {
                receiptStore.WriteUnknown(instanceId, expected.ProcessEpoch, context.OperationId,
                    fingerprint, archive, "operation_outcome_unknown",
                    $"Raster PCB projection {context.OperationId} was cancelled after admission; inspect the board before retrying.");
                throw new AutomationException("operation_outcome_unknown",
                    $"Raster PCB projection {context.OperationId} was cancelled after admission; inspect the board before retrying.");
            }
            bool archiveVerified = true;
            AutomationException? archiveError = null;
            try
            {
                // A source archive can be changed independently while KiCad
                // is committing.  Surface that mismatch with the native
                // receipt intact and keep provenanceAtomic false; do not
                // report an authoritative projection in that case.
                ArtworkSourceArchive.Verify(target.RepositoryRoot!, archive,
                    "invalid_raster_projection_archive", "raster_trace_archive_mismatch");
            }
            catch (AutomationException error)
            {
                archiveVerified = false;
                archiveError = error;
            }
            PcbArtworkTools.CompensatingRollback compensation = !archiveVerified && outcome.Completed
                ? await PcbArtworkTools.TryCompensatingRollbackAsync(registry, instanceId, expected,
                    outcome.Receipt!, created, context.OperationId, CancellationToken.None)
                : PcbArtworkTools.CompensatingRollback.NotAttempted;
            CallToolResult result = BoardProjectionData(outcome, instanceId, document,
                expected, context, archive, archiveBinding, layer, created,
                archiveVerified, archiveError, compensation);
            if (outcome.UntrustedReceipt)
            {
                receiptStore.WriteUnknown(instanceId, expected.ProcessEpoch, context.OperationId,
                    fingerprint, archive, outcome.ErrorCode ?? "invalid_checked_pcb_receipt",
                    outcome.ErrorMessage ?? "The native raster PCB projection receipt was invalid and cannot be trusted.");
            }
            else if (!archiveVerified && outcome.Completed && !compensation.Confirmed)
            {
                // The board transaction is known to have committed, but the
                // source archive is no longer byte-identical. Do not publish
                // a terminal receipt that could be replayed as success: the
                // durable recovery state blocks another dispatch and tells
                // the caller that native geometry may require inspection.
                receiptStore.WriteUnknown(instanceId, expected.ProcessEpoch, context.OperationId,
                    fingerprint, archive, compensation.ErrorCode ?? archiveError?.Code ?? "raster_trace_archive_mismatch",
                    compensation.ErrorMessage ?? $"Raster PCB projection {context.OperationId} committed native board geometry, but source archive verification failed and compensating rollback was not confirmed; inspect the board and archive before retrying. {archiveError?.Message}");
            }
            else if (outcome.NativeReceiptAvailable)
            {
                receiptStore.WriteTerminal(instanceId, expected.ProcessEpoch, context.OperationId,
                    fingerprint, archive, compensation.Confirmed ? "compensated" : outcome.Receipt?.State.ToString() ?? "native-result",
                    archiveVerified, result);
            }
            else if (PcbArtworkTools.ProvenPreNativeRejection(outcome))
            {
                receiptStore.RemoveAdmission(instanceId, expected.ProcessEpoch, context.OperationId,
                    fingerprint, archive);
            }
            else
            {
                receiptStore.WriteUnknown(instanceId, expected.ProcessEpoch, context.OperationId,
                    fingerprint, archive, outcome.ErrorCode ?? "operation_outcome_unknown",
                    outcome.ErrorMessage ?? "The raster PCB projection outcome is unresolved; inspect the board before retrying.");
            }
            return result;
        }
        finally { BoardGate.Release(); }
    }

    private async Task<CallToolResult> ProjectSchematic(string instanceId, RasterTraceProjectionInput input,
        ProjectionContext context, CancellationToken token)
    {
        RasterTraceSchematicTargetInput target = input.Schematic
            ?? throw new AutomationException("invalid_raster_projection_target", "Schematic target fields are required.");
        DocumentSpecifier document = NativeDocumentInput.ToNative(target.Document);
        DocumentLifecycleState expected = DocumentLifecycleInput.ToNative(target.ExpectedState);
        if (document.Type != DocumentType.DoctypeSchematic || !document.Equals(expected.Document)
            || expected.Scope != DocumentLifecycleScope.DlsSchematicHierarchy || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_raster_projection_state", "Schematic target state must identify the same schematic and process epoch.");
        RequireSchematicQuantum(context.Proposal, target.OriginXNm, target.OriginYNm);
        ValidateUuid(target.ManifestGroupId, "ManifestGroupId");
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(token);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("schematic_instance_changed", "The schematic checkpoint belongs to another native process epoch.");
        CheckedSchematicState checkedState = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = document, ProcessEpoch = session.Epoch }, token);
        CheckedSchematicContract.ValidateObservation(checkedState, document, session.Epoch);
        if (!checkedState.State.Equals(expected))
            throw new AutomationException("stale_schematic_state", "The schematic changed before raster projection; inspect it again.");
        SchematicScreenData rootScreen = checkedState.Electrical.Hierarchy.Data.Instances
            .FirstOrDefault(screen => screen.Metadata?.Document?.Equals(document) == true)
            ?? throw new AutomationException("schematic_root_missing", "The checked schematic root screen was not returned.");
        EmbeddedFiles files = rootScreen.Metadata!.EmbeddedFiles?.Clone() ?? new();
        EmbeddedFile sourceFile = EmbeddedFileCodec.FromBytes(context.EmbeddedFileName, context.SourceBytes);
        ExistingEmbeddedFile(files, sourceFile.Name, sourceFile.DataHash);
        files.Files.Add(sourceFile);
        ApplySchematicItemBatch batch = new()
        {
            Document = document.Clone(), OperationId = context.OperationId,
            DocumentEpoch = expected.Revision.Epoch, ExpectedRevision = expected.Revision.Clone(),
            Description = "MCP raster trace filled artwork projection"
        };
        List<string> created = [];
        for (int index = 0; index < context.Proposal.FilledPieces.Count; index++)
        {
            string id = DeterministicId(context.Proposal.GeometrySha256, "schematic", index);
            batch.Operations.Add(new SchematicItemOperation
            {
                Create = Any.Pack(NewSchematicShape(id, context.Proposal.FilledPieces[index], context,
                    target.OriginXNm, target.OriginYNm))
            });
            created.Add(id);
        }
        Kiapi.Schematic.Types.Group group = new() { Id = new() { Value = target.ManifestGroupId }, Name = "Raster trace artwork", Locked = LockedState.LsUnlocked };
        group.Items.Add(created.Select(id => new KIID { Value = id }));
        // Graphic custom properties are currently dropped by the native
        // schematic serializer.  Keep the bounded trace manifest on the
        // group, whose custom properties survive save/reload, so the group is
        // the authoritative durable provenance owner for the projected CAD
        // geometry.
        AddManifestProperties(group.CustomProperties, context, includeManifest: true);
        batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(group) });
        batch.Operations.Add(new SchematicItemOperation
        {
            ReplaceEmbeddedFiles = new SchematicEmbeddedFileState { Files = files, EmbeddedFonts = rootScreen.Metadata.EmbeddedFonts }
        });
        var request = new CheckedSchematicBatch { Batch = batch, ExpectedState = expected.Clone() };
        CheckedSchematicTools.ValidateRequest(request, session.Epoch);
        CheckedSchematicBatchReceipt receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, token);
        CheckedSchematicTools.ValidateResult(request, receipt, inspect: false);
        JsonElement receiptJson = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(receipt));
        bool completed = receipt.Status == CheckedSchematicBatchStatus.CsbsCompleted;
        return Data(new
        {
            operationId = context.OperationId, targetKind = "schematic", accepted = completed, createdIds = created,
            receipt = receiptJson, geometrySha256 = context.Proposal.GeometrySha256, sourceSha256 = context.SourceSha256,
            embeddedSourceVerified = completed, provenanceAtomic = completed,
            provenance = context.Provenance, provenanceVerified = context.Provenance is not null,
            electricalValidated = false, manufacturingValidated = false, designFilesSaved = false,
            limitation = "Save/reload remains a separate required step; this receipt proves one checked native batch only."
        }, !completed);
    }

    private static List<string> AppendSymbolPieces(SchematicSymbol definition, ProjectionContext context)
    {
        List<string> created = [];
        for (int index = 0; index < context.Proposal.FilledPieces.Count; index++)
        {
            string id = DeterministicId(context.Proposal.GeometrySha256, "symbol", index);
            definition.Items.Add(new SchematicSymbolChild { Item = Any.Pack(NewSchematicShape(id,
                context.Proposal.FilledPieces[index], context, 0, 0)) });
            created.Add(id);
        }
        return created;
    }

    private static List<string> AppendFootprintPieces(FootprintInstance footprint,
        ProjectionContext context, BoardLayer layer)
    {
        List<string> created = [];
        for (int index = 0; index < context.Proposal.FilledPieces.Count; index++)
        {
            string id = DeterministicId(context.Proposal.GeometrySha256, "footprint", index);
            footprint.Definition.Items.Add(Any.Pack(NewBoardShape(id, context.Proposal.FilledPieces[index],
                context, layer, 0, 0, null)));
            created.Add(id);
        }
        return created;
    }

    private static SchematicGraphicShape NewSchematicShape(string id, KiCad.Automation.Protocol.RasterTraceFilledPiece piece,
        ProjectionContext context, long originX, long originY)
    {
        var shape = new SchematicGraphicShape
        {
            Id = new() { Value = id },
            Locked = LockedState.LsUnlocked,
            Shape = new GraphicShape
            {
                Attributes = FilledAttributes(),
                Polygon = new PolySet { Polygons = { Polygon(piece, originX, originY) } }
            }
        };
        AddProvenanceProperties(shape.CustomProperties, context, piece.Id);
        return shape;
    }

    private static BoardGraphicShape NewBoardShape(string id, KiCad.Automation.Protocol.RasterTraceFilledPiece piece,
        ProjectionContext context, BoardLayer layer, long originX, long originY, ArtworkSourceArchive.Receipt? archive)
    {
        var shape = new BoardGraphicShape
        {
            Id = new() { Value = id }, Layer = layer,
            Shape = new GraphicShape
            {
                Attributes = FilledAttributes(),
                Polygon = new PolySet { Polygons = { Polygon(piece, originX, originY) } }
            }
        };
        AddProvenanceProperties(shape.CustomProperties, context, piece.Id);
        if (archive is not null)
        {
            shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.source_archive", Value = archive.RelativePath });
            shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.source_archive_receipt", Value = archive.ReceiptId });
            shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.source_archive_bytes", Value = archive.Bytes.ToString(CultureInfo.InvariantCulture) });
        }
        return shape;
    }

    private static GraphicAttributes FilledAttributes() => new()
    {
        Stroke = new StrokeAttributes { Width = new Distance { ValueNm = 0 } },
        Fill = new GraphicFillAttributes { FillType = GraphicFillType.GftFilled }
    };

    private static PolygonWithHoles Polygon(KiCad.Automation.Protocol.RasterTraceFilledPiece piece, long originX, long originY)
    {
        var polygon = new PolygonWithHoles { Outline = new PolyLine { Closed = true } };
        polygon.Outline.Nodes.Add(piece.Points.Select(point => new PolyLineNode
        {
            Point = new Vector2 { XNm = checked(point.XNm + originX), YNm = checked(point.YNm + originY) }
        }));
        return polygon;
    }

    private static SchematicField ManifestField(ProjectionContext context, bool privateField)
    {
        string manifest = Manifest(context);
        var field = new SchematicField
        {
            Name = "kicad.ai.raster_trace_manifest", Visible = false, ShowName = false,
            IsPrivate = privateField,
            Text = new Text { Text_ = manifest, Attributes = new TextAttributes { Multiline = true, Size = new Vector2 { XNm = 1_000_000, YNm = 1_000_000 } } }
        };
        AddManifestProperties(field.CustomProperties, context);
        return field;
    }

    private static string Manifest(ProjectionContext context)
    {
        var value = new
        {
            schema = "kicad.ai.raster_trace.v1", sourceSha256 = context.SourceSha256,
            sourceBytes = context.Proposal.SourceBytes, sourceKind = context.SourceKind,
            sourceOrigin = context.SourceOrigin, embeddedFile = context.EmbeddedFileName,
            provenance = context.Provenance,
            geometrySha256 = context.Proposal.GeometrySha256, contourCount = context.Proposal.ContourCount,
            filledPieceCount = context.Proposal.FilledPieceCount, contributorSemantics = context.Proposal.ContributorSemantics,
            coordinateOrientation = context.Proposal.CoordinateOrientation, rounding = context.Proposal.Rounding,
            contours = context.Proposal.Contours!.Select(c => new { c.Id, c.ParentId, c.Hole, c.Orientation }).ToArray(),
            pieces = context.Proposal.FilledPieces!.Select(p => new { p.Id, p.SourceContourIds, p.SourceContoursAreWholeTrace }).ToArray()
        };
        string json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (json.Length > 2_000_000)
            throw new AutomationException("raster_trace_manifest_oversized", "The bounded topology manifest exceeds 2 MiB.");
        return json;
    }

    private static void AddManifestProperties(ICollection<CustomProperty> properties,
        ProjectionContext context, bool includeManifest = false)
    {
        properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.source_sha256", Value = context.SourceSha256 });
        properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.geometry_sha256", Value = context.Proposal.GeometrySha256 });
        properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.source_kind", Value = context.SourceKind });
        if (context.Provenance is not null)
        {
            properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.asset_id", Value = context.Provenance.AssetId });
            properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.source_bytes", Value = context.Provenance.SourceBytes.ToString(CultureInfo.InvariantCulture) });
            properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.generator", Value = context.Provenance.Generator });
            properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.manifest_schema", Value = context.Provenance.ManifestSchema });
        }
        if (!string.IsNullOrWhiteSpace(context.SourceOrigin))
            properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.source_origin", Value = context.SourceOrigin });
        properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.role", Value = "filled-vector-projection" });
        properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.contributor_semantics", Value = context.Proposal.ContributorSemantics });
        if (includeManifest)
            properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.manifest", Value = Manifest(context) });
    }

    private static void AddProvenanceProperties(ICollection<CustomProperty> properties,
        ProjectionContext context, string pieceId)
    {
        AddManifestProperties(properties, context);
        properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.piece_id", Value = pieceId });
        properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.filled", Value = "true" });
    }

    internal static void ValidateProposal(ProtocolRasterTraceResult proposal)
    {
        if (!proposal.Accepted || proposal.FilledPieces.Count == 0 || proposal.Contours.Count == 0
            || proposal.GeometrySha256.Length != 64 || proposal.GeometrySha256.Any(c => !Uri.IsHexDigit(c))
            || proposal.SourceSha256.Length != 64 || proposal.SourceSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new AutomationException("invalid_raster_trace_proposal", "Only an accepted, complete raster trace proposal can be projected.");
        if (proposal.CoordinateOrientation != "image_x_right_y_down" || proposal.Rounding != "nearest_integer")
            throw new AutomationException("invalid_raster_trace_proposal", "The proposal coordinate convention is unsupported.");
        if (proposal.FilledPieceCount != proposal.FilledPieces.Count || proposal.ContourCount != proposal.Contours.Count)
            throw new AutomationException("invalid_raster_trace_proposal", "Proposal counts do not match its retained geometry.");

        HashSet<string> retainedContourIds = proposal.Contours.Select(contour => contour.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (KiCad.Automation.Protocol.RasterTraceFilledPiece piece in proposal.FilledPieces)
        {
            if (!piece.SourceContoursAreWholeTrace || piece.Points.Count < 3
                || piece.Points.Any(point => !double.IsFinite(point.XNm) || !double.IsFinite(point.YNm)))
                throw new AutomationException("invalid_raster_trace_proposal", "Every filled piece must be a bounded closed whole-trace proposal with finite points.");

            for (int index = 0; index < piece.Points.Count; index++)
            {
                KiCad.Automation.Protocol.RasterTracePoint current = piece.Points[index];
                KiCad.Automation.Protocol.RasterTracePoint next = piece.Points[(index + 1) % piece.Points.Count];
                if (current.XNm == next.XNm && current.YNm == next.YNm)
                    throw new AutomationException("invalid_raster_trace_proposal", "Filled piece vertices must not repeat consecutively.");
            }

            if (piece.Bounds is null || piece.Bounds.MinXNm >= piece.Bounds.MaxXNm
                || piece.Bounds.MinYNm >= piece.Bounds.MaxYNm
                || piece.Points.Any(point => point.XNm < piece.Bounds.MinXNm || point.XNm > piece.Bounds.MaxXNm
                    || point.YNm < piece.Bounds.MinYNm || point.YNm > piece.Bounds.MaxYNm))
                throw new AutomationException("invalid_raster_trace_proposal", "Filled piece bounds must be ordered and contain every point.");

            Int128 signedAreaTwice = 0;
            for (int index = 0; index < piece.Points.Count; index++)
            {
                KiCad.Automation.Protocol.RasterTracePoint current = piece.Points[index];
                KiCad.Automation.Protocol.RasterTracePoint next = piece.Points[(index + 1) % piece.Points.Count];
                signedAreaTwice += (Int128) current.XNm * next.YNm - (Int128) next.XNm * current.YNm;
            }
            if (signedAreaTwice == 0)
                throw new AutomationException("invalid_raster_trace_proposal", "Filled piece geometry must have nonzero signed area.");

            if (piece.SourceContourIds.Any(id => !retainedContourIds.Contains(id)))
                throw new AutomationException("invalid_raster_trace_proposal", "Filled pieces must reference retained source contours.");
        }
    }

    private static void ValidateSymbolTarget(RasterTraceSymbolTargetInput target)
    {
        if (string.IsNullOrWhiteSpace(target.LibraryNickname) || string.IsNullOrWhiteSpace(target.SymbolName)
            || target.LibraryNickname.Contains('\0') || target.SymbolName.Contains('\0'))
            throw new AutomationException("invalid_raster_projection_target", "Symbol library and entry names are required and NUL-free.");
        if (string.IsNullOrWhiteSpace(target.ExpectedModifyHash)
            || target.ExpectedModifyHash.Any(c => !char.IsDigit(c)))
            throw new AutomationException("invalid_raster_projection_state", "ExpectedModifyHash must be a decimal native library modify hash.");
    }

    private static void ValidateFootprintLibraryIdentity(string? libraryNickname, string? footprintName)
    {
        if (string.IsNullOrWhiteSpace(libraryNickname) || string.IsNullOrWhiteSpace(footprintName)
            || libraryNickname.Contains('\0') || footprintName.Contains('\0'))
            throw new AutomationException("invalid_raster_projection_identity",
                "Footprint projection requires a library nickname and footprint name without NUL characters.");
    }

    private static void ValidateUuid(string? value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out Guid id) || id == Guid.Empty || value != id.ToString("D"))
            throw new AutomationException("invalid_raster_projection_identity", name + " must be a canonical nonempty UUID.");
    }

    private static void RequireSchematicQuantum(ProtocolRasterTraceResult proposal, long originX, long originY)
    {
        if (originX % 100 != 0 || originY % 100 != 0
            || proposal.FilledPieces.Any(piece => piece.Points.Any(point => point.XNm % 100 != 0 || point.YNm % 100 != 0)))
            throw new AutomationException("raster_trace_schematic_quantum", "Schematic projection requires every coordinate to be on the native 100 nm grid.");
    }

    private static BoardLayer ParseLayer(string value) => value switch
    {
        "F.Cu" => BoardLayer.BlFCu, "B.Cu" => BoardLayer.BlBCu,
        "F.SilkS" => BoardLayer.BlFSilkS, "B.SilkS" => BoardLayer.BlBSilkS,
        "F.Fab" => BoardLayer.BlFFab, "B.Fab" => BoardLayer.BlBFab,
        "F.CrtYd" => BoardLayer.BlFCrtYd, "B.CrtYd" => BoardLayer.BlBCrtYd,
        "Dwgs.User" => BoardLayer.BlDwgsUser, "Cmts.User" => BoardLayer.BlCmtsUser,
        "Eco1.User" => BoardLayer.BlEco1User, "Eco2.User" => BoardLayer.BlEco2User,
        _ when System.Enum.TryParse<BoardLayer>(value, true, out BoardLayer layer) => layer,
        _ => throw new AutomationException("invalid_raster_projection_layer", "The requested KiCad layer is not recognized.")
    };

    private static void ValidateArtworkLayer(BoardLayer layer)
    {
        if (layer is BoardLayer.BlUnknown or BoardLayer.BlUndefined
            || (int)layer is >= 3 and <= 34
            || layer is BoardLayer.BlFCrtYd or BoardLayer.BlBCrtYd)
            throw new AutomationException("invalid_raster_projection_layer", "Raster artwork must use a concrete non-copper, non-courtyard layer.");
    }

    private static string DeterministicId(string geometry, string target, int index)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(geometry + "\n" + target + "\n" + index.ToString(CultureInfo.InvariantCulture)));
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }

    private static void ExistingEmbeddedFile(EmbeddedFiles files, string name, string hash)
    {
        EmbeddedFile? existing = files.Files.FirstOrDefault(file => file.Name == name);
        if (existing is not null && !string.Equals(existing.DataHash, hash, StringComparison.OrdinalIgnoreCase))
            throw new AutomationException("raster_trace_embedded_conflict", "The target already contains a different embedded raster source with this name.");
        if (existing is not null) files.Files.Remove(existing);
    }

    private static void ExistingEmbeddedFile(EmbeddedFiles files, string name, string hash, bool preserveExisting)
        => ExistingEmbeddedFile(files, name, hash);

    private static ArtworkSourceArchive.Receipt ArchiveSource(string repositoryRoot, string relative,
        byte[] bytes, string hash)
        => ArtworkSourceArchive.Persist(repositoryRoot, relative, bytes, hash,
            "invalid_raster_projection_archive", "raster_trace_source_conflict");

    private static JsonElement Property(JsonElement value, string name)
        => value.TryGetProperty(name, out JsonElement result) ? result : throw new AutomationException("invalid_raster_projection_result", "Native result omitted " + name + ".");

    private static void EnsureSuccess(CallToolResult result, string code)
    {
        if (result.IsError ?? false)
            throw new AutomationException(code, result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "Native authoring operation failed.");
    }

    private static CallToolResult BoardProjectionData(
        PcbCheckedItemDispatch.Outcome outcome,
        string instanceId,
        DocumentSpecifier document,
        DocumentLifecycleState expected,
        ProjectionContext context,
        ArtworkSourceArchive.Receipt archive,
        string archiveBinding,
        BoardLayer layer,
        IReadOnlyList<string> created,
        bool archiveVerified,
        AutomationException? archiveError,
        PcbArtworkTools.CompensatingRollback compensation,
        bool durableReconciled = false)
    {
        CheckedPcbItemTransactionReceipt? receipt = outcome.Receipt;
        bool mutationConfirmed = outcome.Completed;
        JsonElement? receiptJson = receipt is null || outcome.UntrustedReceipt
            ? null : JsonDocument.Parse(SchematicJson.Formatter.Format(receipt)).RootElement.Clone();
        string? before = receipt?.ObservedBefore is null || outcome.UntrustedReceipt
            ? null : SchematicJson.Formatter.Format(receipt.ObservedBefore);
        string? after = receipt?.ObservedAfter is null || outcome.UntrustedReceipt
            ? receipt?.ObservedRollback is null || outcome.UntrustedReceipt
                ? null : SchematicJson.Formatter.Format(receipt.ObservedRollback)
            : SchematicJson.Formatter.Format(receipt.ObservedAfter);
        JsonElement? response = null;
        if (!outcome.UntrustedReceipt && receipt is not null && receipt.Results.Count > 0
            && receipt.Results[0].ResultCase == PcbItemOperationResult.ResultOneofCase.Create)
            response = JsonDocument.Parse(BoardJson.Formatter.Format(receipt.Results[0].Create)).RootElement.Clone();
        bool compensated = compensation.Confirmed;
        string operationOutcome = compensated ? "compensated" : mutationConfirmed ? "completed"
            : outcome.UntrustedReceipt ? "indeterminate" : outcome.ErrorCode ?? receipt?.State.ToString() ?? "not_submitted";
        JsonElement nativeResult = JsonSerializer.SerializeToElement(new
        {
            instanceId,
            processEpoch = receipt?.ProcessEpoch ?? outcome.Request.ProcessEpoch,
            operationId = context.OperationId,
            document,
            operationOutcome,
            state = outcome.UntrustedReceipt
                ? CheckedPcbItemTransactionState.CpbtsIndeterminate.ToString() : receipt?.State.ToString(),
            beforeState = before,
            afterState = after,
            nativeReceiptAvailable = outcome.NativeReceiptAvailable,
            receipt = receiptJson,
            mutationConfirmed = mutationConfirmed && !compensated,
            response
        });
        bool accepted = mutationConfirmed && archiveVerified;
        return Data(new
        {
            instanceId,
            processEpoch = receipt?.ProcessEpoch ?? expected.ProcessEpoch,
            operationId = context.OperationId,
            targetKind = "board",
            accepted,
            createdIds = accepted ? created : Array.Empty<string>(),
            nativeResult,
            receipt = receiptJson,
            nativeReceipt = receiptJson,
            sourceArchive = archive.RelativePath,
            archiveSha256 = archive.Sha256,
            archiveBytes = archive.Bytes,
            archiveReceiptId = archive.ReceiptId,
            archiveBinding,
            archivePreflight = new
            {
                persisted = archive.Persisted,
                sha256 = archive.Sha256,
                bytes = archive.Bytes,
                exactBytesVerified = archive.ExactBytesVerified,
                atomicWrite = archive.AtomicWrite,
                boardTransactionJoined = false,
                readbackVerified = archiveVerified,
                mismatchCode = archiveError?.Code,
                compensatingRollbackAttempted = compensation.Attempted,
                compensatingRollbackConfirmed = compensation.Confirmed,
                compensatingRollbackOperationId = compensation.OperationId.Length == 0 ? null : compensation.OperationId,
                compensatingRollbackErrorCode = compensation.ErrorCode,
                compensatingRollbackErrorMessage = compensation.ErrorMessage
            },
            geometrySha256 = context.Proposal.GeometrySha256,
            sourceSha256 = context.SourceSha256,
            provenance = context.Provenance,
            provenanceVerified = context.Provenance is not null,
            embeddedSourceVerified = false,
            archivePersisted = archive.Persisted,
            archiveAtomicWrite = archive.AtomicWrite,
            provenanceAtomic = compensated,
            archiveReadbackVerified = archiveVerified,
            electricalValidated = false,
            manufacturingValidated = false,
            state = outcome.UntrustedReceipt
                ? CheckedPcbItemTransactionState.CpbtsIndeterminate.ToString() : receipt?.State.ToString(),
            operationOutcome,
            replayed = receipt?.Replayed ?? false,
            nativeReceiptAvailable = outcome.NativeReceiptAvailable,
            nativeMutationConfirmed = mutationConfirmed && !compensated,
            mutationConfirmed = mutationConfirmed && !compensated,
            operationReceipt = outcome.NativeReceiptAvailable,
            replayableWithinProcess = outcome.NativeReceiptAvailable,
            transportRetrySafe = outcome.NativeReceiptAvailable,
            receiptScope = outcome.NativeReceiptAvailable ? "native-process" : "none",
            errorCode = compensation.ErrorCode ?? archiveError?.Code ?? (accepted ? null : outcome.ErrorCode),
            errorMessage = compensation.ErrorMessage ?? archiveError?.Message ?? (accepted ? null : outcome.ErrorMessage),
            compensatingRollbackAttempted = compensation.Attempted,
            compensatingRollbackConfirmed = compensation.Confirmed,
            compensatingRollbackOperationId = compensation.OperationId.Length == 0 ? null : compensation.OperationId,
            compensatingRollbackReceipt = compensation.Outcome?.Receipt is { } rollbackReceipt
                ? JsonDocument.Parse(SchematicJson.Formatter.Format(rollbackReceipt)).RootElement.Clone() : (JsonElement?)null,
            durableReconciled,
            limitation = compensated
                ? "The source archive mismatch was contained by a checked compensating delete that restored the original PCB checkpoint; the source archive remains a separate persistence boundary."
                : "Board embedded-file persistence is a separate native command; the durable archive preflight and per-shape SHA remain a separate persistence boundary."
        }, !accepted);
    }

    private static CallToolResult Data(object value, bool error = false)
    {
        JsonElement data = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new() { IsError = error, StructuredContent = data,
            Content = [new TextContentBlock { Text = data.GetRawText() }] };
    }

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or IOException or InvalidProtocolBufferException or InvalidOperationException or JsonException or OverflowException)
        {
            string code = error switch
            {
                AutomationException known => known.Code,
                NativeApiException native => "native_status_" + native.Status,
                NngException transport => transport.RequestDelivered ? "operation_outcome_unknown" : "native_not_reached",
                _ => "raster_projection_failed"
            };
            return Data(new { accepted = false, errorCode = code, errorMessage = error.Message,
                nativeMutationConfirmed = false, electricalValidated = false, manufacturingValidated = false }, true);
        }
    }

    private sealed record ProjectionContext(ProtocolRasterTraceResult Proposal, byte[] SourceBytes,
        string SourceSha256, string EmbeddedFileName, string SourceKind, string SourceOrigin, string OperationId,
        RasterTraceProvenanceInput? Provenance);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RasterTraceProjectionInput
{
    public string? OperationId { get; set; }
    public RasterTraceProjectionTargetKind TargetKind { get; set; }
    public RasterTraceProposalInput? Proposal { get; set; }
    public string? SourcePath { get; set; }
    public string? SourceKind { get; set; }
    public string? SourceOrigin { get; set; }
    public RasterTraceProvenanceInput? Provenance { get; set; }
    public string? ExpectedProcessEpoch { get; set; }
    public RasterTraceSymbolTargetInput? Symbol { get; set; }
    public RasterTraceFootprintTargetInput? Footprint { get; set; }
    public RasterTraceBoardTargetInput? Board { get; set; }
    public RasterTraceSchematicTargetInput? Schematic { get; set; }

    internal void Validate()
    {
        if (!Guid.TryParseExact(OperationId, "D", out Guid operation) || operation == Guid.Empty || OperationId != operation.ToString("D"))
            throw new AutomationException("invalid_operation_id", "OperationId must be a canonical nonempty UUID.");
        if (Proposal is null || string.IsNullOrWhiteSpace(SourcePath) || !Path.IsPathFullyQualified(SourcePath)
            || SourcePath.IndexOf('\0') >= 0 || !Path.GetExtension(SourcePath).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new AutomationException("invalid_raster_projection_source", "SourcePath must be an absolute PNG path.");
        if (SourceKind is not ("imagegen" or "human" or "generated"))
            throw new AutomationException("invalid_raster_projection_source", "SourceKind must be imagegen, human or generated.");
        if (SourceKind == "imagegen" && Provenance is null)
            throw new AutomationException("invalid_raster_provenance", "ImageGen raster projections require a typed provenance envelope.");
        if (TargetKind is RasterTraceProjectionTargetKind.Unknown)
            throw new AutomationException("invalid_raster_projection_target", "TargetKind is required.");
        int targets = (Symbol is null ? 0 : 1) + (Footprint is null ? 0 : 1) + (Board is null ? 0 : 1) + (Schematic is null ? 0 : 1);
        if (targets != 1)
            throw new AutomationException("invalid_raster_projection_target", "Exactly one target object is required.");
        if ((TargetKind == RasterTraceProjectionTargetKind.Symbol) != (Symbol is not null)
            || (TargetKind == RasterTraceProjectionTargetKind.Footprint) != (Footprint is not null)
            || (TargetKind == RasterTraceProjectionTargetKind.Board) != (Board is not null)
            || (TargetKind == RasterTraceProjectionTargetKind.Schematic) != (Schematic is not null))
            throw new AutomationException("invalid_raster_projection_target", "TargetKind does not match its oneof target object.");
        if (TargetKind == RasterTraceProjectionTargetKind.Board
            && (Board is null || string.IsNullOrWhiteSpace(Board.RepositoryRoot)
                || string.IsNullOrWhiteSpace(Board.SourceArchivePath)))
            throw new AutomationException("invalid_raster_projection_archive", "Board projection requires an absolute repository root and relative source archive path.");
        if (TargetKind == RasterTraceProjectionTargetKind.Symbol && string.IsNullOrWhiteSpace(ExpectedProcessEpoch))
            throw new AutomationException("invalid_process_epoch", "Symbol projection requires the native process epoch.");
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<RasterTraceProjectionTargetKind>))]
public enum RasterTraceProjectionTargetKind
{
    [JsonStringEnumMemberName("UNKNOWN")] Unknown = 0,
    [JsonStringEnumMemberName("SYMBOL")] Symbol = 1,
    [JsonStringEnumMemberName("FOOTPRINT")] Footprint = 2,
    [JsonStringEnumMemberName("BOARD")] Board = 3,
    [JsonStringEnumMemberName("SCHEMATIC")] Schematic = 4
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RasterTraceProposalInput
{
    public bool Accepted { get; set; }
    public string? RejectionCode { get; set; }
    public List<string>? Diagnostics { get; set; }
    public string? SourceSha256 { get; set; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public ulong SourceBytes { get; set; }
    public uint DecodedWidth { get; set; }
    public uint DecodedHeight { get; set; }
    public string? DecodedFormat { get; set; }
    public long PhysicalWidthNm { get; set; }
    public long PhysicalHeightNm { get; set; }
    public long ScaleNumerator { get; set; }
    public long ScaleDenominator { get; set; }
    public string? CoordinateOrientation { get; set; }
    public string? Rounding { get; set; }
    public uint Threshold { get; set; }
    public bool Invert { get; set; }
    public uint TurdSize { get; set; }
    public long CurveToleranceNm { get; set; }
    public uint MaxContours { get; set; }
    public uint MaxVertices { get; set; }
    public List<RasterTraceContourInput?>? Contours { get; set; }
    public List<RasterTraceFilledPieceInput?>? FilledPieces { get; set; }
    public RasterTraceBoundsInput? Bounds { get; set; }
    public string? GeometrySha256 { get; set; }
    public uint ContourCount { get; set; }
    public uint VertexCount { get; set; }
    public uint FilledPieceCount { get; set; }
    public double PotraceOptTolerancePx { get; set; }
    public string? ContributorSemantics { get; set; }
    public bool ProposalOnly { get; set; }
    public bool NativeDocumentMutated { get; set; }
    public bool CadProjectionCommitted { get; set; }
    public bool ElectricalValidated { get; set; }
    public bool ManufacturingValidated { get; set; }

    internal ProtocolRasterTraceResult ToNative()
    {
        var result = new ProtocolRasterTraceResult
        {
            Accepted = Accepted, SourceSha256 = SourceSha256 ?? "", SourceBytes = SourceBytes,
            DecodedWidth = DecodedWidth, DecodedHeight = DecodedHeight, PhysicalWidthNm = PhysicalWidthNm,
            PhysicalHeightNm = PhysicalHeightNm, ScaleNumerator = ScaleNumerator, ScaleDenominator = ScaleDenominator,
            CoordinateOrientation = CoordinateOrientation ?? "", Rounding = Rounding ?? "", Threshold = Threshold,
            Invert = Invert, TurdSize = TurdSize, CurveToleranceNm = CurveToleranceNm,
            MaxContours = MaxContours, MaxVertices = MaxVertices, GeometrySha256 = GeometrySha256 ?? "",
            ContourCount = ContourCount, VertexCount = VertexCount, FilledPieceCount = FilledPieceCount,
            PotraceOptTolerancePx = PotraceOptTolerancePx, ContributorSemantics = ContributorSemantics ?? "",
            Bounds = ToNative(Bounds)
        };
        foreach (RasterTraceContourInput? contour in Contours ?? [])
        {
            if (contour is null) continue;
            result.Contours.Add(new KiCad.Automation.Protocol.RasterTraceContour
            {
                Id = contour.Id ?? "", ParentId = contour.ParentId ?? "", Hole = contour.Hole, Orientation = contour.Orientation,
                Bounds = ToNative(contour.Bounds),
                Points = { (contour.Points ?? []).Where(point => point is not null).Select(point => new KiCad.Automation.Protocol.RasterTracePoint
                    { XNm = point!.XNm, YNm = point.YNm }) }
            });
        }
        foreach (RasterTraceFilledPieceInput? piece in FilledPieces ?? [])
        {
            if (piece is null) continue;
            result.FilledPieces.Add(new KiCad.Automation.Protocol.RasterTraceFilledPiece
            {
                Id = piece.Id ?? "", SourceContoursAreWholeTrace = piece.SourceContoursAreWholeTrace,
                SourceContourIds = { piece.SourceContourIds ?? [] },
                Bounds = ToNative(piece.Bounds),
                Points = { (piece.Points ?? []).Where(point => point is not null).Select(point => new KiCad.Automation.Protocol.RasterTracePoint
                    { XNm = point!.XNm, YNm = point.YNm }) }
            });
        }
        return result;
    }

    private static KiCad.Automation.Protocol.RasterTraceBounds? ToNative(RasterTraceBoundsInput? bounds) =>
        bounds is null ? null : new KiCad.Automation.Protocol.RasterTraceBounds
        {
            MinXNm = bounds.MinXNm, MinYNm = bounds.MinYNm, MaxXNm = bounds.MaxXNm, MaxYNm = bounds.MaxYNm
        };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RasterTraceContourInput
{
    public string? Id { get; set; }
    public string? ParentId { get; set; }
    public bool Hole { get; set; }
    public int Orientation { get; set; }
    public List<RasterTracePointInput?>? Points { get; set; }
    public RasterTraceBoundsInput? Bounds { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RasterTraceFilledPieceInput
{
    public string? Id { get; set; }
    public List<string>? SourceContourIds { get; set; }
    public List<RasterTracePointInput?>? Points { get; set; }
    public bool SourceContoursAreWholeTrace { get; set; }
    public RasterTraceBoundsInput? Bounds { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RasterTraceBoundsInput
{
    public long MinXNm { get; set; }
    public long MinYNm { get; set; }
    public long MaxXNm { get; set; }
    public long MaxYNm { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RasterTracePointInput
{
    public long XNm { get; set; }
    public long YNm { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RasterTraceSymbolTargetInput
{
    public string? LibraryNickname { get; set; }
    public string? SymbolName { get; set; }
    public string? ExpectedModifyHash { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RasterTraceFootprintTargetInput
{
    public NativeDocumentInput? Document { get; set; }
    public DocumentLifecycleInput? ExpectedState { get; set; }
    public string? SourceFootprintId { get; set; }
    public string? TargetFootprintId { get; set; }
    public string? LibraryNickname { get; set; }
    public string? FootprintName { get; set; }
    public long OriginXNm { get; set; }
    public long OriginYNm { get; set; }
    public double OrientationDegrees { get; set; }
    public string Layer { get; set; } = "F.Cu";
    public string ArtworkLayer { get; set; } = "F.SilkS";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RasterTraceBoardTargetInput
{
    public NativeDocumentInput? Document { get; set; }
    public DocumentLifecycleInput? ExpectedState { get; set; }
    public string Layer { get; set; } = "F.SilkS";
    public long OriginXNm { get; set; }
    public long OriginYNm { get; set; }
    public string? RepositoryRoot { get; set; }
    public string? SourceArchivePath { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RasterTraceSchematicTargetInput
{
    public NativeDocumentInput? Document { get; set; }
    public DocumentLifecycleInput? ExpectedState { get; set; }
    public long OriginXNm { get; set; }
    public long OriginYNm { get; set; }
    public string? ManifestGroupId { get; set; }
}

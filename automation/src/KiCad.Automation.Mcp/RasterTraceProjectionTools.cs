using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, StoredReceipt> BoardReceipts = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim BoardGate = new(1, 1);

    [McpServerTool(Name = "kicad_artwork_raster_project", ReadOnly = false),
     Description("Project one accepted KiCad raster trace proposal into an existing symbol library, a cloned PCB footprint, a non-copper PCB board graphic, or a checked schematic drawing. Filled pieces remain filled polygons; signed contour topology stays in a bounded manifest. Existing pins, pads, fields and metadata are retained. Source kind/origin are caller declarations only. Board source embedding is explicitly non-atomic because the current board embedded-file command is separate from the item transaction."),
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
        if (input.SourceKind is not ("imagegen" or "human" or "generated"))
            throw new AutomationException("invalid_raster_projection_source", "SourceKind must be imagegen, human or generated.");
        return new(proposal, source, actual, EmbeddedFileCodec.Name(actual), input.SourceKind,
            input.SourceOrigin ?? "", input.OperationId!);
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
            provenanceAtomic = true, electricalValidated = false, manufacturingValidated = false
        }, true);
        return Data(new
        {
            operationId, targetKind = "symbol", accepted = true, createdIds = created,
            nativeResult = write.StructuredContent, geometrySha256 = context.Proposal.GeometrySha256,
            sourceSha256 = context.SourceSha256, embeddedSourceVerified = true, provenanceAtomic = true,
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
        if (target.Layer is not ("F.Cu" or "B.Cu"))
            throw new AutomationException("invalid_raster_projection_layer", "Footprint instances must use F.Cu or B.Cu.");
        BoardLayer targetLayer = ParseLayer(target.Layer!);
        BoardLayer artworkLayer = ParseLayer(target.ArtworkLayer);
        ValidateArtworkLayer(artworkLayer);
        RequireSchematicQuantum(context.Proposal, target.OriginXNm, target.OriginYNm, require: false);
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
            electricalValidated = false, manufacturingValidated = false
        }, true);
        return Data(new
        {
            operationId = context.OperationId, targetKind = "footprint", accepted = true, createdIds = created,
            targetFootprintId = target.TargetFootprintId, nativeResult = applied.StructuredContent,
            geometrySha256 = context.Proposal.GeometrySha256, sourceSha256 = context.SourceSha256,
            embeddedSourceVerified = true, provenanceAtomic = true, electricalValidated = false,
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
        string archive = ArchiveSource(target.RepositoryRoot!, target.SourceArchivePath!, context.SourceBytes, context.SourceSha256);
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
        string fingerprint = instanceId + "\n" + requestJson + "\n" + expectedJson + "\n" + context.OperationId;
        await BoardGate.WaitAsync(token);
        try
        {
            if (BoardReceipts.TryGetValue(context.OperationId, out StoredReceipt? prior))
            {
                if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
                    throw new AutomationException("operation_id_reused", "The operation ID is already bound to a different raster board projection.");
                return prior.Result;
            }
            CallToolResult native = await new PcbItemTools(registry).Create(instanceId, requestJson, expectedJson, token);
            if (native.IsError ?? false) return Data(new
            {
                operationId = context.OperationId, targetKind = "board", accepted = false,
                nativeResult = native.StructuredContent, sourceArchive = archive,
                geometrySha256 = context.Proposal.GeometrySha256, sourceSha256 = context.SourceSha256,
                provenanceAtomic = false, limitation = "Board embedded-file persistence is a separate native command; only the external archive and per-shape SHA are committed here."
            }, true);
            CallToolResult result = Data(new
            {
                operationId = context.OperationId, targetKind = "board", accepted = true, createdIds = created,
                nativeResult = native.StructuredContent, sourceArchive = archive,
                geometrySha256 = context.Proposal.GeometrySha256, sourceSha256 = context.SourceSha256,
                embeddedSourceVerified = false, provenanceAtomic = false,
                limitation = "Board embedded-file persistence is a separate native command; external archive + SHA is the durable provenance boundary.",
                electricalValidated = false, manufacturingValidated = false, receiptScope = "mcp-process"
            });
            BoardReceipts[context.OperationId] = new(fingerprint, result);
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
        RequireSchematicQuantum(context.Proposal, target.OriginXNm, target.OriginYNm, require: true);
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
        AddManifestProperties(group.CustomProperties, context);
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
                context, layer, 0, 0, "")));
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
        ProjectionContext context, BoardLayer layer, long originX, long originY, string archive)
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
        if (!string.IsNullOrWhiteSpace(archive))
            shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.source_archive", Value = archive });
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

    private static void AddManifestProperties(ICollection<CustomProperty> properties, ProjectionContext context)
    {
        properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.source_sha256", Value = context.SourceSha256 });
        properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.geometry_sha256", Value = context.Proposal.GeometrySha256 });
        properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.source_kind", Value = context.SourceKind });
        properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.role", Value = "filled-vector-projection" });
        properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.contributor_semantics", Value = context.Proposal.ContributorSemantics });
    }

    private static void AddProvenanceProperties(ICollection<CustomProperty> properties,
        ProjectionContext context, string pieceId)
    {
        AddManifestProperties(properties, context);
        properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.piece_id", Value = pieceId });
        properties.Add(new CustomProperty { Key = "kicad.ai.raster_trace.filled", Value = "true" });
    }

    private static void ValidateProposal(ProtocolRasterTraceResult proposal)
    {
        if (!proposal.Accepted || proposal.FilledPieces.Count == 0 || proposal.Contours.Count == 0
            || proposal.GeometrySha256.Length != 64 || proposal.GeometrySha256.Any(c => !Uri.IsHexDigit(c))
            || proposal.SourceSha256.Length != 64 || proposal.SourceSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new AutomationException("invalid_raster_trace_proposal", "Only an accepted, complete raster trace proposal can be projected.");
        if (proposal.CoordinateOrientation != "image_x_right_y_down" || proposal.Rounding != "nearest_integer")
            throw new AutomationException("invalid_raster_trace_proposal", "The proposal coordinate convention is unsupported.");
        if (proposal.FilledPieces.Any(piece => piece.Points.Count < 3 || !piece.SourceContoursAreWholeTrace))
            throw new AutomationException("invalid_raster_trace_proposal", "Every filled piece must be a bounded closed whole-trace proposal.");
        if (proposal.FilledPieceCount != proposal.FilledPieces.Count || proposal.ContourCount != proposal.Contours.Count)
            throw new AutomationException("invalid_raster_trace_proposal", "Proposal counts do not match its retained geometry.");
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

    private static void ValidateUuid(string? value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out Guid id) || id == Guid.Empty || value != id.ToString("D"))
            throw new AutomationException("invalid_raster_projection_identity", name + " must be a canonical nonempty UUID.");
    }

    private static void RequireSchematicQuantum(ProtocolRasterTraceResult proposal, long originX, long originY, bool require)
    {
        if (!require && originX == 0 && originY == 0) return;
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

    private static string ArchiveSource(string repositoryRoot, string relative, byte[] bytes, string hash)
    {
        if (!Path.IsPathFullyQualified(repositoryRoot) || string.IsNullOrWhiteSpace(relative)
            || Path.IsPathFullyQualified(relative) || relative.Split('/', '\\').Any(part => part is "" or "." or ".."))
            throw new AutomationException("invalid_raster_projection_archive", "RepositoryRoot must be absolute and sourceArchivePath repository-relative.");
        string root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(repositoryRoot, relative));
        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw new AutomationException("invalid_raster_projection_archive", "Source archive escapes repositoryRoot.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (File.Exists(full))
        {
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full))), hash, StringComparison.Ordinal))
                throw new AutomationException("raster_trace_source_conflict", "The source archive already contains different bytes.");
        }
        else File.WriteAllBytes(full, bytes);
        return relative.Replace('\\', '/');
    }

    private static JsonElement Property(JsonElement value, string name)
        => value.TryGetProperty(name, out JsonElement result) ? result : throw new AutomationException("invalid_raster_projection_result", "Native result omitted " + name + ".");

    private static void EnsureSuccess(CallToolResult result, string code)
    {
        if (result.IsError ?? false)
            throw new AutomationException(code, result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "Native authoring operation failed.");
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
        string SourceSha256, string EmbeddedFileName, string SourceKind, string SourceOrigin, string OperationId);
    private sealed record StoredReceipt(string Fingerprint, CallToolResult Result);
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
            ContourCount = ContourCount, FilledPieceCount = FilledPieceCount,
            PotraceOptTolerancePx = PotraceOptTolerancePx, ContributorSemantics = ContributorSemantics ?? ""
        };
        foreach (RasterTraceContourInput? contour in Contours ?? [])
        {
            if (contour is null) continue;
            result.Contours.Add(new KiCad.Automation.Protocol.RasterTraceContour
            {
                Id = contour.Id ?? "", ParentId = contour.ParentId ?? "", Hole = contour.Hole, Orientation = contour.Orientation,
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
                Points = { (piece.Points ?? []).Where(point => point is not null).Select(point => new KiCad.Automation.Protocol.RasterTracePoint
                    { XNm = point!.XNm, YNm = point.YNm }) }
            });
        }
        return result;
    }
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

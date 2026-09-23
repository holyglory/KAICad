using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Board.Commands;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class PcbItemTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_items_read", ReadOnly = true),
     Description("Read explicitly typed native PCB items through KiCad's board API. requestJson is GetItems protobuf JSON and may select footprints, pads, tracks, arcs, vias, zones or graphics. Any-packed board fields are preserved; this does not infer routing or claim DRC freshness.")]
    public Task<CallToolResult> Read(string instanceId, string requestJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        var request = BoardJson.Parser.Parse<GetItems>(requestJson);
        ValidateHeader(request.Header, requireItems: false);
        var result = await registry.Client(instanceId).InvokeAsync<GetItems, GetItemsResponse>(request, cancellationToken);
        return Data(new { instanceId, status = result.Status.ToString(), response = JsonDocument.Parse(BoardJson.Formatter.Format(result)).RootElement.Clone() });
    });

    [McpServerTool(Name = "kicad_pcb_items_create"),
     Description("Create typed native PCB items with an exact lifecycle checkpoint. requestJson is CreateItems protobuf JSON containing Any-packed Track, Via, Footprint, Pad or graphic objects; expectedStateJson is the matching kicad_document_state observation. The native API commit is undoable. A changed board is rejected before mutation; the result returns the native created identities and a fresh state observation.")]
    public Task<CallToolResult> Create(string instanceId, string requestJson, string expectedStateJson,
        CancellationToken cancellationToken) => Mutate<CreateItems, CreateItemsResponse>(instanceId, requestJson, expectedStateJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_items_update"),
     Description("Update typed native PCB items with an exact lifecycle checkpoint. requestJson is UpdateItems protobuf JSON; expectedStateJson must identify the same open board and native content digest. Stale boards are rejected and no unchecked fallback is used. This is a primitive edit, not automatic routing.")]
    public Task<CallToolResult> Update(string instanceId, string requestJson, string expectedStateJson,
        CancellationToken cancellationToken) => Mutate<UpdateItems, UpdateItemsResponse>(instanceId, requestJson, expectedStateJson, cancellationToken);

    [McpServerTool(Name = "kicad_pcb_guide_create"),
     Description("Create a visual PCB routing guide as native non-copper board objects. The request may contain only BoardGraphicShape vector geometry or ReferenceImage objects, and every item must use a non-copper layer. The source SHA-256 and guide ID are attached as custom provenance properties. This never creates Track, Arc or Via copper and does not validate RF, impedance, clearance or length constraints; convert a guide to explicitly net-bound copper candidates separately.")]
    public Task<CallToolResult> CreateGuide(string instanceId, string requestJson, string expectedStateJson,
        string guideId, string sourceSha256, CancellationToken cancellationToken)
        => Execute(() => CreateGuideCore(instanceId, requestJson, expectedStateJson, guideId, sourceSha256, cancellationToken));

    private async Task<CallToolResult> CreateGuideCore(string instanceId, string requestJson, string expectedStateJson,
        string guideId, string sourceSha256, CancellationToken cancellationToken)
    {
        var request = BoardJson.Parser.Parse<CreateItems>(requestJson);
        ValidateHeader(request.Header, requireItems: true);
        if (!Guid.TryParseExact(guideId, "D", out var id) || id == Guid.Empty
            || string.IsNullOrWhiteSpace(sourceSha256) || sourceSha256.Length != 64 || sourceSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new AutomationException("invalid_pcb_guide_identity", "Provide a canonical guide ID and exact source SHA-256.");
        if (request.Items.Count == 0) throw new AutomationException("invalid_pcb_guide", "A guide must contain at least one vector or reference-image object.");
        for (int index = 0; index < request.Items.Count; ++index)
        {
            var packed = request.Items[index];
            if (packed.Is(BoardGraphicShape.Descriptor))
            {
                var shape = packed.Unpack<BoardGraphicShape>(); ValidateGuideLayer(shape.Layer);
                shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.guide.id", Value = guideId });
                shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.guide.source_sha256", Value = sourceSha256 });
                shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.guide.role", Value = "visual-underlay" });
                request.Items[index] = Any.Pack(shape);
            }
            else if (packed.Is(ReferenceImage.Descriptor))
            {
                var image = packed.Unpack<ReferenceImage>(); ValidateGuideLayer(image.Layer);
                image.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.guide.id", Value = guideId });
                image.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.guide.source_sha256", Value = sourceSha256 });
                image.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.guide.role", Value = "visual-underlay" });
                request.Items[index] = Any.Pack(image);
            }
            else throw new AutomationException("invalid_pcb_guide", "Guide creation accepts only BoardGraphicShape or ReferenceImage objects.");
        }
        return await Mutate<CreateItems, CreateItemsResponse>(instanceId, BoardJson.Formatter.Format(request), expectedStateJson, cancellationToken);
    }

    [McpServerTool(Name = "kicad_pcb_guide_svg_create"),
     Description("Parse a strict SVG guide subset (line, polyline, polygon and M/L/H/V/Z paths) into native non-copper BoardGraphicShape vectors. The raw SVG is archived under the explicit repository root, and its exact SHA-256 plus source archive path are attached to every guide object. Transforms, images, text, scripts, unsupported paths, out-of-viewBox geometry and copper layers are rejected. This creates a visual underlay only; it never creates copper routing.")]
    public async Task<CallToolResult> CreateSvgGuide(string instanceId, string documentJson, string expectedStateJson,
        string svg, string repositoryRoot, string sourceArchivePath, string guideId, string sourceSha256,
        string layer, string originXNm, string originYNm, string nanometersPerSvgUnit,
        CancellationToken cancellationToken, long strokeWidthNm = 100_000)
    {
        try
        {
            var document = SchematicJson.Parser.Parse<DocumentSpecifier>(documentJson);
            ValidateHeader(new ItemHeader { Document = document }, requireItems: true);
            if (!Guid.TryParseExact(guideId, "D", out var guide) || guide == Guid.Empty)
                throw new AutomationException("invalid_pcb_svg_guide", "Provide a canonical guide UUID.");
            string sourceSvg = svg ?? "";
            var bytes = Encoding.UTF8.GetBytes(sourceSvg);
            string actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(actualHash, sourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new AutomationException("pcb_guide_source_changed", "The supplied SVG bytes do not match sourceSha256.");
            if (strokeWidthNm <= 0) throw new AutomationException("invalid_pcb_svg_guide", "Guide stroke width must be positive.");
            decimal xOrigin = Decimal(originXNm, "originXNm"); decimal yOrigin = Decimal(originYNm, "originYNm");
            decimal scale = Decimal(nanometersPerSvgUnit, "nanometersPerSvgUnit");
            if (scale <= 0) throw new AutomationException("invalid_pcb_svg_guide", "SVG unit scale must be positive.");
            var parsed = KiCad.Automation.Model.PcbSvgGuideParser.Parse(sourceSvg); var boardLayer = ParseGuideLayer(layer);
            string root = RequireRoot(repositoryRoot); string archive = Archive(root, sourceArchivePath, bytes, sourceSha256);
            var request = new CreateItems { Header = new() { Document = document } };
            for (int index = 0; index < parsed.Segments.Length; ++index)
            {
                var segment = parsed.Segments[index];
                var shape = new BoardGraphicShape
                {
                    Id = new() { Value = DeterministicId(guideId, index) }, Layer = boardLayer,
                    Shape = new GraphicShape { Attributes = new() { Stroke = new() { Width = new() { ValueNm = strokeWidthNm } } },
                        Segment = new() { Start = Point(segment.StartX, segment.StartY, xOrigin, yOrigin, scale), End = Point(segment.EndX, segment.EndY, xOrigin, yOrigin, scale) } }
                };
                shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.guide.id", Value = guideId });
                shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.guide.source_sha256", Value = actualHash });
                shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.guide.role", Value = "visual-underlay" });
                shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.guide.source_format", Value = "svg" });
                shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.guide.source_archive", Value = archive });
                shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.guide.segment_index", Value = index.ToString(CultureInfo.InvariantCulture) });
                request.Items.Add(Any.Pack(shape));
            }
            return await Mutate<CreateItems, CreateItemsResponse>(instanceId, BoardJson.Formatter.Format(request), expectedStateJson, cancellationToken);
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException or IOException
            or UnauthorizedAccessException or ArgumentException or InvalidProtocolBufferException or InvalidJsonException)
        { return Data(new { errorCode = error is AutomationException known ? known.Code : "pcb_svg_guide_failed", errorMessage = error.Message }, true); }
    }

    [McpServerTool(Name = "kicad_pcb_route_candidate_from_guide", ReadOnly = true),
     Description("Convert the exact vector segments of a saved, provenance-matched visual guide into a deterministic, read-only CreateItems request containing explicit net-bound Track candidates. The requested net must already exist on the board. This is a handoff from visual intent to a candidate for native validation; it does not commit copper and does not claim DRC, impedance, RF or high-speed correctness. Reference images and non-segment guide geometry are intentionally not converted.")]
    public Task<CallToolResult> RouteCandidateFromGuide(string instanceId, string expectedInstanceEpoch,
        string documentJson, string expectedStateJson, string guideId, string sourceSha256,
        string netName, string layer, long widthNm, CancellationToken cancellationToken) => Execute(async () =>
    {
        var document = SchematicJson.Parser.Parse<DocumentSpecifier>(documentJson);
        ValidateHeader(new ItemHeader { Document = document }, requireItems: true);
        ValidateGuideIdentity(guideId, sourceSha256, "invalid_pcb_route_candidate");
        if (string.IsNullOrWhiteSpace(netName))
            throw new AutomationException("invalid_pcb_route_candidate", "Supply the explicit board net to which the guide candidate belongs.");
        if (widthNm <= 0) throw new AutomationException("invalid_pcb_route_candidate", "Candidate track width must be positive.");
        var boardLayer = ParseCopperLayer(layer);
        var expected = SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedStateJson);
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || expected.ProcessEpoch != expectedInstanceEpoch
            || !expected.Document.Equals(document))
            throw new AutomationException("pcb_state_changed", "The guide checkpoint does not match the exact PCB document and native epoch.");
        var client = registry.Client(instanceId);
        var session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expectedInstanceEpoch)
            throw new AutomationException("pcb_instance_changed", "The native process epoch changed.");
        var current = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!current.Equals(expected))
            throw new AutomationException("pcb_state_changed", "The PCB changed after the guide checkpoint.");

        var nets = await client.InvokeAsync<GetNets, NetsResponse>(new() { Board = document }, cancellationToken);
        if (!nets.Nets.Any(net => string.Equals(net.Name, netName, StringComparison.Ordinal)))
            throw new AutomationException("pcb_net_not_found", $"The requested board net '{netName}' does not exist; no electrically unbound candidate was generated.");

        var guideItems = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
        {
            Header = new() { Document = document },
            Types_ = { KiCadObjectType.KotPcbShape, KiCadObjectType.KotPcbReferenceImage }
        }, cancellationToken);
        var segments = new List<(int Order, string Id, GraphicSegmentAttributes Segment)>();
        foreach (var packed in guideItems.Items)
        {
            if (!packed.Is(BoardGraphicShape.Descriptor)) continue;
            var shape = packed.Unpack<BoardGraphicShape>();
            if (!HasGuideProvenance(packed, guideId, sourceSha256)) continue;
            if (shape.Shape.GeometryCase == GraphicShape.GeometryOneofCase.Segment)
            {
                int order = shape.CustomProperties
                    .FirstOrDefault(property => property.Key == "kicad.ai.guide.segment_index")?.Value is string value
                    && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed : int.MaxValue;
                segments.Add((order, shape.Id.Value, shape.Shape.Segment.Clone()));
            }
        }
        if (segments.Count == 0)
            throw new AutomationException("pcb_guide_has_no_vector_segments", "The provenance-matched guide has no convertible line segments; reference images remain visual-only.");
        segments.Sort((left, right) => left.Order != right.Order
            ? left.Order.CompareTo(right.Order)
            : StringComparer.Ordinal.Compare(left.Id, right.Id));

        var candidate = new CreateItems { Header = new() { Document = document } };
        for (int index = 0; index < segments.Count; ++index)
        {
            var segment = segments[index].Segment;
            var track = new Track
            {
                Id = new() { Value = DeterministicCandidateId(guideId, sourceSha256, netName, boardLayer, widthNm, index) },
                Start = segment.Start.Clone(), End = segment.End.Clone(),
                Width = new() { ValueNm = widthNm }, Layer = boardLayer,
                Net = new() { Name = netName }
            };
            track.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.route_candidate.guide_id", Value = guideId });
            track.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.route_candidate.source_sha256", Value = sourceSha256 });
            track.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.route_candidate.origin", Value = "visual-guide" });
            candidate.Items.Add(Any.Pack(track));
        }
        string candidateJson = BoardJson.Formatter.Format(candidate);
        return Data(new
        {
            instanceId, processEpoch = session.Epoch, document, guideId, guideSourceSha256 = sourceSha256,
            requestedNet = netName, requestedLayer = boardLayer.ToString(), widthNm,
            sourceVectorSegments = segments.Count, candidateItems = candidate.Items.Count,
            candidateRequestJson = candidateJson, structuralValidation = true,
            electricalNetResolved = true, drcValidated = false, nativeCommit = false
        });
    });

    [McpServerTool(Name = "kicad_pcb_route_candidate_validate", ReadOnly = true),
     Description("Validate a guide-derived PCB copper candidate before native mutation. The candidate must contain only exact Track, Arc or Via objects with canonical identities, copper layers and explicit net names. Requires a guide ID/source hash and an unchanged PCB lifecycle checkpoint. This validates structure and provenance supplied by the caller; it does not claim DRC, impedance, length, RF or high-speed correctness and does not mutate the board.")]
    public Task<CallToolResult> ValidateRouteCandidate(string instanceId, string expectedInstanceEpoch,
        string requestJson, string expectedStateJson, string guideId, string sourceSha256,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        var request = BoardJson.Parser.Parse<CreateItems>(requestJson);
        ValidateHeader(request.Header, requireItems: true);
        if (request.Items.Count == 0) throw new AutomationException("invalid_pcb_route_candidate", "Supply at least one route candidate item.");
        if (!Guid.TryParseExact(guideId, "D", out var guide) || guide == Guid.Empty
            || sourceSha256.Length != 64 || sourceSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new AutomationException("invalid_pcb_route_candidate", "Provide a canonical guide ID and exact guide source SHA-256.");
        var expected = SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedStateJson);
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || expected.ProcessEpoch != expectedInstanceEpoch
            || !expected.Document.Equals(request.Header.Document))
            throw new AutomationException("pcb_state_changed", "The candidate checkpoint does not match the exact PCB document and native epoch.");
        var client = registry.Client(instanceId); var session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expectedInstanceEpoch) throw new AutomationException("pcb_instance_changed", "The native process epoch changed.");
        var current = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = request.Header.Document }, cancellationToken);
        if (!current.Equals(expected)) throw new AutomationException("pcb_state_changed", "The PCB changed after the candidate checkpoint.");
        var guideItems = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
        {
            Header = new() { Document = request.Header.Document },
            Types_ = { KiCadObjectType.KotPcbShape, KiCadObjectType.KotPcbReferenceImage }
        }, cancellationToken);
        if (!guideItems.Items.Any(item => HasGuideProvenance(item, guideId, sourceSha256)))
            throw new AutomationException("pcb_guide_not_found", "The exact guide provenance is not present on this board at the supplied checkpoint.");
        var ids = new HashSet<string>(StringComparer.Ordinal); var nets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var packed in request.Items)
        {
            string id; string net; int layer = -1;
            if (packed.Is(Track.Descriptor))
            {
                var item = packed.Unpack<Track>(); id = item.Id.Value; net = item.Net.Name; layer = (int)item.Layer;
                if (item.Width.ValueNm <= 0) throw new AutomationException("invalid_pcb_route_candidate", "Track width must be positive.");
            }
            else if (packed.Is(Arc.Descriptor))
            {
                var item = packed.Unpack<Arc>(); id = item.Id.Value; net = item.Net.Name; layer = (int)item.Layer;
                if (item.Width.ValueNm <= 0) throw new AutomationException("invalid_pcb_route_candidate", "Arc width must be positive.");
            }
            else if (packed.Is(Via.Descriptor))
            {
                var item = packed.Unpack<Via>(); id = item.Id.Value; net = item.Net.Name;
                if (item.PadStack is null || item.PadStack.Layers.Count == 0)
                    throw new AutomationException("invalid_pcb_route_candidate", "A via candidate must declare its pad-stack layers.");
            }
            else throw new AutomationException("invalid_pcb_route_candidate", "Candidates must contain only Track, Arc or Via objects.");
            if (!Guid.TryParseExact(id, "D", out var identity) || identity == Guid.Empty || !ids.Add(id))
                throw new AutomationException("invalid_pcb_route_candidate", "Every route object needs a distinct canonical identity.");
            if (string.IsNullOrWhiteSpace(net)) throw new AutomationException("invalid_pcb_route_candidate", "Every route object needs an explicit net name.");
            if (layer >= 0 && (layer < 3 || layer > 34)) throw new AutomationException("invalid_pcb_route_candidate", "Tracks and arcs must use copper layers.");
            nets.Add(net);
        }
        return Data(new { instanceId, processEpoch = session.Epoch, document = request.Header.Document,
            guideId = guideId, guideSourceSha256 = sourceSha256, candidateItems = ids.Count, nets = nets.Order(StringComparer.Ordinal).ToArray(),
            structuralValidation = true, guideResolutionChecked = true, drcValidated = false, nativeCommit = false });
    });

    [McpServerTool(Name = "kicad_pcb_route_candidate_commit"),
     Description("Commit an explicitly qualified native routing candidate as one undoable atomic PCB transaction. qualificationJson must be the terminal PcbDrcJobState produced by kicad_pcb_drc_start/job for the exact same candidate IDs, revision and process epoch; it must be a completed detached dry-run with a complete snapshot, fresh results and no findings. Stale, incomplete, failed or violating qualifications are rejected, and a failed mutation leaves no partial copper.")]
    public Task<CallToolResult> CommitQualifiedCandidate(string instanceId, string requestJson,
        string expectedStateJson, string qualificationJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        var request = BoardJson.Parser.Parse<CreateItems>(requestJson);
        ValidateHeader(request.Header, requireItems: true);
        var expected = SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedStateJson);
        var qualification = SchematicJson.Parser.Parse<PcbDrcJobState>(qualificationJson);
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(request.Header.Document)
            || qualification.Document is null || !qualification.Document.Equals(request.Header.Document)
            || qualification.ProcessEpoch != expected.ProcessEpoch
            || qualification.CheckedRevision is null || !qualification.CheckedRevision.Equals(expected.Revision))
            throw new AutomationException("route_qualification_stale", "The DRC qualification is for a different PCB revision, process epoch or document.");
        if (!qualification.CandidateDryRun || !qualification.WorkerFinished
            || qualification.Status != PcbDrcJobStatus.PdrcjsCompleted
            || !qualification.SnapshotComplete || !qualification.ResultsFresh)
            throw new AutomationException("route_qualification_incomplete", "Only a completed, fresh detached DRC qualification can authorize a route commit.");
        if (qualification.Findings.Count != 0)
            throw new AutomationException("route_qualification_has_findings", "The detached DRC qualification contains violations; no copper was committed.");
        var candidateIds = CandidateIds(request);
        var qualifiedIds = qualification.CandidateItemIds.ToHashSet(StringComparer.Ordinal);
        if (!candidateIds.SetEquals(qualifiedIds))
            throw new AutomationException("route_qualification_identity_mismatch", "Candidate identities do not match the detached DRC qualification.");
        return await Mutate<CreateItems, CreateItemsResponse>(instanceId, requestJson, expectedStateJson, cancellationToken);
    });

    private static HashSet<string> CandidateIds(CreateItems request)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var packed in request.Items)
        {
            string id = packed.Is(Track.Descriptor) ? packed.Unpack<Track>().Id.Value
                : packed.Is(Arc.Descriptor) ? packed.Unpack<Arc>().Id.Value
                : packed.Is(Via.Descriptor) ? packed.Unpack<Via>().Id.Value
                : throw new AutomationException("invalid_pcb_route_candidate", "Candidates must contain only Track, Arc or Via items.");
            if (!Guid.TryParseExact(id, "D", out var identity) || identity == Guid.Empty || !ids.Add(id))
                throw new AutomationException("invalid_pcb_route_candidate", "Every route candidate needs a distinct canonical identity.");
        }
        if (ids.Count == 0) throw new AutomationException("invalid_pcb_route_candidate", "Supply at least one route candidate item.");
        return ids;
    }

    private static void ValidateGuideIdentity(string guideId, string sourceSha256, string errorCode)
    {
        if (!Guid.TryParseExact(guideId, "D", out var id) || id == Guid.Empty
            || sourceSha256.Length != 64 || sourceSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new AutomationException(errorCode, "Provide a canonical guide ID and exact guide SHA-256.");
    }

    private static bool HasGuideProvenance(Any packed, string guideId, string sourceSha256)
    {
        IEnumerable<CustomProperty> properties = packed.Is(BoardGraphicShape.Descriptor)
            ? packed.Unpack<BoardGraphicShape>().CustomProperties
            : packed.Is(ReferenceImage.Descriptor) ? packed.Unpack<ReferenceImage>().CustomProperties : [];
        var values = properties.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return values.TryGetValue("kicad.ai.guide.id", out var id) && id == guideId
            && values.TryGetValue("kicad.ai.guide.source_sha256", out var hash) && hash == sourceSha256
            && values.TryGetValue("kicad.ai.guide.role", out var role) && role == "visual-underlay";
    }

    private Task<CallToolResult> Mutate<TRequest, TResponse>(string instanceId, string requestJson,
        string expectedStateJson, CancellationToken cancellationToken)
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>, new()
        => Execute(() => MutateCore<TRequest, TResponse>(instanceId, requestJson, expectedStateJson, cancellationToken));

    private async Task<CallToolResult> MutateCore<TRequest, TResponse>(string instanceId, string requestJson,
        string expectedStateJson, CancellationToken cancellationToken)
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>, new()
    {
        TRequest request = typeof(TRequest) == typeof(CreateItems)
            ? (TRequest)(object)BoardJson.Parser.Parse<CreateItems>(requestJson)
            : (TRequest)(object)BoardJson.Parser.Parse<UpdateItems>(requestJson);
        ItemHeader header = request switch
        {
            CreateItems create => create.Header,
            UpdateItems update => update.Header,
            _ => throw new AutomationException("invalid_pcb_item_request", "Use a typed board create or update request.")
        };
        ValidateHeader(header, requireItems: true);
        if (request is CreateItems createRequest && createRequest.Items.Count == 0
            || request is UpdateItems updateRequest && updateRequest.Items.Count == 0)
            throw new AutomationException("invalid_pcb_item_request", "Supply at least one typed board item.");
        var expected = SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedStateJson);
        if (expected.Scope != DocumentLifecycleScope.DlsPcb)
            throw new AutomationException("invalid_pcb_state", "The expected state must be a PCB lifecycle observation.");
        var client = registry.Client(instanceId); var session = await client.HandshakeAsync(cancellationToken);
        if (expected.ProcessEpoch != session.Epoch)
            throw new AutomationException("pcb_instance_changed", "The expected PCB state belongs to another native process epoch.");
        // Reject an already-stale checkpoint before opening a native commit. The
        // second read after admission closes the remaining race without making a
        // stale request create/drop a transaction as a side effect.
        var admission = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = header.Document }, cancellationToken);
        if (!admission.Equals(expected))
            throw new AutomationException("pcb_state_changed", "The board changed before the native transaction could be admitted; retain the edit and inspect it again.");
        var begin = await client.InvokeAsync<BeginCommit, BeginCommitResponse>(new() { Header = header }, cancellationToken);
        bool committed = false;
        try
        {
            var before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = header.Document }, cancellationToken);
            if (!before.Equals(expected))
                throw new AutomationException("pcb_state_changed", "The board changed before the native transaction was admitted; retain the edit and inspect it again.");
            var result = await client.InvokeAsync<TRequest, TResponse>(request, cancellationToken);
            var itemStatuses = typeof(TResponse) == typeof(CreateItemsResponse)
                ? ((CreateItemsResponse)(object)result).CreatedItems.Select(item => item.Status.Code)
                : ((UpdateItemsResponse)(object)result).UpdatedItems.Select(item => item.Status.Code);
            if (itemStatuses.Any(status => status != ItemStatusCode.IscOk))
                throw new AutomationException("pcb_item_transaction_rejected", "The native board rejected one or more items; the staged transaction was dropped.");
            var response = JsonDocument.Parse(typeof(TResponse) == typeof(CreateItemsResponse)
                ? BoardJson.Formatter.Format((CreateItemsResponse)(object)result)
                : BoardJson.Formatter.Format((UpdateItemsResponse)(object)result)).RootElement.Clone();
            await client.InvokeAsync<EndCommit, EndCommitResponse>(new()
            { Id = begin.Id, Action = CommitAction.CmaCommit, Header = header, Message = "MCP PCB item transaction" }, cancellationToken);
            committed = true;
            // Commit itself advances the native board timestamp/digest. Capture
            // the checkpoint only after the commit, so the returned state can
            // safely guard the next mutation.
            var after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = header.Document }, cancellationToken);
            return Data(new { instanceId, processEpoch = session.Epoch, beforeState = SchematicJson.Formatter.Format(before),
                afterState = SchematicJson.Formatter.Format(after), response, mutationConfirmed = true, atomicTransaction = true });
        }
        finally
        {
            if (!committed)
            {
                try { await client.InvokeAsync<EndCommit, EndCommitResponse>(new() { Id = begin.Id, Action = CommitAction.CmaDrop, Header = header }, CancellationToken.None); }
                catch { /* Preserve the original failure; native receipt/state must be inspected. */ }
            }
        }
    }

    private static void ValidateHeader(ItemHeader header, bool requireItems)
    {
        if (header?.Document is null || header.Document.Type != DocumentType.DoctypePcb
            || string.IsNullOrWhiteSpace(header.Document.BoardFilename)
            || Path.GetFileName(header.Document.BoardFilename) != header.Document.BoardFilename
            || !header.Document.BoardFilename.EndsWith(".kicad_pcb", StringComparison.Ordinal))
            throw new AutomationException("invalid_pcb_item_request", "Target an explicit open .kicad_pcb document.");
        if (requireItems)
        {
            bool hasItems = header.Document is not null;
            // The concrete request validation below rejects empty item collections;
            // keep this branch here so read requests can intentionally have no type filter.
            _ = hasItems;
        }
    }

    private static void ValidateGuideLayer(BoardLayer layer)
    {
        // BL_F_Cu through BL_B_Cu are the contiguous copper range in the
        // shared board protocol. Guides belong on documentation/user layers.
        if ((int)layer >= 3 && (int)layer <= 34)
            throw new AutomationException("invalid_pcb_guide_layer", "Visual guides must use a non-copper board layer.");
    }

    private static BoardLayer ParseGuideLayer(string value) => value switch
    {
        "Dwgs.User" => BoardLayer.BlDwgsUser, "Cmts.User" => BoardLayer.BlCmtsUser,
        "Eco1.User" => BoardLayer.BlEco1User, "Eco2.User" => BoardLayer.BlEco2User,
        "F.SilkS" => BoardLayer.BlFSilkS, "B.SilkS" => BoardLayer.BlBSilkS,
        _ when System.Enum.TryParse<BoardLayer>(value, true, out var parsed) => parsed,
        _ => throw new AutomationException("invalid_pcb_svg_guide_layer", "Use a named non-copper board layer such as Dwgs.User or Cmts.User.")
    };

    private static BoardLayer ParseCopperLayer(string value)
    {
        BoardLayer layer = value switch
        {
            "F.Cu" or "F_Cu" or "BL_F_Cu" => BoardLayer.BlFCu,
            "B.Cu" or "B_Cu" or "BL_B_Cu" => BoardLayer.BlBCu,
            _ when System.Enum.TryParse<BoardLayer>(value, true, out var parsed) => parsed,
            _ => throw new AutomationException("invalid_pcb_route_candidate", "Use a named copper layer such as F.Cu, B.Cu or BL_In1_Cu.")
        };
        if ((int)layer < 3 || (int)layer > 34)
            throw new AutomationException("invalid_pcb_route_candidate", "Route candidates must use a copper layer.");
        return layer;
    }

    private static decimal Decimal(string value, string name) => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
        ? parsed : throw new AutomationException("invalid_pcb_svg_guide", $"{name} must be a finite decimal.");

    private static Vector2 Point(decimal x, decimal y, decimal originX, decimal originY, decimal scale)
    {
        decimal px = originX + x * scale, py = originY + y * scale;
        if (decimal.Truncate(px) != px || decimal.Truncate(py) != py || px < long.MinValue || px > long.MaxValue || py < long.MinValue || py > long.MaxValue)
            throw new AutomationException("invalid_pcb_svg_guide_coordinates", "SVG guide coordinates must convert exactly to integer nanometres.");
        return new() { XNm = checked((long)px), YNm = checked((long)py) };
    }

    private static string RequireRoot(string value)
    {
        if (!Path.IsPathFullyQualified(value)) throw new AutomationException("invalid_pcb_svg_archive", "repositoryRoot must be absolute.");
        return Path.GetFullPath(value);
    }

    private static string Archive(string root, string relative, byte[] bytes, string hash)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative)) throw new AutomationException("invalid_pcb_svg_archive", "sourceArchivePath must be repository-relative.");
        string full = Path.GetFullPath(Path.Combine(root, relative)); string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal) || relative.Split('/', '\\').Any(part => part is "" or "." or "..")) throw new AutomationException("invalid_pcb_svg_archive", "sourceArchivePath escapes repositoryRoot.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (File.Exists(full))
        {
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full))), hash, StringComparison.OrdinalIgnoreCase)) throw new AutomationException("pcb_guide_source_conflict", "The guide source archive already contains different bytes.");
        }
        else File.WriteAllBytes(full, bytes);
        return relative.Replace('\\', '/');
    }

    private static string DeterministicId(string guideId, int index)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"kicad-svg-guide-v1\n{guideId}\n{index}"));
        return new Guid(hash.AsSpan(0, 16), bigEndian: true).ToString("D");
    }

    private static string DeterministicCandidateId(string guideId, string sourceSha256, string netName,
        BoardLayer layer, long widthNm, int index)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"kicad-route-candidate-v1\n{guideId}\n{sourceSha256}\n{netName}\n{layer}\n{widthNm}\n{index}"));
        return new Guid(hash.AsSpan(0, 16), bigEndian: true).ToString("D");
    }

    private static CallToolResult Data(object value, bool error = false)
    {
        var structured = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new() { IsError = error, Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured };
    }
    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException or IOException
            or UnauthorizedAccessException or ArgumentException or InvalidProtocolBufferException or InvalidJsonException)
        { return Data(new { errorCode = error is AutomationException known ? known.Code : "pcb_item_operation_failed", errorMessage = error.Message }, true); }
    }
}

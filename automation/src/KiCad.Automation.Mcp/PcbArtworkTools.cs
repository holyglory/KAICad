using System.ComponentModel;
using System.Collections.Concurrent;
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

[McpServerToolType]
public sealed class PcbArtworkTools(InstanceRegistry registry)
{
    private static readonly ConcurrentDictionary<string, StoredReceipt> Receipts = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    [McpServerTool(Name = "kicad_pcb_svg_artwork_commit"),
     Description("Commit a validated strict SVG asset as authoritative non-copper PCB BoardGraphicShape geometry. The source bytes are archived under the explicit repository root, exact SHA-256 and sourceKind are recorded on every object, and the native mutation is lifecycle-checked and undoable. sourceKind may be imagegen, human or generated; this tool never creates Track, Arc or Via copper and does not claim electrical or manufacturing validity."),
     KiCadCapability("pcb-artwork", "compiled-mcp plus native-board-api", "strict SVG, source archive and SHA-256 provenance, non-copper vector geometry, lifecycle checkpoint, operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp", "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Commit(string instanceId, string documentJson, string expectedStateJson,
        string svg, string sourceSha256, string sourceKind, string assetId, string repositoryRoot,
        string sourceArchivePath, string layer, string originXNm, string originYNm,
        string nanometersPerSvgUnit, string operationId, CancellationToken cancellationToken,
        long strokeWidthNm = 100_000) => Execute(async () =>
    {
        ValidateOperation(operationId);
        if (string.IsNullOrWhiteSpace(assetId) || assetId.Length > 256 || assetId.Contains('\0'))
            throw new AutomationException("invalid_pcb_artwork_id", "assetId must be nonempty, bounded and free of NUL characters.");
        if (sourceKind is not ("imagegen" or "human" or "generated"))
            throw new AutomationException("invalid_pcb_artwork_source", "sourceKind must be imagegen, human or generated.");
        DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_pcb_artwork_target");
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_pcb_artwork_target", "Artwork requires an explicit PCB document.");
        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_pcb_artwork_state");
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_pcb_artwork_state", "expectedStateJson must identify the same PCB and native process epoch.");
        if (!Guid.TryParseExact(assetId, "D", out _) || !Guid.TryParseExact(operationId, "D", out _))
            throw new AutomationException("invalid_pcb_artwork_id", "assetId and operationId must be canonical UUIDs.");
        if (string.IsNullOrWhiteSpace(svg) || !IsSha256(sourceSha256))
            throw new AutomationException("invalid_pcb_artwork_source", "SVG source and exact sourceSha256 are required.");
        string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(svg)));
        if (!string.Equals(actualSha256, sourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new AutomationException("pcb_artwork_source_changed", "SVG bytes do not match sourceSha256.");
        BoardLayer boardLayer = ParseLayer(layer);
        ValidateLayer(boardLayer);
        decimal x = Decimal(originXNm, "originXNm"), y = Decimal(originYNm, "originYNm"), scale = Decimal(nanometersPerSvgUnit, "nanometersPerSvgUnit");
        if (scale <= 0 || strokeWidthNm <= 0) throw new AutomationException("invalid_pcb_artwork", "Scale and strokeWidthNm must be positive.");
        PcbSvgGuide parsed = PcbSvgGuideParser.Parse(svg, 20_000);
        string archive = Archive(repositoryRoot, sourceArchivePath, Encoding.UTF8.GetBytes(svg), actualSha256);
        CreateItems create = new() { Header = new() { Document = document } };
        for (int index = 0; index < parsed.Segments.Length; index++)
        {
            var segment = parsed.Segments[index];
            Vector2 start = Point(segment.StartX, segment.StartY, x, y, scale), end = Point(segment.EndX, segment.EndY, x, y, scale);
            var shape = new BoardGraphicShape
            {
                Id = new() { Value = DeterministicId(assetId, actualSha256, index) }, Layer = boardLayer,
                Shape = new GraphicShape { Attributes = new() { Stroke = new() { Width = new() { ValueNm = strokeWidthNm } } }, Segment = new() { Start = start, End = end } }
            };
            shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.artwork.id", Value = assetId });
            shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.artwork.source_sha256", Value = actualSha256 });
            shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.artwork.source_kind", Value = sourceKind });
            shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.artwork.source_archive", Value = archive });
            shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.artwork.role", Value = "authoritative-vector" });
            shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.artwork.segment_index", Value = index.ToString() });
            create.Items.Add(Any.Pack(shape));
        }
        string fingerprint = instanceId + "\n" + BoardJson.Formatter.Format(create) + "\n" + SchematicJson.Formatter.Format(expected) + "\n" + operationId;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (Receipts.TryGetValue(operationId, out StoredReceipt? existing))
            {
                if (existing.Fingerprint != fingerprint) throw new AutomationException("operation_id_reused", "operationId is already bound to another artwork commit.");
                return existing.Result;
            }
            CallToolResult native = await new PcbItemTools(registry).Create(instanceId, BoardJson.Formatter.Format(create), expectedStateJson, cancellationToken);
            if (native.IsError ?? false) return native;
            JsonElement structured = JsonSerializer.SerializeToElement(new
            {
                instanceId, processEpoch = expected.ProcessEpoch, document, operationId, assetId, sourceKind,
                sourceSha256 = actualSha256, sourceArchive = archive, layer = boardLayer.ToString(), segmentCount = create.Items.Count,
                nativeResult = native.StructuredContent, authoritativeVector = true, electricalValidation = false,
                manufacturabilityValidation = false, operationReceipt = true, nativeReceiptAvailable = false,
                replayableWithinProcess = true, receiptScope = "mcp-process"
            });
            var result = new CallToolResult { StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
            Receipts[operationId] = new(fingerprint, result);
            return result;
        }
        finally { Gate.Release(); }
    });

    [McpServerTool(Name = "kicad_pcb_svg_artwork_receipt", ReadOnly = true),
     Description("Read the process-owned receipt for an authoritative SVG artwork commit. It never resubmits board geometry and does not replace a native cross-process receipt."),
     KiCadCapability("pcb-artwork", "compiled-mcp plus native-board-api", "operation UUID and process-owned vector artwork receipt"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Receipt(string instanceId, string expectedProcessEpoch, string operationId,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        ValidateOperation(operationId); ValidateEpoch(expectedProcessEpoch);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (!Receipts.TryGetValue(operationId, out StoredReceipt? stored)) return Data(new { instanceId, processEpoch = expectedProcessEpoch, operationId, found = false });
            JsonElement payload = stored.Result.StructuredContent ?? JsonSerializer.SerializeToElement(new { });
            if (payload.TryGetProperty("instanceId", out JsonElement owner) && owner.GetString() != instanceId)
                throw new AutomationException("wrong_instance_receipt", "The artwork receipt belongs to another instance.");
            return Data(new { instanceId, processEpoch = expectedProcessEpoch, operationId, found = true, receipt = payload, receiptScope = "mcp-process" });
        }
        finally { Gate.Release(); }
    });

    private static string Archive(string root, string relative, byte[] bytes, string hash)
    {
        if (!Path.IsPathFullyQualified(root) || string.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative)) throw new AutomationException("invalid_pcb_artwork_archive", "repositoryRoot must be absolute and sourceArchivePath repository-relative.");
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal) || relative.Split('/', '\\').Any(part => part is "" or "." or "..")) throw new AutomationException("invalid_pcb_artwork_archive", "sourceArchivePath escapes repositoryRoot.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (File.Exists(full) && !string.Equals(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full))), hash, StringComparison.Ordinal)) throw new AutomationException("pcb_artwork_source_conflict", "The source archive already contains different bytes.");
        if (!File.Exists(full)) File.WriteAllBytes(full, bytes);
        return relative.Replace('\\', '/');
    }
    private static BoardLayer ParseLayer(string value) => value switch
    {
        "F.SilkS" => BoardLayer.BlFSilkS, "B.SilkS" => BoardLayer.BlBSilkS,
        "Dwgs.User" => BoardLayer.BlDwgsUser, "Cmts.User" => BoardLayer.BlCmtsUser,
        "Eco1.User" => BoardLayer.BlEco1User, "Eco2.User" => BoardLayer.BlEco2User,
        _ when System.Enum.TryParse<BoardLayer>(value, true, out var layer) => layer,
        _ => throw new AutomationException("invalid_pcb_artwork_layer", "Layer must be a concrete non-copper board layer.")
    };
    private static void ValidateLayer(BoardLayer layer) { if (layer is BoardLayer.BlUnknown or BoardLayer.BlUndefined || (int)layer is >= 3 and <= 34) throw new AutomationException("invalid_pcb_artwork_layer", "Authoritative artwork must use a non-copper layer."); }
    private static Vector2 Point(decimal x, decimal y, decimal ox, decimal oy, decimal scale) { decimal px = ox + x * scale, py = oy + y * scale; if (decimal.Truncate(px) != px || decimal.Truncate(py) != py || px < long.MinValue || px > long.MaxValue || py < long.MinValue || py > long.MaxValue) throw new AutomationException("invalid_pcb_artwork_coordinates", "SVG coordinates must convert to integer nanometres."); return new() { XNm = (long)px, YNm = (long)py }; }
    private static decimal Decimal(string value, string name) => decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new AutomationException("invalid_pcb_artwork", $"{name} must be a finite decimal.");
    private static string DeterministicId(string asset, string hash, int index) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(asset + "\n" + hash + "\n" + index)).AsSpan(0, 16)).ToString("D");
    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static void ValidateOperation(string value) { if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty) throw new AutomationException("invalid_operation_id", "operationId must be a canonical UUID."); }
    private static void ValidateEpoch(string value) { if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty) throw new AutomationException("invalid_process_epoch", "Provide the canonical native process epoch."); }
    private static T Parse<T>(string json, string code) where T : class, Google.Protobuf.IMessage<T>, new() { try { return SchematicJson.Parser.Parse<T>(json); } catch (Exception error) when (error is Google.Protobuf.InvalidProtocolBufferException or InvalidJsonException) { throw new AutomationException(code, error.Message); } }
    private static CallToolResult Data(object value) { JsonElement data = JsonSerializer.SerializeToElement(value); return new() { StructuredContent = data, Content = [new TextContentBlock { Text = data.GetRawText() }] }; }
    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action) { try { return await action(); } catch (Exception error) when (error is AutomationException or NativeApiException or NngException or IOException or InvalidJsonException or Google.Protobuf.InvalidProtocolBufferException) { JsonElement data = JsonSerializer.SerializeToElement(new { errorCode = error is AutomationException known ? known.Code : "pcb_artwork_failed", errorMessage = error.Message }); return new() { IsError = true, StructuredContent = data, Content = [new TextContentBlock { Text = data.GetRawText() }] }; } }
    private sealed record StoredReceipt(string Fingerprint, CallToolResult Result);
}

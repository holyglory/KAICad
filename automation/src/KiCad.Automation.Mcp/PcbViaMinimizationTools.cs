using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Any = Google.Protobuf.WellKnownTypes.Any;
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
public sealed class PcbViaMinimizationTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_route_via_minimize_plan", ReadOnly = true),
     Description("Plan deterministic PCB via minimization from one exact lifecycle checkpoint. candidateRequestJson must contain Track, Arc and Via route objects; requestJson contains optional nets[] and maxMerges. Same-net, same-position vias with contiguous layer spans are combined only when their drill, padstack, lock, parent and custom-property profiles are compatible; all original source IDs are deleted once and only final replacement padstacks are created. The result is exact DeleteItems/CreateItems protobuf JSON for DRC review and route replacement; this tool never mutates the board or claims final DRC/manufacturing correctness."),
     KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "exact PCB lifecycle state, deterministic via-span minimization candidate"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Plan(string instanceId, string documentJson, string expectedStateJson,
        string candidateRequestJson, string requestJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_pcb_via_minimize_target");
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_pcb_via_minimize_target", "Via minimization requires an explicit PCB document.");
        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_pcb_via_minimize_state");
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_pcb_via_minimize_state", "expectedStateJson must identify the same PCB and native process epoch.");
        CreateItems candidate = Parse<CreateItems>(candidateRequestJson, "invalid_pcb_via_minimize_candidate");
        if (candidate.Header?.Document is null || !candidate.Header.Document.Equals(document))
            throw new AutomationException("invalid_pcb_via_minimize_candidate", "candidateRequestJson must target the same PCB.");
        ViaRequest request = JsonSerializer.Deserialize<ViaRequest>(requestJson, JsonOptions)
            ?? throw new AutomationException("invalid_pcb_via_minimize", "requestJson must be a JSON object.");
        Validate(request);
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("pcb_instance_changed", "The process epoch changed before via minimization planning.");
        DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = document }, cancellationToken);
        if (!before.Equals(expected)) throw new AutomationException("pcb_state_changed", "The board changed before via minimization planning.");
        ViaPlan plan = BuildPlan(candidate, request);
        DocumentLifecycleState after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = document }, cancellationToken);
        if (!after.Equals(expected)) throw new AutomationException("pcb_via_minimize_changed", "The board changed while via minimization was planned.");
        JsonElement structured = JsonSerializer.SerializeToElement(new
        {
            instanceId, processEpoch = session.Epoch, document, planId = plan.PlanId,
            beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
            mergedCount = plan.MergedCount, viaCountBefore = plan.ViaCountBefore, viaCountAfter = plan.ViaCountAfter,
            removedItemIds = plan.RemovedItemIds, createdViaIds = plan.CreatedViaIds,
            candidateDeleteRequestJson = BoardJson.Formatter.Format(plan.Delete),
            candidateCreateRequestJson = BoardJson.Formatter.Format(plan.Create),
            proposalOnly = true, nativeCommit = false, drcValidated = false,
            limitations = new[] { "proposal_only", "drc_not_validated", "requires_route_replacement_apply", "does_not_change_tracks" }
        }, JsonOptions);
        return new CallToolResult { StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    });

    internal static ViaPlan BuildPlan(CreateItems candidate, ViaRequest request)
    {
        request.Nets ??= [];
        string planId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(BoardJson.Formatter.Format(candidate) + "\n" + JsonSerializer.Serialize(request, JsonOptions))));
        var vias = candidate.Items.Select((item, index) => (item, index)).Where(pair => pair.item.Is(Via.Descriptor))
            .Select(pair => (Via: pair.item.Unpack<Via>(), pair.index)).ToArray();
        var occupiedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in vias)
        {
            ValidateVia(pair.Via);
            if (!occupiedIds.Add(pair.Via.Id.Value))
                throw new AutomationException("invalid_pcb_via_minimize_candidate", "Via IDs must be distinct canonical UUIDs.");
        }
        var selected = vias.Where(pair => Selected(pair.Via.Net.Name, request.Nets)).ToList();
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in selected)
        {
            if (!sourceIds.Add(pair.Via.Id.Value))
                throw new AutomationException("invalid_pcb_via_minimize_candidate", "Selected vias must have distinct canonical IDs.");
        }
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var finalReplacements = new List<Via>();
        int mergeCount = 0;
        int idIndex = 0;
        foreach (var group in selected.GroupBy(pair => pair.Via.Net.Name + "|" + pair.Via.Position.XNm + "," + pair.Via.Position.YNm, StringComparer.Ordinal))
        {
            var pending = group.Select(pair => pair.Via.Clone()).ToList();
            var groupSources = pending.ToDictionary(via => via.Id.Value,
                via => new HashSet<string>([via.Id.Value], StringComparer.Ordinal), StringComparer.Ordinal);
            while (mergeCount < request.MaxMerges && TryCombine(pending, out Via? replacement, out Via first, out Via second))
            {
                var combinedSources = new HashSet<string>(groupSources[first.Id.Value], StringComparer.Ordinal);
                combinedSources.UnionWith(groupSources[second.Id.Value]);
                string replacementId;
                do replacementId = DeterministicId(planId, idIndex++);
                while (!occupiedIds.Add(replacementId));
                replacement!.Id = new() { Value = replacementId };
                replacement.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.via_minimize_plan", Value = planId });
                replacement.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.via_minimize_sources", Value = string.Join(",", combinedSources.Order(StringComparer.Ordinal)) });
                foreach (string sourceId in combinedSources)
                    removed.Add(sourceId);
                groupSources.Remove(first.Id.Value);
                groupSources.Remove(second.Id.Value);
                groupSources[replacement.Id.Value] = combinedSources;
                pending.Remove(first); pending.Remove(second); pending.Add(replacement);
                mergeCount++;
            }

            finalReplacements.AddRange(pending.Where(via => !sourceIds.Contains(via.Id.Value)));
        }
        var create = new CreateItems { Header = candidate.Header.Clone() };
        create.Items.AddRange(finalReplacements.Select(Any.Pack));
        var delete = new DeleteItems { Header = candidate.Header.Clone() };
        delete.ItemIds.Add(removed.Order(StringComparer.Ordinal).Select(id => new KIID { Value = id }));
        return new ViaPlan(planId, create, delete, mergeCount, vias.Length, vias.Length - removed.Count + finalReplacements.Count,
            removed.Order(StringComparer.Ordinal).ToArray(), finalReplacements.Select(via => via.Id.Value).Order(StringComparer.Ordinal).ToArray());
    }

    private static bool TryCombine(List<Via> vias, out Via? replacement, out Via first, out Via second)
    {
        for (int i = 0; i < vias.Count; i++) for (int j = i + 1; j < vias.Count; j++)
        {
            Via a = vias[i], b = vias[j];
            if (!object.Equals(a.Position, b.Position) || a.PadStack is null || b.PadStack is null) continue;
            int aMin = a.PadStack.Layers.Min(layer => (int)layer), aMax = a.PadStack.Layers.Max(layer => (int)layer);
            int bMin = b.PadStack.Layers.Min(layer => (int)layer), bMax = b.PadStack.Layers.Max(layer => (int)layer);
            if (aMax != bMin && bMax != aMin) continue;
            if (!Compatible(a, b, (BoardLayer)(aMax == bMin ? aMax : bMax))) continue;
            first = a; second = b; replacement = a.Clone();
            int min = Math.Min(aMin, bMin), max = Math.Max(aMax, bMax);
            replacement.PadStack.Layers.Clear(); replacement.PadStack.Layers.Add((BoardLayer)min); replacement.PadStack.Layers.Add((BoardLayer)max);
            replacement.PadStack.CopperLayers.Clear();
            foreach (BoardLayer layer in new[] { (BoardLayer)min, (BoardLayer)max })
            {
                PadStackLayer? source = a.PadStack.CopperLayers.Concat(b.PadStack.CopperLayers).FirstOrDefault(item => item.Layer == layer);
                if (source is not null) replacement.PadStack.CopperLayers.Add(source.Clone());
            }
            if (replacement.PadStack.Drill is not null)
            { replacement.PadStack.Drill.StartLayer = (BoardLayer)min; replacement.PadStack.Drill.EndLayer = (BoardLayer)max; }
            return true;
        }
        replacement = null; first = new(); second = new(); return false;
    }

    private static bool Compatible(Via a, Via b, BoardLayer junction)
    {
        if (!object.Equals(a.Net, b.Net) || a.Type != b.Type || a.Locked != b.Locked || !object.Equals(a.Parent, b.Parent)
            || !CustomPropertiesEqual(a.CustomProperties, b.CustomProperties))
            return false;

        PadStack left = a.PadStack!, right = b.PadStack!;
        if (left.Type != right.Type || left.UnconnectedLayerRemoval != right.UnconnectedLayerRemoval
            || !object.Equals(left.Angle, right.Angle) || !object.Equals(left.FrontOuterLayers, right.FrontOuterLayers)
            || !object.Equals(left.BackOuterLayers, right.BackOuterLayers) || !object.Equals(left.ZoneSettings, right.ZoneSettings)
            || !object.Equals(left.SecondaryDrill, right.SecondaryDrill) || !object.Equals(left.TertiaryDrill, right.TertiaryDrill)
            || !object.Equals(left.FrontPostMachining, right.FrontPostMachining)
            || !object.Equals(left.BackPostMachining, right.BackPostMachining)
            || !DrillProfilesEqual(left.Drill, right.Drill))
            return false;

        var leftJunction = left.CopperLayers.Where(layer => layer.Layer == junction).ToArray();
        var rightJunction = right.CopperLayers.Where(layer => layer.Layer == junction).ToArray();
        return leftJunction.Length == rightJunction.Length
            && leftJunction.SequenceEqual(rightJunction);
    }

    private static bool DrillProfilesEqual(DrillProperties? left, DrillProperties? right)
    {
        if (left is null || right is null) return left is null && right is null;
        DrillProperties normalizedLeft = left.Clone();
        DrillProperties normalizedRight = right.Clone();
        normalizedLeft.StartLayer = BoardLayer.BlUnknown;
        normalizedLeft.EndLayer = BoardLayer.BlUnknown;
        normalizedRight.StartLayer = BoardLayer.BlUnknown;
        normalizedRight.EndLayer = BoardLayer.BlUnknown;
        return normalizedLeft.Equals(normalizedRight);
    }

    private static bool CustomPropertiesEqual(IEnumerable<CustomProperty> left, IEnumerable<CustomProperty> right)
        => left.Where(property => !property.Key.StartsWith("kicad.ai.via_minimize_", StringComparison.Ordinal))
            .SequenceEqual(right.Where(property => !property.Key.StartsWith("kicad.ai.via_minimize_", StringComparison.Ordinal)));

    private static void ValidateVia(Via via)
    {
        if (via.Id is null || !Guid.TryParseExact(via.Id.Value, "D", out var id) || id == Guid.Empty
            || via.Position is null || via.Net is null || string.IsNullOrWhiteSpace(via.Net.Name)
            || via.PadStack is null || via.PadStack.Layers.Count < 2)
            throw new AutomationException("invalid_pcb_via_minimize_candidate", "Every candidate Via must have a canonical ID, position, explicit net and at least two pad-stack layers.");
        var layers = via.PadStack.Layers.Select(layer => (int)layer).ToArray();
        if (layers.Any(layer => layer < (int)BoardLayer.BlFCu || layer > (int)BoardLayer.BlBCu)
            || layers.Distinct().Count() != layers.Length || layers.Min() == layers.Max())
            throw new AutomationException("invalid_pcb_via_minimize_candidate", "Via pad-stack layers must be distinct copper layers spanning at least two levels.");
        if (via.PadStack.CopperLayers.Any(layer => (int)layer.Layer < (int)BoardLayer.BlFCu
                || (int)layer.Layer > (int)BoardLayer.BlBCu)
            || via.PadStack.CopperLayers.Select(layer => layer.Layer).Distinct().Count() != via.PadStack.CopperLayers.Count)
            throw new AutomationException("invalid_pcb_via_minimize_candidate", "Via pad-stack copper definitions must use copper layers.");
        if (via.PadStack.Drill is { } drill
            && (drill.StartLayer != (BoardLayer)layers.Min() || drill.EndLayer != (BoardLayer)layers.Max()
                || drill.Diameter is null || drill.Diameter.XNm <= 0 || drill.Diameter.YNm <= 0))
            throw new AutomationException("invalid_pcb_via_minimize_candidate", "Via drill layers must match the pad-stack span and use a positive diameter.");
    }

    private static string DeterministicId(string planId, int index) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(planId + ":" + index)).AsSpan(0, 16)).ToString("D");
    private static bool Selected(string net, string[] nets) => nets.Length == 0 || nets.Contains(net, StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private static void Validate(ViaRequest request)
    {
        if (request.Nets is null || request.Nets.Length > 4096 || request.Nets.Any(string.IsNullOrWhiteSpace)) throw new AutomationException("invalid_pcb_via_minimize", "nets must contain at most 4096 nonempty names.");
        if (request.MaxMerges is < 1 or > 4096) throw new AutomationException("invalid_pcb_via_minimize", "maxMerges must be between 1 and 4096.");
    }
    private static T Parse<T>(string json, string code) where T : class, IMessage<T>, new()
    { try { return BoardJson.Parser.Parse<T>(json); } catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException) { throw new AutomationException(code, error.Message); } }
    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    { try { return await action(); } catch (Exception error) when (error is AutomationException or NativeApiException or NngException or InvalidProtocolBufferException or InvalidJsonException or JsonException or IOException) { string code = error is AutomationException known ? known.Code : "pcb_via_minimize_failed"; JsonElement structured = JsonSerializer.SerializeToElement(new { errorCode = code, errorMessage = error.Message }); return new CallToolResult { IsError = true, StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] }; } }
    internal sealed class ViaRequest { public string[] Nets { get; set; } = []; public int MaxMerges { get; set; } = 256; }
    internal sealed record ViaPlan(string PlanId, CreateItems Create, DeleteItems Delete, int MergedCount, int ViaCountBefore, int ViaCountAfter, IReadOnlyList<string> RemovedItemIds, IReadOnlyList<string> CreatedViaIds);
}

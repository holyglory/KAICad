using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

using ModelSheetInstance = KiCad.Automation.Model.SheetInstance;

namespace KiCad.Automation.Native;

/// <summary>Native owners that appeared in KiCad since the last synchronization, projected onto the design: owners a
/// verified history restores (<see cref="History"/> is the history used), or symbols placed in KiCad that become new
/// components (<see cref="AddedOccurrences"/>; <see cref="History"/> is then null). Symbols removed in the same KiCad
/// change are already removed from <see cref="BindingCandidate"/>, with <see cref="ComponentChanges"/> for the
/// components they retired.</summary>
internal sealed record SchematicNativeRestorationResult(SchematicDesign BindingCandidate, SchematicOwnershipHistory? History,
    IReadOnlyList<Guid> RestoredOccurrences, IReadOnlyList<Guid> RestoredComponents)
{
    public IReadOnlyList<Guid> AddedOccurrences { get; init; } = [];
    public IReadOnlyList<Guid> AddedComponents { get; init; } = [];
    public IReadOnlyList<Guid> AddedParts { get; init; } = [];
    public IReadOnlyList<Guid> RemovedOccurrences { get; init; } = [];
    public IReadOnlyList<ComponentReferenceChange> ComponentChanges { get; init; } = [];
    /// <summary>Added occurrences exactly as the saved XML declares them: the person's answers to resolution requests
    /// (ledger p35cfdc0345e056a5). The XML differs from the last synchronized design only by them.</summary>
    public IReadOnlyList<Guid> AnsweredOccurrences { get; init; } = [];
    /// <summary>Sheets inserted in KiCad that become design sheets (ledger p5f6d5d0ca242d628).</summary>
    public IReadOnlyList<Guid> AddedSheetInstances { get; init; } = [];
    /// <summary>Design sheets KiCad no longer shows, removed with everything on them.</summary>
    public IReadOnlyList<Guid> RemovedSheetInstances { get; init; } = [];
    /// <summary>Design sheets KiCad shows at another place.</summary>
    public IReadOnlyList<Guid> MovedSheetInstances { get; init; } = [];
    /// <summary>Design sheets a verified history restores with their exact identities.</summary>
    public IReadOnlyList<Guid> RestoredSheetInstances { get; init; } = [];

    /// <summary>The verified history a restoration used; only restorations have one.</summary>
    public SchematicOwnershipHistory Source => History
        ?? throw new AutomationException("native_ownership_history_missing", "Only a restoration from verified history has a source.");
}

internal sealed record SchematicOwnershipInspection(string SnapshotToken, IReadOnlyList<SchematicNativeRestorationResult> Candidates);

/// <summary>Recover identity declarations from verified deletion predecessors.
/// Current requirements and live objects are never replaced with historical text.</summary>
internal static class SchematicNativeRestorationProjection
{
    internal static SchematicNativeRestorationResult Project(DesignRecoveryState state,
        IReadOnlyList<SchematicOwnershipHistory> history, CancellationToken token)
    {
        var inspection = Inspect(state, history, token);
        if (state.OwnershipResolution is { } selected)
        {
            var candidate = inspection.Candidates.SingleOrDefault(c => c.Source.Receipt.OperationId == selected.HistoryOperationId);
            if (selected.SnapshotToken != inspection.SnapshotToken || candidate is null
                || candidate.Source.Receipt.PreviousXmlSha256 != selected.HistoryXmlSha256)
                throw Error("native_owner_resolution_stale", "The selected history no longer matches this snapshot; inspect and choose again.");
            return candidate;
        }
        if (inspection.Candidates.Select(Key).Distinct(StringComparer.Ordinal).Skip(1).Any())
            throw Error("ambiguous_native_ownership_history", "Verified histories disagree about restored identities or unresolved requirement bindings; select an explicit resolution.");
        return inspection.Candidates.OrderBy(c => c.Source.Receipt.OperationId).First();
    }

    internal static SchematicOwnershipInspection Inspect(DesignRecoveryState state,
        IReadOnlyList<SchematicOwnershipHistory> history, CancellationToken token)
    {
        string observedOwners = SchematicNetReconciliation.NativeOwners(state.Observed);
        string currentTopology = SchematicNetReconciliation.Topology(state.Baseline.Engineering.Circuit);
        string currentBindings = SchematicNetReconciliation.Bindings(state.Baseline);
        var candidates = new List<SchematicNativeRestorationResult>();
        foreach (var entry in history)
        {
            token.ThrowIfCancellationRequested();
            if (SchematicNetReconciliation.NativeOwners(entry.Design.Schematic) != observedOwners) continue;
            var report = SchematicDesignBindings.Inspect(entry.Design, state.KnowledgeLibraries, token);
            if (!report.IdentitiesResolved || report.Differences.Any(d => d.Field == "unit")) continue;
            var reduced = SchematicNativeRemovalProjection.Project(entry.Design, state.Baseline.Schematic, state.KnowledgeLibraries, token);
            if (reduced.BindingCandidate is null || reduced.RemovedOccurrences.Count == 0 && reduced.RemovedSheetInstances.Count == 0
                || reduced.MovedSheetInstances.Count != 0
                || SchematicNetReconciliation.Topology(reduced.BindingCandidate.Engineering.Circuit) != currentTopology
                || SchematicNetReconciliation.Bindings(reduced.BindingCandidate) != currentBindings) continue;
            candidates.Add(Build(state, entry, token));
        }
        if (candidates.Count == 0)
            throw Error("native_ownership_history_not_matched", "No verified deletion predecessor matches the exact restored native owners.");
        // Native revision order is meaningful only within one document epoch.
        // Prefer the current receipt; never order different epochs by UUID/time.
        if (candidates.Any(c => c.Source.Latest)) candidates = candidates.Where(c => c.Source.Latest).ToList();
        else if (candidates.Any(c => c.Source.Receipt.NativeDocumentEpoch == state.NativeRevision.Epoch))
        {
            candidates = candidates.Where(c => c.Source.Receipt.NativeDocumentEpoch == state.NativeRevision.Epoch).ToList();
            ulong newest = candidates.Max(c => c.Source.Receipt.NativeRevisionSequence);
            candidates = candidates.Where(c => c.Source.Receipt.NativeRevisionSequence == newest).ToList();
        }
        return new(SnapshotToken(state, history, token), candidates.OrderBy(c => c.Source.Receipt.OperationId).ToArray());
    }

    private static string SnapshotToken(DesignRecoveryState state, IReadOnlyList<SchematicOwnershipHistory> history, CancellationToken token)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Bytes(byte[] bytes)
        {
            token.ThrowIfCancellationRequested();
            Span<byte> length = stackalloc byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(length, bytes.LongLength);
            digest.AppendData(length); digest.AppendData(bytes);
        }
        void Text(string value) => Bytes(Encoding.UTF8.GetBytes(value));
        Text("kicad-ownership-resolution-v1");
        Text(JsonSerializer.Serialize(new { state.OriginId, state.InstanceId, state.NativeRevision, state.TrackingComplete, state.HierarchyResolution }));
        Text(SchematicDesignXml.Write(state.Baseline, state.KnowledgeLibraries)); Bytes(state.DesiredFileBytes);
        Text(SchematicDataXml.Write(state.Observed));
        Bytes(state.BaselineElectrical?.ToByteArray() ?? []); Bytes(state.ObservedElectrical?.ToByteArray() ?? []);
        foreach (var library in state.KnowledgeLibraries) Text(ComponentKnowledgeXml.WriteLibrary(library));
        foreach (var entry in history.OrderBy(h => h.Receipt.OperationId)) Text(JsonSerializer.Serialize(new { entry.Receipt, entry.Latest }));
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static SchematicNativeRestorationResult Build(DesignRecoveryState state, SchematicOwnershipHistory history, CancellationToken token)
    {
        var baseline = state.Baseline; var current = baseline.Engineering.Circuit; var old = history.Design.Engineering.Circuit;
        var componentIds = current.Components.Select(c => c.Id).ToHashSet();
        var symbolIds = current.Symbols.Select(s => s.Id).ToHashSet();
        var definitions = current.Sheets.SelectMany(s => s.Components).Select(c => c.Id).ToHashSet();
        var restoredComponents = old.Components.Where(c => !componentIds.Contains(c.Id)).OrderBy(c => c.Id).ToArray();
        var restoredSymbols = old.Symbols.Where(s => !symbolIds.Contains(s.Id)).OrderBy(s => s.Id).ToArray();
        // Sheets a KiCad undo shows again come back with their exact identities, in the history's order (ledger p5f6d5d0ca242d628).
        var sheetIds = current.SheetInstances.Select(s => s.Id).ToHashSet();
        var sheetDefinitions = current.Sheets.Select(s => s.Id).ToHashSet();
        var restoredSheets = old.SheetInstances.Where(s => !sheetIds.Contains(s.Id)).ToArray();
        var restoredSheetIds = restoredSheets.Select(s => s.Id).ToHashSet();
        var next = current with
        {
            Components = [.. current.Components, .. restoredComponents],
            Symbols = [.. current.Symbols, .. restoredSymbols],
            Sheets = [.. current.Sheets.Select(s => s with { Components = [.. s.Components,
                .. old.Sheets.Single(o => o.Id == s.Id).Components.Where(c => !definitions.Contains(c.Id)).OrderBy(c => c.Id)] }),
                .. old.Sheets.Where(s => !sheetDefinitions.Contains(s.Id))],
            SheetInstances = [.. current.SheetInstances, .. restoredSheets]
        };
        var addedSymbols = restoredSymbols.Select(s => s.Id).ToHashSet();
        var design = baseline with { Engineering = baseline.Engineering with { Circuit = next }, Schematic = state.Observed.Clone(),
            SheetBindings = [.. baseline.SheetBindings, .. history.Design.SheetBindings.Where(b => restoredSheetIds.Contains(b.SheetInstanceId))],
            SymbolBindings = [.. baseline.SymbolBindings, .. history.Design.SymbolBindings.Where(b => addedSymbols.Contains(b.SymbolOccurrenceId))
                .OrderBy(b => b.SymbolOccurrenceId)] };
        var native = SchematicModelProjection.NativeSymbols(design, state.Observed);
        string Field(IEnumerable<SymbolOccurrence> symbols, bool reference, string undrawnValue)
        {
            var values = symbols.Select(s => reference ? native[s.Id].ReferenceField?.Text?.Text_ : native[s.Id].ValueField?.Text?.Text_)
                .Distinct(StringComparer.Ordinal).ToArray();
            // A restored component may deliberately have no drawing. Its verified
            // history supplies the explicit value; there is no native field to read.
            if (values.Length == 0) return undrawnValue;
            if (values.Length != 1 || values[0] is null)
                throw Error("inconsistent_restored_properties", "Restored units must agree about their component reference and shared value.");
            return values[0]!;
        }
        var restoredIds = restoredComponents.Select(c => c.Id).ToHashSet();
        var owners = next.Components.ToDictionary(c => c.Id);
        next = next with
        {
            Components = next.Components.Select(c => restoredIds.Contains(c.Id)
                ? c with { Reference = Field(next.Symbols.Where(s => s.ComponentId == c.Id), true, c.Reference) } : c).ToArray(),
            Sheets = next.Sheets.Select(s => s with { Components = s.Components.Select(c => definitions.Contains(c.Id) ? c
                : c with { Value = Field(next.Symbols.Where(s => owners[s.ComponentId].DefinitionId == c.Id), false, c.Value) }).ToArray() }).ToArray(),
            Symbols = next.Symbols.Select(s => addedSymbols.Contains(s.Id) ? s with
                { Placement = SchematicModelProjection.Placement(native[s.Id]),
                    SheetInstanceId = s.EffectiveSheetInstanceId(owners[s.ComponentId]) == owners[s.ComponentId].SheetInstanceId
                        ? null : s.EffectiveSheetInstanceId(owners[s.ComponentId]) } : s).ToArray()
        };
        token.ThrowIfCancellationRequested(); next.Validate();
        // These provisional nets are only for binding comparison. The real
        // electrical checkpoint supplies the final pin partition later.
        var engineering = ComponentReferenceRetention.Retain(baseline.Engineering, next, [], state.KnowledgeLibraries);
        design = design with { Engineering = engineering };
        var report = SchematicDesignBindings.Inspect(design, state.KnowledgeLibraries, token);
        if (!report.IdentitiesResolved)
            throw Error("unresolved_restored_bindings", "The restored declarations do not resolve every exact native object.");
        return new(design, history, addedSymbols.Order().ToArray(), restoredIds.Order().ToArray())
            { RestoredSheetInstances = [.. restoredSheets.Select(s => s.Id)] };
    }

    internal static EngineeringDesign ResolveRetained(EngineeringDesign design, SchematicNativeRestorationResult restoration,
        IReadOnlyCollection<Guid> restoredNets, IReadOnlyCollection<ComponentKnowledgeLibrary> libraries)
    {
        var historical = restoration.Source.Design.Engineering;
        foreach (var pending in (design.Structure.UnresolvedComponentReferences ?? []).ToArray())
            if (restoration.RestoredComponents.Contains(pending.FormerTarget.ComponentId)
                && !(historical.Structure.UnresolvedComponentReferences ?? []).Any(r => r.OwnerId == pending.OwnerId
                    && r.Slot == pending.Slot && r.FormerTarget == pending.FormerTarget))
                design = ComponentReferenceRetention.Resolve(design, pending.OwnerId, pending.Slot, pending.FormerTarget, pending.FormerTarget, libraries);
        foreach (var pending in (design.UnresolvedGuidanceBindings ?? []).ToArray())
            if (restoration.RestoredComponents.Contains(pending.ComponentInstanceId)
                && !(historical.UnresolvedGuidanceBindings ?? []).Any(r => r.ComponentInstanceId == pending.ComponentInstanceId))
                design = ComponentReferenceRetention.ResolveGuidance(design, pending.ComponentInstanceId, pending.ComponentInstanceId, libraries);
        foreach (var pending in (design.Structure.UnresolvedNetBindings ?? []).ToArray())
            if (restoredNets.Contains(pending.FormerNetId)
                && !(historical.Structure.UnresolvedNetBindings ?? []).Any(r => r.OwnerId == pending.OwnerId && r.FormerNetId == pending.FormerNetId))
                design = design with { Structure = design.Structure.ResolveUnresolvedNet(design.Circuit, pending.OwnerId, pending.FormerNetId, pending.FormerNetId) };
        design.Validate(libraries); return design;
    }

    private static string Key(SchematicNativeRestorationResult candidate)
    {
        var circuit = candidate.BindingCandidate.Engineering.Circuit;
        var old = candidate.Source.Design.Engineering;
        return JsonSerializer.Serialize(new
        {
            topology = SchematicNetReconciliation.Topology(circuit), bindings = SchematicNetReconciliation.Bindings(candidate.BindingCandidate),
            unresolvedComponents = (old.Structure.UnresolvedComponentReferences ?? []).OrderBy(r => r.OwnerId).ThenBy(r => r.Slot)
                .ThenBy(r => r.FormerTarget.ComponentId).ThenBy(r => r.FormerTarget.PinNumber, StringComparer.Ordinal),
            unresolvedGuidance = (old.UnresolvedGuidanceBindings ?? []).OrderBy(r => r.ComponentInstanceId),
            unresolvedNets = (old.Structure.UnresolvedNetBindings ?? []).OrderBy(r => r.OwnerId).ThenBy(r => r.FormerNetId),
            nets = old.Circuit.Nets.OrderBy(n => n.Id).Select(n => new { n.Id, n.Name, pins = SchematicNetReconciliation.Key(n.Pins) })
        });
    }

    private static AutomationException Error(string code, string message) => new(code, message);
}

/// <summary>A decision the synchronization cannot take from exact identities alone. It names the native symbol KiCad
/// shows, the sheet it sits on and the exact candidates; nothing is published until the question is answered, in the XML
/// or with kicad_design_ownership_answer. <paramref name="ProposedComponentId"/> is the component the symbol becomes when
/// it is a component of its own: answering with it keeps the symbol apart from the candidate components.</summary>
public sealed record SchematicOwnershipResolutionRequest(string Code, Guid NativeObjectId, string NativePath,
    Guid SheetInstanceId, string Reference, string LibraryId, int Unit, IReadOnlyList<Guid> CandidatePartIds,
    IReadOnlyList<Guid> CandidateComponentIds, string Reason, Guid ProposedComponentId);

/// <summary>A person's answer for one symbol placed in KiCad (kicad_design_ownership_answer, ledger p35cfdc0345e056a5):
/// <paramref name="PartId"/> is the part the symbol is, one of its request's candidate parts; <paramref name="ComponentId"/>
/// is the component it draws a unit of: an existing component or another new symbol's proposed component from the
/// request's candidates, or the symbol's own proposed component for a component of its own. Null leaves that decision to
/// exact identities.</summary>
public sealed record SchematicOwnershipAnswer(Guid NativeObjectId, Guid? PartId = null, Guid? ComponentId = null)
{
    /// <summary>Optional sheet-instance path that disambiguates equal native UUIDs on repeated sheets.</summary>
    public IReadOnlyList<Guid>? NativePath { get; init; }
}

internal sealed record SchematicNativeAdditionResult(SchematicNativeRestorationResult? Adoption,
    IReadOnlyList<SchematicOwnershipResolutionRequest> Requests, IReadOnlyList<SchematicBindingIssue> Issues,
    IReadOnlyList<HierarchyCoverageGap> CoverageGaps, string? ErrorCode = null, string? ErrorMessage = null)
{
    /// <summary>Sheet moves exact identities cannot decide (<see cref="SchematicNativeSheetChanges.MoveAmbiguous"/>).</summary>
    public IReadOnlyList<SchematicSheetResolutionRequest> SheetRequests { get; init; } = [];
    public IReadOnlyList<SchematicSheetComponentResolutionRequest> SheetComponentRequests { get; init; } = [];
}

/// <summary>The saved XML with a person's answers declared (<see cref="SchematicNativeAdditionProjection.Answer"/>), and the
/// native symbols it declares.</summary>
internal sealed record SchematicOwnershipAnswerResult(SchematicDesign? Answered, IReadOnlyList<SchematicSymbolBinding> DeclaredBindings,
    IReadOnlyList<SchematicOwnershipResolutionRequest> Requests, IReadOnlyList<SchematicBindingIssue> Issues,
    string? ErrorCode = null, string? ErrorMessage = null)
{
    internal IReadOnlyList<Guid> DeclaredSymbols => [.. DeclaredBindings.Select(b => b.NativeObjectId).Order()];
}

/// <summary>Symbols placed in KiCad since the last synchronization become design components (ledger p74ee7c1da24272d9).
/// Each new symbol becomes one new component instance on the sheet instance KiCad shows it on, with identities derived
/// from the circuit, the native sheet path and the symbol's own UUID, so a repeated plan, a replay and a redo give the
/// same identities. Its part is decided only by exact identity: an existing part whose symbols KiCad draws, or whose
/// declared symbol is, the same library symbol with exactly the same units and pins (numbers, names and units); a new
/// part with that library symbol's pins when there is none. Anything else is a resolution request, never a guess: several
/// such parts, a new unit of a multi-unit part that an existing component may be missing, or several new units of one
/// multi-unit part. The person answers a request in the saved XML, by declaring the occurrence bound to that symbol (its
/// component, part, unit and reference as KiCad shows them), or with kicad_design_ownership_answer, which writes that
/// declaration (ledger p35cfdc0345e056a5); a declared answer is taken exactly as declared. Symbols removed in the same
/// KiCad change are removed as the removal projection removes them, with their instructions retained. Sheets KiCad
/// removed or shows at another place follow KiCad as the removal projection projects them, and each sheet inserted in KiCad
/// becomes a design sheet named as KiCad names it, with identities derived from the circuit, its parent sheet's path and its
/// sheet symbol's UUID; symbols on it are adopted like any other (ledger p5f6d5d0ca242d628). A sheet an earlier synchronized
/// design had is restored from that history instead, and a sheet shown several times is not adopted yet.</summary>
public static class SchematicNativeAdditionProjection
{
    public const string ResolutionRequired = "native_ownership_resolution_required";
    /// <summary>An existing sheet moved into a sheet inserted in the same KiCad change: synchronize the new sheet first.</summary>
    public const string MoveIntoNewSheet = "native_sheet_move_into_new_sheet";
    public const string PartAmbiguous = "native_part_ambiguous";
    public const string UnitOwnerAmbiguous = "native_unit_owner_ambiguous";
    public const string UnitGroupingAmbiguous = "native_unit_grouping_ambiguous";
    /// <summary>The saved XML answers a symbol placed in KiCad with something KiCad does not show.</summary>
    public const string AnswerMismatch = "native_ownership_answer_mismatch";
    /// <summary>An answer given with kicad_design_ownership_answer that is not one of the request's choices.</summary>
    public const string AnswerInvalid = "native_ownership_answer_invalid";
    /// <summary>The tool that writes a person's answers into the saved XML.</summary>
    public const string AnswerTool = "kicad_design_ownership_answer";
    internal const string ResolutionMessage = "KiCad shows new symbols whose design owner cannot be decided from exact identities. "
        + "Answer each request with " + AnswerTool + ", or declare the symbol's component in the XML, then synchronize again "
        + "(resume the automatic synchronization, or plan and apply).";

    /// <summary>The stable identity a symbol placed in KiCad gives the design object of <paramref name="kind"/>
    /// ("component", "definition" or "occurrence").</summary>
    public static Guid AdoptedIdentity(string kind, Guid circuitId, IReadOnlyList<Guid> nativePath, Guid nativeObjectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(nativePath);
        return Stable("kicad-native-adoption-v1\n" + kind + "\n" + circuitId.ToString("D") + "\n"
            + string.Join('/', nativePath.Select(p => p.ToString("D"))) + "\n" + nativeObjectId.ToString("D"));
    }

    /// <summary>The stable identity of the new part a library symbol with exactly these units and pins gives the circuit.</summary>
    public static Guid AdoptedPartIdentity(Guid circuitId, string libraryId, int units, IEnumerable<PartPin> pins)
    {
        ArgumentNullException.ThrowIfNull(libraryId);
        ArgumentNullException.ThrowIfNull(pins);
        return Stable("kicad-native-adoption-v1\npart\n" + circuitId.ToString("D") + "\n" + libraryId + "\n" + Signature(units, pins));
    }

    /// <summary>Whether <paramref name="desired"/> binds an occurrence the last synchronized design does not have to a
    /// symbol KiCad shows and that design does not bind: an answer to a resolution request, not a creation.</summary>
    internal static bool DeclaresAddedSymbols(DesignRecoveryState state, SchematicDesign desired)
    {
        var occurrences = state.Baseline.Engineering.Circuit.Symbols.Select(s => s.Id).ToHashSet();
        var bound = state.Baseline.SymbolBindings.Select(b => BindingKey(state.Baseline, b))
            .Where(k => k is not null).Select(k => k!).ToHashSet(StringComparer.Ordinal);
        var candidates = desired.SymbolBindings.Where(b => !occurrences.Contains(b.SymbolOccurrenceId))
            .Select(b => BindingKey(desired, b)).Where(k => k is not null && !bound.Contains(k))
            .Select(k => k!).ToHashSet(StringComparer.Ordinal);
        return candidates.Count != 0 && state.Observed.Instances.Any(screen =>
        {
            string path = SchematicDesignBindings.PathKey(screen.Metadata.Document.SheetPath.Path.Select(id => Guid.Parse(id.Value)).ToArray());
            return screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                .Select(s => path + "#" + s.Id?.Value).Any(candidates.Contains);
        });
    }

    internal static SchematicNativeAdditionResult Project(DesignRecoveryState state, IReadOnlyList<SchematicOwnershipHistory>? history,
        CancellationToken token) => Project(state, history, null, null, token);

    /// <param name="declared">The saved XML. Occurrences it binds to symbols placed in KiCad since the last synchronization
    /// are the person's answers; they must agree with what KiCad shows, and the XML must differ from the last synchronized
    /// design only by them.</param>
    /// <param name="answers">Answers given with kicad_design_ownership_answer that the XML does not declare yet.</param>
    internal static SchematicNativeAdditionResult Project(DesignRecoveryState state, IReadOnlyList<SchematicOwnershipHistory>? history,
        SchematicDesign? declared, IReadOnlyList<SchematicOwnershipAnswer>? answers, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var gaps = new List<HierarchyCoverageGap>();
        SchematicNativeAdditionResult Failure(string code, string message, IReadOnlyList<SchematicBindingIssue>? issues = null) =>
            new(null, [], issues ?? [], gaps.Distinct().ToArray(), code, message);
        try
        {
            var baseline = state.Baseline; var observed = state.Observed; var libraries = state.KnowledgeLibraries;
            var repeatedChoices = SchematicRepeatedSheetChoices.Current(state);
            answers ??= repeatedChoices?.SymbolOwners;
            var original = SchematicDesignBindings.Inspect(baseline, libraries, token);
            gaps.AddRange(original.CoverageGaps);
            if (!original.IdentitiesResolved) return Failure("unresolved_design_bindings", "Resolve the saved design's bindings first.", original.Issues);
            var topology = SchematicHierarchyTopology.Inspect(observed, token);
            gaps.AddRange(topology.CoverageGaps);
            if (!topology.IsValid) return Failure("invalid_native_hierarchy", "Resolve the reported native hierarchy before adopting new symbols.");
            static string Key(SchematicScreenData screen) => string.Join('/', screen.Metadata.Document.SheetPath.Path.Select(p => p.Value));
            var screens = observed.Instances.ToDictionary(Key, StringComparer.Ordinal);
            // Sheets inserted, removed or moved in KiCad (ledger p5f6d5d0ca242d628), by exact identity only.
            var sheetChanges = SchematicNativeSheetChanges.Compare(baseline.Schematic, observed);
            if (sheetChanges.ErrorCode is not null)
                return Failure(sheetChanges.ErrorCode, sheetChanges.ErrorMessage!) with
                    { SheetRequests = SchematicNativeRemovalProjection.Requests(sheetChanges, baseline) };
            var inserted = sheetChanges.Inserted.ToHashSet(StringComparer.Ordinal);
            bool Within(string path, string sheet) => path == sheet || path.StartsWith(sheet + "/", StringComparison.Ordinal);
            if (sheetChanges.Moved.Values.FirstOrDefault(to => inserted.Any(sheet => Within(to, sheet))) is { } into)
                return Failure(MoveIntoNewSheet, "KiCad shows an existing sheet moved into a sheet inserted in the same change. Nothing was "
                    + "published. Undo the move in KiCad, let the new sheet synchronize, then move the sheet into it.");

            var circuit = baseline.Engineering.Circuit;
            var components = circuit.Components.ToDictionary(c => c.Id);
            // Where KiCad shows each design sheet now: moved sheets at their new place, removed sheets nowhere.
            var sheetPaths = sheetChanges.Rebind(baseline.SheetBindings);
            var bound = baseline.SymbolBindings.Select(b =>
            {
                var occurrence = circuit.Symbols.Single(s => s.Id == b.SymbolOccurrenceId);
                return sheetPaths[occurrence.EffectiveSheetInstanceId(components[occurrence.ComponentId])] + "#" + b.NativeObjectId.ToString("D");
            }).ToHashSet(StringComparer.Ordinal);
            var added = screens.OrderBy(p => p.Key, StringComparer.Ordinal).SelectMany(pair => pair.Value.Items
                    .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => (Path: pair.Key, Symbol: i.Unpack<SchematicSymbolInstance>())))
                .Where(x => !bound.Contains(x.Path + "#" + x.Symbol.Id.Value))
                .OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Symbol.Id.Value, StringComparer.Ordinal).ToArray();
            if (added.Length == 0 && inserted.Count == 0)
                return Failure("electrical_ownership_changed", "Unit changes or changed library pin identities require explicit ownership reconciliation.");

            // A symbol KiCad shows again after an undo belongs to the verified history that knew it, not to a new
            // component. Restoring it together with new symbols is two decisions; take them one at a time.
            var historical = (history ?? []).SelectMany(h => h.Design.SymbolBindings.Select(b => BindingKey(h.Design, b)))
                .Where(k => k is not null).Select(k => k!).ToHashSet(StringComparer.Ordinal);
            if (added.Any(x => historical.Contains(x.Path + "#" + x.Symbol.Id.Value)))
                return Failure("native_restoration_with_additions",
                    "KiCad shows symbols an earlier synchronized design had together with newly placed ones. Synchronize them separately: "
                    + "undo the new placement in KiCad, synchronize the restored symbols, then redo it.");
            // Likewise a sheet an earlier synchronized design had is restored from that history with its identities, never adopted anew.
            var historicalSheets = (history ?? []).SelectMany(h => h.Design.SheetBindings.Select(b => SchematicDesignBindings.PathKey(b.NativePath)))
                .ToHashSet(StringComparer.Ordinal);
            if (inserted.Any(historicalSheets.Contains))
                return Failure("native_restoration_with_additions",
                    "KiCad shows a sheet an earlier synchronized design had together with other changes. Synchronize them separately: "
                    + "undo the other changes in KiCad, synchronize the restored sheet, then redo them.");

            // Removals and moves first, exactly as a change without new owners is projected: KiCad's drawing without the
            // inserted sheets and the new symbols.
            var withoutAdded = observed.Clone();
            for (int index = withoutAdded.Instances.Count - 1; index >= 0; --index)
            {
                var screen = withoutAdded.Instances[index];
                string path = Key(screen);
                if (inserted.Contains(path)) { withoutAdded.Instances.RemoveAt(index); continue; }
                for (int i = screen.Items.Count - 1; i >= 0; --i)
                    if (screen.Items[i].Is(SchematicSymbolInstance.Descriptor)
                            && !bound.Contains(path + "#" + screen.Items[i].Unpack<SchematicSymbolInstance>().Id.Value)
                        || screen.Items[i].Is(SheetSymbol.Descriptor) && inserted.Contains(path + "/" + screen.Items[i].Unpack<SheetSymbol>().Id?.Value))
                        screen.Items.RemoveAt(i);
            }
            var removal = SchematicNativeRemovalProjection.Project(baseline, withoutAdded, libraries, token);
            gaps.AddRange(removal.CoverageGaps);
            if (removal.BindingCandidate is null)
                return Failure(removal.ErrorCode ?? "electrical_ownership_changed", removal.ErrorMessage
                    ?? "Unit changes or changed library pin identities require explicit ownership reconciliation.", removal.Issues);
            var kept = removal.BindingCandidate;
            // Each sheet inserted in KiCad becomes a design sheet: a sheet definition and its one instance below the design sheet
            // KiCad shows it in, with identities derived from the circuit, the parent sheet's path and the sheet symbol's UUID,
            // so a repeated plan, a replay and a redo give the same identities. It is named as KiCad names it.
            var addedSheets = new List<(SheetDefinition Definition, ModelSheetInstance Instance, SchematicSheetBinding Binding)>();
            var keptSheets = kept.SheetBindings.ToDictionary(b => SchematicDesignBindings.PathKey(b.NativePath), b => b.SheetInstanceId, StringComparer.Ordinal);
            var originalKept = kept;
            var definitionByScreen = kept.SheetBindings.GroupBy(b => screens[SchematicDesignBindings.PathKey(b.NativePath)].Metadata.ScreenId.Value)
                .ToDictionary(g => g.Key, g => kept.Engineering.Circuit.SheetInstances.Single(s => s.Id == g.First().SheetInstanceId).DefinitionId);
            var sheetDefinitions = kept.Engineering.Circuit.Sheets.ToDictionary(s => s.Id);
            foreach (string path in sheetChanges.Inserted)
            {
                token.ThrowIfCancellationRequested();
                var sheetSymbol = SchematicNativeSheetChanges.SheetSymbolOf(observed, path);
                string parent = SchematicNativeSheetChanges.Parent(path);
                if (sheetSymbol?.NameField?.Text?.Text_ is not { } name || string.IsNullOrWhiteSpace(name) || !keptSheets.TryGetValue(parent, out var parentSheet))
                    return Failure("native_sheet_incomplete", "A sheet inserted in KiCad needs its sheet symbol, a name and a parent sheet to join the design.");
                var parentPath = PathOf(parent); Guid symbolId = Guid.Parse(SchematicNativeSheetChanges.Last(path));
                Guid instanceId = AdoptedIdentity("sheet-instance", kept.Engineering.Circuit.Id, parentPath, symbolId);
                string screenId = screens[path].Metadata.ScreenId.Value;
                if (!definitionByScreen.TryGetValue(screenId, out Guid definitionId))
                {
                    definitionId = AdoptedIdentity("sheet-definition", kept.Engineering.Circuit.Id, parentPath, symbolId);
                    definitionByScreen.Add(screenId, definitionId);
                    sheetDefinitions.Add(definitionId, new(definitionId, name, []));
                }
                addedSheets.Add((sheetDefinitions[definitionId], new(instanceId, definitionId, parentSheet), new(instanceId, PathOf(path))));
                keptSheets.Add(path, instanceId);
            }
            if (addedSheets.Count != 0)
            {
                var withSheets = kept.Engineering.Circuit with
                {
                    Sheets = [.. sheetDefinitions.Values],
                    SheetInstances = [.. kept.Engineering.Circuit.SheetInstances, .. addedSheets.Select(s => s.Instance)]
                };
                // Reused definitions gain their explicit component instances below,
                // before the resulting circuit is validated as a whole.
                kept = kept with { Engineering = kept.Engineering with { Circuit = withSheets },
                    SheetBindings = [.. kept.SheetBindings, .. addedSheets.Select(s => s.Binding)] };
            }
            var repeated = SchematicRepeatedSheetAdoption.Project(originalKept, kept, observed,
                addedSheets.Select(s => s.Instance.Id).ToHashSet(), token, repeatedChoices?.ComponentReferences);
            if (repeated.Requests.Count != 0)
                return Failure(ResolutionRequired, "Give the missing component references with kicad_design_repeated_sheet_answer before synchronizing this repeated sheet.")
                    with { SheetComponentRequests = repeated.Requests };
            kept = repeated.Design;
            var repeatedBindings = repeated.NativeKeys.ToHashSet(StringComparer.Ordinal);
            var keptCircuit = kept.Engineering.Circuit;
            var keptComponents = keptCircuit.Components.ToDictionary(c => c.Id);
            var definitions = keptCircuit.Sheets.SelectMany(s => s.Components).ToDictionary(c => c.Id);
            var parts = keptCircuit.Parts.ToDictionary(p => p.Id);

            // Library evidence for each existing part: the library symbols KiCad draws its units with, and its declared symbol.
            var evidence = new Dictionary<Guid, HashSet<string>>();
            void Evidence(Guid part, string library) { if (!evidence.TryGetValue(part, out var set)) evidence.Add(part, set = new(StringComparer.Ordinal)); set.Add(library); }
            var keptPaths = kept.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
            foreach (var binding in kept.SymbolBindings)
            {
                var occurrence = keptCircuit.Symbols.Single(s => s.Id == binding.SymbolOccurrenceId);
                var owner = keptComponents[occurrence.ComponentId];
                var symbol = screens[keptPaths[occurrence.EffectiveSheetInstanceId(owner)]].Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                    .Select(i => i.Unpack<SchematicSymbolInstance>()).Single(s => s.Id.Value == binding.NativeObjectId.ToString("D"));
                Evidence(definitions[owner.DefinitionId].PartId, LibraryKey(symbol));
            }
            foreach (var partSymbol in baseline.PartSymbols ?? [])
                if (partSymbol.LibraryId is { } library) Evidence(partSymbol.PartId, LibraryKey(library));

            // Each symbol placed in KiCad, as KiCad shows it.
            var additions = new List<Addition>();
            foreach (var (path, symbol) in added.Where(a => !repeatedBindings.Contains(a.Path + "#" + a.Symbol.Id.Value)))
            {
                token.ThrowIfCancellationRequested();
                var sheet = kept.SheetBindings.Single(b => SchematicDesignBindings.PathKey(b.NativePath) == path);
                if (!Guid.TryParseExact(symbol.Id?.Value, "D", out Guid nativeId) || nativeId == Guid.Empty
                    || symbol.Definition is null || symbol.Unit is null || symbol.Unit.Unit < 1 || symbol.Unit.Unit > (int)symbol.Definition.UnitCount
                    || string.IsNullOrWhiteSpace(symbol.ReferenceField?.Text?.Text_))
                    return Failure("native_addition_incomplete", "A symbol placed in KiCad needs its identity, definition, unit and reference to be adopted.");
                var pins = Pins(symbol);
                int units = checked((int)symbol.Definition.UnitCount);
                string library = LibraryKey(symbol);
                additions.Add(new(path, symbol, nativeId, sheet.SheetInstanceId, library, pins, units, Signature(units, pins),
                    AdoptedIdentity("component", keptCircuit.Id, PathOf(path), nativeId), AdoptedPartIdentity(keptCircuit.Id, library, units, pins)));
            }
            var byKey = additions.ToDictionary(a => a.Key, StringComparer.Ordinal);
            SchematicOwnershipResolutionRequest Request(Addition addition, string code, IReadOnlyList<Guid> candidateParts,
                IReadOnlyList<Guid> candidateComponents, string reason) =>
                new(code, addition.NativeId, addition.Path, addition.Sheet, addition.Reference, addition.Library, addition.Unit,
                    candidateParts, candidateComponents, reason, addition.Proposed);

            // The parts a symbol can be, exactly as its request offers them: the existing parts drawn or declared with the
            // library symbol KiCad draws and with exactly its units and pins, and the part that library symbol already made
            // for an earlier adoption. With none, the symbol makes that part (DerivedPart).
            List<Guid> PartChoices(Addition addition)
            {
                var choices = keptCircuit.Parts.Where(p => evidence.TryGetValue(p.Id, out var libraries) && libraries.Contains(addition.Library)
                    && Signature(p.Units, p.Pins) == addition.Signature).Select(p => p.Id).ToList();
                if (parts.TryGetValue(addition.DerivedPart, out var earlier) && Signature(earlier.Units, earlier.Pins) == addition.Signature
                    && !choices.Contains(addition.DerivedPart))
                    choices.Add(addition.DerivedPart);
                return choices;
            }

            // Answers the saved XML declares: occurrences it binds to these symbols, taken exactly as declared.
            var declarations = new Dictionary<string, Declaration>(StringComparer.Ordinal);
            if (declared is not null && declared.Engineering.Circuit.Id == circuit.Id)
            {
                var known = circuit.Symbols.Select(s => s.Id).ToHashSet();
                var dc = declared.Engineering.Circuit;
                var dComponents = dc.Components.ToDictionary(c => c.Id);
                var dDefinitions = dc.Sheets.SelectMany(s => s.Components.Select(d => (Sheet: s.Id, Definition: d))).ToDictionary(x => x.Definition.Id);
                var dParts = dc.Parts.ToDictionary(p => p.Id);
                var dSymbols = dc.Symbols.ToDictionary(s => s.Id);
                var dPaths = declared.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
                // The library symbol the XML declares for each part it adds.
                var dLibraries = (declared.PartSymbols ?? []).Where(s => !parts.ContainsKey(s.PartId))
                    .ToDictionary(s => s.PartId, s => LibraryKey(s.LibraryId));
                foreach (var binding in declared.SymbolBindings.Where(b => !known.Contains(b.SymbolOccurrenceId)))
                {
                    token.ThrowIfCancellationRequested();
                    if (!dSymbols.TryGetValue(binding.SymbolOccurrenceId, out var occurrence) || !dComponents.TryGetValue(occurrence.ComponentId, out var component)
                        || !dPaths.TryGetValue(occurrence.EffectiveSheetInstanceId(component), out var path)
                        || !byKey.TryGetValue(path + "#" + binding.NativeObjectId.ToString("D"), out var addition))
                        return Failure(AnswerMismatch, $"The XML binds occurrence {binding.SymbolOccurrenceId:D} to symbol {binding.NativeObjectId:D}, which KiCad "
                            + "does not show as a symbol placed since the last synchronization on the sheet of that occurrence. Bind the symbol a resolution "
                            + "request names, on the sheet it names; nothing was published.");
                    if (declarations.ContainsKey(addition.Key))
                        return Failure(AnswerMismatch, $"The XML binds two occurrences to symbol {addition.Reference} ({addition.NativeId:D}); bind each symbol once.");
                    var (sheetDefinition, definition) = dDefinitions[component.DefinitionId];
                    var part = dParts[definition.PartId];
                    // The part must be one the request could offer: an existing part drawn or declared with the library symbol
                    // KiCad draws, the part that library symbol makes, or a part the XML adds and declares with that library symbol.
                    bool drawnWith = parts.ContainsKey(part.Id) ? PartChoices(addition).Contains(part.Id)
                        : part.Id == addition.DerivedPart || dLibraries.TryGetValue(part.Id, out var library) && library == addition.Library;
                    string? problem = Signature(part.Units, part.Pins) != addition.Signature
                            ? $"part {part.Name}, whose units and pins are not those of the library symbol {addition.Library} KiCad draws"
                        : !drawnWith ? $"part {part.Name}, which is not drawn or declared with the library symbol {addition.Library} KiCad draws"
                        : occurrence.Unit != addition.Unit ? $"unit {occurrence.Unit}, but KiCad shows unit {addition.Unit}"
                        : component.Reference != addition.Reference ? $"reference {component.Reference}, but KiCad shows {addition.Reference}"
                        : definition.Value != addition.Value ? $"value {definition.Value}, but KiCad shows {addition.Value}"
                        : occurrence.Placement is { } placement && !SchematicOrientation.Equivalent(placement, addition.Placement)
                            ? "a placement other than the one KiCad shows"
                        : null;
                    if (problem is not null)
                        return Failure(AnswerMismatch, $"The XML answers symbol {addition.Reference} ({addition.NativeId:D}) with {problem}. Make the answer "
                            + "agree with what KiCad shows, or change the symbol in KiCad; nothing was published.");
                    declarations.Add(addition.Key, new(addition, occurrence with { Placement = occurrence.Placement ?? addition.Placement },
                        component, sheetDefinition, definition, part));
                }
                if (declarations.Count != 0 && !OnlyDeclarations(baseline, declared, declarations.Values, token))
                    return Failure("ownership_change_with_xml_edits", "The XML answers which design objects the symbols placed in KiCad are, "
                        + "but also changes other components, parts, units, nets or bindings. Both versions are kept: save the answer on its own "
                        + "and synchronize it, then make the other change.");
            }

            // Answers given with kicad_design_ownership_answer.
            var answerBy = new Dictionary<string, SchematicOwnershipAnswer>(StringComparer.Ordinal);
            foreach (var answer in answers ?? [])
            {
                if (answer.NativePath is { Count: 0 })
                    return Failure(AnswerInvalid, $"Symbol {answer.NativeObjectId:D} has an empty native path; answer the sheet path named by its request.");
                string? answerPath = answer.NativePath is { Count: > 0 } ? SchematicDesignBindings.PathKey(answer.NativePath) : null;
                var matches = additions.Where(a => a.NativeId == answer.NativeObjectId
                    && (answerPath is null || a.Path == answerPath)).ToArray();
                if (matches.Length != 1)
                    return Failure(AnswerInvalid, matches.Length > 1
                        ? $"Symbol {answer.NativeObjectId:D} appears on repeated sheets; answer it with the NativePath from its request."
                        : $"Symbol {answer.NativeObjectId:D} is not a symbol KiCad shows since the last synchronization; "
                            + "answer the symbols the resolution requests name.");
                if (declarations.ContainsKey(matches[0].Key))
                    return Failure(AnswerInvalid, $"The XML already answers symbol {matches[0].Reference} ({answer.NativeObjectId:D}).");
                if (!answerBy.TryAdd(matches[0].Key, answer))
                    return Failure(AnswerInvalid, $"Symbol {matches[0].Reference} ({answer.NativeObjectId:D}) is answered twice; answer each symbol once.");
            }

            // Parts, for every symbol the XML does not answer.
            var requests = new List<SchematicOwnershipResolutionRequest>();
            var decided = new List<Decision>();
            var newParts = new Dictionary<Guid, PartDefinition>();
            foreach (var addition in additions.Where(a => !declarations.ContainsKey(a.Key)))
            {
                token.ThrowIfCancellationRequested();
                answerBy.TryGetValue(addition.Key, out var answer);
                var candidates = PartChoices(addition);
                Guid derived = addition.DerivedPart;
                if (answer?.PartId is Guid chosen)
                {
                    // Only a part the request offers, or the part the symbol would be anyway: an answer never overrides a part
                    // exact identities decide, and never makes the symbol a part drawn with another library symbol.
                    if (!(candidates.Count == 0 ? chosen == derived : candidates.Contains(chosen)))
                        return Failure(AnswerInvalid, $"Part {chosen:D} is not one of the parts {addition.Reference} ({addition.NativeId:D}) can be: "
                            + $"a part drawn or declared with the library symbol {addition.Library} KiCad draws, with exactly its units and pins"
                            + (candidates.Count == 0 ? $", of which this design has none, so the answer is the new part {derived:D}"
                                : $" ({string.Join(", ", candidates.Order().Select(c => c.ToString("D")))})")
                            + "; choose one of the request's candidate parts.");
                    if (candidates.Count > 1) candidates = [chosen];
                }
                if (candidates.Count > 1)
                {
                    requests.Add(Request(addition, PartAmbiguous, [.. candidates.Order()], [],
                        "Several parts are drawn with this library symbol and have exactly its pins; choose the part this symbol is."));
                    continue;
                }
                PartDefinition? created = null;
                if (candidates.Count == 0)
                {
                    created = newParts.TryGetValue(derived, out var shared) ? shared : new PartDefinition(derived,
                        (addition.Symbol.LibraryId ?? addition.Symbol.Definition.Id)?.EntryName is { Length: > 0 } entry ? entry : addition.Library,
                        addition.Units, addition.Pins);
                    newParts[derived] = created;
                }
                decided.Add(new(addition, created?.Id ?? candidates[0], created, answer));
            }
            int Units(Guid part) => newParts.TryGetValue(part, out var created) ? created.Units : parts[part].Units;

            // Units each component draws: existing ones, and the ones the XML declares.
            var drawn = new Dictionary<Guid, HashSet<int>>();
            void Draw(Guid component, int unit) { if (!drawn.TryGetValue(component, out var set)) drawn.Add(component, set = []); set.Add(unit); }
            foreach (var occurrence in keptCircuit.Symbols) Draw(occurrence.ComponentId, occurrence.Unit);
            foreach (var declaration in declarations.Values) Draw(declaration.Component.Id, declaration.Occurrence.Unit);
            var owners = keptCircuit.Components.Select(c => (c.Id, Part: definitions[c.DefinitionId].PartId, c.Reference))
                .Concat(declarations.Values.Where(d => !keptComponents.ContainsKey(d.Component.Id))
                    .Select(d => (d.Component.Id, Part: d.Definition.PartId, d.Component.Reference)).Distinct()).ToList();

            // Components answered with kicad_design_ownership_answer: a component of its own, or a unit of an existing component
            // or of another new symbol's component.
            var joins = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var founders = decided.Where(d => d.Answer?.ComponentId is Guid own && own == d.Addition.Proposed).ToHashSet();
            foreach (var founder in founders)
            {
                owners.Add((founder.Addition.Proposed, founder.Part, founder.Addition.Reference));
                Draw(founder.Addition.Proposed, founder.Addition.Unit);
            }
            var declaredOwners = declarations.Values.GroupBy(d => d.Component.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (var entry in decided.Where(d => d.Answer?.ComponentId is Guid target && target != d.Addition.Proposed))
            {
                Guid target = entry.Answer!.ComponentId!.Value;
                var addition = entry.Addition;
                // Another new symbol's own component: that symbol is a component of its own, of which this one is a unit.
                var other = decided.FirstOrDefault(d => d.Addition.Proposed == target && (d.Answer?.ComponentId is null || d.Answer.ComponentId == target));
                (Guid Part, string Reference)? owner = keptComponents.TryGetValue(target, out var existing) ? (definitions[existing.DefinitionId].PartId, existing.Reference)
                    : declaredOwners.TryGetValue(target, out var declaredOwner) ? (declaredOwner.Definition.PartId, declaredOwner.Component.Reference)
                    : other is not null ? (other.Part, other.Addition.Reference) : null;
                if (owner is not { } chosen)
                    return Failure(AnswerInvalid, $"Component {target:D} is not an existing component or a new symbol's own component; answer "
                        + $"{addition.Reference} ({addition.NativeId:D}) with one of its request's candidate components or its proposed component.");
                if (other is not null && founders.Add(other))
                {
                    owners.Add((target, other.Part, other.Addition.Reference));
                    Draw(target, other.Addition.Unit);
                }
                string? problem = chosen.Part != entry.Part ? "is made from another part"
                    : chosen.Reference != addition.Reference
                        ? $"is named {chosen.Reference}, while KiCad shows {addition.Reference}; set the symbol's reference in KiCad first"
                    : drawn.TryGetValue(target, out var units) && units.Contains(addition.Unit) ? $"already draws unit {addition.Unit}"
                    : null;
                if (problem is not null)
                    return Failure(AnswerInvalid, $"{addition.Reference} ({addition.NativeId:D}) cannot be a unit of component {target:D}: that component {problem}.");
                Draw(target, addition.Unit);
                joins.Add(addition.Key, target);
            }

            // Units of a multi-unit part: KiCad joins units into one component by their reference designator, which is a
            // name. Adopt one only when no component could own it: every other component of the part already draws that
            // unit, and it is the only new unit of that part.
            foreach (var group in decided.Where(d => d.Answer?.ComponentId is null && !founders.Contains(d) && Units(d.Part) > 1).GroupBy(d => d.Part))
            {
                var ofPart = owners.Where(o => o.Part == group.Key).Select(o => o.Id).Distinct().ToArray();
                foreach (var entry in group)
                {
                    // The same physical unit on another instance is another component, not
                    // a second unit that could join this component. Keep all other grouping
                    // questions explicit, including different units of a repeated symbol.
                    var alternatives = group.Where(g => g != entry && !(g.Addition.NativeId == entry.Addition.NativeId
                        && g.Addition.Unit == entry.Addition.Unit
                        && screens[g.Addition.Path].Metadata.ScreenId.Equals(screens[entry.Addition.Path].Metadata.ScreenId))).ToArray();
                    var missing = ofPart.Where(c => !(drawn.TryGetValue(c, out var units) && units.Contains(entry.Addition.Unit))).Order().ToArray();
                    if (missing.Length != 0 || alternatives.Length > 0)
                        requests.Add(Request(entry.Addition, missing.Length != 0 ? UnitOwnerAmbiguous : UnitGroupingAmbiguous, [group.Key],
                            missing.Length != 0 ? missing : [.. alternatives.Select(g => g.Addition.Proposed).Order()],
                            missing.Length != 0 ? "A component of this part does not draw this unit; choose whether the new unit is one of its units."
                                : "Several new units of this multi-unit part were placed; choose which of them are one component."));
                }
            }
            if (requests.Count != 0)
            {
                bool repeatedInsertion = addedSheets.Any(s => originalKept.Engineering.Circuit.Sheets.Any(d => d.Id == s.Definition.Id));
                return new(null, [.. requests.OrderBy(r => r.NativePath, StringComparer.Ordinal).ThenBy(r => r.NativeObjectId)], [],
                    gaps.Distinct().ToArray(), ResolutionRequired, repeatedInsertion
                        ? "Resolve the new shared sheet's part and unit-owner choices with kicad_design_repeated_sheet_answer, then synchronize the complete design."
                        : ResolutionMessage);
            }

            // Every decision is exact or answered: create the parts, components and occurrences.
            var sheets = keptCircuit.SheetInstances.ToDictionary(s => s.Id);
            var repeatedDefinitions = keptCircuit.SheetInstances.GroupBy(s => s.DefinitionId)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            var addedDefinitions = new Dictionary<Guid, List<ComponentDefinition>>();
            void Define(Guid sheetDefinition, ComponentDefinition definition)
            {
                if (!addedDefinitions.TryGetValue(sheetDefinition, out var list)) addedDefinitions.Add(sheetDefinition, list = []);
                var previous = list.FirstOrDefault(d => d.Id == definition.Id);
                if (previous is null) list.Add(definition);
                else if (previous != definition)
                    throw new AutomationException(AnswerMismatch,
                        "Instances of one repeated symbol must use the same component definition, part and value. Nothing was published.");
            }
            var addedComponents = new List<ComponentInstance>(); var addedOccurrences = new List<SymbolOccurrence>();
            var addedBindings = new List<SchematicSymbolBinding>(); var declaredParts = new List<PartDefinition>();
            var componentSheets = keptComponents.ToDictionary(p => p.Key, p => p.Value.SheetInstanceId);
            foreach (var declaration in declarations.Values) componentSheets.TryAdd(declaration.Component.Id, declaration.Component.SheetInstanceId);
            foreach (var entry in decided.Where(d => !joins.ContainsKey(d.Addition.Key)))
            {
                var addition = entry.Addition; var nativePath = PathOf(addition.Path);
                Guid sheetDefinition = sheets[addition.Sheet].DefinitionId;
                // A shared drawing defines the component once. Its instances and symbol
                // occurrences still use their full native paths, including distinct references.
                // A model definition is stable if another repeated sheet is added or removed.
                Guid definition = repeatedDefinitions.Contains(sheetDefinition)
                    ? AdoptedIdentity("shared-definition", keptCircuit.Id, [sheetDefinition], addition.NativeId)
                    : AdoptedIdentity("definition", keptCircuit.Id, nativePath, addition.NativeId);
                Guid occurrence = AdoptedIdentity("occurrence", keptCircuit.Id, nativePath, addition.NativeId);
                Define(sheetDefinition, new(definition, entry.Part, addition.Value));
                addedComponents.Add(new(addition.Proposed, definition, addition.Sheet, addition.Reference));
                componentSheets[addition.Proposed] = addition.Sheet;
                addedOccurrences.Add(new(occurrence, addition.Proposed, addition.Unit, addition.Placement));
                addedBindings.Add(new(occurrence, addition.NativeId));
            }
            foreach (var entry in decided.Where(d => joins.ContainsKey(d.Addition.Key)))
            {
                var addition = entry.Addition; Guid target = joins[addition.Key];
                Guid occurrence = AdoptedIdentity("occurrence", keptCircuit.Id, PathOf(addition.Path), addition.NativeId);
                addedOccurrences.Add(new(occurrence, target, addition.Unit, addition.Placement, componentSheets[target] == addition.Sheet ? null : addition.Sheet));
                addedBindings.Add(new(occurrence, addition.NativeId));
            }
            foreach (var declaration in declarations.Values.OrderBy(d => d.Addition.Path, StringComparer.Ordinal).ThenBy(d => d.Addition.NativeId))
            {
                if (!parts.ContainsKey(declaration.Part.Id) && !declaredParts.Any(p => p.Id == declaration.Part.Id)) declaredParts.Add(declaration.Part);
                if (!definitions.ContainsKey(declaration.Definition.Id)) Define(declaration.SheetDefinition, declaration.Definition);
                if (!keptComponents.ContainsKey(declaration.Component.Id) && !addedComponents.Any(c => c.Id == declaration.Component.Id))
                    addedComponents.Add(declaration.Component);
                addedOccurrences.Add(declaration.Occurrence);
                addedBindings.Add(new(declaration.Occurrence.Id, declaration.Addition.NativeId));
            }
            var next = keptCircuit with
            {
                Parts = [.. keptCircuit.Parts, .. newParts.Values.OrderBy(p => p.Id), .. declaredParts],
                Sheets = [.. keptCircuit.Sheets.Select(s => addedDefinitions.TryGetValue(s.Id, out var extra) ? s with { Components = [.. s.Components, .. extra] } : s)],
                Components = [.. keptCircuit.Components, .. addedComponents],
                Symbols = [.. keptCircuit.Symbols, .. addedOccurrences]
            };
            try { next.Validate(); }
            catch (AutomationException error)
            {
                return Failure("native_addition_conflict", "The symbols placed in KiCad cannot join the design as they are: " + error.Message);
            }
            var design = kept with { Engineering = kept.Engineering with { Circuit = next }, Schematic = observed.Clone(),
                SymbolBindings = [.. kept.SymbolBindings, .. addedBindings] };
            design.Engineering.Validate(libraries);
            var report = SchematicDesignBindings.Inspect(design, libraries, token);
            gaps.AddRange(report.CoverageGaps);
            if (!report.IdentitiesResolved)
                return Failure("unresolved_added_bindings", "The adopted symbols do not resolve every exact native object.", report.Issues);
            return new(new SchematicNativeRestorationResult(design, null, [], [])
            {
                AddedOccurrences = [.. repeated.Occurrences.Concat(addedOccurrences.Select(o => o.Id)).Order()],
                AddedComponents = [.. repeated.Components.Concat(addedComponents.Select(c => c.Id)).Order()],
                AddedParts = [.. newParts.Keys.Concat(declaredParts.Select(p => p.Id)).Order()],
                AnsweredOccurrences = [.. declarations.Values.Select(d => d.Occurrence.Id).Order()],
                RemovedOccurrences = removal.RemovedOccurrences, ComponentChanges = removal.ComponentChanges,
                AddedSheetInstances = [.. addedSheets.Select(s => s.Instance.Id)],
                RemovedSheetInstances = removal.RemovedSheetInstances, MovedSheetInstances = removal.MovedSheetInstances
            }, [], [], gaps.Distinct().ToArray());

        }
        catch (AutomationException error) { return Failure(error.Code, error.Message); }
    }

    /// <summary>The saved XML with <paramref name="answers"/> declared, for kicad_design_ownership_answer: for each answered
    /// symbol, and each symbol whose own component an answer joins, the occurrence bound to it and the component, definition
    /// and part it adds. Everything else stays as saved. The next synchronization adopts the declared symbols exactly as
    /// declared, and decides every other symbol placed in KiCad as it would without answers.</summary>
    internal static SchematicOwnershipAnswerResult Answer(DesignRecoveryState state, IReadOnlyList<SchematicOwnershipHistory> history,
        IReadOnlyList<SchematicOwnershipAnswer> answers, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(answers);
        if (answers.Count == 0) return new(null, [], [], [], AnswerInvalid, "Give an answer for at least one symbol a resolution request names.");
        var desired = DesignRecoveryStore.ReadDesired(state);
        var result = Project(state, history, desired, answers, token);
        if (result.Adoption is not { } adoption)
            return new(null, [], result.Requests, result.Issues, result.ErrorCode, result.ErrorMessage);
        var design = adoption.BindingCandidate; var circuit = design.Engineering.Circuit;
        if (adoption.AddedSheetInstances.Any(id => circuit.SheetInstances.Any(s => s.Id == id
                && state.Baseline.Engineering.Circuit.Sheets.Any(d => d.Id == s.DefinitionId))))
            return new(null, [], [], [], AnswerInvalid,
                "Use kicad_design_repeated_sheet_answer for ownership choices on a newly inserted shared sheet; no XML was written.");
        var components = circuit.Components.ToDictionary(c => c.Id);
        var paths = design.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
        var occurrences = circuit.Symbols.ToDictionary(s => s.Id);
        var joined = answers.Where(a => a.ComponentId is not null).Select(a => a.ComponentId!.Value).ToHashSet();
        var desiredCircuit = desired.Engineering.Circuit;
        var known = desiredCircuit.Symbols.Select(s => s.Id).ToHashSet();
        // The added occurrences to declare: the answered symbols and the symbols whose own component an answer joins.
        var declare = design.SymbolBindings.Where(b => !known.Contains(b.SymbolOccurrenceId) && adoption.AddedOccurrences.Contains(b.SymbolOccurrenceId))
            .Where(b =>
            {
                var occurrence = occurrences[b.SymbolOccurrenceId];
                string path = paths[occurrence.EffectiveSheetInstanceId(components[occurrence.ComponentId])];
                bool answered = answers.Any(a => a.NativeObjectId == b.NativeObjectId
                    && (a.NativePath is null || SchematicDesignBindings.PathKey(a.NativePath) == path));
                return answered || joined.Contains(occurrence.ComponentId)
                    && AdoptedIdentity("component", circuit.Id, PathOf(paths[occurrence.EffectiveSheetInstanceId(components[occurrence.ComponentId])]),
                        b.NativeObjectId) == occurrence.ComponentId;
            }).ToArray();
        // Declaring a new shared component definition also requires its other instances.
        // These choices have already been resolved above; without this closure a partial
        // ownership answer would create a definition missing an instance on sibling sheets.
        var desiredDefinitionIds = desiredCircuit.Sheets.SelectMany(s => s.Components).Select(d => d.Id).ToHashSet();
        var declaredDefinitionIds = declare.Select(b => components[occurrences[b.SymbolOccurrenceId].ComponentId].DefinitionId)
            .Where(id => !desiredDefinitionIds.Contains(id)).ToHashSet();
        declare = design.SymbolBindings.Where(b => !known.Contains(b.SymbolOccurrenceId)
                && adoption.AddedOccurrences.Contains(b.SymbolOccurrenceId)
                && (declare.Contains(b) || declaredDefinitionIds.Contains(components[occurrences[b.SymbolOccurrenceId].ComponentId].DefinitionId)))
            .ToArray();
        var declaredOccurrences = declare.Select(b => occurrences[b.SymbolOccurrenceId]).ToArray();
        var newComponents = declaredOccurrences.Select(o => components[o.ComponentId]).Where(c => !desiredCircuit.Components.Any(d => d.Id == c.Id))
            .DistinctBy(c => c.Id).ToArray();
        var definitions = circuit.Sheets.SelectMany(s => s.Components.Select(d => (Sheet: s.Id, Definition: d))).ToDictionary(x => x.Definition.Id);
        var desiredDefinitions = desiredCircuit.Sheets.SelectMany(s => s.Components).Select(d => d.Id).ToHashSet();
        var newDefinitions = newComponents.Select(c => definitions[c.DefinitionId]).Where(d => !desiredDefinitions.Contains(d.Definition.Id))
            .DistinctBy(d => d.Definition.Id).ToArray();
        var newParts = newDefinitions.Select(d => d.Definition.PartId).Distinct().Where(p => !desiredCircuit.Parts.Any(x => x.Id == p))
            .Select(p => circuit.Parts.Single(x => x.Id == p)).ToArray();
        var answeredDesign = desired with
        {
            Engineering = desired.Engineering with
            {
                Circuit = desiredCircuit with
                {
                    Parts = [.. desiredCircuit.Parts, .. newParts],
                    Sheets = [.. desiredCircuit.Sheets.Select(s => s with { Components = [.. s.Components,
                        .. newDefinitions.Where(d => d.Sheet == s.Id).Select(d => d.Definition)] })],
                    Components = [.. desiredCircuit.Components, .. newComponents],
                    Symbols = [.. desiredCircuit.Symbols, .. declaredOccurrences]
                }
            },
            SymbolBindings = [.. desired.SymbolBindings, .. declare]
        };
        return new(answeredDesign, [.. declare.OrderBy(b => b.SymbolOccurrenceId)], [], []);
    }

    // The saved XML is the last synchronized design with only the declared answers added: their occurrences and bindings,
    // the components, definitions and parts only they use, and nets that differ only by the pins only they draw.
    private static bool OnlyDeclarations(SchematicDesign baseline, SchematicDesign declared, IEnumerable<Declaration> declarations,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var circuit = baseline.Engineering.Circuit; var dc = declared.Engineering.Circuit;
        var answers = declarations.ToArray();
        var occurrences = answers.Select(d => d.Occurrence.Id).ToHashSet();
        var bindings = answers.Select(d => d.Occurrence.Id).ToHashSet();
        var baseComponents = circuit.Components.Select(c => c.Id).ToHashSet();
        var baseDefinitions = circuit.Sheets.SelectMany(s => s.Components).Select(d => d.Id).ToHashSet();
        var baseParts = circuit.Parts.Select(p => p.Id).ToHashSet();
        var newComponents = answers.Select(d => d.Component.Id).Where(id => !baseComponents.Contains(id)).ToHashSet();
        var newDefinitions = answers.Select(d => d.Definition.Id).Where(id => !baseDefinitions.Contains(id)).ToHashSet();
        var newParts = answers.Select(d => d.Part.Id).Where(id => !baseParts.Contains(id)).ToHashSet();
        // Pins a declared unit draws that the component did not draw before.
        var answerPins = new HashSet<PinEndpoint>();
        foreach (var owner in answers.GroupBy(d => d.Component.Id))
        {
            var part = owner.First().Part;
            var before = circuit.Symbols.Where(s => s.ComponentId == owner.Key).Select(s => s.Unit).ToHashSet();
            var after = before.Concat(owner.Select(d => d.Occurrence.Unit)).ToHashSet();
            static bool Carried(IEnumerable<PartPin> pins, HashSet<int> units) => pins.Any(p => p.Unit == 0 ? units.Count != 0 : units.Contains(p.Unit));
            foreach (var pins in part.Pins.GroupBy(p => p.Number, StringComparer.Ordinal))
                if (Carried(pins, after) && !Carried(pins, before)) answerPins.Add(new(owner.Key, pins.Key));
        }
        var stripped = dc with
        {
            Parts = [.. dc.Parts.Where(p => !newParts.Contains(p.Id))],
            Sheets = [.. dc.Sheets.Select(s => s with { Components = [.. s.Components.Where(d => !newDefinitions.Contains(d.Id))] })],
            Components = [.. dc.Components.Where(c => !newComponents.Contains(c.Id))],
            Symbols = [.. dc.Symbols.Where(s => !occurrences.Contains(s.Id))],
            Nets = [.. dc.Nets.Select(n => n with { Pins = [.. n.Pins.Where(p => !answerPins.Contains(p))] })]
        };
        try
        {
            return CircuitXml.Write(stripped) == CircuitXml.Write(circuit)
                && SchematicNetReconciliation.Bindings(declared with { SymbolBindings = [.. declared.SymbolBindings.Where(b => !bindings.Contains(b.SymbolOccurrenceId))] })
                    == SchematicNetReconciliation.Bindings(baseline);
        }
        catch (AutomationException) { return false; }
    }

    // DerivedPart is the part this library symbol makes when no part of the design is drawn with it.
    private sealed record Addition(string Path, SchematicSymbolInstance Symbol, Guid NativeId, Guid Sheet, string Library,
        IReadOnlyList<PartPin> Pins, int Units, string Signature, Guid Proposed, Guid DerivedPart)
    {
        public string Key => Path + "#" + NativeId.ToString("D");
        public int Unit => Symbol.Unit.Unit;
        public string Reference => Symbol.ReferenceField.Text.Text_;
        public string Value => Symbol.ValueField?.Text?.Text_ ?? "";
        public SymbolPlacement Placement => SchematicModelProjection.Placement(Symbol);
    }

    private sealed record Declaration(Addition Addition, SymbolOccurrence Occurrence, ComponentInstance Component, Guid SheetDefinition,
        ComponentDefinition Definition, PartDefinition Part);

    private sealed record Decision(Addition Addition, Guid Part, PartDefinition? NewPart, SchematicOwnershipAnswer? Answer);

    private static IReadOnlyList<Guid> PathOf(string path) => [.. path.Split('/').Select(Guid.Parse)];

    private static string? BindingKey(SchematicDesign design, SchematicSymbolBinding binding)
    {
        var circuit = design.Engineering.Circuit;
        var occurrence = circuit.Symbols.SingleOrDefault(s => s.Id == binding.SymbolOccurrenceId);
        if (occurrence is null) return null;
        var component = circuit.Components.SingleOrDefault(c => c.Id == occurrence.ComponentId);
        if (component is null) return null;
        var sheet = design.SheetBindings.SingleOrDefault(s => s.SheetInstanceId == occurrence.EffectiveSheetInstanceId(component));
        return sheet is null ? null : SchematicDesignBindings.PathKey(sheet.NativePath) + "#" + binding.NativeObjectId.ToString("D");
    }

    internal static string LibraryKey(SchematicSymbolInstance symbol) =>
        LibraryKey(symbol.LibraryId ?? symbol.Definition?.Id ?? new Kiapi.Common.Types.LibraryIdentifier());

    private static string LibraryKey(Kiapi.Common.Types.LibraryIdentifier library) =>
        (library.LibraryNickname.Length == 0 ? "" : library.LibraryNickname + ":") + library.EntryName;

    // The electrical pins of the placed body: every unit's pins of the symbol's body style and the common ones.
    private static IReadOnlyList<PartPin> Pins(SchematicSymbolInstance symbol)
    {
        int style = symbol.BodyStyle?.Style is > 0 ? symbol.BodyStyle.Style : 1;
        return [.. symbol.Definition.Items.Where(c => c.Item?.Is(SchematicPin.Descriptor) == true
                && (c.BodyStyle?.Style ?? 0) is var body && (body == 0 || body == style))
            .Select(c => (Pin: c.Item.Unpack<SchematicPin>(), Unit: c.Unit?.Unit ?? 0))
            .Select(x => new PartPin(x.Pin.Number, x.Pin.Name, x.Unit)).Distinct()
            .OrderBy(p => p.Number, StringComparer.Ordinal).ThenBy(p => p.Unit)];
    }

    private static string Signature(int units, IEnumerable<PartPin> pins) => JsonSerializer.Serialize(new
    {
        units, pins = pins.OrderBy(p => p.Number, StringComparer.Ordinal).ThenBy(p => p.Unit).Select(p => new { p.Number, p.Name, p.Unit })
    });

    private static Guid Stable(string text)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        digest[6] = (byte)((digest[6] & 0x0f) | 0x50); digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }
}

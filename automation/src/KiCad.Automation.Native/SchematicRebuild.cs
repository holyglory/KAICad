using System.Security.Cryptography;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using ModelSheetInstance = KiCad.Automation.Model.SheetInstance;

namespace KiCad.Automation.Native;

// Provisional until lane 2C confirms; changes go through a seam request
// (decision kicad-phase2-contract-errata-20260923, item 2). This covers every
// rebuild seam shape in this file: the classification, SchematicRebuildIntent,
// the entry-point signatures and the schematic_rebuild_unavailable code, plus the
// plan's Rebuild parameter and the planner's design_sync_conflict fallback.

public enum SchematicRebuildKind { NotApplicable = 0, Admitted = 1, Rejected = 2 }

public sealed record SchematicRebuildClassification(SchematicRebuildKind Kind, IReadOnlyList<Guid> SheetInstanceIds,
    string? ErrorCode = null, string? ErrorMessage = null);

/// <summary>The model sheet instances an admitted rebuild regenerates natively from
/// saved XML. The synchronization seam only carries it from planner to executor.
/// Provisional until lane 2C confirms; changes go through a seam request.</summary>
public sealed record SchematicRebuildIntent(int Version, IReadOnlyList<Guid> SheetInstanceIds)
{
    public const int CurrentVersion = 1;
}

/// <summary>Native identities sheet generation gives one new model sheet instance: its sheet symbol
/// on the parent sheet and the screen (file) that sheet shows. Derived only from the model identity,
/// so a preview, its apply and a second planner compute the same ones.</summary>
public sealed record SchematicGeneratedSheetIdentity(Guid SheetInstanceId, Guid SheetSymbolId, Guid ScreenId);

/// <summary>Entry points the synchronization seam calls to generate missing native
/// sheets and rebuild deleted native files from saved XML without loss. Lane 2C
/// owns this file. Provisional until lane 2C confirms; changes go through a seam request.
///
/// Two shapes are admitted, and nothing else changes the general path:
/// <list type="bullet">
/// <item>Sheet generation. The saved XML adds model sheets (definitions and their one instance each,
/// below sheets KiCad already shows) and changes nothing else; KiCad has not changed since the last
/// synchronization. Each new sheet becomes a sheet symbol on its parent sheet and an empty screen
/// (file) with identities derived from its model identity. Components on the new sheets follow in a
/// later XML revision, because they need coordinates on sheets that exist.</item>
/// <item>Rebuild. The native files were deleted and KiCad created a new, empty root for the project
/// (a new document session whose only screen was never loaded or saved). The saved XML is the design
/// last synchronized with KiCad, and it holds every part of the deleted files: every screen, object,
/// library cache entry, page, title block, root page and schematic-wide setting. The rebuild gives
/// the new root the identity its file had and recreates everything else with its exact identities.
/// The project file is never written by the rebuild: settings the XML does not type
/// (<c>complete_project_settings</c>) stay in the project file, which a rebuild keeps.</item>
/// </list></summary>
public static class SchematicRebuild
{
    /// <summary>The only unrepresented native state a rebuild accepts: project settings the XML does not
    /// type. They live in the project file, which deleting the schematic files leaves in place and which
    /// the rebuild never writes.</summary>
    public const string RetainedProjectSettings = "complete_project_settings";
    public const string BatchDescription = "Rebuild native sheets from XML";

    private const long Grid = 1_270_000L;
    private const long SheetWidth = 38_100_000L;
    private const long SheetHeight = 25_400_000L;
    private const long SheetInset = 25_400_000L;
    private const long SheetPitch = 50_800_000L;
    private const long RowPitch = 38_100_000L;
    private const int SheetsPerRow = 5;

    private static SchematicRebuildClassification NotApplicable => new(SchematicRebuildKind.NotApplicable, []);
    private static SchematicRebuildClassification Rejected(string code, string message) => new(SchematicRebuildKind.Rejected, [], code, message);

    /// <summary>Decide whether a saved XML revision needs native sheets generated or
    /// rebuilt. Pure: reads no files or editor. A shape neither rule admits keeps the general path
    /// and its error codes; a rebuild or generation the saved state cannot support is refused here
    /// with its own code, before anything is sent to KiCad.</summary>
    public static SchematicRebuildClassification Classify(DesignRecoveryState state, SchematicDesign desired,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(desired);
        token.ThrowIfCancellationRequested();
        if (state.HasPendingWork) return NotApplicable;
        try
        {
            if (IsReplacedEmptyRoot(state)) return ClassifyRebuild(state, desired, token);
            return ClassifyGeneration(state, desired, token);
        }
        catch (AutomationException)
        {
            // An input the rules cannot read stays on the general path, which reports it.
            return NotApplicable;
        }
    }

    public static SchematicSynchronizationPlan Prepare(DesignRecoveryState state, SchematicDesign desired,
        SchematicHierarchyMergeResult hierarchy, SchematicRebuildClassification shape,
        List<HierarchyCoverageGap> gaps, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(gaps);
        if (shape.Kind != SchematicRebuildKind.Admitted)
            throw new ArgumentException("Only an admitted rebuild can be prepared.", nameof(shape));
        token.ThrowIfCancellationRequested();
        try
        {
            var planned = IsReplacedEmptyRoot(state) ? PlanRebuild(state, desired, token) : PlanGeneration(state, desired, shape, token);
            var bindings = SchematicDesignBindings.Inspect(planned.Design, state.KnowledgeLibraries, token);
            gaps.AddRange(bindings.CoverageGaps);
            if (!bindings.IdentitiesResolved)
                return Failure("rebuild_binding_invalid", "The rebuilt native identities do not resolve the XML exactly.", bindings.Issues);
            string xml = SchematicDesignXml.Write(planned.Design, state.KnowledgeLibraries);
            if (SchematicDesignXml.Write(SchematicDesignXml.Read(xml, state.KnowledgeLibraries), state.KnowledgeLibraries) != xml)
                return Failure("inconsistent_design_serialization", "The rebuilt candidate must round-trip without information loss.");
            if (planned.Operations.Count == 0)
                return Failure("rebuild_nothing_to_do", "KiCad already shows every sheet the XML declares.");
            return new(planned.Design, xml, planned.Operations.Select(o => o.Clone()).ToArray(), hierarchy, null, null, [], [], null,
                gaps.Distinct().ToArray(), true, Rebuild: new(SchematicRebuildIntent.CurrentVersion, shape.SheetInstanceIds.ToArray()));
        }
        catch (AutomationException error) { return Failure(error.Code, error.Message); }

        SchematicSynchronizationPlan Failure(string code, string message, IReadOnlyList<SchematicBindingIssue>? issues = null) =>
            new(null, null, [], hierarchy, null, null, issues ?? [], [], null, gaps.Distinct().ToArray(), true, code, message);
    }

    /// <summary>Whether the pending layout recorded in <paramref name="state"/> was produced
    /// by a rebuild plan rather than a connected move or a connection realization. The executor
    /// routes by the lane recorded in the layout intent; this predicate only validates
    /// that record and must never claim a layout another lane recorded.</summary>
    internal static bool IsRebuild(DesignRecoveryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.PendingLayout?.Lane == DesignLayoutIntent.RebuildLane && state.PendingMutation is { } batch
            && batch.Description == BatchDescription && batch.Operations.Count != 0
            && batch.Operations.Select((o, index) => (o, index)).All(p => p.o.OperationCase is SchematicItemOperation.OperationOneofCase.Create
                or SchematicItemOperation.OperationOneofCase.Update or SchematicItemOperation.OperationOneofCase.ReplaceLibraryCache
                || RecreatesFileState(p.o, p.index));
    }

    /// <summary>Operations a rebuild journal holds besides creations, updates and library caches: the page, title
    /// block, root page, embedded files and schematic-wide settings that recreate a deleted file's non-item state,
    /// and the new root's saved identity, only as the batch's first operation. Never a removal, connected move or
    /// transform, or lock change: a rebuild only adds what the XML holds to sheets KiCad shows empty.</summary>
    internal static bool RecreatesFileState(SchematicItemOperation operation, int index)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation.OperationCase switch
        {
            SchematicItemOperation.OperationOneofCase.RebuildScreenIdentity => index == 0,
            SchematicItemOperation.OperationOneofCase.SetPageSettings or SchematicItemOperation.OperationOneofCase.SetTitleBlock
                or SchematicItemOperation.OperationOneofCase.SetRootInstance or SchematicItemOperation.OperationOneofCase.ReplaceEmbeddedFiles
                or SchematicItemOperation.OperationOneofCase.ReplaceBusAliases or SchematicItemOperation.OperationOneofCase.ReplaceTextVariables
                or SchematicItemOperation.OperationOneofCase.ReplaceNetChains or SchematicItemOperation.OperationOneofCase.ReplaceVariantRegistry
                or SchematicItemOperation.OperationOneofCase.SetDrawingRatios or SchematicItemOperation.OperationOneofCase.SetFormatting
                or SchematicItemOperation.OperationOneofCase.SetErcSettings or SchematicItemOperation.OperationOneofCase.ReplaceNetChainClasses
                or SchematicItemOperation.OperationOneofCase.SetAnnotation or SchematicItemOperation.OperationOneofCase.SetReferenceInventory
                or SchematicItemOperation.OperationOneofCase.SetFieldTemplates or SchematicItemOperation.OperationOneofCase.SetSymbolComparison
                or SchematicItemOperation.OperationOneofCase.SetBomSettings or SchematicItemOperation.OperationOneofCase.SetNetSettings => true,
            _ => false
        };
    }

    /// <summary>Return the rebuild operations for this checkpoint plus the exact design they
    /// plan to publish, after checking the <paramref name="session"/> handshake for the native
    /// support they need. The executor builds, validates and journals the batch envelope.</summary>
    internal static Task<SchematicPreparedRealization> RealizeAsync(NativeClient client, AutomationSession session,
        DesignRecoveryState state, SchematicSynchronizationPlan plan, CheckedSchematicState checkpoint, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(checkpoint);
        token.ThrowIfCancellationRequested();
        if (session.InstanceId != state.InstanceId.ToString("D"))
            throw new AutomationException("recovery_instance_mismatch", "The native connection belongs to another saved instance.");
        if (plan.Rebuild is null || plan.Candidate is null || plan.NativeOperations.Count == 0)
            throw new AutomationException("invalid_layout_intent", "Only a prepared rebuild plan can be realized.");
        // The executor has checked that this checkpoint is the observation the plan was made from.
        // The plan is a pure function of that observation and the saved XML, so it is used unchanged.
        if (!checkpoint.Electrical.Hierarchy.Data.Equals(state.Observed))
            throw new AutomationException("native_checkpoint_stale", "Refresh the native observation before rebuilding.");
        byte[] planned = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(plan.Candidate, state.KnowledgeLibraries));
        return Task.FromResult(new SchematicPreparedRealization(plan.NativeOperations.Select(o => o.Clone()).ToArray(), planned));
    }

    /// <summary>Check a rebuild receipt before generic handling. A batch KiCad refused without changing
    /// anything is abandoned, so the recovery record holds no pending work, and reported with KiCad's
    /// reason; nothing was changed and no XML is published.</summary>
    internal static void CheckReceipt(DesignRecoveryStore store, StoredDesignRecovery saved,
        CheckedSchematicBatchReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(receipt);
        var batch = saved.State.PendingMutation;
        var guard = saved.State.PendingNativeState;
        if (receipt.Status == CheckedSchematicBatchStatus.CsbsRejected)
        {
            if (batch is null || guard is null || saved.State.PendingPublication is not null || receipt.OperationId != batch.OperationId
                || !Equals(receipt.Document, batch.Document) || !receipt.ExpectedRequestVerified || receipt.Result is not null
                || !Equals(receipt.ObservedBefore, guard) || !Equals(receipt.ObservedAfter, guard) || !IsRebuild(saved.State))
                throw new AutomationException("invalid_rebuild_rejection",
                    "KiCad refused the rebuild without proving that nothing changed; the pending operation is kept for inspection.");
            store.AbandonRejectedRealization(saved, receipt);
            string detail = receipt.ErrorMessage.Length <= 2048 ? receipt.ErrorMessage : receipt.ErrorMessage[..2048];
            throw new AutomationException("rebuild_rejected",
                "KiCad refused to rebuild the sheets from the XML, so nothing was changed and no XML was published. KiCad reported: " + detail);
        }
        if (receipt.Status == CheckedSchematicBatchStatus.CsbsCompleted && batch is not null)
        {
            bool identity = batch.Operations.Any(o => o.RebuildScreenIdentity is not null);
            if (receipt.Result is null || receipt.Result.ScreenIdentityChanged != identity)
                throw new AutomationException("invalid_rebuild_receipt",
                    "KiCad's receipt does not confirm the root identity the rebuild requested; the pending operation is kept for inspection.");
        }
    }

    /// <summary>Resolve a committed rebuild against the planned design: KiCad must now hold exactly the
    /// planned sheets and objects. The result is the planned design with KiCad's own enumeration.</summary>
    internal static SchematicDesign Resolve(SchematicDesign planned, DesignRecoveryState state,
        SchematicElectricalState native, ApplySchematicItemBatch batch, CheckedSchematicBatchReceipt receipt,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(receipt);
        token.ThrowIfCancellationRequested();
        var observed = native.Hierarchy?.Data ?? throw new AutomationException("rebuild_native_mismatch", "KiCad returned no hierarchy after the rebuild.");
        static string Key(SchematicScreenData screen) => string.Join('/', screen.Metadata.Document.SheetPath.Path.Select(id => id.Value));
        var expected = planned.Schematic.Instances.Select(s => (Key(s), s.Metadata.ScreenId.Value)).Order().ToArray();
        var actual = observed.Instances.Select(s => (Key(s), s.Metadata.ScreenId.Value)).Order().ToArray();
        if (!expected.SequenceEqual(actual))
            throw new AutomationException("rebuild_native_mismatch", "KiCad's sheets after the rebuild are not the planned sheets; nothing is published.");
        // Sheet symbols sheet generation created are KiCad's own rendering of what was requested: their
        // identity, sheet, name, file, page, place and size must be exactly the planned ones, and KiCad's
        // copy is then what the published XML records. Everything else must be exactly as planned.
        var expectedSchematic = planned.Schematic.Clone();
        var generated = planned.SheetBindings.Select(b => GeneratedSheet(b.SheetInstanceId).SheetSymbolId.ToString("D")).ToHashSet(StringComparer.Ordinal);
        var created = batch.Operations.Where(o => o.Create?.Is(SheetSymbol.Descriptor) == true)
            .Select(o => o.Create.Unpack<SheetSymbol>().Id.Value).Where(generated.Contains).ToHashSet(StringComparer.Ordinal);
        foreach (var screen in expectedSchematic.Instances)
        {
            var nativeScreen = observed.Instances.Single(s => Key(s) == Key(screen));
            for (int i = 0; i < screen.Items.Count; ++i)
            {
                if (!screen.Items[i].Is(SheetSymbol.Descriptor)) continue;
                var wanted = screen.Items[i].Unpack<SheetSymbol>();
                if (!created.Contains(wanted.Id.Value)) continue;
                var made = nativeScreen.Items.Where(x => x.Is(SheetSymbol.Descriptor)).Select(x => x.Unpack<SheetSymbol>())
                    .SingleOrDefault(x => x.Id.Value == wanted.Id.Value);
                if (made is null || !Equals(made.ChildScreenId, wanted.ChildScreenId) || !Equals(made.Path, wanted.Path)
                    || made.NameField?.Text?.Text_ != wanted.NameField.Text.Text_ || made.FilenameField?.Text?.Text_ != wanted.FilenameField.Text.Text_
                    || made.PageNumber != wanted.PageNumber || !Equals(made.Position, wanted.Position) || !Equals(made.Size, wanted.Size)
                    || made.Pins.Count != 0 || made.InstanceRecords?.Records.Count != 1
                    || !made.InstanceRecords.Records[0].Path.SequenceEqual(wanted.InstanceRecords.Records[0].Path)
                    || made.InstanceRecords.Records[0].PageNumber != wanted.PageNumber
                    || made.InstanceRecords.Records[0].ProjectName != wanted.InstanceRecords.Records[0].ProjectName)
                    throw new AutomationException("rebuild_native_mismatch",
                        $"KiCad did not create the planned sheet {wanted.NameField.Text.Text_}; nothing is published.");
                screen.Items[i] = Any.Pack(made);
            }
        }
        IReadOnlyList<SchematicItemOperation> remaining;
        try { remaining = SchematicHierarchyDelta.Plan(observed, expectedSchematic, token); }
        catch (AutomationException error)
        {
            throw new AutomationException("rebuild_native_mismatch", "KiCad's rebuilt schematic differs from the planned one: " + error.Message);
        }
        if (remaining.Count != 0)
            throw new AutomationException("rebuild_native_mismatch",
                $"KiCad's rebuilt schematic differs from the planned one in {remaining.Count} object(s); nothing is published.");
        return planned with { Schematic = observed.Clone() };
    }

    /// <summary>The native identities sheet generation gives a new model sheet instance.</summary>
    public static SchematicGeneratedSheetIdentity GeneratedSheet(Guid sheetInstanceId)
    {
        if (sheetInstanceId == Guid.Empty) throw new ArgumentException("A model sheet instance identity is required.", nameof(sheetInstanceId));
        return new(sheetInstanceId, Stable("sheet-symbol", sheetInstanceId), Stable("screen", sheetInstanceId));
    }

    /// <summary>The file name sheet generation gives a sheet named <paramref name="name"/>: lower case,
    /// with every character other than a letter, digit, '-' or '_' replaced by '_'.</summary>
    public static string GeneratedFileStem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var text = new StringBuilder(name.Length);
        foreach (char c in name.Trim().ToLowerInvariant())
            text.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return text.Length == 0 ? "sheet" : text.ToString();
    }

    // ---- Classification -------------------------------------------------------------------

    // KiCad replaced the document (a new session) and shows only a root it created: one screen, never
    // loaded from or saved to a file, with nothing on it. That is what opening a project whose schematic
    // files are gone leaves; deleting everything inside KiCad keeps the loaded root and its session.
    private static bool IsReplacedEmptyRoot(DesignRecoveryState state)
    {
        var observed = state.Observed;
        if (observed.Instances.Count != 1) return false;
        var root = observed.Instances[0];
        if (root.Metadata?.Document is null || !root.Metadata.Document.Equals(observed.Document)
            || root.Items.Count != 0 || root.CachedSymbols.Count != 0 || root.UnrepresentedItems.Count != 0
            || root.Metadata.LoadedNativeFormatVersion != 0)
            return false;
        var baselineEpoch = state.BaselineElectrical?.Hierarchy?.Revision?.Epoch;
        if (baselineEpoch is null || baselineEpoch == state.NativeRevision.Epoch) return false;
        var baseline = state.Baseline.Schematic;
        return baseline.Instances.Count > 1 || baseline.Instances.Any(s => s.Items.Count != 0 || s.CachedSymbols.Count != 0);
    }

    private static SchematicRebuildClassification ClassifyRebuild(DesignRecoveryState state, SchematicDesign desired, CancellationToken token)
    {
        var baseline = state.Baseline;
        if (!SameDesign(baseline, desired, state.KnowledgeLibraries, token))
            return Rejected("rebuild_requires_settled_xml",
                "KiCad's schematic files are gone and the saved XML has changes KiCad never showed. Rebuild from the XML last "
                + "synchronized with KiCad, then apply the newer changes.");
        var missing = baseline.Schematic.Instances.SelectMany(s => s.Metadata.UnrepresentedState)
            .Where(m => m != RetainedProjectSettings).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length != 0 || baseline.Schematic.Instances.Any(s => s.UnrepresentedItems.Count != 0))
            return Rejected("rebuild_state_unrepresented",
                "The saved XML does not hold every part of the deleted schematic files ("
                + string.Join(", ", missing.Concat(baseline.Schematic.Instances.Any(s => s.UnrepresentedItems.Count != 0) ? ["unsupported objects"] : []))
                + "), so they cannot be rebuilt without loss.");
        if (baseline.Schematic.Instances.GroupBy(s => s.Metadata.ScreenId.Value, StringComparer.Ordinal).Any(g => g.Count() > 1))
            return Rejected("rebuild_state_unrepresented", "Rebuilding a sheet file shown by several sheets is not supported yet.");
        return new(SchematicRebuildKind.Admitted, [.. baseline.SheetBindings.Select(b => b.SheetInstanceId)]);
    }

    private static SchematicRebuildClassification ClassifyGeneration(DesignRecoveryState state, SchematicDesign desired, CancellationToken token)
    {
        var baseline = state.Baseline;
        var before = baseline.Engineering.Circuit;
        var after = desired.Engineering.Circuit;
        if (before.Id != after.Id) return NotApplicable;
        var known = before.SheetInstances.Select(s => s.Id).ToHashSet();
        var added = after.SheetInstances.Where(s => !known.Contains(s.Id)).ToArray();
        if (added.Length == 0) return NotApplicable;
        var knownDefinitions = before.Sheets.Select(s => s.Id).ToHashSet();
        var addedDefinitions = after.Sheets.Where(s => !knownDefinitions.Contains(s.Id)).ToDictionary(s => s.Id);
        var addedIds = added.Select(s => s.Id).ToHashSet();
        // New sheets that bring components along cannot be generated in one step: the components need coordinates on
        // sheets that exist first.
        if (addedDefinitions.Values.Any(d => d.Components.Count != 0) || after.Components.Any(c => addedIds.Contains(c.SheetInstanceId))
            || after.Symbols.Any(s => s.SheetInstanceId is Guid sheet && addedIds.Contains(sheet)))
            return Rejected("rebuild_sheet_generation_requires_empty_sheets",
                "Add the new sheets to the XML first; KiCad creates them empty, and their components follow in the next XML revision.");
        // Everything but the added sheets is the design last synchronized with KiCad.
        var withoutAdded = desired with
        {
            Engineering = desired.Engineering with
            {
                Circuit = after with
                {
                    Sheets = [.. after.Sheets.Where(s => !addedDefinitions.ContainsKey(s.Id))],
                    SheetInstances = [.. after.SheetInstances.Where(s => known.Contains(s.Id))]
                }
            }
        };
        if (!SameDesign(baseline, withoutAdded, state.KnowledgeLibraries, token) || !NativeUnchanged(state, token)) return NotApplicable;
        foreach (var instance in added)
        {
            if (!addedDefinitions.TryGetValue(instance.DefinitionId, out var definition))
                return Rejected("rebuild_shared_sheet_unsupported", "A new sheet can only show a new sheet definition.");
            if (instance.ParentId is null)
                return Rejected("rebuild_additional_root_unsupported", "A new sheet must be placed inside an existing or new sheet.");
        }
        if (added.GroupBy(i => i.DefinitionId).Any(g => g.Count() > 1) || addedDefinitions.Count != added.Length)
            return Rejected("rebuild_shared_sheet_unsupported", "Each new sheet definition must be shown by exactly one new sheet.");
        var order = TopologicalOrder(added, baseline.SheetBindings.Select(b => b.SheetInstanceId).ToHashSet());
        if (order is null)
            return Rejected("rebuild_sheet_parent_unresolved", "Every new sheet must sit below a sheet KiCad shows or another new sheet.");
        return new(SchematicRebuildKind.Admitted, [.. order.Select(i => i.Id)]);
    }

    private static IReadOnlyList<ModelSheetInstance>? TopologicalOrder(IReadOnlyList<ModelSheetInstance> added, HashSet<Guid> bound)
    {
        var placed = new HashSet<Guid>(bound);
        var remaining = added.ToList();
        var order = new List<ModelSheetInstance>();
        while (remaining.Count != 0)
        {
            // Keep the XML's own order among sheets whose parents already exist.
            var ready = remaining.FirstOrDefault(i => i.ParentId is Guid parent && placed.Contains(parent));
            if (ready is null) return null;
            order.Add(ready); placed.Add(ready.Id); remaining.Remove(ready);
        }
        return order;
    }

    // KiCad shows exactly the baseline: no native edit waits to be reconciled.
    private static bool NativeUnchanged(DesignRecoveryState state, CancellationToken token)
    {
        var baseline = state.Baseline.Schematic;
        static string Key(SchematicScreenData screen) => string.Join('/', screen.Metadata.Document.SheetPath.Path.Select(id => id.Value));
        if (!state.Observed.Instances.Select(Key).Order(StringComparer.Ordinal)
                .SequenceEqual(baseline.Instances.Select(Key).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            return false;
        if (SchematicHierarchyDelta.Plan(state.Observed, baseline, token).Count != 0) return false;
        return state.ObservedElectrical is null || state.BaselineElectrical is null
            || state.ObservedElectrical.Nets.Equals(state.BaselineElectrical.Nets);
    }

    // The same design: the same engineering, bindings and part symbols, and native objects that need no
    // edit to become each other. Loaded-format provenance and item enumeration are not design content.
    private static bool SameDesign(SchematicDesign a, SchematicDesign b, IReadOnlyCollection<ComponentKnowledgeLibrary> libraries,
        CancellationToken token)
    {
        try
        {
            if (SchematicDesignXml.Write(a with { Schematic = b.Schematic }, libraries) != SchematicDesignXml.Write(b, libraries)) return false;
            return SchematicHierarchyDelta.Plan(WithoutProvenance(a.Schematic), WithoutProvenance(b.Schematic), token).Count == 0
                && a.Schematic.Instances.Count == b.Schematic.Instances.Count;
        }
        catch (AutomationException) { return false; }
    }

    private static SchematicHierarchyData WithoutProvenance(SchematicHierarchyData data)
    {
        var result = data.Clone();
        foreach (var screen in result.Instances) screen.Metadata.LoadedNativeFormatVersion = 0;
        return result;
    }

    // ---- Planning -------------------------------------------------------------------------

    private sealed record PlannedRebuild(SchematicDesign Design, IReadOnlyList<SchematicItemOperation> Operations);

    private static PlannedRebuild PlanGeneration(DesignRecoveryState state, SchematicDesign desired,
        SchematicRebuildClassification shape, CancellationToken token)
    {
        var baseline = state.Baseline;
        var circuit = desired.Engineering.Circuit;
        var definitions = circuit.Sheets.ToDictionary(s => s.Id);
        var instances = circuit.SheetInstances.ToDictionary(s => s.Id);
        var schematic = baseline.Schematic.Clone();
        static string Key(IEnumerable<string> path) => string.Join('/', path);
        var screens = schematic.Instances.ToDictionary(s => Key(s.Metadata.Document.SheetPath.Path.Select(id => id.Value)), StringComparer.Ordinal);
        var rootScreen = schematic.Instances.Single(s => s.Metadata.Document.Equals(schematic.Document));
        var paths = baseline.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => b.NativePath);
        var bindings = baseline.SheetBindings.ToList();
        // Existing file names and page numbers, so generated ones never collide.
        var sheets = schematic.Instances.SelectMany(s => s.Items).Where(i => i.Is(SheetSymbol.Descriptor)).Select(i => i.Unpack<SheetSymbol>()).ToList();
        var files = sheets.Select(s => s.FilenameField?.Text?.Text_ ?? "").Append((schematic.Document.Project?.Name ?? "") + ".kicad_sch")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedIds = schematic.Instances.SelectMany(s => SchematicItemDelta.Index(s.Items).Keys)
            .Concat(schematic.Instances.Select(s => Guid.Parse(s.Metadata.ScreenId.Value))).ToHashSet();
        int page = sheets.Select(s => s.PageNumber).Append(rootScreen.Metadata.RootInstance?.PageNumber ?? "1")
            .Select(p => int.TryParse(p, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        var placedOn = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Guid id in shape.SheetInstanceIds)
        {
            token.ThrowIfCancellationRequested();
            var instance = instances[id];
            var definition = definitions[instance.DefinitionId];
            var identity = GeneratedSheet(id);
            var parentPath = paths[instance.ParentId!.Value];
            string parentKey = Key(parentPath.Select(p => p.ToString("D")));
            var parent = screens[parentKey];
            if (!usedIds.Add(identity.ScreenId) || !usedIds.Add(identity.SheetSymbolId))
                throw new AutomationException("rebuild_identity_conflict", "A generated sheet identity is already used in KiCad.");
            string stem = GeneratedFileStem(definition.Name), file = stem + ".kicad_sch";
            for (int n = 2; files.Contains(file); ++n) file = $"{stem}_{n}.kicad_sch";
            files.Add(file);
            string pageNumber = (++page).ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Below the sheets the parent already had (none for a sheet generated in this plan).
            var existing = baseline.Schematic.Instances.SingleOrDefault(s => Key(s.Metadata.Document.SheetPath.Path.Select(p => p.Value)) == parentKey);
            var position = NextSheetPosition(existing, placedOn.GetValueOrDefault(parentKey));
            placedOn[parentKey] = placedOn.GetValueOrDefault(parentKey) + 1;
            var symbol = GeneratedSheetSymbol(identity, definition.Name, file, pageNumber, position, parent.Metadata.Document,
                schematic.Document.Project?.Name ?? "");
            parent.Items.Add(Any.Pack(symbol));
            var path = parentPath.Append(identity.SheetSymbolId).ToArray();
            var document = parent.Metadata.Document.Clone();
            document.SheetPath.Path.Add(new KIID { Value = identity.SheetSymbolId.ToString("D") });
            var metadata = rootScreen.Metadata.Clone();
            metadata.Document = document;
            metadata.ScreenId = new KIID { Value = identity.ScreenId.ToString("D") };
            metadata.Page = rootScreen.Metadata.Page?.Clone();
            metadata.TitleBlock = new TitleBlockInfo();
            metadata.RootInstance = new SchematicRootInstance();
            metadata.LoadedNativeFormatVersion = 0;
            var screen = new SchematicScreenData { Metadata = metadata };
            schematic.Instances.Add(screen);
            screens.Add(Key(path.Select(p => p.ToString("D"))), screen);
            paths.Add(id, path);
            bindings.Add(new(id, path));
        }
        var design = desired with { Schematic = schematic, SheetBindings = bindings };
        var operations = SchematicHierarchyDelta.Plan(state.Observed, DeltaTarget(schematic, state.Observed), token);
        return new(design, operations);
    }

    private static PlannedRebuild PlanRebuild(DesignRecoveryState state, SchematicDesign desired, CancellationToken token)
    {
        var observed = state.Observed;
        var observedRoot = observed.Instances[0];
        var schematic = desired.Schematic.Clone();
        var root = schematic.Instances.Single(s => s.Metadata.Document.Equals(schematic.Document));
        // KiCad reports the same schematic-wide coverage for every screen, and none was loaded from a file.
        foreach (var screen in schematic.Instances)
        {
            screen.Metadata.LoadedNativeFormatVersion = 0;
            screen.Metadata.UnrepresentedState.Clear();
            screen.Metadata.UnrepresentedState.Add(observedRoot.Metadata.UnrepresentedState);
        }
        var operations = new List<SchematicItemOperation>();
        var current = observed.Clone();
        var currentRoot = current.Instances[0];
        if (!Equals(currentRoot.Metadata.ScreenId, root.Metadata.ScreenId))
        {
            operations.Add(new SchematicItemOperation { TargetDocument = observed.Document.Clone(),
                RebuildScreenIdentity = root.Metadata.ScreenId.Clone() });
            currentRoot.Metadata.ScreenId = root.Metadata.ScreenId.Clone();
        }
        // Schematic-wide files and fonts first: every new screen must agree with them.
        if (!Equals(currentRoot.Metadata.EmbeddedFiles, root.Metadata.EmbeddedFiles) || currentRoot.Metadata.EmbeddedFonts != root.Metadata.EmbeddedFonts)
        {
            operations.Add(new SchematicItemOperation { TargetDocument = observed.Document.Clone(),
                ReplaceEmbeddedFiles = new SchematicEmbeddedFileState { Files = root.Metadata.EmbeddedFiles?.Clone() ?? new EmbeddedFiles(),
                    EmbeddedFonts = root.Metadata.EmbeddedFonts } });
            currentRoot.Metadata.EmbeddedFiles = root.Metadata.EmbeddedFiles?.Clone();
            currentRoot.Metadata.EmbeddedFonts = root.Metadata.EmbeddedFonts;
        }
        operations.AddRange(SchematicHierarchyDelta.Plan(current, DeltaTarget(schematic, current), token));
        return new(desired with { Schematic = schematic }, operations);
    }

    // The delta planner creates a screen only from explicit contents with no unrepresented state; KiCad
    // then reports its schematic-wide coverage for the new screen like every other one.
    private static SchematicHierarchyData DeltaTarget(SchematicHierarchyData planned, SchematicHierarchyData current)
    {
        static string Key(SchematicScreenData screen) => string.Join('/', screen.Metadata.Document.SheetPath.Path.Select(id => id.Value));
        var existing = current.Instances.Select(Key).ToHashSet(StringComparer.Ordinal);
        var target = planned.Clone();
        foreach (var screen in target.Instances.Where(s => !existing.Contains(Key(s))))
            screen.Metadata.UnrepresentedState.Clear();
        return target;
    }

    private static Vector2 NextSheetPosition(SchematicScreenData? parent, int ordinal)
    {
        // Below every sheet symbol already on the parent, in rows of five on a 50 mil grid.
        long top = SheetInset;
        foreach (var sheet in (parent?.Items.AsEnumerable() ?? []).Where(i => i.Is(SheetSymbol.Descriptor)).Select(i => i.Unpack<SheetSymbol>()))
            if (sheet.Position is not null && sheet.Size is not null)
                top = Math.Max(top, sheet.Position.YNm + sheet.Size.YNm + SheetInset);
        top = (top + Grid - 1) / Grid * Grid;
        return new Vector2 { XNm = SheetInset + ordinal % SheetsPerRow * SheetPitch, YNm = top + ordinal / SheetsPerRow * RowPitch };
    }

    private static SheetSymbol GeneratedSheetSymbol(SchematicGeneratedSheetIdentity identity, string name, string file, string page,
        Vector2 position, DocumentSpecifier parent, string project)
    {
        SchematicField Field(string fieldName, string text, long y, VerticalAlignment vertical) => new()
        {
            Name = fieldName, Visible = true, AllowAutoPlace = true,
            Text = new Text
            {
                Text_ = text, Position = new Vector2 { XNm = position.XNm, YNm = y },
                Attributes = new TextAttributes
                {
                    HorizontalAlignment = HorizontalAlignment.HaLeft, VerticalAlignment = vertical, Angle = new Angle(),
                    LineSpacing = 1, StrokeWidth = new Distance(), Visible = true, Multiline = true,
                    Size = new Vector2 { XNm = Grid, YNm = Grid }
                }
            }
        };
        var record = new SheetPlacementRecord { ProjectName = project, PageNumber = page, Variants = new SheetVariants() };
        record.Path.Add(parent.SheetPath.Path.Select(id => id.Clone()));
        var symbol = new SheetSymbol
        {
            Id = new KIID { Value = identity.SheetSymbolId.ToString("D") },
            Position = position.Clone(), Size = new Vector2 { XNm = SheetWidth, YNm = SheetHeight },
            BorderStroke = new StrokeAttributes { Width = new Distance(), Style = StrokeLineStyle.SlsSolid },
            Fill = new GraphicFillAttributes { FillType = GraphicFillType.GftUnfilled },
            Locked = LockedState.LsUnlocked,
            NameField = Field("Sheetname", name, position.YNm, VerticalAlignment.VaBottom),
            FilenameField = Field("Sheetfile", file, position.YNm + SheetHeight, VerticalAlignment.VaTop),
            Path = parent.SheetPath.Clone(), PageNumber = page, Variants = new SheetVariants(),
            InstanceRecords = new SheetPlacementRecords(),
            ChildScreenId = new KIID { Value = identity.ScreenId.ToString("D") }
        };
        symbol.InstanceRecords.Records.Add(record);
        return symbol;
    }

    private static Guid Stable(string kind, Guid id)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("kicad-generated-sheet-v1\n" + kind + "\n" + id.ToString("D")));
        digest[6] = (byte)((digest[6] & 0x0f) | 0x50); digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }
}

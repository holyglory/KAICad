using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public sealed record SchematicHistoricalOwner(Guid SymbolOccurrenceId, Guid ComponentId, string Reference,
    int Unit, Guid SheetInstanceId, Guid NativeObjectId);
public sealed record SchematicOwnershipChoice(Guid HistoryOperationId, string HistoryXmlSha256,
    IReadOnlyList<SchematicHistoricalOwner> RestoredSymbols, IReadOnlyList<Guid> RestoredComponentIds);
public sealed record SchematicOwnershipChoices(string SnapshotToken, IReadOnlyList<SchematicOwnershipChoice> Choices,
    DesignOwnershipResolution? Selection);

/// <summary>Inspect and save an explicit history choice, never edit a design.
/// The executor independently reloads verified history before using the choice.</summary>
public static class SchematicOwnershipResolutionService
{
    public static async Task<SchematicOwnershipChoices> InspectAsync(DesignRecoveryStore store,
        StoredDesignRecovery saved, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); RequireIdle(saved);
        var history = await SchematicOwnershipHistoryReader.ReadAsync(store, saved.State, token);
        var inspection = SchematicNativeRestorationProjection.Inspect(saved.State, history, token);
        var choices = inspection.Candidates.Select(candidate =>
        {
            var design = candidate.BindingCandidate; var circuit = design.Engineering.Circuit;
            var components = circuit.Components.ToDictionary(c => c.Id);
            var symbols = circuit.Symbols.ToDictionary(s => s.Id);
            var bindings = design.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId);
            var owners = candidate.RestoredOccurrences.Select(id =>
            {
                var symbol = symbols[id]; var component = components[symbol.ComponentId];
                return new SchematicHistoricalOwner(id, component.Id, component.Reference, symbol.Unit,
                    symbol.EffectiveSheetInstanceId(component), bindings[id].NativeObjectId);
            }).ToArray();
            return new SchematicOwnershipChoice(candidate.History.Receipt.OperationId, candidate.History.Receipt.PreviousXmlSha256!,
                owners, candidate.RestoredComponents);
        }).ToArray();
        token.ThrowIfCancellationRequested(); RequireCurrent(store, saved);
        return new(inspection.SnapshotToken, choices, saved.State.OwnershipResolution);
    }

    public static async Task<StoredDesignRecovery> ResolveAsync(DesignRecoveryStore store, StoredDesignRecovery saved,
        string expectedSnapshotToken, Guid historyOperationId, CancellationToken token = default)
    {
        var inspection = await InspectAsync(store, saved, token);
        if (inspection.SnapshotToken != expectedSnapshotToken)
            throw Error("native_owner_resolution_stale", "History or design inputs changed; inspect the current choices before selecting.");
        var choice = inspection.Choices.SingleOrDefault(c => c.HistoryOperationId == historyOperationId)
            ?? throw Error("invalid_history_choice", "Choose one history operation from this exact inspection.");
        token.ThrowIfCancellationRequested();
        return store.Save(saved.State with { OwnershipResolution = new(inspection.SnapshotToken,
            choice.HistoryOperationId, choice.HistoryXmlSha256) }, saved.RevisionToken);
    }

    public static StoredDesignRecovery Clear(DesignRecoveryStore store, StoredDesignRecovery saved, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); RequireIdle(saved); RequireCurrent(store, saved);
        return store.Save(saved.State with { OwnershipResolution = null }, saved.RevisionToken);
    }

    private static void RequireIdle(StoredDesignRecovery saved)
    {
        if (saved.State.HasPendingWork)
            throw Error("ownership_resolution_pending", "Finish or recover the pending synchronization before changing ownership choices.");
    }

    private static void RequireCurrent(DesignRecoveryStore store, StoredDesignRecovery saved)
    {
        if (store.Read()?.RevisionToken != saved.RevisionToken)
            throw Error("design_recovery_changed", "Recovery changed while inspecting history; reload it before choosing.");
    }

    private static AutomationException Error(string code, string message) => new(code, message);
}

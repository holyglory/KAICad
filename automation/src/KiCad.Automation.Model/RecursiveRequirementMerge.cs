using System.Collections.Immutable;

namespace KiCad.Automation.Model;

public sealed record RecursiveRequirementMergeResult(BlockSelection ExpectedRoot,
    ImmutableArray<BlockSelection> BlockPath, RecursiveBlockDraft? Candidate,
    ImmutableArray<DiagramRequirementConflict> Conflicts)
{
    public bool CanSave => Candidate is not null && Conflicts.IsEmpty;
}

/// <summary>Rebase a requirement-only draft onto the currently selected block. Structural
/// changes need their own comparison; no block, implementation or endpoint is guessed.</summary>
public sealed class RecursiveRequirementMerge
{
    public BlockSelection ExpectedRoot { get; }
    public ImmutableArray<BlockSelection> BlockPath { get; }
    public RecursiveBlockDraft OriginalDraft { get; }
    public RecursiveBlockDraft SavedDraft { get; }
    public DiagramRequirementMerge Requirements { get; }
    private readonly DiagramRequirementHistory _history;

    private RecursiveRequirementMerge(RecursiveBlockGraph latest, RecursiveBlockDraft draft,
        ImmutableArray<BlockSelection> path, DiagramRequirementHistory history)
    {
        ExpectedRoot = latest.SelectedRoot; BlockPath = path; OriginalDraft = draft;
        SavedDraft = latest.StartDraft(path[^1]); _history = history;
        Requirements = history.PrepareMerge(draft.Requirements);
    }

    public static RecursiveRequirementMerge Prepare(RecursiveBlockGraph latest, RecursiveBlockDraft draft)
    {
        if (latest is null || draft?.Requirements is null || draft.Baseline is null)
            throw Invalid("invalid_requirement_rebase", "Provide the latest saved graph and the retained editing draft.");
        var baseline = latest.Inspect(draft.Baseline);
        if (draft.Requirements.Baseline != latest.Requirements(draft.Baseline))
            throw Invalid("changed_draft_baseline", "The draft no longer identifies its exact saved requirement baseline.");
        if (draft.Name != baseline.Name || !draft.Children.SequenceEqual(baseline.Children)
            || !draft.LocalDiagram.SameContents(baseline.LocalDiagram) || draft.RestoredFrom is not null)
            throw Invalid("structural_draft_requires_comparison", "Keep this draft: its diagram structure or whole-version restoration needs a separate comparison.");
        var pending = new Stack<ImmutableArray<BlockSelection>>(); pending.Push([latest.SelectedRoot]);
        while (pending.TryPop(out var path))
        {
            var selected = latest.Inspect(path[^1]);
            if (selected.Selection.BlockId == draft.Baseline.BlockId)
            {
                if (selected.Selection.StateId != draft.Baseline.StateId)
                    throw Invalid("selected_implementation_changed", "Another implementation is selected for this block; the retained draft was not applied to it.");
                var state = latest.States.Single(s => s.Id == selected.Selection.StateId);
                if (state.HeadRevisionId != selected.Selection.RevisionId)
                    throw Invalid("unselected_block_candidate", "This implementation has a newer unselected saved revision; compare it before rebasing the draft.");
                var history = latest.RequirementHistories.Single(h => h.Scope.DesignStateId == state.Id);
                if (history.Current.Id != selected.RequirementRevisionId)
                    throw Invalid("unselected_requirement_candidate", "This implementation has newer unselected requirement text; retain it for comparison.");
                return new(latest, draft, path, history);
            }
            foreach (var child in selected.Children) pending.Push(path.Add(child));
        }
        throw Invalid("draft_block_no_longer_selected", "This block is no longer in the selected design; retain its draft rather than matching another block by name.");
    }

    public RecursiveRequirementMergeResult Inspect(IEnumerable<DiagramRequirementResolution>? resolutions = null)
    {
        var result = Requirements.Inspect(resolutions);
        if (result.Candidate is null) return new(ExpectedRoot, BlockPath, null, result.Conflicts);
        // A resolution may choose saved text instead of a restored field. Do not
        // attach false restoration provenance to the resulting candidate.
        var restored = OriginalDraft.Requirements.RestoredFields.Where(r =>
            _history.Inspect(r.Value).Requirements.Get(r.Key) == result.Candidate.Get(r.Key)).ToImmutableDictionary();
        var candidate = SavedDraft with { Requirements = SavedDraft.Requirements with
            { Requirements = result.Candidate, RestoredFields = restored } };
        return new(ExpectedRoot, BlockPath, candidate, []);
    }

    private static AutomationException Invalid(string code, string message) => new(code, message);
}

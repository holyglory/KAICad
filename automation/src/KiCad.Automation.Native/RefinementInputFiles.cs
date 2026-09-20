using System.Collections.Immutable;
using System.Text;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public sealed record RefinementInputFileResult(RecursiveBlockFileSnapshot Snapshot, DiagramRefinementInput Input, bool Added);

/// <summary>Original-input archive insertion is separate from agent execution and
/// design selection. The existing publisher preserves displaced files on conflicts;
/// observing an input already present does not resolve an earlier publication conflict.</summary>
public static class RefinementInputFiles
{
    public static async Task<RefinementInputFileResult> RecordAsync(string repositoryRoot, string path,
        Guid documentId, string expectedSourceToken, DiagramRefinementInput input, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(input); input.Validate(); token.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(expectedSourceToken))
            throw new AutomationException("missing_refinement_source_token", "Supply the observed diagram file hash, including on retries.");
        var loaded = await RecursiveBlockFiles.Load(repositoryRoot, path, documentId, token);
        var graph = loaded.Snapshot.Graph;
        input.ValidateAgainst(graph);
        if (graph.RefinementInputs.SingleOrDefault(i => i.Id == input.Id) is { } existing)
        {
            if (!existing.SameContents(input)) throw new AutomationException("refinement_input_conflict",
                "This identity already records different original input; neither version was replaced.");
            // This is a current-state observation, not a success receipt for any
            // previously ambiguous publication or later native design operation.
            return new(loaded.Snapshot, existing, false);
        }
        if (string.IsNullOrEmpty(expectedSourceToken) || loaded.Snapshot.ContentSha256 != expectedSourceToken
            || input.SourceSha256 != expectedSourceToken || input.BlockPath[0] != graph.SelectedRoot)
            throw new AutomationException("refinement_context_changed", "The captured context is not the observed selected diagram; retain the input and inspect the newer state.");
        foreach (var attachment in input.Attachments)
            await RefinementAssetFiles.RequireAvailable(repositoryRoot, attachment, token);
        var updated = graph.WithRefinementInput(input);
        byte[] bytes = Encoding.UTF8.GetBytes(RecursiveBlockGraphXml.Write(updated));
        string hash = await DesignFilePublisher.WriteIfUnchangedAsync(loaded.Snapshot.Path, loaded.Bytes, bytes, token);
        return new(new(loaded.Snapshot.Path, hash, updated), input, true);
    }

    public static RequirementRevisionOrigin AttachOrigin(RecursiveBlockGraph graph, ImmutableArray<BlockSelection> targetPath,
        Guid? inputId, RequirementRevisionOrigin origin)
    {
        if (inputId is null) return origin;
        var input = graph.RefinementInput(inputId.Value);
        if (targetPath.IsDefaultOrEmpty || input.BlockPath.Length > targetPath.Length
            || !input.BlockPath.Select(p => p.BlockId).SequenceEqual(targetPath.Take(input.BlockPath.Length).Select(p => p.BlockId)))
            throw new AutomationException("wrong_refinement_input_scope", "Use the original input for its exact block or descendants, not an unrelated diagram scope.");
        var result = origin with { InputIds = origin.InputIds.Add(input.Id).Distinct().ToImmutableArray(),
            Sources = origin.Sources.Concat(input.Origin.Sources).Concat(input.Attachments.Where(a => a.Source is not null)
                .Select(a => a.Source!)).Distinct().ToImmutableArray() };
        result.Validate(); return result;
    }
}

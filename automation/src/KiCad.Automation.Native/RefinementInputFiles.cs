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
        Guid documentId, string expectedSourceToken, DiagramRefinementInput input, CancellationToken token = default,
        string? stateDirectory = null) => await RecordCoreAsync(repositoryRoot, path, documentId, expectedSourceToken, input, token, stateDirectory, null);

    internal static async Task<RefinementInputFileResult> RecordCoreAsync(string repositoryRoot, string path,
        Guid documentId, string expectedSourceToken, DiagramRefinementInput input, CancellationToken token,
        string? stateDirectory, Func<string, CancellationToken, Task>? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(input); input.Validate(); token.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(expectedSourceToken))
            throw new AutomationException("missing_refinement_source_token", "Supply the observed diagram file hash, including on retries.");
        var receipts = stateDirectory is null ? null : new RefinementInputReceipts(stateDirectory);
        using var operation = receipts?.Acquire(input.Id);
        var prior = receipts?.Read(input.Id);
        if (prior is not null)
        {
            if (prior.DesignPath != Path.GetFullPath(path) || prior.InputXml != DiagramRefinementInputXml.Write(input))
                throw new AutomationException("refinement_receipt_conflict", "The recorded input identity belongs to a different request or design file.");
            if (prior.Stage == RefinementPublicationStage.Replacing)
                throw new AutomationException("refinement_publication_recovery_required", "Inspect and recover the recorded replacement before submitting this input again.");
        }
        var loaded = await RecursiveBlockFiles.Load(repositoryRoot, path, documentId, token);
        var graph = loaded.Snapshot.Graph;
        input.ValidateAgainst(graph);
        if (graph.RefinementInputs.SingleOrDefault(i => i.Id == input.Id) is { } existing)
        {
            if (!existing.SameContents(input)) throw new AutomationException("refinement_input_conflict",
                "This identity already records different original input; neither version was replaced.");
            if (prior is { Stage: not RefinementPublicationStage.Published } pending)
                throw new AutomationException("refinement_publication_recovery_required",
                    $"The current XML contains this input, but its publication receipt is {pending.Stage}; inspect the retained preimage/postimage before retrying.");
            // This is a current-state observation, not a success receipt for any
            // previously ambiguous publication or later native design operation.
            return new(loaded.Snapshot, existing, false);
        }
        if (prior is { Stage: RefinementPublicationStage.Published })
            throw new AutomationException("refinement_publication_recovery_required", "This input was published previously but is absent now; preserve the newer diagram and inspect its history.");
        if (string.IsNullOrEmpty(expectedSourceToken) || loaded.Snapshot.ContentSha256 != expectedSourceToken
            || input.SourceSha256 != expectedSourceToken || input.BlockPath[0] != graph.SelectedRoot)
            throw new AutomationException("refinement_context_changed", "The captured context is not the observed selected diagram; retain the input and inspect the newer state.");
        foreach (var attachment in input.Attachments)
            await RefinementAssetFiles.RequireAvailable(repositoryRoot, attachment, token);
        var updated = graph.WithRefinementInput(input);
        var (bytes, version) = RecursiveBlockFiles.Serialize(updated);
        var prepared = new RefinementInputPublicationReceipt(1, input.Id, loaded.Snapshot.Path, DiagramRefinementInputXml.Write(input),
            loaded.Snapshot.ContentSha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
            RefinementPublicationStage.Prepared, null, null);
        receipts?.Write(prepared);
        if (checkpoint is not null) await checkpoint("input-prepared", token);
        string? staged = null;
        string? retained = null;
        string hash = await DesignFilePublisher.WriteCoreAsync(loaded.Snapshot.Path, loaded.Bytes, bytes,
            beforeReplace: null,
            replace: (target, temporary) =>
            {
                staged = temporary; retained = PreservingFileReplacement.PreviousPath(temporary);
                receipts?.Write(prepared with { Stage = RefinementPublicationStage.Replacing, StagedPath = staged, RetainedPath = retained });
                checkpoint?.Invoke("input-replacing", token).GetAwaiter().GetResult();
                PreservingFileReplacement.Replace(target, temporary);
                checkpoint?.Invoke("input-replaced", token).GetAwaiter().GetResult();
            }, token);
        receipts?.Write(prepared with { Stage = RefinementPublicationStage.Published, StagedPath = staged,
            RetainedPath = retained, ConfirmedAt = DateTimeOffset.UtcNow });
        if (checkpoint is not null) await checkpoint("input-published", token);
        return new(RecursiveBlockFiles.Published(loaded.Snapshot, loaded.Snapshot.Path, hash, updated, version), input, true);
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

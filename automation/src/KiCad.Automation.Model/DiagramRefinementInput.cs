using System.Collections.Immutable;

namespace KiCad.Automation.Model;

/// <summary>Reference to preserved original bytes, not an assertion that a mutable
/// user file still contains them. The file layer must verify/copy the declared asset.</summary>
public sealed record DiagramRefinementAttachment(Guid Id, string OriginalName, string AssetPath,
    string ContentSha256, long ByteCount, string MediaType, SourceReference? Source = null)
{
    public void Validate()
    {
        string[] parts = MediaType?.Split('/') ?? [];
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(OriginalName) || ByteCount < 0 || MediaType is null
            || parts.Length != 2 || parts.Any(p => p.Length == 0
                || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '+' and not '.' and not '_')))
            throw DiagramRefinementInput.Invalid("An attachment needs an identity, original name, exact byte count and media type.");
        DiagramEndpointBinding.Text(OriginalName); HardwareRepository.ValidatePath(AssetPath);
        DiagramRefinementInput.Hash(ContentSha256);
        if (Source is { } source)
        {
            if (string.IsNullOrWhiteSpace(source.DocumentId) || string.IsNullOrWhiteSpace(source.Revision) || source.Page is <= 0)
                throw DiagramRefinementInput.Invalid("Retain an attachment's exact source document revision and positive page when specified.");
            DiagramEndpointBinding.Text(source.DocumentId); DiagramEndpointBinding.Text(source.Revision);
            if (source.Table is { } table) DiagramEndpointBinding.Text(table);
            if (source.PartVariant is { } variant) DiagramEndpointBinding.Text(variant);
        }
    }
}

/// <summary>Provider-neutral original request. Exact historical selections recover
/// its diagram/comments without copying all history or following today's mutable heads.</summary>
public sealed record DiagramRefinementInput(Guid Id, Guid DocumentId, string SourceSha256,
    ImmutableArray<BlockSelection> BlockPath, ImmutableArray<ConnectionSelection> ConnectionPath,
    string Prompt, RequirementRevisionOrigin Origin, ImmutableArray<DiagramRefinementAttachment> Attachments)
{
    public void Validate()
    {
        if (Id == Guid.Empty || DocumentId == Guid.Empty || BlockPath.IsDefaultOrEmpty || ConnectionPath.IsDefault
            || Prompt is null || Origin is null || Attachments.IsDefault
            || BlockPath.Any(p => p is null || p.BlockId == Guid.Empty || p.StateId == Guid.Empty || p.RevisionId == Guid.Empty)
            || BlockPath.Select(p => p.BlockId).Distinct().Count() != BlockPath.Length
            || ConnectionPath.Any(p => p is null || p.ConnectionId == Guid.Empty || p.StateId == Guid.Empty || p.RevisionId == Guid.Empty)
            || ConnectionPath.Select(p => p.ConnectionId).Distinct().Count() != ConnectionPath.Length)
            throw Invalid("A refinement input needs an identity, document, exact context path, original prompt and origin.");
        Hash(SourceSha256); DiagramEndpointBinding.Text(Prompt); Origin.Validate();
        if (Origin.InputIds.Contains(Id)) throw Invalid("An original input cannot derive from itself.");
        var ids = new HashSet<Guid> { Id };
        var assets = new Dictionary<string, DiagramRefinementAttachment>(StringComparer.Ordinal);
        foreach (var attachment in Attachments)
        {
            if (attachment is null || !ids.Add(attachment.Id)) throw Invalid("Attachment identities must be distinct from each other and the input.");
            attachment.Validate();
            if (assets.TryGetValue(attachment.AssetPath, out var previous)
                && (previous.ContentSha256 != attachment.ContentSha256 || previous.ByteCount != attachment.ByteCount))
                throw Invalid("One preserved asset path cannot stand for different original bytes.");
            assets[attachment.AssetPath] = attachment;
        }
    }

    public void ValidateAgainst(RecursiveBlockGraph graph)
    {
        Validate();
        if (graph.DocumentId != DocumentId) throw Invalid("The input belongs to a different diagram document.");
        for (int i = 0; i < BlockPath.Length; ++i)
        {
            var block = graph.Inspect(BlockPath[i]);
            if (i + 1 < BlockPath.Length && !block.Children.Contains(BlockPath[i + 1]))
                throw Invalid("The input path must follow exact child revisions pinned by its original diagram.");
        }
        if (ConnectionPath.IsEmpty) return;
        var selected = graph.Inspect(BlockPath[^1]);
        if (!selected.LocalDiagram.Connections.Contains(ConnectionPath[0]))
            throw Invalid("The requested connection does not belong to the original local diagram.");
        var archive = graph.Connections(selected.Selection.BlockId);
        for (int i = 0; i < ConnectionPath.Length; ++i)
        {
            var connection = archive.Inspect(ConnectionPath[i]);
            if (i + 1 < ConnectionPath.Length && !connection.Members.Contains(ConnectionPath[i + 1]))
                throw Invalid("The connection path must follow its exact original member revisions.");
        }
    }

    public bool SameContents(DiagramRefinementInput other) => other is not null && Id == other.Id && DocumentId == other.DocumentId
        && SourceSha256 == other.SourceSha256 && BlockPath.SequenceEqual(other.BlockPath) && ConnectionPath.SequenceEqual(other.ConnectionPath)
        && Prompt == other.Prompt && DiagramRequirementHistory.SameOrigin(Origin, other.Origin) && Attachments.SequenceEqual(other.Attachments);

    internal static void Hash(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw Invalid("Use the exact lowercase SHA256 of the captured source bytes.");
    }
    internal static AutomationException Invalid(string message) => new("invalid_refinement_input", message);
}

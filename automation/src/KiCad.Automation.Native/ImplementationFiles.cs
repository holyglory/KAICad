using System.Text;
using KiCad.Automation.Model;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Native;

public sealed record ImplementationFileResult(RecursiveBlockFileSnapshot Snapshot, Guid StateId);

public static class ImplementationFiles
{
    public static async Task<ImplementationFileResult> ApplyAsync(string root, string path, Guid documentId,
        string expectedToken, P.ManageImplementationData request, CancellationToken token = default)
    {
        if (request is null || request.ExpectedRoot is null || request.Source is null || request.Origin is null
            || expectedToken is null || expectedToken.Length != 64 || !Enum.IsDefined(request.Action) || request.Action == P.ImplementationActionKind.IakUnknown)
            throw Invalid("invalid_implementation_request", "Provide the management action, exact source/root, source token and change origin.");
        var source = RecursiveBlockCodec.DecodeSelection(request.Source); var expectedRoot = RecursiveBlockCodec.DecodeSelection(request.ExpectedRoot);
        var origin = RecursiveBlockCodec.DecodeOrigin(request.Origin);
        origin.Validate();
        var loaded = await RecursiveBlockFiles.Load(root, path, documentId, token);
        if (loaded.Snapshot.ContentSha256 != expectedToken || loaded.Snapshot.Graph.SelectedRoot != expectedRoot)
            throw Invalid("recursive_block_file_changed", "The saved design changed; reload it before managing implementations.");
        var graph = loaded.Snapshot.Graph; _ = graph.Inspect(source);
        Guid stateId = source.StateId;
        bool create = request.Action is P.ImplementationActionKind.IakNew or P.ImplementationActionKind.IakDuplicate;
        if (create ? request.ChangeId.Length != 0 : request.NewStateId.Length != 0 || request.NewRevisionId.Length != 0 || request.NewRequirementRevisionId.Length != 0)
            throw Invalid("ambiguous_implementation_request", "Supply only the identities belonging to the selected management action.");
        if (create)
        {
            stateId = RecursiveBlockCodec.DecodeIdentity(request.NewStateId);
            graph = graph.ForkImplementation(source, stateId, RecursiveBlockCodec.DecodeIdentity(request.NewRevisionId),
                RecursiveBlockCodec.DecodeIdentity(request.NewRequirementRevisionId), request.Name, origin,
                emptyInterior: request.Action == P.ImplementationActionKind.IakNew);
        }
        else if (request.Action == P.ImplementationActionKind.IakRename)
            graph = graph.RenameImplementation(stateId, request.Name, RecursiveBlockCodec.DecodeIdentity(request.ChangeId), origin);
        else
        {
            if (request.Name.Length != 0) throw Invalid("ambiguous_implementation_request", "Removal and restoration retain the implementation name.");
            graph = graph.SetImplementationArchived(stateId, request.Action == P.ImplementationActionKind.IakArchive,
                RecursiveBlockCodec.DecodeIdentity(request.ChangeId), origin);
        }
        token.ThrowIfCancellationRequested();
        if (ReferenceEquals(graph, loaded.Snapshot.Graph)) return new(loaded.Snapshot, stateId);
        byte[] bytes = Encoding.UTF8.GetBytes(RecursiveBlockGraphXml.Write(graph));
        string hash = await DesignFilePublisher.WriteIfUnchangedAsync(loaded.Snapshot.Path, loaded.Bytes, bytes, token);
        return new(new(loaded.Snapshot.Path, hash, graph), stateId);
    }

    private static AutomationException Invalid(string code, string message) => new(code, message);
}

using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

/// <summary>A move of a sheet KiCad shows that exact identities cannot decide (ledger p5f6d5d0ca242d628): the sheet file
/// (<paramref name="ScreenId"/>) is no longer shown at <paramref name="FormerPaths"/> and is now shown at
/// <paramref name="NewPaths"/>, several of either, so which sheet moved where is a guess. <paramref name="FormerSheetInstanceIds"/>
/// are the design's sheet instances KiCad showed at the former paths. Nothing is published until KiCad shows one of
/// them moved at a time: undo in KiCad and move the sheets one by one, synchronizing after each.</summary>
public sealed record SchematicSheetResolutionRequest(string Code, Guid ScreenId, string FileName,
    IReadOnlyList<string> FormerPaths, IReadOnlyList<string> NewPaths, IReadOnlyList<Guid> FormerSheetInstanceIds, string Reason);

internal sealed record SchematicSheetPathMove(string Before, string After);
internal sealed record SchematicSheetInstanceChoices(IReadOnlyList<SchematicSheetPathMove> Moves,
    IReadOnlyList<string> Retired, IReadOnlyList<string> Added);

/// <summary>How the sheets KiCad shows changed since a snapshot, by exact native identity only (ledger p5f6d5d0ca242d628).
/// A sheet is identified by the screen (the file) it shows: a sheet file no longer shown at one path and now shown at exactly
/// one other is that sheet moved, whether or not KiCad kept its sheet symbol's UUID (KiCad's cut and paste gives the pasted
/// sheet symbol a new one). Its descendants move with it. Paths shown only before are removed sheets, paths shown only now
/// are inserted sheets. Several former and new paths of one file are undecidable (<see cref="Requests"/>), and an inserted
/// sheet that shows a file KiCad already shows elsewhere (a repeated sheet) is not supported yet
/// (<see cref="RepeatedUnsupported"/>). Names, positions and pages decide nothing.</summary>
internal sealed record SchematicNativeSheetChanges(IReadOnlyDictionary<string, string> Moved, IReadOnlyList<string> Removed,
    IReadOnlyList<string> Inserted, IReadOnlyList<SchematicSheetResolutionRequest> Requests, string? ErrorCode = null, string? ErrorMessage = null)
{
    public const string MoveAmbiguous = "native_sheet_move_ambiguous";
    public const string MoveAnswerInvalid = "native_sheet_move_answer_invalid";
    public const string RepeatedUnsupported = "native_sheet_repeated_unsupported";
    /// <summary>A sheet shown at the same place with another file: not reflected yet (the earlier refusal code).</summary>
    public const string FileChanged = "sheet_ownership_changed";
    internal const string MoveAmbiguousMessage = "KiCad shows sheets moved in a way exact identities cannot decide: a sheet file is no longer "
        + "shown at several places and now shown at several others. Nothing was published. Use kicad_design_sheet_move_answer "
        + "to map the existing instances and identify retired or newly added paths, then refresh and synchronize.";

    public bool Changed => Moved.Count != 0 || Removed.Count != 0 || Inserted.Count != 0;

    /// <summary>The path at which KiCad now shows the sheet it showed at <paramref name="before"/>; null when it is removed.</summary>
    public string? Now(string before) => Moved.TryGetValue(before, out var after) ? after
        : Removed.Contains(before, StringComparer.Ordinal) ? null : before;

    internal static string Key(SchematicScreenData screen) => string.Join('/', screen.Metadata.Document.SheetPath.Path.Select(p => p.Value));
    internal static string Parent(string path) => path.LastIndexOf('/') is var split and >= 0 ? path[..split] : "";
    internal static string Last(string path) => path[(path.LastIndexOf('/') + 1)..];

    internal static SchematicNativeSheetChanges Compare(SchematicHierarchyData before, SchematicHierarchyData after,
        SchematicSheetInstanceChoices? choices = null)
    {
        var was = before.Instances.ToDictionary(Key, s => s.Metadata.ScreenId.Value, StringComparer.Ordinal);
        var now = after.Instances.ToDictionary(Key, s => s.Metadata.ScreenId.Value, StringComparer.Ordinal);
        var gone = was.Keys.Where(p => !now.ContainsKey(p)).ToArray();
        var fresh = now.Keys.Where(p => !was.ContainsKey(p)).ToArray();
        var moved = new Dictionary<string, string>(StringComparer.Ordinal);
        List<string> removed = [], inserted = [];
        var requests = new List<SchematicSheetResolutionRequest>();
        var explicitMoves = new Dictionary<string, string>(StringComparer.Ordinal);
        var explicitTargets = new HashSet<string>(StringComparer.Ordinal);
        var explicitRetired = new HashSet<string>(StringComparer.Ordinal);
        var explicitAdded = new HashSet<string>(StringComparer.Ordinal);
        static bool Within(string path, string parent) => path == parent || path.StartsWith(parent + "/", StringComparison.Ordinal);
        SchematicNativeSheetChanges InvalidAnswer() => new(new Dictionary<string, string>(), [], [], [], MoveAnswerInvalid,
            "The sheet-instance choices must account for the exact former and new paths without duplicates or changed file identities. Nothing was published.");
        if (choices is not null)
        {
            if (choices.Moves.Select(m => m.Before).Distinct(StringComparer.Ordinal).Count() != choices.Moves.Count
                || choices.Moves.Select(m => m.After).Distinct(StringComparer.Ordinal).Count() != choices.Moves.Count
                || choices.Retired.Distinct(StringComparer.Ordinal).Count() != choices.Retired.Count
                || choices.Added.Distinct(StringComparer.Ordinal).Count() != choices.Added.Count
                || choices.Moves.Any(m => !gone.Contains(m.Before, StringComparer.Ordinal)
                    || !fresh.Contains(m.After, StringComparer.Ordinal) || was[m.Before] != now[m.After])
                || choices.Retired.Any(p => !gone.Contains(p, StringComparer.Ordinal))
                || choices.Added.Any(p => !fresh.Contains(p, StringComparer.Ordinal))) return InvalidAnswer();
            foreach (string path in gone)
            {
                var move = choices.Moves.Where(m => Within(path, m.Before)).OrderByDescending(m => m.Before.Length).FirstOrDefault();
                string? retired = choices.Retired.Where(p => Within(path, p)).OrderByDescending(p => p.Length).FirstOrDefault();
                if (move is not null && retired == move.Before) return InvalidAnswer();
                if (retired is not null && (move is null || retired.Length > move.Before.Length)) explicitRetired.Add(path);
                else if (move is not null)
                {
                    string target = move.After + path[move.Before.Length..];
                    if (!fresh.Contains(target, StringComparer.Ordinal) || was[path] != now[target]
                        || !explicitTargets.Add(target)) return InvalidAnswer();
                    explicitMoves.Add(path, target);
                }
            }
            if (choices.Added.Any(explicitTargets.Contains)) return InvalidAnswer();
            foreach (string path in fresh)
                if (!explicitTargets.Contains(path) && choices.Added.Any(p => Within(path, p))) explicitAdded.Add(path);
        }
        foreach (string screen in gone.Select(p => was[p]).Concat(fresh.Select(p => now[p])).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var from = gone.Where(p => was[p] == screen).Order(StringComparer.Ordinal).ToArray();
            var to = fresh.Where(p => now[p] == screen).Order(StringComparer.Ordinal).ToArray();
            bool answered = from.Any(p => explicitMoves.ContainsKey(p) || explicitRetired.Contains(p))
                || to.Any(p => explicitTargets.Contains(p) || explicitAdded.Contains(p));
            if (answered)
            {
                if (from.Any(p => !explicitMoves.ContainsKey(p) && !explicitRetired.Contains(p))
                    || to.Any(p => !explicitTargets.Contains(p) && !explicitAdded.Contains(p))) return InvalidAnswer();
                foreach (string path in from)
                    if (explicitMoves.TryGetValue(path, out string? target)) moved.Add(path, target);
                    else removed.Add(path);
                inserted.AddRange(to.Where(explicitAdded.Contains));
            }
            else if (from.Length == 0) inserted.AddRange(to);
            else if (to.Length == 0) removed.AddRange(from);
            else if (from.Length == 1 && to.Length == 1) moved.Add(from[0], to[0]);
            else requests.Add(new(MoveAmbiguous, Guid.Parse(screen), FileName(after, to[0]) ?? FileName(before, from[0]) ?? "", from, to, [],
                "This sheet file is no longer shown at several places and is now shown at several others; which sheet moved where is not "
                + "decided by exact identities."));
        }
        inserted = [.. inserted.OrderBy(p => p.Count(c => c == '/')).ThenBy(p => p, StringComparer.Ordinal)];
        var changes = new SchematicNativeSheetChanges(moved, [.. removed.Order(StringComparer.Ordinal)], inserted, requests);
        // A sheet KiCad still shows at the same place but with another file (its sheet symbol now names another file).
        if (was.FirstOrDefault(p => now.TryGetValue(p.Key, out var screen) && screen != p.Value) is { Key: not null } replaced)
            return changes with { ErrorCode = FileChanged, ErrorMessage = $"KiCad shows the sheet at {replaced.Key} with another sheet file "
                + "than before. A sheet whose file changed cannot be reflected in the design yet; nothing was published. Undo the change in "
                + "KiCad, or remove the sheet and insert one with the other file." };
        if (requests.Count != 0) return changes with { ErrorCode = MoveAmbiguous, ErrorMessage = MoveAmbiguousMessage };
        // Repeated insertions are classified here and admitted only by the addition
        // projection, which reuses the exact existing definition and unit ownership.
        return changes;
    }

    /// <summary>The sheet symbol that shows the sheet at <paramref name="path"/>, on its parent sheet.</summary>
    internal static SheetSymbol? SheetSymbolOf(SchematicHierarchyData data, string path)
    {
        string parent = Parent(path), id = Last(path);
        return data.Instances.SingleOrDefault(s => Key(s) == parent)?.Items.Where(i => i.Is(SheetSymbol.Descriptor))
            .Select(i => i.Unpack<SheetSymbol>()).SingleOrDefault(s => s.Id?.Value == id);
    }

    private static string? FileName(SchematicHierarchyData data, string path) => SheetSymbolOf(data, path)?.FilenameField?.Text?.Text_;

    /// <summary>For each design sheet instance bound to a path KiCad showed, the path it is shown at now (null: removed).</summary>
    internal IReadOnlyDictionary<Guid, string?> Rebind(IReadOnlyList<SchematicSheetBinding> bindings) =>
        bindings.ToDictionary(b => b.SheetInstanceId, b => Now(SchematicDesignBindings.PathKey(b.NativePath)));
}

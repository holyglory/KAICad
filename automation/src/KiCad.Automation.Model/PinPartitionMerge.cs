namespace KiCad.Automation.Model;

public sealed record PinPartitionConflict(string Reason, IReadOnlyList<PinEndpoint> Pins);
public sealed record PinPartitionMergeResult(IReadOnlyList<IReadOnlyList<PinEndpoint>>? Groups,
    IReadOnlyList<PinPartitionConflict> Conflicts);

/// <summary>Three-way equivalence-relation merge. The small pairwise definition
/// is tested independently; production uses group intersections and union-find.</summary>
public static class PinPartitionMerge
{
    public static PinPartitionMergeResult Plan(IReadOnlyList<IReadOnlyList<PinEndpoint>> baseline,
        IReadOnlyList<IReadOnlyList<PinEndpoint>> desired, IReadOnlyList<IReadOnlyList<PinEndpoint>> native,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var bMap = Index(baseline); var dMap = Index(desired); var nMap = Index(native);
        if (!bMap.Keys.ToHashSet().SetEquals(dMap.Keys) || !bMap.Keys.ToHashSet().SetEquals(nMap.Keys))
            throw Invalid("Every partition must contain exactly the same explicitly identified pins.");
        var pins = bMap.Keys.OrderBy(p => p.ComponentId).ThenBy(p => p.Pin, StringComparer.Ordinal).ToArray();
        var b = pins.Select(p => bMap[p]).ToArray(); var d = pins.Select(p => dMap[p]).ToArray(); var n = pins.Select(p => nMap[p]).ToArray();
        var parent = Enumerable.Range(0, pins.Length).ToArray();
        int Root(int index)
        {
            while (parent[index] != index) { parent[index] = parent[parent[index]]; index = parent[index]; }
            return index;
        }
        void Join(IEnumerable<int> group)
        {
            int first = -1;
            foreach (int index in group)
            {
                token.ThrowIfCancellationRequested(); int root = Root(index);
                if (first < 0) first = root;
                else { int left = Root(first); parent[Math.Max(left, root)] = Math.Min(left, root); first = Math.Min(left, root); }
            }
        }
        var indexes = Enumerable.Range(0, pins.Length).ToArray();
        // Inside an old net, either side's split must be respected.
        foreach (var cell in indexes.GroupBy(i => (b[i], d[i], n[i]))) Join(cell);
        // Across old nets, a one-sided join is an explicit changed relation.
        foreach (var labels in new[] { d, n })
        foreach (var group in indexes.GroupBy(i => labels[i]))
            if (group.Select(i => b[i]).Distinct().Skip(1).Any()) Join(group);

        var conflicts = new List<PinPartitionConflict>();
        var groups = new List<IReadOnlyList<PinEndpoint>>();
        foreach (var component in indexes.GroupBy(Root))
        {
            token.ThrowIfCancellationRequested(); var members = component.ToArray();
            var values = members.Select(i => pins[i]).ToArray();
            bool contradictsSplit = members.GroupBy(i => b[i])
                .Any(old => old.Select(i => (d[i], n[i])).Distinct().Skip(1).Any());
            bool contradictsSeparation = members.Select(i => d[i]).Distinct().Skip(1).Any()
                && members.Select(i => n[i]).Distinct().Skip(1).Any();
            if (contradictsSplit || contradictsSeparation)
                conflicts.Add(new(contradictsSplit ? "split_join_conflict" : "inconsistent_transitive_join", values));
            groups.Add(values);
        }
        return new(conflicts.Count == 0 ? groups : null, conflicts);

        Dictionary<PinEndpoint, int> Index(IReadOnlyList<IReadOnlyList<PinEndpoint>> source)
        {
            if (source is null) throw Invalid("Partitions are required.");
            var result = new Dictionary<PinEndpoint, int>();
            for (int group = 0; group < source.Count; ++group)
            {
                token.ThrowIfCancellationRequested();
                if (source[group] is not { Count: > 0 }) throw Invalid("A partition cannot contain an empty group.");
                foreach (var pin in source[group])
                    if (pin is null || pin.ComponentId == Guid.Empty || string.IsNullOrWhiteSpace(pin.Pin) || !result.TryAdd(pin, group))
                        throw Invalid("Partition pins must have exact identities and belong to exactly one group.");
            }
            return result;
        }
    }

    private static AutomationException Invalid(string message) => new("invalid_pin_partition", message);
}

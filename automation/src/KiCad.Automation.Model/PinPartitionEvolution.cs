namespace KiCad.Automation.Model;

public sealed record PinPartitionEvolutionResult(IReadOnlyList<IReadOnlyList<PinEndpoint>>? Groups,
    IReadOnlyList<PinPartitionConflict> Conflicts, IReadOnlyList<PinEndpoint> AddedPins,
    IReadOnlyList<PinEndpoint> RemovedPins);

/// <summary>Merge connections after exact pin ownership has been established.
/// Absence is not an assertion of disconnection. This does not discover owners,
/// match replacement pins, or transfer requirements to new identities.</summary>
public static class PinPartitionEvolution
{
    public static PinPartitionEvolutionResult Plan(IReadOnlyList<IReadOnlyList<PinEndpoint>> baseline,
        IReadOnlyList<IReadOnlyList<PinEndpoint>> desired, IReadOnlyList<IReadOnlyList<PinEndpoint>> native,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var b = Index(baseline); var d = Index(desired); var n = Index(native);
        var pins = Ordered(d.Keys.Concat(n.Keys).Distinct()
            .Where(p => !b.ContainsKey(p) || (d.ContainsKey(p) && n.ContainsKey(p))));
        var retained = pins.ToHashSet();
        var added = pins.Where(p => !b.ContainsKey(p)).ToArray();
        var removed = Ordered(b.Keys.Where(p => !retained.Contains(p)));
        var conflicts = new List<PinPartitionConflict>();

        CheckDeletions(d, desired, n);
        CheckDeletions(n, native, d);
        var old = retained.Where(b.ContainsKey).ToHashSet();
        IReadOnlyList<IReadOnlyList<PinEndpoint>> Project(IReadOnlyList<IReadOnlyList<PinEndpoint>> source) =>
            source.Select(group => (IReadOnlyList<PinEndpoint>)group.Where(old.Contains).ToArray())
                .Where(group => group.Count > 0).ToArray();
        var oldMerge = PinPartitionMerge.Plan(Project(baseline), Project(desired), Project(native), token);
        conflicts.AddRange(oldMerge.Conflicts);
        if (conflicts.Count > 0) return Result(null);

        var index = pins.Select((pin, i) => (pin, i)).ToDictionary(x => x.pin, x => x.i);
        var parent = Enumerable.Range(0, pins.Length).ToArray();
        var oldLabels = new Dictionary<PinEndpoint, int>();
        int Root(int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }
        void Join(IEnumerable<PinEndpoint> group)
        {
            int first = -1;
            foreach (var pin in group)
            {
                token.ThrowIfCancellationRequested();
                if (!index.TryGetValue(pin, out int i)) continue;
                int root = Root(i);
                if (first < 0) first = root;
                else
                {
                    int left = Root(first); parent[Math.Max(left, root)] = Math.Min(left, root);
                    first = Math.Min(left, root);
                }
            }
        }
        for (int label = 0; label < oldMerge.Groups!.Count; ++label)
        {
            var group = oldMerge.Groups[label]; Join(group);
            foreach (var pin in group) oldLabels.Add(pin, label);
        }
        // Connections involving a new pin are asserted only by a side on
        // which that pin exists. Old-only relations were merged above.
        foreach (var source in new[] { desired, native })
        foreach (var group in source)
        {
            token.ThrowIfCancellationRequested();
            if (group.Any(p => !b.ContainsKey(p))) Join(group);
        }

        var groups = new List<IReadOnlyList<PinEndpoint>>();
        foreach (var component in Enumerable.Range(0, pins.Length).GroupBy(Root))
        {
            token.ThrowIfCancellationRequested();
            var members = component.Select(i => pins[i]).ToArray();
            if (members.Where(oldLabels.ContainsKey).Select(p => oldLabels[p]).Distinct().Skip(1).Any())
                conflicts.Add(new("inconsistent_existing_pin_join", members));
            foreach (var side in new[] { d, n })
            {
                var present = members.Where(side.ContainsKey).ToArray();
                if (present.Any(p => !b.ContainsKey(p)) && present.Select(p => side[p]).Distinct().Skip(1).Any())
                {
                    conflicts.Add(new("new_pin_connection_conflict", members));
                    break;
                }
            }
            groups.Add(members);
        }
        return Result(conflicts.Count == 0 ? groups : null);

        PinPartitionEvolutionResult Result(IReadOnlyList<IReadOnlyList<PinEndpoint>>? result) =>
            new(result, conflicts.OrderBy(c => c.Reason, StringComparer.Ordinal)
                .ThenBy(c => c.Pins[0].ComponentId).ThenBy(c => c.Pins[0].Pin, StringComparer.Ordinal).ToArray(), added, removed);

        void CheckDeletions(Dictionary<PinEndpoint, int> side,
            IReadOnlyList<IReadOnlyList<PinEndpoint>> source, Dictionary<PinEndpoint, int> other)
        {
            // Compare whole indexed groups once, not every pair of pins.
            // Deleting another old pin alone is not a connectivity edit.
            var expectedCounts = side.Keys.Where(b.ContainsKey).GroupBy(p => b[p])
                .ToDictionary(group => group.Key, group => group.Count());
            var originalLabels = source.Select(group =>
            {
                token.ThrowIfCancellationRequested();
                int label = b.GetValueOrDefault(group[0], -1);
                return label >= 0 && group.All(p => b.GetValueOrDefault(p, -1) == label) ? label : -1;
            }).ToArray();
            var disputed = new List<PinEndpoint>();
            foreach (var pin in removed.Where(p => side.ContainsKey(p) && !other.ContainsKey(p)))
            {
                token.ThrowIfCancellationRequested();
                int label = b[pin]; int current = side[pin];
                if (originalLabels[current] != label || source[current].Count != expectedCounts[label])
                    disputed.Add(pin);
            }
            if (disputed.Count > 0) conflicts.Add(new("pin_removed_while_rewired", disputed));
        }

        Dictionary<PinEndpoint, int> Index(IReadOnlyList<IReadOnlyList<PinEndpoint>> source)
        {
            if (source is null) throw Invalid("Partitions are required.");
            var result = new Dictionary<PinEndpoint, int>();
            for (int group = 0; group < source.Count; ++group)
            {
                token.ThrowIfCancellationRequested();
                if (source[group] is not { Count: > 0 }) throw Invalid("A partition cannot contain an empty group.");
                foreach (var pin in source[group])
                    if (pin is null || pin.ComponentId == Guid.Empty || string.IsNullOrWhiteSpace(pin.Pin)
                        || !result.TryAdd(pin, group))
                        throw Invalid("Partition pins must have exact identities and belong to exactly one group.");
            }
            return result;
        }
    }

    private static PinEndpoint[] Ordered(IEnumerable<PinEndpoint> pins) =>
        pins.OrderBy(p => p.ComponentId).ThenBy(p => p.Pin, StringComparer.Ordinal).ToArray();

    private static AutomationException Invalid(string message) => new("invalid_pin_partition", message);
}

using KiCad.Automation.Model;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PinPartitionEvolutionTests
{
    private static readonly Guid Component = Guid.Parse("8fc15ca1-c0f9-474f-820d-461fa45f1348");
    private static PinEndpoint Pin(int i) => new(Component, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static IReadOnlyList<IReadOnlyList<PinEndpoint>> Groups(params int[] labels) =>
        Enumerable.Range(0, labels.Length).Where(i => labels[i] >= 0).GroupBy(i => labels[i])
            .Select(g => (IReadOnlyList<PinEndpoint>)g.Select(Pin).ToArray()).ToArray();

    [TestMethod]
    public void IndependentAdditionsJoinThroughAnExistingNetWithoutInventingSeparation()
    {
        var result = PinPartitionEvolution.Plan(Groups(0, 0, -1, -1), Groups(0, 0, 0, -1), Groups(0, 0, -1, 0));
        Assert.IsEmpty(result.Conflicts); Assert.HasCount(1, result.Groups!);
        CollectionAssert.AreEqual(new[] { Pin(0), Pin(1), Pin(2), Pin(3) }, result.Groups![0].ToArray());
        CollectionAssert.AreEqual(new[] { Pin(2), Pin(3) }, result.AddedPins.ToArray());
        Assert.IsEmpty(result.RemovedPins);
    }

    [TestMethod]
    public void IndependentDeletionAndUnrelatedAdditionPreserveTheOtherChange()
    {
        var result = PinPartitionEvolution.Plan(Groups(0, 0, 1, -1), Groups(-1, 0, 1, -1), Groups(0, 0, 1, 1));
        Assert.IsEmpty(result.Conflicts);
        Assert.AreEqual("1;2,3", Key(result.Groups!));
        CollectionAssert.AreEqual(new[] { Pin(0) }, result.RemovedPins.ToArray());
        CollectionAssert.AreEqual(new[] { Pin(3) }, result.AddedPins.ToArray());
        var deletions = PinPartitionEvolution.Plan(Groups(0, 0), Groups(-1, 0), Groups(0, -1));
        Assert.IsEmpty(deletions.Groups!); Assert.IsEmpty(deletions.Conflicts);
        Assert.HasCount(2, deletions.RemovedPins);
    }

    [TestMethod]
    public void DeletingAPinWhileTheOtherSideRewiresOrDisconnectsItConflicts()
    {
        foreach (var native in new[] { Groups(0, 1, 1), Groups(0, 1, 2), Groups(0, 0, 0), Groups(0, 0, 1, 0) })
        {
            var result = PinPartitionEvolution.Plan(Groups(0, 0, 1), Groups(-1, 0, 1), native);
            Assert.IsNull(result.Groups); Assert.IsTrue(result.Conflicts.Any(c => c.Reason == "pin_removed_while_rewired"));
            Assert.IsTrue(result.Conflicts.Any(c => c.Pins.Contains(Pin(0))));
        }
    }

    [TestMethod]
    public void SharedNewPinIdentityCannotCarryConflictingConnectionsOrBridgeAnOldSeparation()
    {
        foreach (var native in new[] { Groups(0, 1, 1), Groups(0, 1, 2) })
        {
            var result = PinPartitionEvolution.Plan(Groups(0, 1, -1), Groups(0, 1, 0), native);
            Assert.IsNull(result.Groups); Assert.IsNotEmpty(result.Conflicts);
        }
        var convergent = PinPartitionEvolution.Plan(Groups(0, 1), Groups(0, 1, 0), Groups(0, 1, 0));
        Assert.AreEqual("0,2;1", Key(convergent.Groups!));
        var unrelated = PinPartitionEvolution.Plan([], Groups(0, -1), Groups(-1, 0));
        Assert.AreEqual("0;1", Key(unrelated.Groups!));
    }

    [TestMethod, DataRow(3, 15), DataRow(4, 52)]
    public void EverySmallPresenceAndPartitionCombinationMatchesIndependentOracle(int size, int count)
    {
        var partitions = PartialPartitions(size).ToArray(); Assert.AreEqual(count, partitions.Length);
        foreach (var b in partitions)
        foreach (var d in partitions)
        foreach (var n in partitions)
        {
            var expected = Oracle(b, d, n);
            var actual = PinPartitionEvolution.Plan(Groups(b), Groups(d), Groups(n));
            string context = $"b={string.Join(',', b)} d={string.Join(',', d)} n={string.Join(',', n)}";
            Assert.AreEqual(expected is not null, actual.Groups is not null, context);
            if (expected is null) { Assert.IsNotEmpty(actual.Conflicts, context); continue; }
            Assert.IsEmpty(actual.Conflicts, context);
            Assert.AreEqual(Key(expected), Key(actual.Groups!), context);
        }
    }

    [TestMethod]
    public void ReorderingAndReversingSidesIsDeterministicAndASettledPartitionIsIdempotent()
    {
        var b = Groups(0, 0, 1, -1, -1); var d = Groups(-1, 0, 1, 1, -1); var n = Groups(0, 0, 1, -1, 1);
        static IReadOnlyList<IReadOnlyList<PinEndpoint>> Reverse(IReadOnlyList<IReadOnlyList<PinEndpoint>> groups) =>
            groups.Reverse().Select(g => (IReadOnlyList<PinEndpoint>)g.Reverse().ToArray()).ToArray();
        var first = PinPartitionEvolution.Plan(b, d, n);
        var second = PinPartitionEvolution.Plan(Reverse(b), Reverse(n), Reverse(d));
        Assert.AreEqual(Key(first.Groups!), Key(second.Groups!));
        CollectionAssert.AreEqual(first.AddedPins.ToArray(), second.AddedPins.ToArray());
        CollectionAssert.AreEqual(first.RemovedPins.ToArray(), second.RemovedPins.ToArray());
        var settled = PinPartitionEvolution.Plan(first.Groups!, first.Groups!, first.Groups!);
        Assert.AreEqual(Key(first.Groups!), Key(settled.Groups!));
        Assert.IsEmpty(settled.AddedPins); Assert.IsEmpty(settled.RemovedPins);
    }

    [TestMethod, Timeout(15000)]
    public void LargeSparseEvolutionDoesNotEnumerateEveryPair()
    {
        var baseline = Enumerable.Range(0, 20000).Select(i => (IReadOnlyList<PinEndpoint>)new[] { Pin(i) }).ToArray();
        var desired = baseline.Where((_, i) => i % 2 == 0).Concat(Enumerable.Range(20000, 10000)
            .Select(i => (IReadOnlyList<PinEndpoint>)new[] { Pin(i) })).ToArray();
        var native = baseline.Concat(Enumerable.Range(30000, 10000)
            .Select(i => (IReadOnlyList<PinEndpoint>)new[] { Pin(i) })).ToArray();
        var result = PinPartitionEvolution.Plan(baseline, desired, native);
        Assert.IsEmpty(result.Conflicts); Assert.HasCount(30000, result.Groups!);
        Assert.HasCount(20000, result.AddedPins); Assert.HasCount(10000, result.RemovedPins);
    }

    [TestMethod]
    public void InvalidIdentityMembershipAndCancellationAreRejectedWithoutWeakeningStrictMerge()
    {
        foreach (IReadOnlyList<IReadOnlyList<PinEndpoint>> invalid in new IReadOnlyList<IReadOnlyList<PinEndpoint>>[]
            { [[Pin(0)], [Pin(0)]], [[]], [[new(Guid.Empty, "0")]], [[new(Component, " ")]], [null!], [[null!]] })
        foreach (int side in Enumerable.Range(0, 3))
            Assert.ThrowsExactly<AutomationException>(() => PinPartitionEvolution.Plan(
                side == 0 ? invalid : [], side == 1 ? invalid : [], side == 2 ? invalid : []));
        Assert.ThrowsExactly<OperationCanceledException>(() => PinPartitionEvolution.Plan([], [], [], new(true)));
        Assert.ThrowsExactly<AutomationException>(() => PinPartitionMerge.Plan(Groups(0), Groups(0, 0), Groups(0)));
        Assert.IsEmpty(PinPartitionEvolution.Plan([], [], []).Groups!);
    }

    private static string Key(IReadOnlyList<IReadOnlyList<PinEndpoint>> groups) =>
        string.Join(';', groups.Select(g => string.Join(',', g.Select(p => p.Pin).Order(StringComparer.Ordinal))).Order(StringComparer.Ordinal));

    // Deliberately pairwise and small: independent of production's group indexes.
    private static IReadOnlyList<IReadOnlyList<PinEndpoint>>? Oracle(int[] b, int[] d, int[] n)
    {
        int size = b.Length;
        bool Related(int[] side, int i, int j) => side[i] >= 0 && side[j] >= 0 && side[i] == side[j];
        var kept = Enumerable.Range(0, size).Where(i => b[i] < 0 ? d[i] >= 0 || n[i] >= 0 : d[i] >= 0 && n[i] >= 0).ToArray();
        foreach (int i in Enumerable.Range(0, size).Except(kept).Where(i => b[i] >= 0))
        foreach (var side in new[] { d, n }.Where(side => side[i] >= 0))
        foreach (int j in Enumerable.Range(0, size).Where(j => side[j] >= 0))
            if (Related(b, i, j) != Related(side, i, j)) return null;
        var required = new bool?[size, size]; var closure = new bool[size, size];
        foreach (int i in kept)
        foreach (int j in kept)
        {
            bool? relation;
            if (b[i] >= 0 && b[j] >= 0)
                relation = Related(b, i, j) ? Related(d, i, j) && Related(n, i, j) : Related(d, i, j) || Related(n, i, j);
            else
            {
                var assertions = new[] { d, n }.Where(side => side[i] >= 0 && side[j] >= 0)
                    .Select(side => Related(side, i, j)).Distinct().ToArray();
                if (assertions.Length > 1) return null;
                relation = assertions.Length == 0 ? null : assertions[0];
            }
            required[i, j] = relation; closure[i, j] = relation == true;
        }
        foreach (int k in kept)
        foreach (int i in kept)
        foreach (int j in kept) closure[i, j] |= closure[i, k] && closure[k, j];
        foreach (int i in kept)
        foreach (int j in kept) if (required[i, j] == false && closure[i, j]) return null;
        return kept.GroupBy(i => kept.First(j => closure[i, j])).Select(g => (IReadOnlyList<PinEndpoint>)g.Select(Pin).ToArray()).ToArray();
    }

    private static IEnumerable<int[]> PartialPartitions(int size)
    {
        var labels = new int[size];
        IEnumerable<int[]> Fill(int i, int maximum)
        {
            if (i == size) { yield return labels.ToArray(); yield break; }
            for (int value = -1; value <= maximum + 1; ++value)
            {
                labels[i] = value;
                foreach (var candidate in Fill(i + 1, Math.Max(maximum, value))) yield return candidate;
            }
        }
        return Fill(0, -1);
    }
}

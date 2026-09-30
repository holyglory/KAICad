using KiCad.Automation.Model;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PinPartitionMergeTests
{
    private static readonly Guid Component = Guid.Parse("278350ba-9328-4f00-9c2d-7f29ea02b3dd");
    private static PinEndpoint Pin(int index) => new(Component, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static IReadOnlyList<IReadOnlyList<PinEndpoint>> Groups(int[] labels) => Enumerable.Range(0, labels.Length)
        .GroupBy(i => labels[i]).Select(g => (IReadOnlyList<PinEndpoint>)g.Select(Pin).ToArray()).ToArray();

    [TestMethod]
    public void ExhaustiveFourPinPartitionsMatchTheIndependentPairwiseOracle()
    {
        var partitions = Partitions(4).ToArray(); Assert.AreEqual(15, partitions.Length);
        foreach (var b in partitions)
        foreach (var d in partitions)
        foreach (var n in partitions)
        {
            bool Relation(int i, int j) => b[i] == b[j] ? d[i] == d[j] && n[i] == n[j] : d[i] == d[j] || n[i] == n[j];
            bool consistent = true;
            for (int i = 0; i < 4; ++i)
            for (int j = 0; j < 4; ++j)
            for (int k = 0; k < 4; ++k)
                if (Relation(i, j) && Relation(j, k) && !Relation(i, k)) consistent = false;
            var result = PinPartitionMerge.Plan(Groups(b), Groups(d), Groups(n));
            Assert.AreEqual(consistent, result.Groups is not null,
                $"b={string.Join(',', b)} d={string.Join(',', d)} n={string.Join(',', n)}");
            if (!consistent) { Assert.IsNotEmpty(result.Conflicts); continue; }
            Assert.IsEmpty(result.Conflicts);
            for (int i = 0; i < 4; ++i)
            for (int j = 0; j < 4; ++j)
                Assert.AreEqual(Relation(i, j), result.Groups!.Any(g => g.Contains(Pin(i)) && g.Contains(Pin(j))));
        }
    }

    [TestMethod]
    public void ReorderingInputsDoesNotChangeGroupsOrConflictMeaning()
    {
        var b = Groups([0, 0, 1, 1]); var d = Groups([0, 1, 2, 2]); var n = Groups([0, 0, 1, 2]);
        var first = PinPartitionMerge.Plan(b, d, n);
        static IReadOnlyList<IReadOnlyList<PinEndpoint>> Reverse(IReadOnlyList<IReadOnlyList<PinEndpoint>> groups) =>
            groups.Reverse().Select(g => (IReadOnlyList<PinEndpoint>)g.Reverse().ToArray()).ToArray();
        var second = PinPartitionMerge.Plan(Reverse(b), Reverse(d), Reverse(n));
        Assert.AreEqual(Key(first), Key(second));
    }

    [TestMethod, Timeout(15000)]
    public void LargeIndependentPartitionsDoNotEnumerateEveryPinPair()
    {
        int[] labels = Enumerable.Range(0, 20000).ToArray();
        var b = Groups(labels); var d = Groups(labels); var n = Groups(labels);
        var result = PinPartitionMerge.Plan(b, d, n);
        Assert.AreEqual(20000, result.Groups!.Count); Assert.IsEmpty(result.Conflicts);
    }

    [TestMethod]
    public void InvalidMembershipOrUniverseAndCancellationAreRejected()
    {
        var valid = Groups([0, 1]);
        foreach (IReadOnlyList<IReadOnlyList<PinEndpoint>> invalid in new IReadOnlyList<IReadOnlyList<PinEndpoint>>[]
            { [[Pin(0)], [Pin(0)]], [[], [Pin(0), Pin(1)]], [[Pin(0)]], [[new(Guid.Empty, "0"), Pin(1)]] })
            Assert.ThrowsExactly<AutomationException>(() => PinPartitionMerge.Plan(valid, invalid, valid));
        Assert.ThrowsExactly<OperationCanceledException>(() => PinPartitionMerge.Plan(valid, valid, valid, new(true)));
        Assert.IsEmpty(PinPartitionMerge.Plan([], [], []).Groups!);
    }

    private static string Key(PinPartitionMergeResult result) => result.Groups is null
        ? string.Join(';', result.Conflicts.Select(c => c.Reason + ":" + string.Join(',', c.Pins.Select(p => p.Pin))))
        : string.Join(';', result.Groups.Select(g => string.Join(',', g.Select(p => p.Pin))));

    private static IEnumerable<int[]> Partitions(int size)
    {
        int[] labels = new int[size];
        IEnumerable<int[]> Fill(int index, int max)
        {
            if (index == size) { yield return labels.ToArray(); yield break; }
            for (int value = 0; value <= max + 1; ++value)
            {
                labels[index] = value;
                foreach (var result in Fill(index + 1, Math.Max(max, value))) yield return result;
            }
        }
        return Fill(1, 0);
    }
}

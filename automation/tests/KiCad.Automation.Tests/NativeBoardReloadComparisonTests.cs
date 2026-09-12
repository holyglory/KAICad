using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class NativeBoardReloadComparisonTests
{
    private const string FirstRoot = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string NextRoot = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

    private static GetItemsResponse Fixture(string root) => new()
    {
        Items =
        {
            Any.Pack(new Track { Id = new() { Value = "11111111-1111-4111-8111-111111111111" },
                Parent = new() { Value = root }, Start = new() { XNm = 100 }, Width = new() { ValueNm = 250000 },
                Net = new() { Name = "POWER" } }),
            Any.Pack(new Via { Id = new() { Value = "22222222-2222-4222-8222-222222222222" },
                Parent = new() { Value = root } })
        }
    };

    [TestMethod]
    public void OnlyRuntimeContainerIsReboundWithoutMutatingEitherSnapshot()
    {
        var before = Fixture(FirstRoot); var after = Fixture(NextRoot);
        var savedBefore = before.Clone(); var savedAfter = after.Clone();
        Assert.AreNotEqual(before, after);
        Assert.AreEqual(after, NativeBoardReloadComparison.RebindRuntimeContainer(before, after));
        Assert.AreEqual(savedBefore, before); Assert.AreEqual(savedAfter, after);
    }

    [TestMethod]
    public void PersistedIdentityGeometryLockLayerAndNetChangesAreNotHidden()
    {
        Action<Track>[] edits =
        [
            track => track.Id.Value = "33333333-3333-4333-8333-333333333333",
            track => track.Start.XNm++,
            track => track.Width.ValueNm++,
            track => track.Locked = (Kiapi.Common.Types.LockedState)2,
            track => track.Layer = (BoardLayer)3,
            track => track.Net.Name = "OTHER"
        ];
        foreach (var edit in edits)
        {
            var before = Fixture(FirstRoot); var after = Fixture(NextRoot);
            var track = after.Items[0].Unpack<Track>(); edit(track); after.Items[0] = Any.Pack(track);
            Assert.AreNotEqual(after, NativeBoardReloadComparison.RebindRuntimeContainer(before, after));
        }
    }

    [TestMethod]
    public void MissingMixedAndUnsupportedContainersAreRejected()
    {
        foreach (int kind in Enumerable.Range(0, 3))
        {
            var before = Fixture(FirstRoot); var after = Fixture(NextRoot);
            var via = after.Items[1].Unpack<Via>();
            switch (kind)
            {
                case 0: via.Parent = null; break;
                case 1: via.Parent.Value = FirstRoot; break;
            }
            after.Items[1] = kind == 2 ? Any.Pack(new Kiapi.Common.Types.DocumentSpecifier()) : Any.Pack(via);
            Assert.ThrowsExactly<InvalidDataException>(() => NativeBoardReloadComparison.RebindRuntimeContainer(before, after));
        }
    }
}

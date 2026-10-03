using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbConnectivityToolsTests
{
    [TestMethod]
    public void NormalizeReturnsTypedItemsInDeterministicOrder()
    {
        var track = new Track { Id = new() { Value = "BBBBBBBB-BBBB-4BBB-8BBB-BBBBBBBBBBBB" }, Layer = BoardLayer.BlFCu,
            Net = new() { Name = "POWER" } };
        var pad = new Pad { Id = new() { Value = "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA" }, Number = "1",
            Parent = new() { Value = "CCCCCCCC-CCCC-4CCC-8CCC-CCCCCCCCCCCC" }, Net = new() { Name = "POWER" } };
        var arc = new Arc { Id = new() { Value = "DDDDDDDD-DDDD-4DDD-8DDD-DDDDDDDDDDDD" }, Layer = BoardLayer.BlFCu,
            Net = new() { Name = "POWER" } };
        var via = new Via { Id = new() { Value = "EEEEEEEE-EEEE-4EEE-8EEE-EEEEEEEEEEEE" }, Net = new() { Name = "POWER" },
            PadStack = new() { Layers = { BoardLayer.BlFCu, BoardLayer.BlBCu } } };
        var zone = new Zone { Id = new() { Value = "FFFFFFFF-FFFF-4FFF-8FFF-FFFFFFFFFFFF" }, Layers = { BoardLayer.BlFCu } };

        var result = PcbConnectivityTools.Normalize([
            Any.Pack(zone), Any.Pack(via), Any.Pack(arc), Any.Pack(track), Any.Pack(pad)
        ]);

        CollectionAssert.AreEqual(new[] { "pad", "track", "arc", "via", "zone" }, result.Select(item => item.Kind).ToArray());
        Assert.AreEqual("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", result[0].Id);
        Assert.AreEqual("cccccccc-cccc-4ccc-8ccc-cccccccccccc", result[0].ParentId);
        CollectionAssert.AreEqual(new[] { "BlBCu", "BlFCu" }, result[3].Layers);
        Assert.IsFalse(result.Any(item => item.Kind == "zone" && item.NetName is not null), "The wrapper must not invent an unavailable zone net.");
    }

    [TestMethod]
    public void NormalizeRejectsUnknownNativeItemsAndDuplicateIdentities()
    {
        var id = new KIID { Value = "11111111-1111-4111-8111-111111111111" };
        var pad = Any.Pack(new Pad { Id = id });
        var duplicate = Assert.ThrowsExactly<AutomationException>(() => PcbConnectivityTools.Normalize([pad, pad.Clone()]));
        Assert.AreEqual("duplicate_pcb_connectivity_item", duplicate.Code);

        var unknown = Assert.ThrowsExactly<AutomationException>(() => PcbConnectivityTools.Normalize([
            new Any { TypeUrl = "type.googleapis.com/kiapi.board.types.FutureItem" }
        ]));
        Assert.AreEqual("unsupported_pcb_connectivity_item", unknown.Code);
    }
}

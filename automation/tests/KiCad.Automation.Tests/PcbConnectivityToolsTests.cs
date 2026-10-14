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
        var track = new Track { Id = new() { Value = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb" }, Layer = BoardLayer.BlFCu,
            Net = new() { Name = "POWER" } };
        var pad = new Pad { Id = new() { Value = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa" }, Number = "1",
            Parent = new() { Value = "cccccccc-cccc-4ccc-8ccc-cccccccccccc" }, Net = new() { Name = "POWER" } };
        var arc = new Arc { Id = new() { Value = "dddddddd-dddd-4ddd-8ddd-dddddddddddd" }, Layer = BoardLayer.BlFCu,
            Net = new() { Name = "POWER" } };
        var via = new Via { Id = new() { Value = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee" }, Net = new() { Name = "POWER" },
            PadStack = new() { Layers = { BoardLayer.BlFCu, BoardLayer.BlBCu } } };
        var zone = new Zone { Id = new() { Value = "ffffffff-ffff-4fff-8fff-ffffffffffff" }, Layers = { BoardLayer.BlFCu } };

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

        var uppercase = Assert.ThrowsExactly<AutomationException>(() => PcbConnectivityTools.Normalize([
            Any.Pack(new Pad { Id = new() { Value = "a2222222-2222-4222-8222-222222222222".ToUpperInvariant() } })
        ]));
        Assert.AreEqual("invalid_pcb_connectivity_item_identity", uppercase.Code);

        var unknown = Assert.ThrowsExactly<AutomationException>(() => PcbConnectivityTools.Normalize([
            new Any { TypeUrl = "type.googleapis.com/kiapi.board.types.FutureItem" }
        ]));
        Assert.AreEqual("unsupported_pcb_connectivity_item", unknown.Code);
    }

    [TestMethod]
    public void NormalizeEmptyNativeResponseReturnsDeterministicEmptyResult()
    {
        var result = PcbConnectivityTools.Normalize([]);

        Assert.AreEqual(0, result.Length);
    }

    [TestMethod]
    public void NormalizeSupportsFootprintIdentityForItemQueries()
    {
        var footprint = new FootprintInstance
        {
            Id = new() { Value = "11111111-1111-4111-8111-111111111111" },
            Layer = BoardLayer.BlFCu,
            Parent = new() { Value = "22222222-2222-4222-8222-222222222222" }
        };

        var result = PcbConnectivityTools.Normalize([Any.Pack(footprint)]);

        Assert.AreEqual(1, result.Length);
        Assert.AreEqual("footprint", result[0].Kind);
        Assert.AreEqual(footprint.Id.Value, result[0].Id);
        Assert.AreEqual(footprint.Parent.Value, result[0].ParentId);
        Assert.AreEqual(nameof(BoardLayer.BlFCu), result[0].Layer);
    }

    [TestMethod]
    public void DescribeSelectorReturnsReplayableTypedFields()
    {
        var net = PcbConnectivityTools.DescribeSelector("net", "POWER", null);
        Assert.AreEqual("net", net.Kind);
        Assert.AreEqual("POWER", net.NetName);
        Assert.IsNull(net.NetClass);
        Assert.IsNull(net.SourceItemIds);
        Assert.IsNull(net.ItemIds);

        var netClass = PcbConnectivityTools.DescribeSelector("net-class", "HighSpeed", null);
        Assert.AreEqual("net-class", netClass.Kind);
        Assert.AreEqual("HighSpeed", netClass.NetClass);

        string first = "11111111-1111-4111-8111-111111111111";
        string second = "22222222-2222-4222-8222-222222222222";
        var connected = PcbConnectivityTools.DescribeSelector("connected", $"{first},{second}", [first, second]);
        CollectionAssert.AreEqual(new[] { first, second }, connected.SourceItemIds);
        Assert.IsNull(connected.ItemIds);

        var byId = PcbConnectivityTools.DescribeSelector("items-by-id", $"{first},{second}", [first, second]);
        CollectionAssert.AreEqual(new[] { first, second }, byId.ItemIds);
        Assert.IsNull(byId.SourceItemIds);

        var invalid = Assert.ThrowsExactly<AutomationException>(() =>
            PcbConnectivityTools.DescribeSelector("connected", first, null));
        Assert.AreEqual("invalid_pcb_connectivity_selector", invalid.Code);
    }
}

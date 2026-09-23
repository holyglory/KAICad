using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using DocumentRevision = KiCad.Automation.Model.DocumentRevision;

namespace KiCad.Automation.Tests;

// Contract tests for the wiring planner's pure building blocks (cn1-wiring-intent.md §6.1 policy and
// geometry, §6.7 identities). They are unit tests on purpose: these helpers are isolated arithmetic and
// hashing with no native or file boundary, and no existing journey can reach them until the editor
// advertises schematic.connection-realization.v1. The identity vectors were computed independently of
// this implementation (Python hashlib and uuid over the §6.7 material).
[TestClass]
public sealed class SchematicWiringPlannerTests
{
    [TestMethod]
    public void ConnectionPolicyComesOnlyFromTheProjectGridAndTextSize()
    {
        var standard = SchematicConnectionPolicy.FromSnapshot(Snapshot((1_270_000, 1_270_000), (1_270_000, 1_270_000)));
        Assert.AreEqual(new SchematicConnectionPolicy(1_270_000, 635_000, 1_270_000, 2_540_000, 2_540_000, 635_000), standard);
        // Clearance is half a grid floored to the 100 nm native unit; the orientation tolerance is exactly half a grid.
        Assert.AreEqual(new SchematicConnectionPolicy(1_300, 600, 500, 2_600, 2_600, 650),
            SchematicConnectionPolicy.FromSnapshot(Snapshot((1_300, 500))));
        Assert.AreEqual(new SchematicConnectionPolicy(100, 0, 100, 200, 200, 50), SchematicConnectionPolicy.FromSnapshot(Snapshot((100, 100))));
        CollectionAssert.AreEqual(new[] { 2, 3, 4, 6, 8 }, SchematicConnectionPolicy.StubMultiples);

        var noFormatting = Snapshot((1_270_000, 1_270_000), (1_270_000, 1_270_000));
        noFormatting.Instances[1].Metadata.Formatting = null;
        foreach (var (problem, data) in new (string, SchematicHierarchyData)[]
        {
            ("no sheet instance", new SchematicHierarchyData()),
            ("an instance without formatting (older peer)", noFormatting),
            ("instances disagree on the grid", Snapshot((1_270_000, 1_270_000), (2_540_000, 1_270_000))),
            ("instances disagree on the text size", Snapshot((1_270_000, 1_270_000), (1_270_000, 1_524_000))),
            ("zero grid", Snapshot((0, 1_270_000))),
            ("negative grid", Snapshot((-1_270_000, 1_270_000))),
            ("grid off the 100 nm unit", Snapshot((1_270_050, 1_270_000))),
            ("zero text size", Snapshot((1_270_000, 0))),
            ("text size off the 100 nm unit", Snapshot((1_270_000, 1_270_050))),
            ("grid too large to double", Snapshot((long.MaxValue / 100 * 100, 1_270_000)))
        })
            Assert.AreEqual(SchematicConnectionErrors.RealizationGridUnavailable,
                Assert.ThrowsExactly<AutomationException>(() => SchematicConnectionPolicy.FromSnapshot(data), problem).Code, problem);
    }

    [TestMethod]
    public void StubsLeaveThePinAwayFromItsBodyAndTheirLabelsFaceTheSameWay()
    {
        foreach (var (bodyX, bodyY, outward, spin) in new (int, int, (int, int), SchematicLabelSpinStyle)[]
        {
            (-1, 0, (1, 0), SchematicLabelSpinStyle.SlssRight),
            (1, 0, (-1, 0), SchematicLabelSpinStyle.SlssLeft),
            (0, 1, (0, -1), SchematicLabelSpinStyle.SlssUp),
            (0, -1, (0, 1), SchematicLabelSpinStyle.SlssBottom)
        })
        {
            var anchor = new SchematicPinAnchor { Position = new() { XNm = 50_800_000, YNm = 25_400_000 }, BodyDirectionX = bodyX, BodyDirectionY = bodyY };
            Assert.AreEqual(outward, SchematicConnectionGeometry.Outward(anchor));
            Assert.AreEqual(spin, SchematicConnectionGeometry.Spin(outward));
            var end = SchematicConnectionGeometry.StubEnd(anchor.Position, outward, 2_540_000);
            Assert.AreEqual(new Vector2 { XNm = 50_800_000 + outward.Item1 * 2_540_000L, YNm = 25_400_000 + outward.Item2 * 2_540_000L }, end);
            Assert.AreEqual(new Vector2 { XNm = 50_800_000, YNm = 25_400_000 }, anchor.Position, "The measured anchor must not change.");
        }
        foreach (var (x, y) in new[] { (0, 0), (1, 1), (2, 0), (0, -2), (-1, 1), (int.MinValue, 0), (1, int.MaxValue) })
        {
            string problem = $"body direction ({x},{y})";
            Assert.AreEqual(SchematicConnectionErrors.RealizationPinGeometryMismatch, Assert.ThrowsExactly<AutomationException>(() =>
                SchematicConnectionGeometry.Outward(new SchematicPinAnchor { BodyDirectionX = x, BodyDirectionY = y }), problem).Code, problem);
            Assert.AreEqual(SchematicConnectionErrors.RealizationPinGeometryMismatch,
                Assert.ThrowsExactly<AutomationException>(() => SchematicConnectionGeometry.Spin((x, y)), problem).Code, problem);
            Assert.AreEqual(SchematicConnectionErrors.RealizationPinGeometryMismatch, Assert.ThrowsExactly<AutomationException>(() =>
                SchematicConnectionGeometry.StubEnd(new Vector2(), (x, y), 2_540_000), problem).Code, problem);
        }
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SchematicConnectionGeometry.StubEnd(new Vector2(), (1, 0), 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SchematicConnectionGeometry.StubEnd(new Vector2(), (1, 0), -2_540_000));
    }

    [TestMethod]
    public void GeneratedIdentitiesAreDeterministicVersionedAndIndependentlyReproducible()
    {
        Guid origin = Guid.Parse("7e57f1c5-0000-4000-8000-000100000001"), screen = Guid.Parse("7e57f1c5-0000-4000-8000-002000000005");
        Guid pin = Guid.Parse("7e57f1c5-0000-4000-8000-002100000007");
        var revision = new DocumentRevision("epoch-1", 42);
        const string Sha = "04f0ad7a532115e491ba5bb3f7b65808cc7fa4300de61fb437aae9ccffb1dd85"; // SHA-256 of "<design/>"
        string pinKey = SchematicConnectionIdentity.PinAnchorKey(pin);
        Assert.AreEqual("7e57f1c5-0000-4000-8000-002100000007", pinKey);

        // Independent vectors (Python hashlib/uuid over the §6.7 material).
        var stub = SchematicConnectionIdentity.Generated(origin, revision, Sha, screen, GeneratedConnectionRole.StubWire, pinKey);
        Assert.AreEqual("70e57fbd-17f9-805c-b25b-0ec57dc87727", stub.ToString("D"));
        Assert.AreEqual("efec540c-3473-8f73-8641-8fa44cf87244", SchematicConnectionIdentity.Generated(origin, revision, Sha, screen,
            GeneratedConnectionRole.RouteWire, SchematicConnectionIdentity.RouteAnchorKey(pinKey, 3), 3).ToString("D"));
        Assert.AreEqual("98afc114-f345-8064-b4bc-8a09574363fc", SchematicConnectionIdentity.Probe(revision, screen,
            LocalLabel.Descriptor, "RAIL_A", SchematicLabelSpinStyle.SlssRight, SchematicLabelShape.SlshPassive).ToString("D"));
        Assert.AreEqual("d16a4c98-1180-8969-ae57-51572cf731b3", SchematicConnectionIdentity.Probe(revision, screen,
            SchematicSymbolInstance.Descriptor, "", SchematicLabelSpinStyle.SlssUnknown, SchematicLabelShape.SlshUnknown,
            Guid.Parse("7e57f1c5-0000-4000-8000-002000000010"), 90).ToString("D"));

        // A retry repeats the ID; every input of the material changes it.
        Assert.AreEqual(stub, SchematicConnectionIdentity.Generated(origin, revision, Sha, screen, GeneratedConnectionRole.StubWire, pinKey));
        var variants = new[]
        {
            SchematicConnectionIdentity.Generated(Guid.NewGuid(), revision, Sha, screen, GeneratedConnectionRole.StubWire, pinKey),
            SchematicConnectionIdentity.Generated(origin, revision with { Epoch = "epoch-2" }, Sha, screen, GeneratedConnectionRole.StubWire, pinKey),
            SchematicConnectionIdentity.Generated(origin, revision with { Sequence = 43 }, Sha, screen, GeneratedConnectionRole.StubWire, pinKey),
            SchematicConnectionIdentity.Generated(origin, revision, new string('0', 64), screen, GeneratedConnectionRole.StubWire, pinKey),
            SchematicConnectionIdentity.Generated(origin, revision, Sha, Guid.NewGuid(), GeneratedConnectionRole.StubWire, pinKey),
            SchematicConnectionIdentity.Generated(origin, revision, Sha, screen, GeneratedConnectionRole.StubLabel, pinKey),
            SchematicConnectionIdentity.Generated(origin, revision, Sha, screen, GeneratedConnectionRole.StubWire, SchematicConnectionIdentity.PinAnchorKey(Guid.NewGuid())),
            SchematicConnectionIdentity.Probe(revision, screen, LineProbe(), pinKey, SchematicLabelSpinStyle.SlssUnknown, SchematicLabelShape.SlshUnknown)
        };
        CollectionAssert.AllItemsAreUnique(variants.Append(stub).ToArray());
        Assert.AreNotEqual(SchematicConnectionIdentity.Generated(origin, revision, Sha, screen, GeneratedConnectionRole.RouteWire, pinKey, 0),
            SchematicConnectionIdentity.Generated(origin, revision, Sha, screen, GeneratedConnectionRole.RouteWire, pinKey, 1));
        foreach (var id in variants.Append(stub))
        {
            string text = id.ToString("D");
            Assert.AreEqual('8', text[14], "Identity material must carry version nibble 8.");
            Assert.IsTrue(text[19] is '8' or '9' or 'a' or 'b', "Identity material must carry the RFC 4122 variant.");
        }
        CollectionAssert.AreEqual(new[] { "stub-wire", "stub-label", "anchor-label", "sheet-pin", "sheet-pin-wire", "sheet-pin-label", "route-wire", "junction" },
            Enum.GetValues<GeneratedConnectionRole>().Select(SchematicConnectionIdentity.RoleName).ToArray());

        // Anchor keys (§6.7).
        Guid sheet = Guid.Parse("7e57f1c5-0000-4000-8000-002000000002");
        Assert.AreEqual("7e57f1c5-0000-4000-8000-002000000002#RAIL_A", SchematicConnectionIdentity.SheetPinAnchorKey(sheet, "RAIL_A"));
        string net = SchematicConnectionIdentity.NetKey([Guid.Parse("f0000000-0000-4000-8000-000000000001"),
            Guid.Parse("0a000000-0000-4000-8000-000000000002"), Guid.Parse("a0000000-0000-4000-8000-000000000003")]);
        Assert.AreEqual("0a000000-0000-4000-8000-000000000002", net);
        Assert.AreEqual(net + "#12", SchematicConnectionIdentity.RouteAnchorKey(net, 12));
        Assert.AreEqual(net + "#-2540000,127000000", SchematicConnectionIdentity.JunctionAnchorKey(net, -2_540_000, 127_000_000));

        // Inputs that would make the material ambiguous or break the contract are refused.
        Assert.ThrowsExactly<ArgumentException>(() => SchematicConnectionIdentity.Generated(origin, revision, Sha.ToUpperInvariant(), screen, GeneratedConnectionRole.StubWire, pinKey));
        Assert.ThrowsExactly<ArgumentException>(() => SchematicConnectionIdentity.Generated(origin, revision, Sha[1..], screen, GeneratedConnectionRole.StubWire, pinKey));
        Assert.ThrowsExactly<ArgumentException>(() => SchematicConnectionIdentity.Generated(Guid.Empty, revision, Sha, screen, GeneratedConnectionRole.StubWire, pinKey));
        Assert.ThrowsExactly<ArgumentException>(() => SchematicConnectionIdentity.Generated(origin, revision, Sha, Guid.Empty, GeneratedConnectionRole.StubWire, pinKey));
        Assert.ThrowsExactly<ArgumentException>(() => SchematicConnectionIdentity.Generated(origin, revision with { Epoch = "" }, Sha, screen, GeneratedConnectionRole.StubWire, pinKey));
        Assert.ThrowsExactly<ArgumentException>(() => SchematicConnectionIdentity.Generated(origin, revision, Sha, screen, GeneratedConnectionRole.StubWire, pinKey + "\nforged"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SchematicConnectionIdentity.Generated(origin, revision, Sha, screen, GeneratedConnectionRole.StubWire, pinKey, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SchematicConnectionIdentity.Generated(origin, revision, Sha, screen, GeneratedConnectionRole.RouteWire, pinKey, -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SchematicConnectionIdentity.Generated(origin, revision, Sha, screen, (GeneratedConnectionRole)99, pinKey));
        Assert.ThrowsExactly<ArgumentException>(() => SchematicConnectionIdentity.Probe(revision, screen, SchematicSymbolInstance.Descriptor, "",
            SchematicLabelSpinStyle.SlssUnknown, SchematicLabelShape.SlshUnknown, symbolId: Guid.NewGuid()));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SchematicConnectionIdentity.Probe(revision, screen, SchematicSymbolInstance.Descriptor, "",
            SchematicLabelSpinStyle.SlssUnknown, SchematicLabelShape.SlshUnknown, Guid.NewGuid(), 45));
        Assert.ThrowsExactly<ArgumentException>(() => SchematicConnectionIdentity.Probe(revision, screen, LocalLabel.Descriptor, "A\tB",
            SchematicLabelSpinStyle.SlssRight, SchematicLabelShape.SlshPassive));
        Assert.ThrowsExactly<ArgumentException>(() => SchematicConnectionIdentity.SheetPinAnchorKey(sheet, ""));
        Assert.ThrowsExactly<ArgumentException>(() => SchematicConnectionIdentity.NetKey([]));
        Assert.ThrowsExactly<ArgumentException>(() => SchematicConnectionIdentity.PinAnchorKey(Guid.Empty));

        // An ID already used by the schematic, the candidate or an earlier item is a collision, not a silent reuse.
        var used = new HashSet<Guid> { pin };
        SchematicConnectionIdentity.Claim(used, stub);
        Assert.AreEqual(SchematicConnectionErrors.RealizationIdentityCollision,
            Assert.ThrowsExactly<AutomationException>(() => SchematicConnectionIdentity.Claim(used, stub)).Code);
        Assert.AreEqual(SchematicConnectionErrors.RealizationIdentityCollision,
            Assert.ThrowsExactly<AutomationException>(() => SchematicConnectionIdentity.Claim(used, pin)).Code);
        Assert.HasCount(2, used);
    }

    private static Google.Protobuf.Reflection.MessageDescriptor LineProbe() => SchematicLine.Descriptor;

    private static SchematicHierarchyData Snapshot(params (long Grid, long Text)[] instances)
    {
        var data = new SchematicHierarchyData();
        foreach (var (grid, text) in instances)
            data.Instances.Add(new SchematicScreenData { Metadata = new()
                { Formatting = new() { ConnectionGridNm = grid, DefaultTextSizeNm = text } } });
        return data;
    }
}

using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

// Isolated rules of continuing a released operation (ExitedOperationRelease). The native journey
// NativeSessionTests.NativeCrashReleasesTheExitedOperation proves the continuation end to end against killed KiCad processes,
// including a note carried on a sheet the operation does not change, a wire there refused, and edits on the operation's own
// sheets refused. Two things it cannot reach are checked here: every kind of edit on such a sheet (the journey makes a note
// and a wire, not every object type), and the advice of refusals that need a KiCad killed during the XML publication itself,
// which the journey's kills (at the native commit and inside the native save) never produce.
[TestClass]
public sealed class ExitedOperationReleaseTests
{
    private static readonly string Id = Guid.NewGuid().ToString("D");

    private static IReadOnlyDictionary<Guid, IMessage> Live(params IMessage[] items) =>
        SchematicItemDelta.Index(items.Select(item => Any.Pack(item)));

    private static SchematicLine Line(SchematicLineType type) => new() { Id = new() { Value = Id }, Type = type,
        Start = new() { XNm = 0, YNm = 0 }, End = new() { XNm = 2_540_000, YNm = 0 } };

    // A continuation may publish, as KiCad holds it, an edit on a sheet the operation does not change only when the edit
    // changes neither the engineering design nor a connection. The operation turns KiCad's sheet back into the synchronized
    // one, so a Remove is an object the user added, a Create one the user removed and an Update one the user changed.
    [TestMethod]
    public void OnlyEditsThatChangeNoDesignOrConnectionAreCarried()
    {
        var remove = new SchematicItemOperation { Remove = new() { Value = Id } };
        // Carried: notes, text boxes, drawings, images, tables, graphic lines, title block and page settings.
        foreach (IMessage item in new IMessage[] { new SchematicText { Id = new() { Value = Id } }, new SchematicTextBox { Id = new() { Value = Id } },
                     new SchematicGraphicShape { Id = new() { Value = Id } }, new SchematicImage { Id = new() { Value = Id } },
                     new SchematicTable { Id = new() { Value = Id } }, Line(SchematicLineType.SltGraphic) })
        {
            Assert.IsNull(ReleasedSheets.UncarriedEdit(remove, Live(item)), "added " + item.Descriptor.Name);
            Assert.IsNull(ReleasedSheets.UncarriedEdit(new() { Create = Any.Pack(item) }, Live()), "removed " + item.Descriptor.Name);
            Assert.IsNull(ReleasedSheets.UncarriedEdit(new() { Update = Any.Pack(item) }, Live(item)), "changed " + item.Descriptor.Name);
        }
        Assert.IsNull(ReleasedSheets.UncarriedEdit(new() { SetTitleBlock = new TitleBlockInfo { Title = "User title" } }, Live()));
        Assert.IsNull(ReleasedSheets.UncarriedEdit(new() { SetPageSettings = new PageSettings() }, Live()));

        // Not carried: anything that places, removes or connects a component, or changes shared settings.
        foreach (var (item, kind) in new (IMessage, string)[] {
                     (Line(SchematicLineType.SltWire), "wire"), (Line(SchematicLineType.SltBus), "bus"),
                     (new Junction { Id = new() { Value = Id } }, "junction"), (new NoConnectMarker { Id = new() { Value = Id } }, "no-connect marker"),
                     (new BusEntry { Id = new() { Value = Id } }, "bus entry"), (new LocalLabel { Id = new() { Value = Id } }, "label"),
                     (new GlobalLabel { Id = new() { Value = Id } }, "label"), (new HierarchicalLabel { Id = new() { Value = Id } }, "label"),
                     (new DirectiveLabel { Id = new() { Value = Id } }, "label"), (new SheetSymbol { Id = new() { Value = Id } }, "sheet"),
                     (new SchematicSymbolInstance { Id = new() { Value = Id } }, "symbol"), (new Group { Id = new() { Value = Id } }, "group"),
                     (new SchematicRuleArea { Id = new() { Value = Id } }, "rule area") })
        {
            Assert.AreEqual($"added {kind} {Id}", ReleasedSheets.UncarriedEdit(remove, Live(item)));
            Assert.AreEqual($"removed {kind} {Id}", ReleasedSheets.UncarriedEdit(new() { Create = Any.Pack(item) }, Live()));
            Assert.AreEqual($"changed {kind} {Id}", ReleasedSheets.UncarriedEdit(new() { Update = Any.Pack(item) }, Live(item)));
        }
        // A graphic line the user turned into a wire is a wire now.
        Assert.AreEqual($"changed wire {Id}", ReleasedSheets.UncarriedEdit(new() { Update = Any.Pack(Line(SchematicLineType.SltGraphic)) },
            Live(Line(SchematicLineType.SltWire))));
        Assert.AreEqual($"added an object {Id}", ReleasedSheets.UncarriedEdit(remove, Live()), "An object KiCad does not report is never carried.");
        Assert.AreEqual("changed the sheet's cached library symbols",
            ReleasedSheets.UncarriedEdit(new() { ReplaceLibraryCache = new() }, Live()));
        StringAssert.StartsWith(ReleasedSheets.UncarriedEdit(new() { SetErcSettings = new() }, Live()), "changed the sheet's setting");
    }

    // Resume refuses a publication that had started when KiCad ended. The refusal points to roll-back whenever roll-back works
    // (the XML file still holds the version the operation started from), and otherwise names what to put back, and where a
    // kept copy of it is.
    [TestMethod]
    public void StartedPublicationRefusalPointsToTheContinuationThatWorks()
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "lane-2D-release-dead-end", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string design = Path.Combine(folder, "design.xml");
            byte[] input = Encoding.UTF8.GetBytes("<design>input</design>"), result = Encoding.UTF8.GetBytes("<design>result</design>");
            byte[] user = Encoding.UTF8.GetBytes("<design>user</design>");
            string inputSha = DesignReleasedOperation.Sha(input);
            var staged = DesignPublicationIntent.Create(design, input, result) with { Phase = DesignPublicationPhase.Staged };
            string operation = staged.OperationId.ToString("D");

            string still = ExitedOperationRelease.PublicationStartedMessage(operation, staged, inputSha, input, input);
            StringAssert.Contains(still, "phase Staged");
            StringAssert.Contains(still, "Roll it back instead (continuation 'roll-back')");
            Assert.IsFalse(still.Contains("othing was changed", StringComparison.Ordinal), still);

            // The publication had replaced the file with the operation's result: put the version it started from back, which
            // the publication kept next to the file.
            var published = staged with { Phase = DesignPublicationPhase.Published };
            File.WriteAllBytes(published.PreviousPath, input);
            string replaced = ExitedOperationRelease.PublicationStartedMessage(operation, published, inputSha, input, result);
            StringAssert.Contains(replaced, "already holds operation " + operation + "'s own result");
            StringAssert.Contains(replaced, inputSha);
            StringAssert.Contains(replaced, "that version is kept at " + published.PreviousPath);
            StringAssert.Contains(replaced, "call again with continuation 'roll-back'");
            File.WriteAllBytes(published.PreviousPath, user);
            Assert.IsFalse(ExitedOperationRelease.PublicationStartedMessage(operation, published, inputSha, input, result).Contains("kept at", StringComparison.Ordinal),
                "A retained file that is not the version the operation started from is never named as it.");

            // Someone else's XML: roll-back would overwrite it.
            string changed = ExitedOperationRelease.RollbackFileMessage(operation, design, inputSha, published, user);
            StringAssert.Contains(changed, "changed after the recovery record last read it");
            StringAssert.Contains(changed, "Put that version of the XML back");
            Assert.IsFalse(changed.Contains("othing was changed", StringComparison.Ordinal), changed);

            // Resume after the XML changed: roll-back works only while the file is still the version the record holds.
            var prepared = DesignPublicationIntent.Create(design, input, result);
            StringAssert.Contains(ExitedOperationRelease.InputChangedMessage(operation, prepared, user, user), "Roll it back instead (continuation 'roll-back')");
            string elsewhere = ExitedOperationRelease.InputChangedMessage(operation, prepared, input, user);
            Assert.IsFalse(elsewhere.Contains("Roll it back instead", StringComparison.Ordinal), elsewhere);
            StringAssert.Contains(elsewhere, "Put that version back as " + design);
        }
        finally { Directory.Delete(folder, true); }
    }
}

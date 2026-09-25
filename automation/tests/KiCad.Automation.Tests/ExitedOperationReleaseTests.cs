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
// sheets refused. Three things it cannot reach are checked here: every kind of edit on such a sheet (the journey makes a note
// and a wire, not every object type); the advice of refusals that need a KiCad killed during the XML publication itself, or
// while the operation was still resolving its native layout (before it had a candidate), which the journey's kills (at the
// native commit and inside the native save) never produce; and a continuation that would reach beyond the operation's sheets,
// which a real comparison of the running KiCad with the operation keeps from happening.
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

    // Changes on the operation's own sheets are refused with what they may be and the way out. Without a final candidate (the
    // operation was still resolving its native layout when KiCad ended) nothing is known of its result, so they may be its
    // partial result or other edits, which cannot be told apart; closing without saving helps only while the interrupted save
    // had replaced no file. A continuation that would change a sheet the operation does not change is refused with a way out.
    [TestMethod]
    public void OperationSheetRefusalsSayWhatCannotBeToldApartAndTheWayOut()
    {
        string root = Guid.NewGuid().ToString("D"), psu = root + "/" + Guid.NewGuid().ToString("D"), cpu = root + "/" + Guid.NewGuid().ToString("D");
        var names = new Dictionary<string, string> { [root] = "the root sheet", [psu] = "sheet PSU", [cpu] = "sheet CPU" };
        var edited = new ReleasedSheets(root, [psu], [], [psu], [], [], names);
        var clean = new ReleasedSheets(root, [psu], [], [], [], [], names);
        string operation = Guid.NewGuid().ToString("D");
        string sheetFile = Path.Combine(Path.GetTempPath(), "lane-2D-stress-and-release-fixes", "psu.kicad_sch");
        var unchanged = new DesignReleasedFile(sheetFile, true, new string('a', 64), true, new string('a', 64));
        var replaced = unchanged with { Sha256Now = new string('b', 64) };

        var layout = Receipt(null, unchanged);
        var error = Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => edited.RequireOperationSheets(operation, layout));
        Assert.AreEqual("released_operation_sheets_edited", error.Code);
        StringAssert.Contains(error.Message, "no final XML candidate yet");
        StringAssert.Contains(error.Message, "the operation's partial result or other edits, which cannot be told apart");
        StringAssert.Contains(error.Message, "On sheet PSU (sheet path " + psu + ")");
        StringAssert.Contains(error.Message, "The way out: in KiCad, make those sheets as the last synchronization left them");
        StringAssert.Contains(error.Message, "close the schematic without saving and open it again");
        error = Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => edited.RequireOperationSheets(operation, Receipt(null, replaced)));
        Assert.AreEqual("released_operation_sheets_edited", error.Code);
        StringAssert.Contains(error.Message, "which cannot be told apart");
        StringAssert.Contains(error.Message, "closing the schematic without saving does not help here, because the interrupted save had already replaced " + sheetFile);
        Assert.IsFalse(error.Message.Contains("close the schematic without saving and open it again", StringComparison.Ordinal),
            "Reopening is never offered when it would load the partial result again: " + error.Message);
        clean.RequireOperationSheets(operation, layout);

        // With a final candidate the operation's result is known: the edits are neither it nor the synchronized version.
        var publication = DesignPublicationIntent.Create(Path.Combine(Path.GetTempPath(), "design.xml"), "<a/>"u8.ToArray(), "<b/>"u8.ToArray());
        error = Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => edited.RequireOperationSheets(operation, Receipt(publication, unchanged)));
        Assert.AreEqual("released_operation_sheets_edited", error.Code);
        StringAssert.Contains(error.Message, "neither as the last synchronization left them nor exactly as the operation left them");

        // The continuation's own change stays on the operation's sheets; an operation without a target edits the root.
        SchematicItemOperation Remove(string? sheet)
        {
            var remove = new SchematicItemOperation { Remove = new() { Value = Guid.NewGuid().ToString("D") } };
            if (sheet is not null)
            {
                remove.TargetDocument = new DocumentSpecifier { Type = DocumentType.DoctypeSchematic, SheetPath = new() };
                remove.TargetDocument.SheetPath.Path.Add(sheet.Split('/').Select(id => new KIID { Value = id }));
            }
            return remove;
        }
        clean.RequireWithinOperation([Remove(psu)]);
        foreach (var (outside, name) in new[] { (Remove(cpu), "sheet CPU (sheet path " + cpu + ")"), (Remove(null), "the root sheet (sheet path " + root + ")") })
        {
            error = Assert.ThrowsExactly<KiCad.Automation.Model.AutomationException>(() => clean.RequireWithinOperation([Remove(psu), outside]));
            Assert.AreEqual("released_operation_diverged", error.Code);
            StringAssert.Contains(error.Message, "The continuation would change " + name);
            StringAssert.Contains(error.Message, "Nothing was journaled. The way out: undo the edits in KiCad that changed those sheets");
            StringAssert.Contains(error.Message, "close the schematic without saving and open it again; then call this tool again");
        }
    }

    // A repeated call while a continuation waits reports the sheets it was planned from, compared again from the recovery
    // record's own observation of the running KiCad at the revision the continuation is guarded by. The native journey
    // cannot make that observation move on (refreshing or initializing the record is refused while work is pending), so the
    // unknown case is checked here: without that observation the lists are null, never empty, and the next step says why.
    [TestMethod]
    public void PlannedSheetsAreUnknownOnceTheRecordObservationMovedOn()
    {
        string epoch = Guid.NewGuid().ToString("D");
        var guarded = new DocumentRevision { Epoch = epoch, Sequence = 7 };
        var later = new DocumentRevision { Epoch = epoch, Sequence = 9 };
        var publication = DesignPublicationIntent.Create(Path.Combine(Path.GetTempPath(), "design.xml"), "<a/>"u8.ToArray(), "<b/>"u8.ToArray());
        var receipt = Receipt(publication, new DesignReleasedFile(Path.Combine(Path.GetTempPath(), "psu.kicad_sch"), true,
            new string('a', 64), true, new string('a', 64)));
        var design = new SchematicDesign(PsuCpuFixture.Engineering(PsuCpuStage.SheetsOnly), new(), [], []);
        DesignRecoveryState Waiting(DocumentRevision? observedAt, DocumentRevision? batchGuard) => new(Guid.NewGuid(), Guid.NewGuid(),
            new KiCad.Automation.Model.DocumentRevision(epoch, guarded.Sequence), true, design, [], new(), [],
            PendingMutation: batchGuard is null ? null : new ApplySchematicItemBatch { ExpectedRevision = batchGuard.Clone() },
            ObservedElectrical: observedAt is null ? null
                : new SchematicElectricalState { Hierarchy = new() { Data = new(), Revision = observedAt.Clone() } },
            PendingNativeState: new DocumentLifecycleState { ProcessEpoch = epoch, Revision = guarded.Clone() }, PendingPublication: publication);

        Assert.IsNull(ExitedOperationRelease.PlannedSheets(Waiting(null, null), receipt, default), "The record holds no observation.");
        Assert.IsNull(ExitedOperationRelease.PlannedSheets(Waiting(later, null), receipt, default),
            "The record's observation moved past the revision the continuation is guarded by.");
        Assert.IsNull(ExitedOperationRelease.PlannedSheets(Waiting(guarded, later), receipt, default),
            "The continuation's native edit is guarded by another revision than the observation.");
        string step = ExitedOperationRelease.PendingStep(publication, null);
        StringAssert.Contains(step, "Operation " + publication.OperationId.ToString("D") + " waits on the running KiCad");
        StringAssert.Contains(step, "Its sheet lists are unknown (null): the recovery record's observation of the running KiCad has moved on");
    }

    // A release receipt with the given publication (none: the operation was still resolving its native layout) whose save
    // named one sheet file.
    private static DesignReleasedOperation Receipt(DesignPublicationIntent? publication, DesignReleasedFile file)
    {
        string epoch = Guid.NewGuid().ToString("D");
        return new(DesignReleasedOperation.CurrentSchemaVersion, publication?.OperationId ?? Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), epoch,
            new InstanceExit(Guid.NewGuid().ToString("D"), epoch, 4242, null, 9, InstanceExit.ExitStatusEvidence, DateTimeOffset.UtcNow), "token",
            new KiCad.Automation.Model.DocumentRevision(Guid.NewGuid().ToString("D"), 3), new string('c', 64), new string('d', 64),
            null, new DocumentLifecycleState().ToByteArray(), null, null, publication, null, [file], DateTimeOffset.UtcNow);
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

using System.Collections.Immutable;
using System.Xml.Linq;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DiagramAnnotationTests
{
    private static DiagramAnnotation Comment(Guid target, string text = "Keep this unit replaceable.") => new(Guid.NewGuid(),
        DiagramAnnotationRole.Comment, text, new(DiagramAnnotationTargetKind.Block, target), null, [], RecursiveBlockFixture.Origin());

    private static RecursiveBlockGraph Save(RecursiveBlockGraph graph, ImmutableArray<DiagramAnnotation> notes)
    {
        var draft = graph.StartDraft(graph.SelectedRoot);
        draft = draft with { Diagram = draft.LocalDiagram with { Annotations = notes } };
        return graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot], draft, Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph;
    }

    [TestMethod]
    public void OriginalCommentsAndSketchesSurviveLaterEditsAndBothSerializationPaths()
    {
        var f = LinkedDiagramFixture.Create(); var graph = f.Graph;
        var note = Comment(f.Blocks["PSU"].BlockId, "User's original intent\r\n  Keep Ω unchanged. 🙂");
        var markup = new DiagramAnnotation(Guid.NewGuid(), DiagramAnnotationRole.Instruction, "Keep clear of this region.",
            new(DiagramAnnotationTargetKind.Canvas, null), new(40.5m, 80.125m),
            [new([new(10.125m, 20.5m), new(50.25m, 60.75m), new(70m, 30m)])], RecursiveBlockFixture.Origin() with
                { Sources = [new("user-sketch", "rev1", 1, null, null)], InputIds = [Guid.NewGuid()] });
        var link = new DiagramAnnotation(Guid.NewGuid(), DiagramAnnotationRole.Comment, "Keep interface choices open.",
            new(DiagramAnnotationTargetKind.Connection, f.Links["System/Power"].ConnectionId), null, [], RecursiveBlockFixture.Origin());
        var first = Save(graph, [note, markup, link]);
        var revised = note with { Text = "Clarified intent", Origin = RecursiveBlockFixture.Origin("Agent client") };
        var second = Save(first, [revised, markup, link]);
        Assert.AreEqual(note.Text, second.Inspect(first.SelectedRoot).LocalDiagram.Notes[0].Text);
        Assert.AreEqual(revised.Text, second.Inspect(second.SelectedRoot).LocalDiagram.Notes[0].Text);
        Assert.IsEmpty(second.Inspect(graph.SelectedRoot).LocalDiagram.Notes);
        string xml = RecursiveBlockGraphXml.Write(second);
        var fromXml = RecursiveBlockGraphXml.Read(xml);
        Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(fromXml));
        var fromBinary = RecursiveBlockCodec.Decode(P.RecursiveBlockGraphData.Parser.ParseFrom(RecursiveBlockCodec.Encode(second).ToByteArray()));
        Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(fromBinary));
        Assert.IsTrue(markup.SameContents(fromBinary.Inspect(fromBinary.SelectedRoot).LocalDiagram.Notes[1]));
        Assert.AreEqual("rev1", fromXml.Inspect(fromXml.SelectedRoot).LocalDiagram.Notes[1].Origin.Sources.Single().Revision);
        Assert.AreEqual(DiagramAnnotationRole.Comment, fromXml.Inspect(first.SelectedRoot).LocalDiagram.Notes[0].Role);
        Assert.AreSame(second, Save(second, second.Inspect(second.SelectedRoot).LocalDiagram.Notes));
    }

    [TestMethod]
    public void CoordinateFreeNotesAreValidAndCommentRoleDoesNotBecomeAnInstruction()
    {
        var graph = RecursiveBlockFixture.Create().Graph;
        var note = Comment(graph.SelectedRoot.BlockId);
        var free = note with { Id = Guid.NewGuid(), Target = new(DiagramAnnotationTargetKind.Canvas, null) };
        var saved = Save(graph, [note, free]);
        var loaded = RecursiveBlockGraphXml.Read(RecursiveBlockGraphXml.Write(saved));
        Assert.IsTrue(loaded.Inspect(loaded.SelectedRoot).LocalDiagram.Notes.All(n => n.Position is null && n.Strokes.IsEmpty));
        Assert.IsTrue(loaded.Inspect(loaded.SelectedRoot).LocalDiagram.Notes.All(n => n.Role == DiagramAnnotationRole.Comment));
    }

    [TestMethod]
    public void MissingTargetsNeedExplicitUnresolvedStateAndNeverGuessByName()
    {
        var f = RecursiveBlockFixture.Create(); var graph = f.Graph; var note = Comment(f.Selected["CPU"].BlockId);
        var first = Save(graph, [note]);
        var draft = first.StartDraft(first.SelectedRoot) with { Children = [f.Selected["PSU"]] };
        Assert.ThrowsExactly<AutomationException>(() => first.SaveDraft(first.SelectedRoot, [first.SelectedRoot], draft,
            Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()));
        var retained = note with { Target = note.Target with { UnresolvedReason = "The target occurrence was removed from this diagram." } };
        draft = draft with { Diagram = draft.LocalDiagram with { Annotations = [retained] } };
        var saved = first.SaveDraft(first.SelectedRoot, [first.SelectedRoot], draft,
            Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph;
        var loaded = RecursiveBlockGraphXml.Read(RecursiveBlockGraphXml.Write(saved));
        var originalTarget = loaded.Inspect(loaded.SelectedRoot).LocalDiagram.Notes.Single().Target;
        Assert.AreEqual(f.Selected["CPU"].BlockId, originalTarget.TargetId); Assert.IsNotNull(originalTarget.UnresolvedReason);
        Assert.IsTrue(note.SameContents(loaded.Inspect(first.SelectedRoot).LocalDiagram.Notes.Single()));
        Assert.ThrowsExactly<AutomationException>(() => Save(graph, [retained])); // It still exists here.
    }

    [TestMethod]
    public void ConnectionCommentsUpdateOnlyTheDiagramSnapshotWithoutInventingAnElectricalRevision()
    {
        var f = LinkedDiagramFixture.Create(); var graph = f.Graph; var cpu = f.Blocks["CPU"]; var link = f.Links["CPU/Memory"];
        var comment = new DiagramAnnotation(Guid.NewGuid(), DiagramAnnotationRole.Comment, "Leave pin choices open until placement.",
            new(DiagramAnnotationTargetKind.Connection, link.ConnectionId), null, [], RecursiveBlockFixture.Origin());
        var draft = graph.Connections(cpu.BlockId).StartDraft(link) with { DiagramAnnotations = [comment] };
        var decoded = RecursiveBlockCodec.Decode(RecursiveBlockCodec.Encode(draft), graph.DocumentId);
        Assert.IsTrue(comment.SameContents(decoded.DiagramAnnotations.Single()));
        var saved = graph.SaveConnectionDraft(graph.SelectedRoot, [graph.SelectedRoot, cpu], [link], decoded,
            Guid.NewGuid(), Guid.NewGuid(), [], Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
        var changedCpu = saved.Inspect(saved.SelectedRoot).Children[1];
        Assert.AreEqual(link, saved.Inspect(changedCpu).LocalDiagram.Connections[2]);
        Assert.IsTrue(comment.SameContents(saved.Inspect(changedCpu).LocalDiagram.Notes.Single()));
        Assert.IsEmpty(saved.Inspect(cpu).LocalDiagram.Notes);
        Assert.AreEqual(graph.Connections(cpu.BlockId).Revisions.Length, saved.Connections(cpu.BlockId).Revisions.Length);
        var unchanged = saved.Connections(cpu.BlockId).StartDraft(link) with { DiagramAnnotations = [comment] };
        Assert.IsFalse(saved.SaveConnectionDraft(saved.SelectedRoot, [saved.SelectedRoot, changedCpu], [link], unchanged,
            Guid.NewGuid(), Guid.NewGuid(), [], Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Changed);
    }

    [TestMethod]
    public void InvalidAliasesCrossDiagramReuseAndImpreciseSketchCoordinatesAreRejected()
    {
        var f = LinkedDiagramFixture.Create(); var graph = f.Graph; var note = Comment(f.Blocks["PSU"].BlockId);
        Assert.ThrowsExactly<AutomationException>(() => Save(graph, [note, note]));
        Assert.ThrowsExactly<AutomationException>(() => Save(graph, [note with { Id = graph.SelectedRoot.BlockId }]));
        Assert.ThrowsExactly<AutomationException>(() => Save(graph, [note with { Text = "", Strokes = [] }]));
        Assert.ThrowsExactly<AutomationException>(() => Save(graph, [note with { Role = (DiagramAnnotationRole)999 }]));
        Assert.ThrowsExactly<AutomationException>(() => Save(graph, [note with { Position = new(decimal.MaxValue, 0) }]));
        Assert.ThrowsExactly<AutomationException>(() => Save(graph, [note with { Strokes = [new([new(1, 2)])] }]));
        var initial = Save(graph, [note]); var cpu = initial.Inspect(initial.SelectedRoot).Children[1];
        var childDraft = initial.StartDraft(cpu);
        childDraft = childDraft with { Diagram = childDraft.LocalDiagram with { Annotations = [note with
            { Target = new(DiagramAnnotationTargetKind.Canvas, null), Position = new(10, 20) }] } };
        Assert.ThrowsExactly<AutomationException>(() => initial.SaveDraft(initial.SelectedRoot, [initial.SelectedRoot, cpu], childDraft,
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()));
        var positioned = Save(graph, [note with { Position = new(1m, 2m) }]);
        XNamespace ns = RecursiveBlockGraphXml.Namespace; // Every write is schema 2 (contract rbg-v2 R4).
        var xml = XElement.Parse(RecursiveBlockGraphXml.Write(positioned));
        xml.Descendants(ns + "position").Single().SetAttributeValue("x", "0.123456789012345678901234567891");
        Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockGraphXml.Read(xml.ToString()));
        var data = RecursiveBlockCodec.Encode(positioned);
        data.Revisions.Single(r => r.Selection.RevisionId == positioned.SelectedRoot.RevisionId.ToString("D")).LocalDiagram.Annotations[0].Position.X = "0.123456789012345678901234567891";
        Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockCodec.Decode(data));
        Assert.ThrowsExactly<AutomationException>(() => DiagramAnnotationPoint.Parse("1e5", "0"));
    }
}

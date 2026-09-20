using System.Collections.Immutable;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Tests;

internal sealed record LinkedDiagramFixture(RecursiveBlockGraph Graph, ImmutableDictionary<string, BlockSelection> Blocks,
    ImmutableDictionary<string, ConnectionSelection> Links, ImmutableDictionary<string, Guid> Ports)
{
    public static LinkedDiagramFixture Create()
    {
        var f = RecursiveBlockFixture.Create();
        var ports = new Dictionary<string, Guid>();
        var boundaries = new Dictionary<Guid, ImmutableArray<DiagramBoundaryInterface>>();
        void Boundaries(string name, params string[] names)
        {
            var items = ImmutableArray.CreateBuilder<DiagramBoundaryInterface>();
            foreach (string portName in names)
            { Guid id = Guid.NewGuid(); ports.Add(name + "/" + portName, id); items.Add(new(id, portName, "Test-only abstract interface.")); }
            boundaries.Add(f.Selected[name].BlockId, items.ToImmutable());
        }
        Boundaries("PSU", "Power", "Telemetry"); Boundaries("CPU", "Power", "Telemetry");
        Boundaries("Power stage", "Output"); Boundaries("Telemetry", "Control");
        Boundaries("Processor", "Power", "Control", "Memory"); Boundaries("Memory", "Data");
        var archives = new List<DiagramConnectionArchive>(); var roots = new Dictionary<Guid, ImmutableArray<ConnectionSelection>>();
        var links = ImmutableDictionary.CreateBuilder<string, ConnectionSelection>();
        DiagramEndpointBinding Endpoint(string port)
        {
            string name = port[..port.IndexOf('/')];
            return new(DiagramEndpointKind.Interface, f.Selected[name].BlockId, ports[port], "", null, [], null);
        }
        void Diagram(string owner, params (string Name, string First, string Second)[] definitions)
        {
            var states = new List<ConnectionDesignState>(); var revisions = new List<DiagramConnectionRevision>();
            var histories = new List<DiagramRequirementHistory>(); var selections = ImmutableArray.CreateBuilder<ConnectionSelection>();
            foreach (var definition in definitions)
            {
                Guid id = Guid.NewGuid(), state = Guid.NewGuid(), revision = Guid.NewGuid(), requirements = Guid.NewGuid();
                var selected = new ConnectionSelection(id, state, revision); selections.Add(selected); links.Add(owner + "/" + definition.Name, selected);
                states.Add(new(state, id, "Initial interface", revision));
                histories.Add(new(new(f.Graph.DocumentId, id, state), [new(requirements, null,
                    new("Test-only " + definition.Name, "", ""), RecursiveBlockFixture.Origin(), [])]));
                revisions.Add(new(selected, null, definition.Name, DiagramConnectionKind.Interface,
                    [Endpoint(definition.First), Endpoint(definition.Second)], requirements, [], RecursiveBlockFixture.Origin()));
            }
            archives.Add(new(f.Graph.DocumentId, f.Selected[owner].BlockId, states, revisions, histories));
            roots.Add(f.Selected[owner].BlockId, selections.ToImmutable());
        }
        Diagram("System", ("Power", "PSU/Power", "CPU/Power"), ("Telemetry", "PSU/Telemetry", "CPU/Telemetry"));
        Diagram("PSU", ("Supply", "Power stage/Output", "PSU/Power"), ("Telemetry", "Telemetry/Control", "PSU/Telemetry"));
        Diagram("CPU", ("Supply", "CPU/Power", "Processor/Power"), ("Control", "CPU/Telemetry", "Processor/Control"), ("Memory", "Processor/Memory", "Memory/Data"));
        var graph = new RecursiveBlockGraph(f.Graph.DocumentId, f.Graph.SelectedRoot, f.Graph.States,
            f.Graph.Revisions.Select(r => r with { Diagram = new(boundaries.GetValueOrDefault(r.Selection.BlockId, []), roots.GetValueOrDefault(r.Selection.BlockId, [])) }),
            f.Graph.RequirementHistories, archives);
        return new(graph, f.Selected, links.ToImmutable(), ports.ToImmutableDictionary());
    }
}

[TestClass]
public sealed class RecursiveBlockLocalDiagramTests
{
    [TestMethod]
    public void RootPsuAndCpuHaveSeparateConnectedDiagramsWithTheSameExactBoundaryIdentity()
    {
        var f = LinkedDiagramFixture.Create(); var graph = f.Graph;
        Assert.HasCount(2, graph.Inspect(graph.SelectedRoot).LocalDiagram.Connections);
        Assert.HasCount(2, graph.Inspect(f.Blocks["PSU"]).LocalDiagram.Connections);
        Assert.HasCount(3, graph.Inspect(f.Blocks["CPU"]).LocalDiagram.Connections);
        var outer = graph.Connections(graph.SelectedRoot.BlockId).Inspect(f.Links["System/Power"]);
        var inner = graph.Connections(f.Blocks["PSU"].BlockId).Inspect(f.Links["PSU/Supply"]);
        Assert.AreEqual(f.Ports["PSU/Power"], outer.Endpoints[0].InterfaceId);
        Assert.AreEqual(outer.Endpoints[0].InterfaceId, inner.Endpoints[1].InterfaceId);
        Assert.IsTrue(outer.Endpoints.All(e => e.Pin is null));
        string xml = RecursiveBlockGraphXml.Write(graph);
        var fromXml = RecursiveBlockGraphXml.Read(xml);
        Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(fromXml));
        var fromMessages = RecursiveBlockCodec.Decode(P.RecursiveBlockGraphData.Parser.ParseFrom(RecursiveBlockCodec.Encode(graph).ToByteArray()));
        Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(fromMessages));
    }

    [TestMethod]
    public void ChoosingAnImplementationWithAMissingParentInterfaceFailsWithoutChangingTheRoot()
    {
        var f = LinkedDiagramFixture.Create(); var graph = f.Graph;
        var cpu = f.Blocks["CPU"];
        var alternative = graph.States.Single(s => s.BlockId == cpu.BlockId && s.Id != cpu.StateId);
        var choice = new BlockSelection(cpu.BlockId, alternative.Id, alternative.HeadRevisionId);
        var modified = new RecursiveBlockGraph(graph.DocumentId, graph.SelectedRoot, graph.States,
            graph.Revisions.Select(r => r.Selection == choice ? r with { Diagram = BlockLocalDiagram.Empty } : r), graph.RequirementHistories, graph.ConnectionArchives);
        Assert.ThrowsExactly<AutomationException>(() => modified.Select(graph.SelectedRoot, [graph.SelectedRoot, cpu], choice, [Guid.NewGuid()], RecursiveBlockFixture.Origin()));
        Assert.AreEqual(cpu, modified.Inspect(modified.SelectedRoot).Children[1]);
        Assert.AreEqual(graph.SelectedRoot, modified.SelectedRoot);
        // A compatible alternative is allowed; the inactive one was never guessed by name.
        var selected = graph.Select(graph.SelectedRoot, [graph.SelectedRoot, cpu], choice, [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
        Assert.AreEqual(choice, selected.Inspect(selected.SelectedRoot).Children[1]);
        Assert.AreEqual(graph.Inspect(graph.SelectedRoot).LocalDiagram, selected.Inspect(selected.SelectedRoot).LocalDiagram);
    }

    [TestMethod]
    public void ConnectionEditsPublishWithTheirBlockAndAncestorsAndPreservePreviousWholeDesign()
    {
        var f = LinkedDiagramFixture.Create(); var graph = f.Graph; var cpu = f.Blocks["CPU"];
        var archive = graph.Connections(cpu.BlockId); var oldLink = f.Links["CPU/Memory"];
        var previous = archive.Inspect(oldLink);
        var changed = previous with { Selection = oldLink with { RevisionId = Guid.NewGuid() }, ParentRevisionId = oldLink.RevisionId,
            Endpoints = [previous.Endpoints[0] with { Kind = DiagramEndpointKind.Compatible, Selector = new("Memory data", "", [], []) },
                previous.Endpoints[1].Choose(new(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], "D15"))] };
        var appended = archive.AppendRevision(oldLink.RevisionId, changed);
        var prepared = graph.WithConnections(appended);
        var draft = prepared.StartDraft(cpu);
        draft = draft with { Diagram = draft.LocalDiagram with { Connections = draft.LocalDiagram.Connections.Select(c => c == oldLink ? changed.Selection : c).ToImmutableArray() } };
        var saved = prepared.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot, cpu], draft, Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
        var newCpu = saved.Inspect(saved.SelectedRoot).Children[1];
        Assert.AreEqual(changed.Selection, saved.Inspect(newCpu).LocalDiagram.Connections[2]);
        Assert.AreEqual(oldLink, saved.Inspect(cpu).LocalDiagram.Connections[2]);
        Assert.AreEqual(f.Blocks["PSU"], saved.Inspect(saved.SelectedRoot).Children[0]);
        Assert.IsNull(saved.Connections(cpu.BlockId).Inspect(oldLink).Endpoints[1].Pin);
        Assert.AreEqual("D15", saved.Connections(cpu.BlockId).Inspect(changed.Selection).Endpoints[1].Pin!.Pin);
        var loaded = RecursiveBlockGraphXml.Read(RecursiveBlockGraphXml.Write(saved));
        CollectionAssert.AreEqual(graph.Walk(graph.SelectedRoot).ToArray(), loaded.Walk(graph.SelectedRoot).ToArray());
        Assert.AreEqual(oldLink, loaded.Inspect(cpu).LocalDiagram.Connections[2]);
        Assert.AreEqual(RecursiveBlockGraphXml.Write(saved), RecursiveBlockGraphXml.Write(RecursiveBlockCodec.Decode(RecursiveBlockCodec.Encode(saved))));
    }

    [TestMethod]
    public void CrossLevelTargetsUnknownPortsAndRewrittenArchiveHistoryAreRejected()
    {
        var f = LinkedDiagramFixture.Create(); var graph = f.Graph; var owner = f.Blocks["CPU"].BlockId;
        var archive = graph.Connections(owner); var link = f.Links["CPU/Memory"]; var previous = archive.Inspect(link);
        DiagramConnectionArchive Replace(DiagramConnectionRevision revision) => new(archive.DocumentId, archive.OwnerBlockId,
            archive.States, archive.Revisions.Select(r => r.Selection == link ? revision : r), archive.RequirementHistories);
        Assert.ThrowsExactly<AutomationException>(() => graph.WithConnections(Replace(previous with { Name = "Rewritten past" })));
        var bad = previous with { Selection = link with { RevisionId = Guid.NewGuid() }, ParentRevisionId = link.RevisionId,
            Endpoints = [DiagramEndpointBinding.Unknown(f.Blocks["Regulator"].BlockId), previous.Endpoints[1]] };
        var prepared = graph.WithConnections(archive.AppendRevision(link.RevisionId, bad));
        var draft = prepared.StartDraft(f.Blocks["CPU"]);
        draft = draft with { Diagram = draft.LocalDiagram with { Connections = draft.LocalDiagram.Connections.SetItem(2, bad.Selection) } };
        Assert.ThrowsExactly<AutomationException>(() => prepared.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot, f.Blocks["CPU"]], draft,
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()));
        var port = previous with { Selection = link with { RevisionId = Guid.NewGuid() }, ParentRevisionId = link.RevisionId,
            Endpoints = [previous.Endpoints[0] with { InterfaceId = Guid.NewGuid() }, previous.Endpoints[1]] };
        prepared = graph.WithConnections(archive.AppendRevision(link.RevisionId, port));
        draft = prepared.StartDraft(f.Blocks["CPU"]);
        draft = draft with { Diagram = draft.LocalDiagram with { Connections = draft.LocalDiagram.Connections.SetItem(2, port.Selection) } };
        Assert.ThrowsExactly<AutomationException>(() => prepared.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot, f.Blocks["CPU"]], draft,
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()));
        var reloaded = DiagramConnectionArchiveXml.Read(DiagramConnectionArchiveXml.Write(archive));
        Assert.AreEqual(RecursiveBlockGraphXml.Write(graph), RecursiveBlockGraphXml.Write(graph.WithConnections(reloaded)));
    }

    [TestMethod]
    public void SavingConnectionRequirementsCreatesOneCoherentRootAndLeavesOtherRelationsUntouched()
    {
        var f = LinkedDiagramFixture.Create(); var graph = f.Graph; var cpu = f.Blocks["CPU"];
        var link = f.Links["CPU/Memory"]; var archive = graph.Connections(cpu.BlockId);
        var draft = archive.StartDraft(link);
        draft = draft with { Requirements = draft.Requirements.Edit(DiagramRequirementField.Routing, "Keep this interface clear of the heat sink.") };
        var saved = graph.SaveConnectionDraft(graph.SelectedRoot, [graph.SelectedRoot, cpu], [link], draft,
            Guid.NewGuid(), Guid.NewGuid(), [], Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()).Graph;
        var newCpu = saved.Inspect(saved.SelectedRoot).Children[1];
        var newLink = saved.Inspect(newCpu).LocalDiagram.Connections[2];
        Assert.AreEqual("Keep this interface clear of the heat sink.", saved.Connections(cpu.BlockId).Requirements(newLink).Requirements.Routing);
        Assert.AreEqual("", saved.Connections(cpu.BlockId).Requirements(link).Requirements.Routing);
        Assert.AreEqual(f.Links["CPU/Supply"], saved.Inspect(newCpu).LocalDiagram.Connections[0]);
        Assert.AreEqual(cpu, saved.Inspect(graph.SelectedRoot).Children[1]);
        Assert.AreEqual(f.Blocks["PSU"], saved.Inspect(saved.SelectedRoot).Children[0]);
        Assert.AreEqual(graph.Revisions.Length + 2, saved.Revisions.Length);
        Assert.AreEqual(archive.Revisions.Length + 1, saved.Connections(cpu.BlockId).Revisions.Length);
        var noOp = saved.SaveConnectionDraft(saved.SelectedRoot, [saved.SelectedRoot, newCpu], [newLink],
            saved.Connections(cpu.BlockId).StartDraft(newLink), Guid.NewGuid(), Guid.NewGuid(), [], Guid.NewGuid(), Guid.NewGuid(),
            [Guid.NewGuid()], RecursiveBlockFixture.Origin());
        Assert.IsFalse(noOp.Changed); Assert.AreSame(saved, noOp.Graph);
        Assert.ThrowsExactly<AutomationException>(() => saved.SaveConnectionDraft(graph.SelectedRoot, [graph.SelectedRoot, cpu], [link], draft,
            Guid.NewGuid(), Guid.NewGuid(), [], Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()));
    }

    [TestMethod]
    public void ConnectionDraftCannotRedirectIntoAnotherDiagramOrUseForgedRequirementBaseline()
    {
        var f = LinkedDiagramFixture.Create(); var graph = f.Graph; var cpu = f.Blocks["CPU"];
        var link = f.Links["CPU/Memory"]; var draft = graph.Connections(cpu.BlockId).StartDraft(link);
        Assert.ThrowsExactly<AutomationException>(() => graph.SaveConnectionDraft(graph.SelectedRoot, [graph.SelectedRoot, f.Blocks["PSU"]], [link], draft,
            Guid.NewGuid(), Guid.NewGuid(), [], Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()));
        var forged = draft with { Requirements = draft.Requirements with { Baseline = draft.Requirements.Baseline with
            { Requirements = new("Forged previous requirements", "", "") } } };
        Assert.ThrowsExactly<AutomationException>(() => graph.SaveConnectionDraft(graph.SelectedRoot, [graph.SelectedRoot, cpu], [link], forged,
            Guid.NewGuid(), Guid.NewGuid(), [], Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin()));
        Assert.AreEqual(link, graph.Inspect(cpu).LocalDiagram.Connections[2]);
    }

    [TestMethod]
    public async Task FileSaveIncludesConnectionArchiveAndPinnedRootInOneGuardedWrite()
    {
        string root = Directory.CreateTempSubdirectory("kicad-linked-diagram-").FullName;
        try
        {
            var f = LinkedDiagramFixture.Create(); var graph = f.Graph; var cpu = f.Blocks["CPU"];
            string path = Path.Combine(root, "design.xml"); await File.WriteAllTextAsync(path, RecursiveBlockGraphXml.Write(graph));
            var baseline = await RecursiveBlockFiles.ReadAsync(root, path, graph.DocumentId);
            var archive = graph.Connections(cpu.BlockId); var oldLink = f.Links["CPU/Memory"]; var previous = archive.Inspect(oldLink);
            var next = previous with { Selection = oldLink with { RevisionId = Guid.NewGuid() }, ParentRevisionId = oldLink.RevisionId, Name = "Refined memory interface" };
            var updated = archive.AppendRevision(oldLink.RevisionId, next);
            var draft = graph.StartDraft(cpu);
            await Assert.ThrowsExactlyAsync<AutomationException>(() => RecursiveBlockFiles.SaveDraftAsync(root, path, graph.DocumentId, baseline.ContentSha256,
                graph.SelectedRoot, [graph.SelectedRoot, cpu], draft, Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin(), connectionArchives: [updated]));
            Assert.AreEqual(baseline.ContentSha256, (await RecursiveBlockFiles.ReadAsync(root, path, graph.DocumentId)).ContentSha256);
            draft = draft with { Diagram = draft.LocalDiagram with { Connections = draft.LocalDiagram.Connections.SetItem(2, next.Selection) } };
            var saved = await RecursiveBlockFiles.SaveDraftAsync(root, path, graph.DocumentId, baseline.ContentSha256,
                graph.SelectedRoot, [graph.SelectedRoot, cpu], draft, Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin(), connectionArchives: [updated]);
            var reopened = await RecursiveBlockFiles.ReadAsync(root, path, graph.DocumentId);
            Assert.AreEqual(saved.ContentSha256, reopened.ContentSha256);
            var newCpu = reopened.Graph.Inspect(reopened.Graph.SelectedRoot).Children[1];
            Assert.AreEqual(next.Selection, reopened.Graph.Inspect(newCpu).LocalDiagram.Connections[2]);
            Assert.AreEqual("Refined memory interface", reopened.Graph.Connections(cpu.BlockId).Inspect(next.Selection).Name);
            Assert.AreEqual("Memory", reopened.Graph.Connections(cpu.BlockId).Inspect(oldLink).Name);
        }
        finally { Directory.Delete(root, true); }
    }
}

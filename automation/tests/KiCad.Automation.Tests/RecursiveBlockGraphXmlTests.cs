using System.Xml.Linq;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveBlockGraphXmlTests
{
    // Every write is schema 2 (contract rbg-v2 R4), also for a graph without schema 2 facts.
    private static readonly XNamespace Ns = RecursiveBlockGraphXml.Namespace;

    [TestMethod]
    public void RoundTripPreservesSelectedAndHistoricTreesAlternativesAndRequirementText()
    {
        var f = RecursiveBlockFixture.Create(); var graph = f.Graph;
        var draft = graph.StartDraft(f.Selected["PSU"]);
        draft = draft with { Name = "PSU Δ\r\n  ", Requirements = draft.Requirements.Edit(DiagramRequirementField.Routing, "Keep\r\n  sensing quiet. \t🙂") };
        var origin = RecursiveBlockFixture.Origin("Any agent") with
        { Sources = [new("user-brief", "revision 2", 3, "Table A", "Industrial")], InputIds = [Guid.NewGuid()] };
        var changed = graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot, f.Selected["PSU"]], draft,
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], origin).Graph;
        string xml = RecursiveBlockGraphXml.Write(changed);
        var loaded = RecursiveBlockGraphXml.Read(xml);
        Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(loaded));
        Assert.AreEqual(changed.SelectedRoot, loaded.SelectedRoot);
        CollectionAssert.AreEqual(changed.Walk(graph.SelectedRoot).ToArray(), loaded.Walk(graph.SelectedRoot).ToArray());
        CollectionAssert.AreEqual(changed.Walk(changed.SelectedRoot).ToArray(), loaded.Walk(loaded.SelectedRoot).ToArray());
        var psu = loaded.Inspect(loaded.SelectedRoot).Children[0];
        Assert.AreEqual(draft.Name, loaded.Inspect(psu).Name);
        Assert.AreEqual(draft.Requirements.Requirements, loaded.Requirements(psu).Requirements);
        Assert.AreEqual("", loaded.Requirements(f.Selected["PSU"]).Requirements.Routing);
        Assert.AreEqual("Table A", loaded.Inspect(psu).Origin.Sources.Single().Table);
        CollectionAssert.AreEqual(origin.InputIds.ToArray(), loaded.Inspect(psu).Origin.InputIds.ToArray());
        Assert.HasCount(2, loaded.History(psu.StateId));
        Assert.HasCount(1, loaded.History(f.Alternatives["PSU"].StateId));
    }

    [TestMethod]
    public void StrictSchemaRejectsUnknownFieldsVersionsMalformedOriginsAndWrongReferences()
    {
        var graph = RecursiveBlockFixture.Create().Graph;
        string xml = RecursiveBlockGraphXml.Write(graph);
        void Reject(Action<XElement> change)
        {
            var root = XElement.Parse(xml, LoadOptions.PreserveWhitespace); change(root);
            Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockGraphXml.Read(root.ToString(SaveOptions.DisableFormatting)));
        }
        Assert.AreEqual(Ns + "recursive-block-graph", XElement.Parse(xml).Name);
        Reject(r => r.SetAttributeValue("version", 1));
        // A version 1 document (the frozen fixture format) cannot claim version 2 either.
        var v1 = XElement.Parse(RecursiveBlockGraphXml.Write(graph, 1), LoadOptions.PreserveWhitespace);
        Assert.AreEqual(XName.Get("recursive-block-graph", RecursiveBlockGraphXml.NamespaceV1), v1.Name);
        v1.SetAttributeValue("version", 2);
        Assert.AreEqual("invalid_recursive_block_graph_xml", Assert.ThrowsExactly<AutomationException>(() =>
            RecursiveBlockGraphXml.Read(v1.ToString(SaveOptions.DisableFormatting))).Code);
        Reject(r => r.SetAttributeValue("unknown", "must not drop"));
        Reject(r => r.Add(new XElement(Ns + "future-field", "must not drop")));
        Reject(r => r.Element(Ns + "selected-root")!.SetAttributeValue("revision", Guid.NewGuid()));
        Reject(r => r.Descendants(Ns + "child").First().SetAttributeValue("state", Guid.NewGuid()));
        Reject(r => r.Descendants(Ns + "revision").First().SetAttributeValue("requirements", Guid.NewGuid()));
        Reject(r => r.Descendants(Ns + "origin").First().SetAttributeValue("actor-kind", "Invented"));
        Reject(r => r.Descendants(Ns + "origin").First().SetAttributeValue("at", "2026-09-19T12:00:00+01:00"));
        Reject(r => r.Element(Ns + "requirement-histories")!.Elements().First().Remove());
        Reject(r => r.Element(Ns + "states")!.Add(new XElement(r.Element(Ns + "states")!.Elements().First())));
        Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockGraphXml.Read("<!DOCTYPE graph [<!ENTITY x 'unsafe'>]>" + xml));
    }

    [TestMethod]
    public void InputOrderCannotChangePinnedHierarchyOrCauseFileChurn()
    {
        var graph = RecursiveBlockFixture.Create().Graph;
        var shuffled = new RecursiveBlockGraph(graph.DocumentId, graph.SelectedRoot,
            graph.States.Reverse(), graph.Revisions.Reverse(), graph.RequirementHistories.Reverse());
        Assert.AreEqual(RecursiveBlockGraphXml.Write(graph), RecursiveBlockGraphXml.Write(shuffled));
        CollectionAssert.AreEqual(graph.Inspect(graph.SelectedRoot).Children.ToArray(), shuffled.Inspect(shuffled.SelectedRoot).Children.ToArray());
    }
}

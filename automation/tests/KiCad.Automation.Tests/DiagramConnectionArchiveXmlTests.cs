using System.Xml.Linq;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DiagramConnectionArchiveXmlTests
{
    [TestMethod]
    public void RoundTripPreservesAbstractAndDetailedHistoriesAndIndependentlyResolvedEndpoints()
    {
        var f = DiagramConnectionFixture.Create(); var archive = f.Archive; var selection = f.Selected["Data+"];
        var initial = archive.Inspect(selection);
        var pin = new DiagramPinTarget(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()], "D15");
        var selector = new DiagramPinSelector("Data", "", ["Bidirectional I/O"], [new("source brief", "rev2", 3, "Table A", "variant 2")]);
        var processor = new DiagramEndpointBinding(DiagramEndpointKind.Compatible, initial.Endpoints[0].BlockId, Guid.NewGuid(),
            "Choose a compatible pin.\r\n  Preserve intent Ω🙂", selector, [], null);
        var memory = new DiagramEndpointBinding(DiagramEndpointKind.Candidates, initial.Endpoints[1].BlockId, Guid.NewGuid(),
            "Memory side", selector, [pin, pin with { Pin = "D14" }], null).Choose(pin);
        var requirements = archive.RequirementHistories.Single(h => h.Scope.DesignStateId == selection.StateId);
        requirements = requirements.Commit(requirements.Current.Id, requirements.StartDraft().Edit(DiagramRequirementField.Routing,
            "Place this member carefully.\r\n  Text stays exact."), Guid.NewGuid(), RecursiveBlockFixture.Origin()).History;
        var origin = RecursiveBlockFixture.Origin("Any agent") with { Sources = selector.Sources, InputIds = [Guid.NewGuid()] };
        var revision = initial with { Selection = selection with { RevisionId = Guid.NewGuid() }, ParentRevisionId = selection.RevisionId,
            RequirementRevisionId = requirements.Current.Id, Endpoints = [processor, memory], Origin = origin };
        var changed = archive.AppendRevision(selection.RevisionId, revision, requirements);
        string xml = DiagramConnectionArchiveXml.Write(changed);
        var loaded = DiagramConnectionArchiveXml.Read(xml);
        Assert.AreEqual(xml, DiagramConnectionArchiveXml.Write(loaded));
        var endpoints = loaded.Inspect(revision.Selection).Endpoints;
        Assert.AreEqual(DiagramEndpointKind.Compatible, endpoints[0].Kind); Assert.IsNull(endpoints[0].Pin);
        Assert.AreEqual(processor.Intent, endpoints[0].Intent); Assert.AreEqual("", endpoints[0].Selector!.Protocol);
        Assert.AreEqual(DiagramEndpointKind.Pin, endpoints[1].Kind); Assert.IsTrue(pin.SamePin(endpoints[1].Pin!));
        Assert.HasCount(2, endpoints[1].Candidates); Assert.AreEqual("Table A", endpoints[1].Selector!.Sources.Single().Table);
        Assert.IsNull(loaded.Inspect(selection).Endpoints[1].Pin);
        Assert.HasCount(2, loaded.History(selection.StateId));
        Assert.AreEqual(requirements.Current.Requirements, loaded.Requirements(revision.Selection).Requirements);
        Assert.AreEqual("", loaded.Requirements(selection).Requirements.Routing);
        CollectionAssert.AreEqual(archive.Walk([f.Alternatives["Memory interface"]]).ToArray(), loaded.Walk([f.Alternatives["Memory interface"]]).ToArray());
    }

    [TestMethod]
    public void StrictXmlRejectsUnknownFieldsInconsistentEndpointStatesAndMissingMembers()
    {
        var archive = DiagramConnectionFixture.Create().Archive;
        string xml = DiagramConnectionArchiveXml.Write(archive); XNamespace ns = DiagramConnectionArchiveXml.Namespace;
        // Contract rbg-v2 section 5: the archive writer emits schema 2 and the reader also accepts the frozen schema 1.
        Assert.AreEqual(ns + "connection-archive", XElement.Parse(xml).Name);
        string v1 = xml.Replace(DiagramConnectionArchiveXml.Namespace, DiagramConnectionArchiveXml.NamespaceV1, StringComparison.Ordinal)
            .Replace("version=\"2\"", "version=\"1\"", StringComparison.Ordinal);
        Assert.AreEqual(xml, DiagramConnectionArchiveXml.Write(DiagramConnectionArchiveXml.Read(v1)), "A version 1 archive is read and written as version 2.");
        void Reject(Action<XElement> change)
        {
            var root = XElement.Parse(xml, LoadOptions.PreserveWhitespace); change(root);
            Assert.ThrowsExactly<AutomationException>(() => DiagramConnectionArchiveXml.Read(root.ToString(SaveOptions.DisableFormatting)));
        }
        Reject(r => r.SetAttributeValue("version", 1));
        Reject(r => r.SetAttributeValue("unknown", "must not lose"));
        Reject(r => r.Add(new XElement(ns + "future-field", "must not lose")));
        Reject(r => r.Descendants(ns + "revision").First().SetAttributeValue("kind", "FutureType"));
        Reject(r => r.Descendants(ns + "endpoint").First().SetAttributeValue("kind", "Pin"));
        Reject(r => r.Descendants(ns + "endpoint").First().SetAttributeValue("block", Guid.Empty));
        Reject(r => r.Descendants(ns + "member").First().SetAttributeValue("revision", Guid.NewGuid()));
        Reject(r => r.Descendants(ns + "endpoint").First().Element(ns + "candidates")!.Remove());
        Reject(r => r.Element(ns + "requirement-histories")!.Elements().First().Remove());
        Reject(r => r.Descendants(ns + "origin").First().SetAttributeValue("at", "2026-09-19T12:00:00+02:00"));
        Assert.ThrowsExactly<AutomationException>(() => DiagramConnectionArchiveXml.Read("<!DOCTYPE graph [<!ENTITY x 'unsafe'>]>" + xml));
    }

    [TestMethod]
    public void EmptyArchiveAndDifferentInputOrderingAreStableWithoutInventingConnections()
    {
        var empty = new DiagramConnectionArchive(Guid.NewGuid(), Guid.NewGuid(), [], [], []);
        var loaded = DiagramConnectionArchiveXml.Read(DiagramConnectionArchiveXml.Write(empty));
        Assert.IsEmpty(loaded.Revisions); Assert.IsEmpty(loaded.Walk([]));
        var archive = DiagramConnectionFixture.Create().Archive;
        var reordered = new DiagramConnectionArchive(archive.DocumentId, archive.OwnerBlockId,
            archive.States.Reverse(), archive.Revisions.Reverse(), archive.RequirementHistories.Reverse());
        Assert.AreEqual(DiagramConnectionArchiveXml.Write(archive), DiagramConnectionArchiveXml.Write(reordered));
    }
}

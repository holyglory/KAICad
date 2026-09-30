using System.Xml.Linq;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DiagramRequirementHistoryXmlTests
{
    private static readonly XNamespace Ns = DiagramRequirementHistoryXml.Namespace;
    private static DiagramRequirementHistory Fixture()
    {
        var origin = new RequirementRevisionOrigin(RequirementRevisionActor.User, "Fixture user",
            new DateTimeOffset(2026, 9, 19, 12, 34, 56, TimeSpan.Zero).AddTicks(1234567), "Original\r\nintent",
            [new SourceReference("requirements & notes", "rev1", 2, "Table A", "variant B")], [Guid.NewGuid()]);
        var first = new DiagramRequirementHistory(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            [new(Guid.NewGuid(), null, new("  Preserve\r\nthis text Ω 第二行  ", "", "Keep power short"), origin, [])]);
        var second = first.Commit(first.Current.Id, first.StartDraft().Edit(DiagramRequirementField.Routing, "Keep sensing quiet"),
            Guid.NewGuid(), origin with { ActorKind = RequirementRevisionActor.Agent, Actor = "Fixture agent", Summary = "Refined routing" }).History;
        return second.Commit(second.Current.Id, second.RestoreField(second.StartDraft(), first.Current.Id, DiagramRequirementField.Routing),
            Guid.NewGuid(), origin with { Summary = "Restore one field" }).History;
    }

    [TestMethod]
    public void TypedHistoryPreservesEveryRevisionSourceAndUserTextWithoutFileChurn()
    {
        var original = Fixture(); string xml = DiagramRequirementHistoryXml.Write(original);
        var restored = DiagramRequirementHistoryXml.Read(xml);
        Assert.AreEqual(original.Scope, restored.Scope); Assert.HasCount(3, restored.Revisions);
        for (int i = 0; i < original.Revisions.Length; i++)
        {
            var expected = original.Revisions[i]; var actual = restored.Revisions[i];
            Assert.AreEqual(expected.Id, actual.Id); Assert.AreEqual(expected.ParentId, actual.ParentId);
            Assert.AreEqual(expected.Requirements, actual.Requirements);
            Assert.AreEqual(expected.Origin.ActorKind, actual.Origin.ActorKind); Assert.AreEqual(expected.Origin.Actor, actual.Origin.Actor);
            Assert.AreEqual(expected.Origin.RecordedAt, actual.Origin.RecordedAt); Assert.AreEqual(expected.Origin.Summary, actual.Origin.Summary);
            CollectionAssert.AreEqual(expected.Origin.Sources.ToArray(), actual.Origin.Sources.ToArray());
            CollectionAssert.AreEqual(expected.Origin.InputIds.ToArray(), actual.Origin.InputIds.ToArray());
            CollectionAssert.AreEqual(expected.Restorations.ToArray(), actual.Restorations.ToArray());
        }
        Assert.AreEqual(xml, DiagramRequirementHistoryXml.Write(restored));
        Assert.AreEqual(xml, DiagramRequirementHistoryXml.Write(DiagramRequirementHistoryXml.Read("\uFEFF" + xml)));
        Assert.IsTrue(xml.Contains("<general>", StringComparison.Ordinal));
        Assert.IsFalse(xml.Contains("base64", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UnsupportedAndUnknownPersistedFieldsAreRejectedInsteadOfDropped()
    {
        string xml = DiagramRequirementHistoryXml.Write(Fixture());
        foreach (Action<XElement> change in new Action<XElement>[]
        {
            root => root.SetAttributeValue("version", 2),
            root => root.SetAttributeValue("future-field", "must not disappear"),
            root => root.Add(new XElement(Ns + "future-content", "keep me")),
            root => root.Element(Ns + "revision")!.Element(Ns + "requirements")!.Add(new XElement(Ns + "future-requirement", "preserve")),
            root => root.Element(Ns + "revision")!.Element(Ns + "requirements")!.Element(Ns + "general")!.Remove(),
            root => root.Element(Ns + "revision")!.Element(Ns + "origin")!.SetAttributeValue("actor-kind", "99"),
            root => root.Elements(Ns + "revision").Last().Element(Ns + "restored-field")!.SetAttributeValue("field", "Placement"),
        })
        {
            var root = XElement.Parse(xml); change(root);
            Assert.ThrowsExactly<AutomationException>(() => DiagramRequirementHistoryXml.Read(root.ToString()));
        }
        Assert.ThrowsExactly<AutomationException>(() => DiagramRequirementHistoryXml.Read(xml.Replace(DiagramRequirementHistoryXml.Namespace,
            "urn:kicad:automation:requirement-history:2", StringComparison.Ordinal)));
        Assert.AreEqual(xml, DiagramRequirementHistoryXml.Write(DiagramRequirementHistoryXml.Read(xml)));
    }

    [TestMethod]
    public void InvalidIdentityHistoryAndFalseRestorationDoNotPartiallyImport()
    {
        string xml = DiagramRequirementHistoryXml.Write(Fixture());
        foreach (Action<XElement> change in new Action<XElement>[]
        {
            root => root.SetAttributeValue("document", Guid.Empty.ToString("D")),
            root => root.SetAttributeValue("owner", "not-an-identity"),
            root => root.Elements(Ns + "revision").Skip(1).First().SetAttributeValue("parent", Guid.NewGuid()),
            root => root.Elements(Ns + "revision").Last().SetAttributeValue("id", root.Elements(Ns + "revision").First().Attribute("id")!.Value),
            root => root.Elements(Ns + "revision").Last().Element(Ns + "restored-field")!.SetAttributeValue("source-revision", Guid.NewGuid()),
            root => root.Elements(Ns + "revision").Last().Element(Ns + "requirements")!.Element(Ns + "routing")!.SetValue("Not the retained value"),
            root => root.Element(Ns + "revision")!.Element(Ns + "origin")!.Element(Ns + "source")!.SetAttributeValue("page", "99999999999999999999999"),
            root => root.Element(Ns + "revision")!.Element(Ns + "origin")!.SetAttributeValue("at", "2026-09-19T12:00:00+01:00"),
        })
        {
            var root = XElement.Parse(xml); change(root);
            Assert.ThrowsExactly<AutomationException>(() => DiagramRequirementHistoryXml.Read(root.ToString()));
        }
    }

    [TestMethod]
    public void UtcXmlTimesAreAcceptedAndDocumentDeclarationsCannotResolveEntities()
    {
        var root = XElement.Parse(DiagramRequirementHistoryXml.Write(Fixture()));
        root.Element(Ns + "revision")!.Element(Ns + "origin")!.SetAttributeValue("at", "2026-09-19T12:00:00Z");
        Assert.AreEqual(TimeSpan.Zero, DiagramRequirementHistoryXml.Read(root.ToString()).Revisions[0].Origin.RecordedAt.Offset);
        string input = "<!DOCTYPE requirement-history [<!ENTITY external SYSTEM 'file:///does-not-exist'>]>" + root;
        Assert.ThrowsExactly<AutomationException>(() => DiagramRequirementHistoryXml.Read(input));
    }
}

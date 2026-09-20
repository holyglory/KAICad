using System.Collections.Immutable;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveBlockDefinitionTests
{
    internal static BlockDefinition Partial() => new(
        Purpose: Choice(DefinitionChoiceState.Selected, ["Supply the controller without selecting rails yet."]),
        Type: Choice(DefinitionChoiceState.Candidates, ["LDO", "DC-DC converter"], GuidanceStrength.Preference),
        Model: Choice(DefinitionChoiceState.Unknown, [], reason: "Choose after evaluating load and sources."),
        Package: Choice(DefinitionChoiceState.Selected, ["Thermally accessible package"], GuidanceStrength.Requirement) with
        { Applicability = "For the removable assembly", Sources = [new("mechanical-requirement", "r1", null, null, null)] },
        KnowledgeClass: new(DefinitionChoiceState.Selected,
            [new(Guid.Parse("af3b6ac0-c36d-4e1e-a9af-cfbc51f1a96e"), "r2", Guid.Parse("8c724780-c44f-44a6-8909-7d97a6c467f2"))],
            GuidanceStrength.Information, "", []));

    internal static DefinitionChoice<string> Choice(DefinitionChoiceState state, ImmutableArray<string> values,
        GuidanceStrength strength = GuidanceStrength.Information, string? reason = null) => new(state, values, strength, "", [], UnknownReason: reason);

    private static RecursiveBlockGraph Save(RecursiveBlockGraph graph, BlockDefinition? definition)
    {
        var draft = graph.StartDraft(graph.SelectedRoot) with { Definition = definition };
        return graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot], draft, Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph;
    }

    [TestMethod]
    public void PackageAndClassCanBeSelectedWhileModelAndElectricalValuesRemainUnknown()
    {
        var definition = Partial(); definition.Validate();
        Assert.AreEqual(DefinitionChoiceState.Unknown, definition.Model!.State);
        Assert.IsEmpty(definition.Model.Values);
        Assert.AreEqual(DefinitionChoiceState.Selected, definition.Package!.State);
        Assert.AreEqual(GuidanceStrength.Requirement, definition.Package.Strength);
        Assert.AreEqual(GuidanceStrength.Preference, definition.Type!.Strength);
        Assert.AreEqual(VerificationState.Unverified, definition.Package.Verification);
        Assert.AreEqual(DefinitionChoiceState.Unspecified, definition.Get(BlockDefinitionFacet.Manufacturer).State);
        Assert.AreEqual(DefinitionChoiceState.Unspecified, definition.Get(BlockDefinitionFacet.OrderablePart).State);
        Assert.AreEqual("r2", definition.KnowledgeClass!.Values.Single().LibraryRevision);
    }

    [TestMethod]
    public void StrictXmlAndSharedMessagesKeepEveryFacetSourceConditionAndUnknownReason()
    {
        var definition = Partial() with
        {
            Manufacturer = Choice(DefinitionChoiceState.Candidates, ["Fixture manufacturer A", "Fixture manufacturer B"]),
            Family = Choice(DefinitionChoiceState.Selected, ["Fixture family"]),
            OrderablePart = Choice(DefinitionChoiceState.Unknown, [], reason: "Package and supplier option are not chosen."),
            Purpose = Choice(DefinitionChoiceState.Selected, [" exact\r\nUnicode Ω & <text>\n "])
        };
        string xml = BlockDefinitionXml.Write(definition);
        var loaded = BlockDefinitionXml.Read(xml); Assert.IsTrue(definition.SameContents(loaded));
        Assert.AreEqual(xml, BlockDefinitionXml.Write(loaded));
        var wire = RecursiveBlockCodec.Encode(definition);
        Assert.IsTrue(definition.SameContents(RecursiveBlockCodec.Decode(P.BlockDefinitionData.Parser.ParseFrom(wire.ToByteArray()))));
        var graph = Save(LinkedDiagramFixture.Create().Graph, definition);
        string graphXml = RecursiveBlockGraphXml.Write(graph);
        var graphRoundTrip = RecursiveBlockGraphXml.Read(graphXml);
        Assert.IsTrue(definition.SameContents(graphRoundTrip.Inspect(graphRoundTrip.SelectedRoot).EffectiveDefinition));
        Assert.AreEqual(graphXml, RecursiveBlockGraphXml.Write(graphRoundTrip));
        var graphWire = RecursiveBlockCodec.Decode(RecursiveBlockCodec.Encode(graph));
        Assert.AreEqual(graphXml, RecursiveBlockGraphXml.Write(graphWire));
        var draft = graph.StartDraft(graph.SelectedRoot);
        Assert.IsTrue(definition.SameContents(RecursiveBlockCodec.Decode(RecursiveBlockCodec.Encode(draft), graph.DocumentId).EffectiveDefinition));
    }

    [TestMethod]
    public void RefinementForkRestorationAndNoOpPreserveOriginalRequirementsAndDefinitions()
    {
        var original = LinkedDiagramFixture.Create().Graph; string originalXml = RecursiveBlockGraphXml.Write(original);
        var partial = Save(original, Partial());
        Assert.IsNull(partial.Inspect(original.SelectedRoot).Definition);
        Assert.AreEqual(original.Requirements(original.SelectedRoot).Requirements, partial.Requirements(partial.SelectedRoot).Requirements);
        var selected = Save(partial, Partial().With(BlockDefinitionFacet.Model, Choice(DefinitionChoiceState.Selected, ["Fixture model X"])));
        Assert.IsEmpty(selected.Inspect(partial.SelectedRoot).Definition!.Model!.Values);
        Assert.AreEqual("Fixture model X", selected.Inspect(selected.SelectedRoot).Definition!.Model!.Values.Single());
        var changes = DiagramHistoryQuery.Compare(selected, selected.SelectedRoot, partial.SelectedRoot).Changes;
        Assert.HasCount(1, changes); Assert.AreEqual(DiagramHistoryChangeCategory.Definition, changes[0].Category);
        var restored = selected.RestoreAsDraft(selected.StartDraft(selected.SelectedRoot), partial.SelectedRoot);
        Assert.IsTrue(Partial().SameContents(restored.EffectiveDefinition));
        var saved = selected.SaveDraft(selected.SelectedRoot, [selected.SelectedRoot], restored, Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph;
        Assert.IsTrue(Partial().SameContents(saved.Inspect(saved.SelectedRoot).EffectiveDefinition));
        foreach (bool emptyInterior in new[] { false, true })
        {
            Guid state = Guid.NewGuid(); var fork = saved.ForkImplementation(saved.SelectedRoot, state, Guid.NewGuid(), Guid.NewGuid(),
                "Another approach", RecursiveBlockFixture.Origin(), emptyInterior);
            Assert.IsTrue(Partial().SameContents(fork.History(state).Single().EffectiveDefinition));
        }
        var same = Save(saved, BlockDefinitionXml.Read(BlockDefinitionXml.Write(Partial())));
        Assert.AreSame(saved, same);
        Assert.AreEqual(originalXml, RecursiveBlockGraphXml.Write(Save(original, BlockDefinition.Empty)));
        Assert.AreEqual(originalXml, RecursiveBlockGraphXml.Write(RecursiveBlockGraphXml.Read(originalXml)));
    }

    [TestMethod]
    public void RequirementOnlyRebaseRetainsNewerDefinitionButNeverDiscardsAChangedDefinitionDraft()
    {
        var original = LinkedDiagramFixture.Create().Graph;
        var draft = original.StartDraft(original.SelectedRoot);
        draft = draft with { Requirements = draft.Requirements.Edit(DiagramRequirementField.Routing, "Keep the thermal path accessible.") };
        var latest = Save(original, Partial());
        var merged = RecursiveRequirementMerge.Prepare(latest, draft).Inspect();
        Assert.IsNotNull(merged.Candidate); Assert.IsTrue(Partial().SameContents(merged.Candidate.EffectiveDefinition));
        Assert.AreEqual("Keep the thermal path accessible.", merged.Candidate.Requirements.Requirements.Routing);
        var definitionDraft = draft with { Definition = Partial() };
        Assert.ThrowsExactly<AutomationException>(() => RecursiveRequirementMerge.Prepare(latest, definitionDraft));
        Assert.ThrowsExactly<AutomationException>(() => latest.RestoreAsDraft(definitionDraft, original.SelectedRoot));
        Assert.ThrowsExactly<AutomationException>(() => latest.SaveDraft(original.SelectedRoot, [original.SelectedRoot], definitionDraft,
            Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()));
    }

    [TestMethod]
    public void InvalidStatesCannotCreateAFalseSelectionOrLoseUnsupportedFields()
    {
        foreach (var choice in new[]
        {
            Choice(DefinitionChoiceState.Selected, []), Choice(DefinitionChoiceState.Selected, ["A", "B"]),
            Choice(DefinitionChoiceState.Unknown, ["A"], reason: "Unknown"), Choice(DefinitionChoiceState.Unknown, []),
            Choice(DefinitionChoiceState.Candidates, []), Choice(DefinitionChoiceState.Candidates, ["A", "A"]),
            Choice(DefinitionChoiceState.Unspecified, ["A"]), Choice(DefinitionChoiceState.Selected, [" "]),
            Choice(DefinitionChoiceState.Selected, ["A"], reason: "Not unknown"), Choice((DefinitionChoiceState)88, []),
            Choice(DefinitionChoiceState.Selected, ["a\0b"]), Choice(DefinitionChoiceState.Selected, ["A"]) with { Verification = (VerificationState)99 }
        }) Assert.ThrowsExactly<AutomationException>(() => new BlockDefinition(Model: choice).Validate());
        var invalidClass = Partial() with { KnowledgeClass = Partial().KnowledgeClass! with { Values = [new(Guid.Empty, "r1", Guid.NewGuid())] } };
        Assert.ThrowsExactly<AutomationException>(invalidClass.Validate);
        string xml = BlockDefinitionXml.Write(Partial());
        Assert.ThrowsExactly<AutomationException>(() => BlockDefinitionXml.Read(xml.Replace("version=\"1\"", "version=\"2\"", StringComparison.Ordinal)));
        Assert.ThrowsExactly<AutomationException>(() => BlockDefinitionXml.Read(xml.Replace("<values>", "<values future=\"true\">", StringComparison.Ordinal)));
        var wire = RecursiveBlockCodec.Encode(Partial()); wire.MergeFrom(new byte[] { 0x98, 0x06, 1 });
        Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockCodec.Decode(wire));
    }
}

using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveBlockRefinementInputTests
{
    /// <summary>An original input captured against the file bytes <c>Write(graph, storedSchemaVersion)</c>; pass 1 for a
    /// version 1 file as an earlier build stored it.</summary>
    internal static DiagramRefinementInput Input(RecursiveBlockGraph graph, int storedSchemaVersion = RecursiveBlockGraphXml.SchemaVersion) => new(Guid.NewGuid(), graph.DocumentId,
        Hash(RecursiveBlockGraphXml.Write(graph, storedSchemaVersion)), [graph.SelectedRoot], [], "  Original prompt\r\nΩ & <vision>\n ",
        RecursiveBlockFixture.Origin(), [new(Guid.NewGuid(), "Original drawing Ω.png", "assets/original.png", Hash("fixture bytes"),
            Encoding.UTF8.GetByteCount("fixture bytes"), "image/png", new("source-document", "r3", 7, "Table 2", "Variant B"))]);
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [TestMethod]
    public void OriginalInputsRemainImmutableAcrossGraphEditsAndSharedMessages()
    {
        var original = LinkedDiagramFixture.Create().Graph; string old = RecursiveBlockGraphXml.Write(original);
        var input = Input(original); var graph = original.WithRefinementInput(input);
        Assert.AreEqual(original.SelectedRoot, graph.SelectedRoot);
        Assert.AreSame(graph, graph.WithRefinementInput(DiagramRefinementInputXml.Read(DiagramRefinementInputXml.Write(input))));
        Assert.ThrowsExactly<AutomationException>(() => graph.WithRefinementInput(input with { Prompt = "Replace the original." }));
        Assert.ThrowsExactly<AutomationException>(() => original.WithRefinementInput(input with { Id = original.SelectedRoot.BlockId }));
        string xml = RecursiveBlockGraphXml.Write(graph);
        Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(RecursiveBlockGraphXml.Read(xml)));
        Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(RecursiveBlockCodec.Decode(RecursiveBlockCodec.Encode(graph))));
        var wire = RecursiveBlockCodec.Encode(input);
        Assert.IsTrue(input.SameContents(RecursiveBlockCodec.Decode(P.DiagramRefinementInputData.Parser.ParseFrom(wire.ToByteArray()))));
        wire.Attachments[0].MergeFrom(new byte[] { 0x98, 0x06, 1 });
        Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockCodec.Decode(wire));
        var draft = graph.StartDraft(graph.SelectedRoot);
        var origin = RecursiveBlockFixture.Origin() with { InputIds = [input.Id] };
        graph = graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot], draft with
            { Requirements = draft.Requirements.Edit(DiagramRequirementField.General, "A refined current requirement.") },
            Guid.NewGuid(), Guid.NewGuid(), [], origin).Graph;
        Assert.IsTrue(input.SameContents(graph.RefinementInput(input.Id)));
        Assert.AreEqual(input.Id, graph.Inspect(graph.SelectedRoot).Origin.InputIds.Single());
        Guid state = Guid.NewGuid(); graph = graph.ForkImplementation(graph.SelectedRoot, state, Guid.NewGuid(), Guid.NewGuid(), "Alternative", origin);
        graph = graph.RenameImplementation(state, "Another implementation", Guid.NewGuid(), origin);
        graph = graph.SetImplementationArchived(state, true, Guid.NewGuid(), origin);
        graph = graph.SetImplementationArchived(state, false, Guid.NewGuid(), origin);
        Assert.IsTrue(input.SameContents(graph.RefinementInput(input.Id)));
        var restored = graph.RestoreAsDraft(graph.StartDraft(graph.SelectedRoot), original.SelectedRoot);
        graph = graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot], restored, Guid.NewGuid(), Guid.NewGuid(), [], origin).Graph;
        Assert.IsTrue(input.SameContents(graph.RefinementInput(input.Id)));
        Assert.AreEqual(original.Requirements(original.SelectedRoot).Requirements, graph.Requirements(graph.SelectedRoot).Requirements);
        Assert.AreEqual(old, RecursiveBlockGraphXml.Write(RecursiveBlockGraphXml.Read(old)));
    }

    [TestMethod]
    public void OriginalPromptAttachmentAndSourceMetadataSurviveStrictXml()
    {
        var graph = LinkedDiagramFixture.Create().Graph; var input = Input(graph); input.ValidateAgainst(graph);
        string xml = DiagramRefinementInputXml.Write(input);
        var loaded = DiagramRefinementInputXml.Read(xml); Assert.IsTrue(input.SameContents(loaded));
        Assert.AreEqual(xml, DiagramRefinementInputXml.Write(loaded));
        Assert.AreEqual(input.Prompt, loaded.Prompt);
        Assert.AreEqual(input.Attachments[0].Source, loaded.Attachments[0].Source);
        foreach (string unsupported in new[]
        {
            xml.Replace("version=\"1\"", "version=\"2\"", StringComparison.Ordinal),
            xml.Replace("<prompt>", "<prompt future=\"true\">", StringComparison.Ordinal),
            xml.Replace("<attachments>", "<attachments><future/>", StringComparison.Ordinal)
        }) Assert.ThrowsExactly<AutomationException>(() => DiagramRefinementInputXml.Read(unsupported));
    }

    [TestMethod]
    public void HistoricalContextDoesNotFollowNewerHeadsOrSelectAnotherDiagram()
    {
        var graph = LinkedDiagramFixture.Create().Graph; var input = Input(graph);
        var draft = graph.StartDraft(graph.SelectedRoot);
        draft = draft with { Requirements = draft.Requirements.Edit(DiagramRequirementField.General, "A later interpretation.") };
        var newer = graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot], draft, Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph;
        input.ValidateAgainst(newer);
        Assert.AreEqual(graph.SelectedRoot, input.BlockPath[0]);
        Assert.AreNotEqual(newer.SelectedRoot, input.BlockPath[0]);
        Assert.AreEqual(graph.Requirements(graph.SelectedRoot).Requirements, newer.Requirements(input.BlockPath[0]).Requirements);
        Assert.AreEqual(input.Prompt, DiagramRefinementInputXml.Read(DiagramRefinementInputXml.Write(input)).Prompt);
        Assert.ThrowsExactly<AutomationException>(() => (input with { DocumentId = Guid.NewGuid() }).ValidateAgainst(newer));
        Assert.ThrowsExactly<AutomationException>(() => (input with { BlockPath = [graph.SelectedRoot, graph.SelectedRoot] }).ValidateAgainst(newer));
        var child = graph.Inspect(graph.SelectedRoot).Children[0];
        (input with { BlockPath = [graph.SelectedRoot, child] }).ValidateAgainst(graph);
        Assert.ThrowsExactly<AutomationException>(() => (input with { BlockPath = [child, graph.SelectedRoot] }).ValidateAgainst(graph));
    }

    [TestMethod]
    public void ConnectionContextKeepsItsExactOwnerAndRevision()
    {
        var graph = LinkedDiagramFixture.Create().Graph; var input = Input(graph);
        var link = graph.Inspect(graph.SelectedRoot).LocalDiagram.Connections[0];
        var targeted = input with { ConnectionPath = [link] }; targeted.ValidateAgainst(graph);
        Assert.IsTrue(targeted.SameContents(DiagramRefinementInputXml.Read(DiagramRefinementInputXml.Write(targeted))));
        Assert.ThrowsExactly<AutomationException>(() => (targeted with { ConnectionPath = [link with { RevisionId = Guid.NewGuid() }] }).ValidateAgainst(graph));
        Assert.ThrowsExactly<AutomationException>(() => (targeted with { BlockPath = [graph.SelectedRoot, graph.Inspect(graph.SelectedRoot).Children[0]] }).ValidateAgainst(graph));
    }

    [TestMethod]
    public void InvalidReferencesDoNotMasqueradeAsPreservedOriginalAssets()
    {
        var input = Input(LinkedDiagramFixture.Create().Graph); var asset = input.Attachments[0];
        foreach (var invalid in new[]
        {
            input with { SourceSha256 = "unknown" }, input with { Prompt = "bad\0text" }, input with { BlockPath = [] },
            input with { Attachments = [asset with { AssetPath = "../original.png" }] },
            input with { Attachments = [asset with { ContentSha256 = asset.ContentSha256.ToUpperInvariant() }] },
            input with { Attachments = [asset with { ByteCount = -1 }] },
            input with { Attachments = [asset with { MediaType = "image/png; execute" }] },
            input with { Attachments = [asset, asset] },
            input with { Attachments = [asset, asset with { Id = Guid.NewGuid(), ContentSha256 = Hash("different bytes") }] },
            input with { Attachments = [asset with { Source = asset.Source! with { Page = 0 } }] }
        }) Assert.ThrowsExactly<AutomationException>(invalid.Validate);
        // Two references may cite different pages in the same preserved document.
        (input with { Attachments = [asset, asset with { Id = Guid.NewGuid(), Source = asset.Source! with { Page = 8 } }] }).Validate();
        // An action can refer to existing annotations without inventing prompt text.
        (input with { Prompt = "", Attachments = [] }).Validate();
    }
}

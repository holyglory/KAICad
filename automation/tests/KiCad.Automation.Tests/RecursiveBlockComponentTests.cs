using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveBlockComponentTests
{
    internal static BlockComponentBindings UnresolvedFixture() => new([new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())]);
    private static RecursiveBlockGraph Save(RecursiveBlockGraph graph, BlockComponentBindings? bindings) => graph.SaveDraft(
        graph.SelectedRoot, [graph.SelectedRoot], graph.StartDraft(graph.SelectedRoot) with { ComponentBindings = bindings },
        Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph;

    [TestMethod]
    public void ExactMappingsRoundTripWithoutInventingComponentsOrChangingOldDocuments()
    {
        var original = LinkedDiagramFixture.Create().Graph; string oldXml = RecursiveBlockGraphXml.Write(original);
        Assert.AreEqual(oldXml, RecursiveBlockGraphXml.Write(Save(original, BlockComponentBindings.Empty)));
        Assert.AreEqual(oldXml, RecursiveBlockGraphXml.Write(RecursiveBlockGraphXml.Read(oldXml)));
        var bindings = UnresolvedFixture(); var graph = Save(original, bindings);
        string xml = RecursiveBlockGraphXml.Write(graph);
        Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(RecursiveBlockGraphXml.Read(xml)));
        Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(RecursiveBlockCodec.Decode(RecursiveBlockCodec.Encode(graph))));
        Assert.IsTrue(bindings.SameContents(RecursiveBlockCodec.Decode(RecursiveBlockCodec.Encode(graph.StartDraft(graph.SelectedRoot)), graph.DocumentId).EffectiveComponentBindings));
        Assert.AreSame(graph, Save(graph, new([.. bindings.Targets])));
        Assert.AreEqual(original.Requirements(original.SelectedRoot).Requirements, graph.Requirements(graph.SelectedRoot).Requirements);
        Assert.IsNull(graph.Inspect(original.SelectedRoot).ComponentBindings);
        Assert.HasCount(1, DiagramHistoryQuery.Compare(graph, graph.SelectedRoot, original.SelectedRoot).Changes);
        Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockGraphXml.Read(xml.Replace("<component-bindings>", "<component-bindings future=\"true\">", StringComparison.Ordinal)));
        var wire = RecursiveBlockCodec.Encode(bindings); wire.MergeFrom(new byte[] { 0x98, 0x06, 1 });
        Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockCodec.Decode(wire));
        var nested = RecursiveBlockCodec.Encode(bindings); nested.Targets[0].MergeFrom(new byte[] { 0x98, 0x06, 1 });
        Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockCodec.Decode(nested));
        foreach (var invalid in new[] { new BlockComponentBindings(default), new([bindings.Targets[0], bindings.Targets[0]]),
            new([bindings.Targets[0] with { ComponentId = Guid.Empty }]),
            new([bindings.Targets[0], bindings.Targets[0] with { CircuitId = Guid.NewGuid() }]) })
            Assert.ThrowsExactly<AutomationException>(invalid.Validate);
    }

    [TestMethod]
    public void ForkRestoreAndRequirementRebaseRetainExactMappingsAndDirtyDrafts()
    {
        var original = LinkedDiagramFixture.Create().Graph; var bindings = UnresolvedFixture();
        var saved = Save(original, bindings);
        foreach (bool emptyInterior in new[] { false, true })
        {
            Guid state = Guid.NewGuid(); var fork = saved.ForkImplementation(saved.SelectedRoot, state, Guid.NewGuid(), Guid.NewGuid(),
                "Another implementation", RecursiveBlockFixture.Origin(), emptyInterior);
            Assert.IsTrue(bindings.SameContents(fork.History(state).Single().EffectiveComponentBindings));
        }
        var removed = Save(saved, BlockComponentBindings.Empty);
        var restored = removed.RestoreAsDraft(removed.StartDraft(removed.SelectedRoot), saved.SelectedRoot);
        Assert.IsTrue(bindings.SameContents(restored.EffectiveComponentBindings));
        Assert.IsEmpty(removed.Inspect(removed.SelectedRoot).EffectiveComponentBindings.Targets);
        var draft = original.StartDraft(original.SelectedRoot);
        draft = draft with { Requirements = draft.Requirements.Edit(DiagramRequirementField.General, "Keep the original enclosure.") };
        var rebased = RecursiveRequirementMerge.Prepare(saved, draft).Inspect();
        Assert.IsNotNull(rebased.Candidate); Assert.IsTrue(bindings.SameContents(rebased.Candidate.EffectiveComponentBindings));
        Assert.AreEqual("Keep the original enclosure.", rebased.Candidate.Requirements.Requirements.General);
        var changed = draft with { ComponentBindings = bindings };
        Assert.ThrowsExactly<AutomationException>(() => RecursiveRequirementMerge.Prepare(saved, changed));
        Assert.ThrowsExactly<AutomationException>(() => saved.RestoreAsDraft(changed, original.SelectedRoot));
        Assert.ThrowsExactly<AutomationException>(() => saved.SaveDraft(original.SelectedRoot, [original.SelectedRoot], changed,
            Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()));
    }

    internal static (HardwareRepository Repository, SchematicDesign Design, IReadOnlyCollection<ComponentKnowledgeLibrary> Libraries,
        BlockComponentBindings Bindings) ElectricalFixture()
    {
        var state = SymbolSheetOwnershipTests.Fixture(); var design = state.Baseline; Guid designId = Guid.NewGuid();
        var repository = new HardwareRepository(Guid.NewGuid(), "Fixture device",
            [new(designId, "Controller", "controller.kicad_pro", "controller.design.xml", [])], [],
            design.Engineering.KnowledgeLibraries, []);
        var bindings = new BlockComponentBindings([.. design.Engineering.Circuit.Components.Select(c =>
            new ComponentRealization(designId, design.Engineering.Circuit.Id, c.Id))]);
        return (repository, design, state.KnowledgeLibraries, bindings);
    }

    [TestMethod]
    public void ResolvesEveryUnitAtItsOwnSheetWithoutConfusingRepeatedNativeUuids()
    {
        var f = ElectricalFixture();
        var result = BlockComponentResolver.Inspect(f.Bindings, f.Repository,
            new Dictionary<Guid, SchematicDesign> { [f.Repository.Designs[0].Id] = f.Design }, f.Libraries);
        Assert.HasCount(2, result);
        foreach (var component in result)
        {
            Assert.AreEqual(ComponentRealizationStatus.ComponentResolved, component.Status);
            Assert.AreEqual(component.Target.ComponentId, component.Component!.Id);
            Assert.IsTrue(component.NativeBindings!.IdentitiesResolved);
            Assert.HasCount(2, component.NativeLocations);
            Assert.AreEqual(2, component.NativeLocations.Select(s => s.SheetInstanceId).Distinct().Count());
            Assert.AreEqual(1, component.NativeLocations.Single(s => s.Unit == 2).NativeSheetPath.Length);
            Assert.AreEqual(2, component.NativeLocations.Single(s => s.Unit == 1).NativeSheetPath.Length);
        }
        var first = result[0].NativeLocations.Single(s => s.Unit == 1);
        var second = result[1].NativeLocations.Single(s => s.Unit == 1);
        Assert.AreEqual(first.NativeObjectId, second.NativeObjectId);
        Assert.IsFalse(first.NativeSheetPath.SequenceEqual(second.NativeSheetPath));
    }

    [TestMethod]
    public void MissingReplacedAndAmbiguousTargetsNeverMatchNamesOrOfferFalseNativeLocations()
    {
        var f = ElectricalFixture(); var target = f.Bindings.Targets[0];
        var designs = new Dictionary<Guid, SchematicDesign> { [target.DesignId] = f.Design };
        var mixed = new BlockComponentBindings([target, target with { ComponentId = Guid.NewGuid() },
            target with { DesignId = Guid.NewGuid() }]);
        var result = BlockComponentResolver.Inspect(mixed, f.Repository, designs, f.Libraries);
        Assert.AreEqual(ComponentRealizationStatus.ComponentResolved, result[0].Status);
        Assert.AreEqual(ComponentRealizationStatus.MissingComponent, result[1].Status);
        Assert.AreEqual(ComponentRealizationStatus.MissingDesign, result[2].Status);
        Assert.IsTrue(mixed.Targets.SequenceEqual(result.Select(r => r.Target)));
        Assert.AreEqual(ComponentRealizationStatus.CircuitChanged, BlockComponentResolver.Inspect(
            new([target with { CircuitId = Guid.NewGuid() }]), f.Repository, designs, f.Libraries)[0].Status);
        Assert.AreEqual(ComponentRealizationStatus.UnavailableDesign, BlockComponentResolver.Inspect(
            new([target]), f.Repository, new Dictionary<Guid, SchematicDesign>(), f.Libraries)[0].Status);
        designs[target.DesignId] = f.Design with { SymbolBindings = [] };
        var unbound = BlockComponentResolver.Inspect(new([target]), f.Repository, designs, f.Libraries)[0];
        Assert.AreEqual(ComponentRealizationStatus.ComponentResolved, unbound.Status);
        Assert.IsEmpty(unbound.NativeLocations); Assert.IsFalse(unbound.NativeBindings!.IdentitiesResolved);
        Assert.IsTrue(unbound.NativeBindings.Issues.Any(i => i.Code == "unmapped_model_symbol"));
        var circuit = f.Design.Engineering.Circuit;
        designs[target.DesignId] = f.Design with { Engineering = f.Design.Engineering with { Circuit = circuit with
            { Components = circuit.Components.Select(c => c.Id == target.ComponentId ? c with { Reference = "U999" } : c).ToArray() } } };
        var renamed = BlockComponentResolver.Inspect(new([target]), f.Repository, designs, f.Libraries)[0];
        Assert.AreEqual("U999", renamed.Component!.Reference); Assert.HasCount(2, renamed.NativeLocations);
        Assert.IsTrue(renamed.NativeBindings!.Differences.Any(d => d.Field == "reference"));
    }

    internal static async Task<(BlockComponentBindings Bindings, string ManifestHash)> WriteRepository(string root, CancellationToken token)
    {
        var f = ElectricalFixture();
        foreach (var declared in f.Repository.Libraries)
        {
            string path = Path.Combine(root, declared.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, ComponentKnowledgeXml.WriteLibrary(f.Libraries.Single(l => l.Id == declared.Id)), token);
        }
        await File.WriteAllTextAsync(Path.Combine(root, "controller.design.xml"), SchematicDesignXml.Write(f.Design, f.Libraries), token);
        byte[] manifest = Encoding.UTF8.GetBytes(HardwareRepositoryXml.Write(f.Repository));
        await File.WriteAllBytesAsync(Path.Combine(root, "hardware.xml"), manifest, token);
        return (f.Bindings, Convert.ToHexStringLower(SHA256.HashData(manifest)));
    }

    [TestMethod]
    public async Task DeclaredFileInspectionKeepsHashesFailuresCancellationAndRecoveryTruthful()
    {
        string root = Directory.CreateTempSubdirectory("kicad-component-binding-").FullName;
        try
        {
            var f = await WriteRepository(root, CancellationToken.None);
            var result = await BlockComponentFiles.InspectAsync(root, "hardware.xml", f.ManifestHash, f.Bindings);
            Assert.HasCount(2, result.Components); Assert.IsEmpty(result.Failures);
            Assert.IsTrue(result.Sources.All(s => s.ContentSha256.Length == 64));
            Assert.IsTrue(result.Components.All(c => c.Status == ComponentRealizationStatus.ComponentResolved && c.NativeLocations.Length == 2));
            Assert.AreEqual(f.ManifestHash, result.Sources.Single(s => s.RelativePath == "hardware.xml").ContentSha256);
            string model = Path.Combine(root, "controller.design.xml"); byte[] bytes = await File.ReadAllBytesAsync(model);
            File.Move(model, model + ".retained");
            var missing = await BlockComponentFiles.InspectAsync(root, "hardware.xml", f.ManifestHash, f.Bindings);
            Assert.HasCount(1, missing.Failures); Assert.IsTrue(missing.Components.All(c => c.Status == ComponentRealizationStatus.UnavailableDesign));
            await File.WriteAllTextAsync(model, "<unsupported-design/>");
            var invalid = await BlockComponentFiles.InspectAsync(root, "hardware.xml", f.ManifestHash, f.Bindings);
            Assert.HasCount(1, invalid.Failures); Assert.IsTrue(invalid.Components.All(c => c.Status == ComponentRealizationStatus.UnavailableDesign));
            await File.WriteAllBytesAsync(model, bytes);
            var recovered = await BlockComponentFiles.InspectAsync(root, "hardware.xml", f.ManifestHash, f.Bindings);
            Assert.IsEmpty(recovered.Failures); Assert.IsTrue(recovered.Components.All(c => c.NativeLocations.Length == 2));
            Assert.ThrowsExactly<AutomationException>(() => BlockComponentResolver.Inspect(f.Bindings,
                ElectricalFixture().Repository, new Dictionary<Guid, SchematicDesign> { [Guid.NewGuid()] = ElectricalFixture().Design }, []));
            await Assert.ThrowsExactlyAsync<AutomationException>(() => BlockComponentFiles.InspectAsync(root, "hardware.xml", "stale", f.Bindings));
            await Assert.ThrowsExactlyAsync<AutomationException>(() => BlockComponentFiles.InspectAsync(root, "../hardware.xml", f.ManifestHash, f.Bindings));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => BlockComponentFiles.InspectAsync(root, "hardware.xml", f.ManifestHash, f.Bindings, cancellation.Token));
            byte[] recoveredBytes = await File.ReadAllBytesAsync(model);
            Assert.IsTrue(bytes.SequenceEqual(recoveredBytes));
        }
        finally { Directory.Delete(root, true); }
    }
}

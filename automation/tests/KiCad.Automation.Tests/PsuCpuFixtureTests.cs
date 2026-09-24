using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Any = Google.Protobuf.WellKnownTypes.Any;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Tests;

/// <summary>Integrity of the frozen PSU-CPU fixture v1: pinned bytes and sources, deterministic
/// identities, builder equality, stage filters, block-graph invariants and the independent
/// checks of every expected-*.json table.</summary>
[TestClass]
public sealed class PsuCpuFixtureTests
{
    private static readonly string[] Sheets = ["ROOT", "PSU", "CPU", "CPU_POWER"];

    [TestMethod]
    public void EveryFixtureFileMatchesItsRecordedDigest()
    {
        var manifest = PsuCpuFixture.Manifest;
        Assert.AreEqual("psu-cpu", manifest.GetProperty("fixture").GetString());
        Assert.AreEqual(PsuCpuFixture.Version, manifest.GetProperty("version").GetInt32());
        Assert.AreEqual(PsuCpuIds.Prefix, manifest.GetProperty("idPrefix").GetString());
        Assert.IsTrue(Regex.IsMatch(manifest.GetProperty("normalizedAt").GetString()!, "^[0-9a-f]{40}$"));
        CollectionAssert.AreEquivalent(PsuCpuFixture.DataFiles.ToArray(), manifest.GetProperty("files").EnumerateObject().Select(f => f.Name).ToArray());
        CollectionAssert.AreEquivalent(PsuCpuFixture.DataFiles.Append("fixture.json").ToArray(),
            Directory.GetFiles(PsuCpuFixture.Directory).Select(f => Path.GetFileName(f)).ToArray(), "The fixture directory holds only pinned files.");
        foreach (string name in PsuCpuFixture.DataFiles) _ = PsuCpuFixture.ReadBytes(name);

        byte[] changed = File.ReadAllBytes(PsuCpuFixture.PathOf("hardware.xml"));
        changed[^2] ^= 1;
        Code(Assert.ThrowsExactly<AssertFailedException>(() => PsuCpuFixture.RequireDigest("hardware.xml", changed)), "psu_cpu_fixture_file_changed");
        string xml = PsuCpuFixture.ReadText("hardware.xml");
        Assert.AreEqual(xml, PsuCpuFixture.RequireBuilt("hardware.xml", xml, xml));
        Code(Assert.ThrowsExactly<AssertFailedException>(() => PsuCpuFixture.RequireBuilt("hardware.xml", xml, xml + " ")), "psu_cpu_fixture_builder_drift");
    }

    [TestMethod]
    public void IdentitiesAreDeterministicLowercaseAndScopedByKind()
    {
        Assert.AreEqual("7e57f1c5-0000-4000-8000-0021000000de", PsuCpuIds.Id(0x21, 222).ToString("D"));
        Assert.AreEqual(Guid.ParseExact("7e57f1c5-0000-4000-8000-00200000001b", "D"), PsuCpuIds.Id(0x20, 0x1b));
        var circuit = PsuCpuFixture.Engineering().Circuit;
        void Kind(int kind, IEnumerable<Guid> ids, int count)
        {
            var list = ids.ToArray();
            CollectionAssert.AreEquivalent(Enumerable.Range(1, count).Select(n => PsuCpuIds.Id(kind, n)).ToArray(), list, $"kind {kind:x2}");
        }
        Assert.AreEqual(PsuCpuIds.Id(0x02, 1), circuit.Id);
        Assert.AreEqual(PsuCpuIds.Id(0x02, 2), PsuCpuFixture.Engineering().Structure.Id);
        Kind(0x03, circuit.Parts.Select(p => p.Id), 8);
        Kind(0x04, circuit.Sheets.Select(s => s.Id), 4);
        Kind(0x05, circuit.SheetInstances.Select(s => s.Id), 4);
        Kind(0x06, circuit.Sheets.SelectMany(s => s.Components).Select(c => c.Id), 8);
        Kind(0x07, circuit.Components.Select(c => c.Id), 8);
        Kind(0x08, circuit.Nets.Select(n => n.Id), 11);
        Kind(0x09, circuit.Symbols.Select(s => s.Id), 11);
        var hardware = PsuCpuFixture.Hardware();
        CollectionAssert.AreEqual(new[] { PsuCpuIds.Id(0x01, 1), PsuCpuIds.Id(0x01, 2), PsuCpuIds.Id(0x01, 3) },
            new[] { hardware.Id, hardware.Designs.Single().Id, hardware.Designs.Single().Ports.Single().Id });
        var graph = PsuCpuFixture.Graph();
        Assert.AreEqual(PsuCpuIds.Id(0x10, 1), graph.DocumentId);
        Kind(0x11, graph.States.Select(s => s.BlockId), 9);
        Kind(0x12, graph.States.Select(s => s.Id), 9);
        Kind(0x13, graph.Revisions.Select(r => r.Selection.RevisionId), 9);
        Kind(0x14, graph.Revisions.Select(r => r.RequirementRevisionId), 9);
        Kind(0x15, graph.Revisions.SelectMany(r => r.LocalDiagram.Interfaces).Select(i => i.Id), 0x17);
        Kind(0x16, graph.ConnectionArchives.SelectMany(a => a.States).Select(s => s.ConnectionId), 0x1f);
        Kind(0x17, graph.ConnectionArchives.SelectMany(a => a.States).Select(s => s.Id), 0x1f);
        Kind(0x18, graph.ConnectionArchives.SelectMany(a => a.Revisions).Select(r => r.Selection.RevisionId), 0x1f);
        Kind(0x19, graph.ConnectionArchives.SelectMany(a => a.Revisions).Select(r => r.RequirementRevisionId), 0x1f);
        Kind(0x1a, graph.Revisions.SelectMany(r => r.LocalDiagram.Notes).Select(n => n.Id), 5);
        var flat = PsuCpuFixture.FlatStructure().Structure;
        Assert.AreEqual(PsuCpuIds.Id(0x30, 1), flat.Id);
        CollectionAssert.IsSubsetOf(flat.Blocks.Select(b => b.Id).ToArray(), graph.States.Select(s => s.BlockId).ToArray());
        CollectionAssert.IsSubsetOf(flat.Ports.Select(p => p.Id).ToArray(),
            graph.Revisions.SelectMany(r => r.LocalDiagram.Interfaces).Select(i => i.Id).ToArray());
        CollectionAssert.AreEquivalent(new[] { 0x02, 0x06, 0x09, 0x0a, 0x0b }.Select(c => PsuCpuIds.Id(0x16, c)).Append(PsuCpuIds.Id(0x30, 0x10)).ToArray(),
            flat.Connections.Select(c => c.Id).ToArray());
        Assert.AreEqual(PsuCpuIds.Id(0x30, 0x20), flat.Statements.Single().Id);
    }

    [TestMethod]
    public void PartsArePinnedToTheirSourceBlobsAndDerivedFromThem()
    {
        var symbols = PsuCpuFixture.Manifest.GetProperty("symbols").EnumerateArray().Select(s => (CacheKey: s.GetProperty("cacheKey").GetString()!,
            SourcePath: s.GetProperty("sourcePath").GetString()!, GitBlob: s.GetProperty("gitBlob").GetString()!)).ToArray();
        CollectionAssert.AreEqual(PsuCpuFixtureBuilder.PartSources.Select(s => (s.CacheKey, s.SourcePath)).ToArray(),
            symbols.Select(s => (s.CacheKey, s.SourcePath)).ToArray());
        var blobs = symbols.ToDictionary(s => s.CacheKey, s => s.GitBlob);
        var derived = PsuCpuFixtureBuilder.DeriveParts(PsuCpuFixture.RepositoryRoot, blobs);
        var parts = PsuCpuFixture.Parts();
        Assert.HasCount(8, parts);
        var none = Array.Empty<PartPin>();
        foreach (var (actual, expected) in parts.Zip(derived))
        {
            Assert.AreEqual(expected with { Pins = none }, actual with { Pins = none }, actual.CacheKey);
            CollectionAssert.AreEqual(expected.Pins.ToArray(), actual.Pins.ToArray(), actual.CacheKey);
            Assert.IsTrue(Regex.IsMatch(actual.Source.GitBlob, "^[0-9a-f]{40}$"), actual.CacheKey);
        }
        CollectionAssert.AreEqual(new[] { 2, 5, 2, 9, 11, 8, 177, 8 }, parts.Select(p => p.Pins.Count).ToArray());
        Assert.AreEqual(222, parts.Sum(p => p.Pins.Count));
        Assert.IsTrue(parts.SelectMany(p => p.Pins).All(p => !p.Number.Contains(' ') && !p.Name.Contains(' ')));
        CollectionAssert.AreEqual(new[] { 36, 98, 12, 31 }, Enumerable.Range(1, 4).Select(u => parts[6].Pins.Count(p => p.Unit == u)).ToArray());
        CollectionAssert.AreEqual(new PartPin[] { new("1", "OUT", 1), new("4", "OUT", 1) }, parts[3].Pins.Where(p => p.Name == "OUT").ToArray());
        CollectionAssert.AreEqual(new PartPin[] { new("4", "GND", 0), new("8", "VCC", 0) }, parts[7].Pins.Where(p => p.Unit == 0).ToArray());
        // Device.kicad_sym predates format 20250318, where a lone "~" still spelled an empty pin name.
        CollectionAssert.AreEqual(new PartPin[] { new("1", "", 1), new("2", "", 1) }, parts[2].Pins.ToArray());

        var source = PsuCpuFixtureBuilder.PartSources[0];
        Code(Assert.ThrowsExactly<AssertFailedException>(() => PsuCpuFixtureBuilder.ReadPinnedSource(
            PsuCpuFixture.RepositoryRoot, source.SourcePath, new string('0', 40))), "psu_cpu_fixture_file_changed");
    }

    [TestMethod]
    public void CheckedInFilesEqualTheBuilderOutput()
    {
        // The fixture-contracts check retains this directory, so it always holds this run's receipt
        // and, on drift, the builder's replacement for each drifted file.
        string generated = OutputDirectory("generated");
        var drift = new List<string>();
        foreach (var (name, built) in PsuCpuFixtureBuilder.Files(PsuCpuFixture.Parts()))
        {
            string path = PsuCpuFixture.PathOf(name);
            if (File.Exists(path) && File.ReadAllText(path) == built) continue;
            File.WriteAllText(Path.Combine(generated, name), built, new UTF8Encoding(false)); drift.Add(name);
        }
        File.WriteAllText(Path.Combine(generated, "builder-drift.txt"), $"PSU-CPU fixture version {PsuCpuFixture.Version} builder drift: "
            + (drift.Count == 0 ? "none" : string.Join(", ", drift)) + "\n", new UTF8Encoding(false));
        Assert.IsEmpty(drift, "psu_cpu_fixture_builder_drift: " + string.Join(", ", drift));
        Assert.AreEqual(PsuCpuFixture.ReadText("hardware.xml"), HardwareRepositoryXml.Write(PsuCpuFixture.Hardware()));
    }

    [TestMethod]
    public void ProductionCodecsReadAndValidateEveryFileAndStage()
    {
        var hardware = PsuCpuFixture.Hardware();
        Assert.AreEqual("PSU-CPU acceptance fixture", hardware.Name);
        var design = hardware.Designs.Single();
        Assert.AreEqual(("Controller", "fixture.kicad_pro", "design.xml"), (design.Name, design.ProjectPath, design.ModelPath));
        Assert.AreEqual(new HardwarePort(PsuCpuIds.Id(0x01, 3), "DC input", "External supply connector J1 (fixture data)."), design.Ports.Single());
        Assert.IsEmpty(hardware.Documents); Assert.IsEmpty(hardware.Libraries); Assert.IsEmpty(hardware.Interfaces);
        foreach (string name in new[] { "design.engineering.xml", "flat-structure.engineering.xml" })
        {
            string text = PsuCpuFixture.ReadText(name);
            Assert.AreEqual(text, EngineeringDesignXml.Write(EngineeringDesignXml.Read(text, []), []), name);
        }
        string blocks = PsuCpuFixture.ReadText("system.blocks.xml");
        Assert.AreEqual(blocks, RecursiveBlockGraphXml.Write(RecursiveBlockGraphXml.Read(blocks)));
        foreach (var stage in Enum.GetValues<PsuCpuStage>())
        {
            var staged = PsuCpuFixture.Engineering(stage);
            Assert.AreEqual(PsuCpuIds.Id(0x02, 1), staged.Circuit.Id);
            Assert.AreEqual(PsuCpuIds.Id(0x02, 2), staged.Structure.Id);
            string xml = EngineeringDesignXml.Write(staged, []);
            Assert.AreEqual(xml, EngineeringDesignXml.Write(EngineeringDesignXml.Read(xml, []), []), stage.ToString());
            Assert.AreEqual(stage, PsuCpuFixture.ExpectedNative(stage).Stage);
        }
        Code(Assert.ThrowsExactly<AssertFailedException>(() => PsuCpuFixture.Engineering((PsuCpuStage)99)), "psu_cpu_unknown_stage");
        Code(Assert.ThrowsExactly<AssertFailedException>(() => PsuCpuFixture.ExpectedNative((PsuCpuStage)99)), "psu_cpu_unknown_stage");
    }

    [TestMethod]
    public void CircuitCountsAndPlacementsMatchTheContract()
    {
        var circuit = PsuCpuFixture.Engineering().Circuit;
        Assert.HasCount(8, circuit.Parts); Assert.AreEqual(222, circuit.Parts.Sum(p => p.Pins.Count));
        Assert.HasCount(4, circuit.Sheets); Assert.HasCount(4, circuit.SheetInstances);
        Assert.HasCount(8, circuit.Components); Assert.HasCount(11, circuit.Symbols); Assert.HasCount(11, circuit.Nets);
        Assert.AreEqual(41, circuit.Nets.Sum(n => n.Pins.Count));
        CollectionAssert.AreEqual(new[] { "System", "PSU", "CPU", "CPU_POWER" }, circuit.Sheets.Select(s => s.Name).ToArray());
        CollectionAssert.AreEqual(new Guid?[] { null, PsuCpuIds.Id(0x05, 1), PsuCpuIds.Id(0x05, 1), PsuCpuIds.Id(0x05, 3) },
            circuit.SheetInstances.Select(s => s.ParentId).ToArray());
        Assert.IsTrue(circuit.Symbols.All(s => s.Placement is null), "Every occurrence is coordinate-free.");
        Assert.AreEqual(PsuCpuIds.Id(0x05, 4), circuit.Symbols.Single(s => s.SheetInstanceId is not null).SheetInstanceId);
        Assert.AreEqual((PsuCpuIds.Id(0x07, 7), 4), circuit.Symbols.Select(s => (s.ComponentId, s.Unit)).ElementAt(9));
        var connected = circuit.Nets.SelectMany(n => n.Pins).ToHashSet();
        var unconnected = PlacedPins(circuit).Where(p => !connected.Contains(p.Pin)).GroupBy(p => p.Reference)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Pin.Pin).Order(StringComparer.Ordinal).ToArray());
        Assert.AreEqual(181, unconnected.Values.Sum(v => v.Length));
        CollectionAssert.AreEqual(new[] { "5" }, unconnected["U1"]);
        CollectionAssert.AreEqual(new[] { "5", "6" }, unconnected["U2"]);
        CollectionAssert.AreEqual(new[] { "11", "3", "4", "9" }, unconnected["U3"]);
        CollectionAssert.AreEqual(new[] { "7" }, unconnected["U4"]);
        Assert.HasCount(172, unconnected["U5"]);
        CollectionAssert.AreEqual(new[] { "5" }, unconnected["U6"], "The memory SDA pin stays unresolved.");
        Assert.IsFalse(unconnected.ContainsKey("J1") || unconnected.ContainsKey("R1"));
    }

    [TestMethod]
    public void StagesArePureIdentityFiltersOfTheCompleteCircuit()
    {
        var complete = PsuCpuFixture.Engineering().Circuit;
        foreach (var stage in Enum.GetValues<PsuCpuStage>())
        {
            var circuit = PsuCpuFixture.Engineering(stage).Circuit;
            Assert.IsTrue(circuit.Parts.All(complete.Parts.Contains) && circuit.SheetInstances.All(complete.SheetInstances.Contains)
                && circuit.Components.All(complete.Components.Contains) && circuit.Nets.All(complete.Nets.Contains)
                && circuit.Symbols.All(complete.Symbols.Contains), stage.ToString());
            foreach (var sheet in circuit.Sheets)
                CollectionAssert.IsSubsetOf(sheet.Components.ToArray(), complete.Sheets.Single(s => s.Id == sheet.Id).Components.ToArray());
            (int Sheets, int Parts, int Components, int Symbols, int Nets) counts = (circuit.Sheets.Count, circuit.Parts.Count,
                circuit.Components.Count, circuit.Symbols.Count, circuit.Nets.Count);
            Assert.AreEqual(stage switch
            {
                PsuCpuStage.RootOnly => (1, 0, 0, 0, 0), PsuCpuStage.SheetsOnly => (4, 0, 0, 0, 0),
                PsuCpuStage.PsuComponents => (4, 6, 6, 6, 0), PsuCpuStage.Components => (4, 8, 8, 11, 0), _ => (4, 8, 8, 11, 11)
            }, counts, stage.ToString());
            Assert.AreEqual(circuit.Sheets.Count, circuit.SheetInstances.Count);
        }
        var psu = PsuCpuFixture.Engineering(PsuCpuStage.PsuComponents).Circuit;
        CollectionAssert.AreEqual(Enumerable.Range(1, 6).Select(n => PsuCpuIds.Id(0x09, n)).ToArray(), psu.Symbols.Select(s => s.Id).ToArray());
        Assert.IsEmpty(psu.Sheets.Single(s => s.Name == "CPU").Components);
        Assert.AreEqual(PsuCpuIds.Id(0x04, 1), PsuCpuFixture.Engineering(PsuCpuStage.RootOnly).Circuit.Sheets.Single().Id);
    }

    [TestMethod]
    public void BlockGraphInvariantsHold()
    {
        var graph = PsuCpuFixture.Graph();
        var circuit = PsuCpuFixture.Engineering().Circuit;
        var components = circuit.Components.ToDictionary(c => c.Id);
        var parts = circuit.Parts.ToDictionary(p => p.Id);
        var definitions = circuit.Sheets.SelectMany(s => s.Components).ToDictionary(d => d.Id);
        var closure = graph.Walk(graph.SelectedRoot).Select(graph.Inspect).ToDictionary(r => r.Selection.BlockId);
        Assert.HasCount(9, closure);
        Assert.IsTrue(closure.Values.All(r => r.ParentRevisionId is null && r.Definition is null && r.PhysicalAllocation is null));
        Assert.IsTrue(graph.States.All(s => s.Name == "Initial approach") && graph.RequirementHistories.All(h => h.Revisions.Length == 1));
        // G-1: each circuit component is bound by exactly one block of the selected closure.
        var bindings = closure.Values.SelectMany(r => r.EffectiveComponentBindings.Targets.Select(t => (Block: r.Selection.BlockId, Target: t))).ToArray();
        Assert.IsTrue(bindings.All(b => b.Target.DesignId == PsuCpuIds.Id(0x01, 2) && b.Target.CircuitId == circuit.Id));
        CollectionAssert.AreEquivalent(components.Keys.ToArray(), bindings.Select(b => b.Target.ComponentId).ToArray());
        var connections = new List<(Guid Owner, DiagramConnectionRevision Revision)>();
        foreach (var revision in closure.Values.Where(r => !r.LocalDiagram.Connections.IsEmpty))
        {
            var archive = graph.Connections(revision.Selection.BlockId);
            connections.AddRange(archive.Walk(revision.LocalDiagram.Connections).Select(s => (revision.Selection.BlockId, archive.Inspect(s))));
        }
        Assert.HasCount(0x1f, connections);
        foreach (var (owner, connection) in connections)
        {
            var pins = connection.Endpoints.Where(e => e.Kind == DiagramEndpointKind.Pin).Select(e => (e.BlockId, e.Pin!)).ToArray();
            foreach (var (block, pin) in pins)
            {
                var component = components[pin.ComponentId];
                // G-2 owner, G-3 path, G-4 pin number.
                Assert.IsTrue(closure[block].EffectiveComponentBindings.Targets.Any(t => t.ComponentId == pin.ComponentId), connection.Name);
                CollectionAssert.AreEqual(new[] { PsuCpuIds.Id(0x05, 1), component.SheetInstanceId }, pin.SheetInstancePath.ToArray(), connection.Name);
                Assert.IsTrue(parts[definitions[component.DefinitionId].PartId].Pins.Any(p => p.Number == pin.Pin), connection.Name);
            }
            // G-5: a Pin-only Signal lies in one circuit net.
            if (connection.Kind == DiagramConnectionKind.Signal && pins.Length == connection.Endpoints.Length)
                Assert.IsTrue(circuit.Nets.Any(n => pins.All(p => n.Pins.Contains(new(p.Item2.ComponentId, p.Item2.Pin)))), connection.Name);
        }
        Assert.AreEqual(DiagramEndpointKind.Compatible, connections.Single(c => c.Revision.Selection.ConnectionId == PsuCpuIds.Id(0x16, 0x1f)).Revision.Endpoints[0].Kind);
        Assert.AreEqual(PsuCpuFixtureBuilder.MemoryInterfaceRequirements,
            graph.Connections(PsuCpuIds.Id(0x11, 3)).Requirements(new(PsuCpuIds.Id(0x16, 0x1d), PsuCpuIds.Id(0x17, 0x1d), PsuCpuIds.Id(0x18, 0x1d))).Requirements);
        // G-6: storage and wire codecs are lossless.
        string xml = PsuCpuFixture.ReadText("system.blocks.xml");
        Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(RecursiveBlockGraphXml.Read(xml)));
        Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(RecursiveBlockCodec.Decode(
            P.RecursiveBlockGraphData.Parser.ParseFrom(RecursiveBlockCodec.Encode(graph).ToByteArray()))));
    }

    [TestMethod]
    public void AnnotationsAndTopLevelConnectionsStayInTheirLocalDiagrams()
    {
        var graph = PsuCpuFixture.Graph();
        var revisions = graph.Walk(graph.SelectedRoot).Select(graph.Inspect).ToDictionary(r => r.Name);
        CollectionAssert.AreEqual(new[] { 0x01, 0x02, 0x06 }.Select(c => PsuCpuIds.Id(0x16, c)).ToArray(),
            revisions["System"].LocalDiagram.Connections.Select(c => c.ConnectionId).ToArray());
        CollectionAssert.AreEqual(new[] { 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x10, 0x11, 0x14, 0x15 }.Select(c => PsuCpuIds.Id(0x16, c)).ToArray(),
            revisions["PSU"].LocalDiagram.Connections.Select(c => c.ConnectionId).ToArray());
        CollectionAssert.AreEqual(new[] { 0x18, 0x19, 0x1a, 0x1d }.Select(c => PsuCpuIds.Id(0x16, c)).ToArray(),
            revisions["CPU"].LocalDiagram.Connections.Select(c => c.ConnectionId).ToArray());
        string Notes(string block) => string.Join("|", revisions[block].LocalDiagram.Notes.Select(n => $"{n.Target.Kind}:{n.Position?.X},{n.Position?.Y}:{n.Text}"));
        Assert.AreEqual("Canvas:40,260:Keep PSU replaceable as a unit.|Block:,:Explore a quieter supply.", Notes("System"));
        Assert.AreEqual("Canvas:40,300:Keep sensing away from switching nodes.", Notes("PSU"));
        Assert.AreEqual("Canvas:40,300:Compare memory-interface implementations.|Connection:,:Keep pin choices open for placement.", Notes("CPU"));
        Assert.IsTrue(revisions.Values.SelectMany(r => r.LocalDiagram.Interfaces).All(i => i.Intent.Length == 0));
        Assert.AreEqual(new DiagramRequirements("Supply CPU power and report rail status.", "Separate power conversion and telemetry.",
            "Keep high-current paths away from sensing."), graph.Requirements(revisions["PSU"].Selection).Requirements);
    }

    [TestMethod]
    public void ExpectedNativeAgreesWithAnIndependentBoundaryComputation()
    {
        var expected = PsuCpuFixture.ExpectedNative(PsuCpuStage.Complete);
        var circuit = PsuCpuFixture.Engineering().Circuit;
        var parts = PsuCpuFixture.Parts().ToDictionary(p => p.Id);
        var definitions = circuit.Sheets.SelectMany(s => s.Components).ToDictionary(d => d.Id);
        var references = circuit.Components.ToDictionary(c => c.Id, c => c.Reference);
        var keyOf = circuit.SheetInstances.Select((s, i) => (s.Id, Key: Sheets[i])).ToDictionary(s => s.Id, s => s.Key);
        Assert.AreEqual((PsuCpuSeed.Sheets, "leaf"), (expected.Seed, expected.NetNameRule));

        Guid root = Guid.NewGuid();
        foreach (var (sheet, index) in expected.Sheets.Select((s, i) => (s, i)))
        {
            var instance = circuit.SheetInstances[index];
            Assert.AreEqual((Sheets[index], instance.Id, instance.DefinitionId), (sheet.Key, sheet.ModelSheetInstance, sheet.Definition));
            Assert.AreEqual(instance.ParentId is Guid parent ? keyOf[parent] : null, sheet.Parent);
            var path = PsuCpuFixture.NativePath(index + 1, root);
            Assert.AreEqual(index == 0 ? (Guid?)null : path[^1], sheet.NativeSheetSymbol);
        }
        CollectionAssert.AreEqual(new[] { 1, 5, 6, 7 }.Select(n => PsuCpuIds.Id(0x20, n)).ToArray(), expected.Sheets.Select(s => s.NativeScreen).ToArray());
        CollectionAssert.AreEqual(new[] { "1|A4|fixture.kicad_sch", "2|A4|psu.kicad_sch", "3|A3|cpu.kicad_sch", "4|A4|cpu_power.kicad_sch" },
            expected.Sheets.Select(s => $"{s.Page}|{s.Paper}|{s.File}").ToArray());

        var components = circuit.Components.ToDictionary(c => c.Id);
        CollectionAssert.AreEqual(circuit.Symbols.Select(s =>
        {
            var component = components[s.ComponentId]; var part = parts[definitions[component.DefinitionId].PartId];
            return new PsuCpuExpectedSymbol(component.Id, s.Id, component.Reference, part.CacheKey, part.Name, s.Unit, keyOf[s.EffectiveSheetInstanceId(component)]);
        }).ToArray(), expected.Symbols.ToArray());
        Assert.AreEqual(11, expected.Symbols.Count);
        foreach (var (net, row) in circuit.Nets.Zip(expected.Nets))
        {
            Assert.AreEqual((net.Id, net.Name), (row.Net, row.Name));
            CollectionAssert.AreEquivalent(net.Pins.Select(p => new PsuCpuPinReference(references[p.ComponentId], p.Pin)).ToArray(), row.Pins.ToArray());
        }
        Assert.HasCount(circuit.Nets.Count, expected.Nets);

        // Independent computation: the sheets on which each net has pins, and sheet subtrees.
        var pinsOnSheets = PlacedPins(circuit).ToArray();
        var connected = circuit.Nets.SelectMany(n => n.Pins).ToHashSet();
        var isolated = pinsOnSheets.Where(p => !connected.Contains(p.Pin)).GroupBy(p => p.Reference)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Pin.Pin).Distinct().Order(StringComparer.Ordinal).ToArray());
        CollectionAssert.AreEquivalent(isolated.Keys.ToArray(), expected.IsolatedPins.Keys.ToArray());
        foreach (var (reference, numbers) in isolated) CollectionAssert.AreEqual(numbers, expected.IsolatedPins[reference].ToArray(), reference);
        var sheetsOf = circuit.Nets.ToDictionary(n => n.Name, n => pinsOnSheets.Where(p => n.Pins.Contains(p.Pin)).Select(p => keyOf[p.Sheet]).ToHashSet());
        var parentOf = circuit.SheetInstances.ToDictionary(s => keyOf[s.Id], s => s.ParentId is Guid parent ? keyOf[parent] : null);
        HashSet<string> Subtree(string key) => [.. Sheets.Where(s => { for (string? k = s; k is not null; k = parentOf[k]) if (k == key) return true; return false; })];
        string[] Crossing(string key)
        {
            if (parentOf[key] is null) return [];
            var inside = Subtree(key);
            return [.. sheetsOf.Where(n => n.Value.Overlaps(inside) && !n.Value.IsSubsetOf(inside)).Select(n => n.Key).Order(StringComparer.Ordinal)];
        }
        foreach (string key in Sheets)
        {
            CollectionAssert.AreEqual(Crossing(key), expected.HierarchicalLabels[key].ToArray(), key);
            if (parentOf[key] is not null) CollectionAssert.AreEqual(Crossing(key), expected.SheetPins[key].ToArray(), key);
            var present = sheetsOf.Where(n => n.Value.Contains(key)).Select(n => n.Key).Concat(Crossing(key))
                .Concat(Sheets.Where(child => parentOf[child] == key).SelectMany(Crossing)).Distinct().Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(present, expected.AllowedLabelNames[key].ToArray(), key);
            string[] local = [.. sheetsOf.Where(n => n.Value.SetEquals([key])).Select(n => n.Key).Order(StringComparer.Ordinal)];
            CollectionAssert.AreEqual(local, expected.RequiredLocalLabelNames.GetValueOrDefault(key, []).ToArray(), key);
        }
        Assert.IsFalse(expected.SheetPins.ContainsKey("ROOT"));
        CollectionAssert.AreEquivalent(new[] { "global_label", "power_symbol", "no_connect", "bus", "bus_entry", "unlisted_text",
            "cross_net_wire", "mem_sda_net" }, expected.Forbidden.ToArray());
        Assert.AreEqual(new PsuCpuPresentationPolicy(10, 50, true), expected.Presentation);

        // Erratum 2026-09-24: KiCad joins the LP3982 pins its symbol stacks at one point, so the unwired stages have
        // every pin alone except U2 pins 1 and 4, which form one native net. Complete keeps both in RAIL_B.
        Assert.IsEmpty(expected.JoinedPins);
        var railB = expected.Nets.Single(n => n.Name == "RAIL_B").Pins;
        Assert.IsTrue(railB.Contains(new("U2", "1")) && railB.Contains(new("U2", "4")));
        PsuCpuPinReference[][] stackedU2 = [[new("U2", "1"), new("U2", "4")]];
        var unwired = PsuCpuFixture.ExpectedNative(PsuCpuStage.Components);
        Assert.IsEmpty(unwired.Nets); Assert.AreEqual(220, unwired.IsolatedPins.Values.Sum(p => p.Count));
        CollectionAssert.AreEqual(stackedU2.Select(g => string.Join(" ", g)).ToArray(), unwired.JoinedPins.Select(g => string.Join(" ", g)).ToArray());
        CollectionAssert.AreEqual(new[] { "2", "3", "5", "6", "7", "8", "9" }, unwired.IsolatedPins["U2"].ToArray());
        Assert.AreEqual(222, unwired.IsolatedPins.Values.Sum(p => p.Count) + unwired.JoinedPins.Sum(g => g.Count), "Every pin is isolated or joined once.");
        Assert.IsTrue(unwired.HierarchicalLabels.Values.Concat(unwired.SheetPins.Values).Concat(unwired.AllowedLabelNames.Values).All(v => v.Count == 0));
        var psu = PsuCpuFixture.ExpectedNative(PsuCpuStage.PsuComponents);
        Assert.IsTrue(psu.Symbols.Count == 6 && psu.Symbols.All(s => s.Sheet == "PSU") && psu.IsolatedPins.Values.Sum(p => p.Count) == 35);
        CollectionAssert.AreEqual(stackedU2.Select(g => string.Join(" ", g)).ToArray(), psu.JoinedPins.Select(g => string.Join(" ", g)).ToArray());
        Assert.IsEmpty(PsuCpuFixture.ExpectedNative(PsuCpuStage.SheetsOnly).JoinedPins);
        Assert.IsEmpty(PsuCpuFixture.ExpectedNative(PsuCpuStage.SheetsOnly).Symbols);
        var rootOnly = PsuCpuFixture.ExpectedNative(PsuCpuStage.RootOnly);
        Assert.AreEqual((PsuCpuSeed.RootOnly, "ROOT"), (rootOnly.Seed, rootOnly.Sheets.Single().Key));
    }

    [TestMethod]
    public void StackedPinsComeFromOneUnitsExactDefinitionGeometry()
    {
        // Erratum 2026-09-24: the joined U2 pair is read from the frozen definition geometry, not from pin names.
        var stacked = PsuCpuFixture.StackedPins().Single();
        Assert.AreEqual(("Regulator_Linear:LP3982ILD-3.3", 1), (stacked.CacheKey, stacked.Unit));
        CollectionAssert.AreEqual(new[] { "1", "4" }, stacked.Numbers.ToArray());
        var regulator = PsuCpuFixture.Parts().Single(p => p.CacheKey == stacked.CacheKey);
        Assert.AreEqual(regulator.Pins.Single(p => p.Number == "1").Name, regulator.Pins.Single(p => p.Number == "4").Name,
            "Both stacked pins are the regulator output (OUT).");

        // False-positive guard: the four-unit processor draws pins of different units at the same local points
        // (independently counted here), and none of them is stacked, because each unit is its own placed symbol.
        var positions = new Dictionary<(string X, string Y), HashSet<int>>();
        foreach (var symbol in PsuCpuSexpr.Parse(PsuCpuFixture.ReadText("lib_symbols.kicad_sexpr")).Children("symbol")
            .Where(s => s.Value(1) == "Library:F28P659DK8PTPQ1"))
            foreach (var body in symbol.Children("symbol"))
                foreach (var pin in body.Children("pin"))
                {
                    var at = pin.Child("at");
                    if (!positions.TryGetValue((at.Value(1), at.Value(2)), out var units)) positions.Add((at.Value(1), at.Value(2)), units = []);
                    units.Add(int.Parse(body.Value(1).Split('_')[^2], CultureInfo.InvariantCulture));
                }
        Assert.IsTrue(positions.Values.Count(units => units.Count > 1) > 0, "The processor units share local pin positions.");
        Assert.IsFalse(PsuCpuFixture.StackedPins().Any(s => s.CacheKey == "Library:F28P659DK8PTPQ1"));
    }

    [TestMethod]
    public void ExpectedRealizationFollowsTheDerivationRules()
    {
        var expected = PsuCpuFixture.ExpectedRealization();
        var graph = PsuCpuFixture.Graph();
        var circuit = PsuCpuFixture.Engineering().Circuit;
        Assert.AreEqual((graph.DocumentId, circuit.Id), (expected.Graph, expected.Circuit));
        var references = circuit.Components.ToDictionary(c => c.Id, c => c.Reference);
        var names = graph.States.ToDictionary(s => s.BlockId, s => graph.Inspect(new(s.BlockId, s.Id, s.HeadRevisionId)).Name);
        var connections = graph.ConnectionArchives.SelectMany(a => a.Revisions.Select(r => (Owner: a.OwnerBlockId, Revision: r)))
            .ToDictionary(c => c.Revision.Selection.ConnectionId);
        var results = new Dictionary<Guid, (string Status, Guid[] Nets, int? Boundary)>();
        (string Status, Guid[] Nets, int? Boundary) Derive(Guid id)
        {
            if (results.TryGetValue(id, out var known)) return known;
            var (owner, revision) = connections[id];
            (string, Guid[], int?) result;
            if (!revision.Members.IsEmpty)
            {
                var members = revision.Members.Select(m => Derive(m.ConnectionId)).ToArray();
                string status = members.Any(m => m.Status == "Unresolved") ? "Unresolved" : members.Any(m => m.Status == "Conflict") ? "Conflict"
                    : members.All(m => m.Status == "Abstract") ? "Abstract" : members.All(m => m.Status == "Realized") ? "Realized" : "Partial";
                result = (status, [.. members.SelectMany(m => m.Nets).Distinct().OrderBy(n => circuit.Nets.Select(x => x.Id).ToList().IndexOf(n))], null);
            }
            else if (revision.Endpoints.Any(e => e.Kind is DiagramEndpointKind.Unresolved or DiagramEndpointKind.Compatible or DiagramEndpointKind.Candidates))
                result = ("Unresolved", [], null);
            else if (revision.Endpoints.All(e => e.Kind != DiagramEndpointKind.Pin)) result = ("Abstract", [], null);
            else
            {
                var pins = revision.Endpoints.Where(e => e.Kind == DiagramEndpointKind.Pin).Select(e => new PinEndpoint(e.Pin!.ComponentId, e.Pin.Pin)).ToArray();
                var nets = circuit.Nets.Where(n => pins.Any(p => n.Pins.Contains(p))).ToArray();
                if (nets.Length == 1 && pins.All(p => nets[0].Pins.Contains(p)))
                    result = ("Realized", [nets[0].Id], revision.Endpoints.Count(e => e.Kind == DiagramEndpointKind.Interface && e.BlockId == owner));
                else result = ("Conflict", [], null);
            }
            results[id] = result;
            return result;
        }
        Assert.HasCount(0x1f, expected.Connections);
        foreach (var row in expected.Connections)
        {
            var (owner, revision) = connections[row.Connection];
            Assert.AreEqual((names[owner], revision.Name, revision.Kind), (row.Owner, row.Name, row.Kind));
            CollectionAssert.AreEqual(revision.Members.Select(m => m.ConnectionId).ToArray(), row.Members.ToArray(), row.Name);
            var derived = Derive(row.Connection);
            Assert.AreEqual(derived.Status, row.Status, row.Name);
            CollectionAssert.AreEqual(derived.Nets, row.Nets.ToArray(), row.Name);
            Assert.AreEqual(derived.Boundary, row.Boundary, row.Name);
            var endpointPins = revision.Endpoints.Where(e => e.Kind == DiagramEndpointKind.Pin)
                .Select(e => new PsuCpuPinReference(references[e.Pin!.ComponentId], e.Pin.Pin)).ToArray();
            CollectionAssert.AreEqual(endpointPins, row.Pins.ToArray(), row.Name);
            var netPins = circuit.Nets.Where(n => row.Nets.Contains(n.Id)).SelectMany(n => n.Pins)
                .Select(p => new PsuCpuPinReference(references[p.ComponentId], p.Pin)).ToHashSet();
            var anyNet = circuit.Nets.SelectMany(n => n.Pins).Select(p => new PsuCpuPinReference(references[p.ComponentId], p.Pin)).ToHashSet();
            Assert.IsTrue(row.Pins.All(p => row.Nets.Count == 0 ? !anyNet.Contains(p) : netPins.Contains(p)), row.Name);
        }
        var memory = expected.Connections.Single(c => c.Connection == PsuCpuIds.Id(0x16, 0x1d));
        Assert.AreEqual("Unresolved", memory.Status);
        CollectionAssert.AreEqual(new[] { PsuCpuIds.Id(0x08, 11) }, memory.Nets.ToArray());
    }

    [TestMethod]
    public void ExpectedMigrationDescribesTheFlatStructure()
    {
        var migration = PsuCpuFixture.ExpectedMigration();
        var flat = PsuCpuFixture.FlatStructure().Structure;
        var graph = PsuCpuFixture.Graph();
        Assert.AreEqual(flat.Id, migration.Structure);
        var blocks = flat.Blocks.ToDictionary(b => b.Id);
        var ports = flat.Ports.ToDictionary(p => p.Id);
        CollectionAssert.AreEqual(flat.Blocks.Where(b => b.ParentId is null).Select(b => b.Id).ToArray(), migration.RootChildren.ToArray());
        foreach (var (parent, children) in migration.Children)
            CollectionAssert.AreEqual(flat.Blocks.Where(b => b.ParentId == parent).Select(b => b.Id).ToArray(), children.ToArray());
        CollectionAssert.AreEquivalent(flat.Blocks.Where(b => flat.Blocks.Any(c => c.ParentId == b.Id)).Select(b => b.Id).ToArray(), migration.Children.Keys.ToArray());
        CollectionAssert.AreEqual(flat.Blocks.Select(b => b.Id).ToArray(), migration.Blocks.Select(b => b.Block).ToArray());
        var interfaces = graph.Revisions.ToDictionary(r => r.Selection.BlockId, r => r.LocalDiagram.Interfaces.Select(i => i.Id).ToArray());
        foreach (var block in migration.Blocks)
        {
            Assert.AreEqual(blocks[block.Block].Name, block.Name);
            CollectionAssert.AreEqual(flat.Ports.Where(p => p.BlockId == block.Block).Select(p => p.Id).ToArray(), block.Interfaces.ToArray(), block.Name);
            CollectionAssert.IsSubsetOf(block.Interfaces.ToArray(), interfaces[block.Block], block.Name);
            CollectionAssert.AreEqual(blocks[block.Block].ComponentIds.ToArray(), block.ComponentBindings.ToArray(), block.Name);
        }
        // Independent level placement: every endpoint block is the diagram owner or its direct child.
        Guid? Level(StructuralConnection connection)
        {
            var ends = new[] { blocks[ports[connection.FirstPortId].BlockId], blocks[ports[connection.SecondPortId].BlockId] };
            foreach (Guid? owner in flat.Blocks.Select(b => (Guid?)b.Id).Prepend(null))
                if (ends.All(e => e.ParentId == owner || (owner is not null && e.Id == owner))) return owner ?? Guid.Empty;
            return null;
        }
        var levels = flat.Connections.ToDictionary(c => c.Id, Level);
        foreach (var diagram in migration.LocalDiagrams)
        {
            Guid owner = diagram.Owner ?? Guid.Empty;
            CollectionAssert.AreEqual(flat.Connections.Where(c => levels[c.Id] == owner).Select(c => c.Id).ToArray(), diagram.Connections.ToArray());
            CollectionAssert.AreEqual(flat.Connections.Where(c => levels[c.Id] == owner && (blocks[ports[c.FirstPortId].BlockId].Id == owner
                || blocks[ports[c.SecondPortId].BlockId].Id == owner)).Select(c => c.Id).ToArray(), diagram.BoundaryEndpointConnections.ToArray());
        }
        CollectionAssert.AreEqual(flat.Connections.Where(c => levels[c.Id] is null).Select(c => c.Id).ToArray(), migration.CrossLevelConnections.ToArray());
        CollectionAssert.AreEqual(new[] { PsuCpuIds.Id(0x30, 0x10) }, migration.CrossLevelConnections.ToArray());
        Assert.IsFalse(migration.LocalDiagrams.Any(d => d.Connections.Intersect(migration.CrossLevelConnections).Any()));
        var kept = migration.LocalDiagrams.SelectMany(d => d.Connections).ToHashSet();
        CollectionAssert.AreEquivalent(kept.ToArray(), migration.RealizationLinks.Keys.ToArray());
        // Realization links are sets; the structure codec stores them in identity order.
        foreach (var (connection, nets) in migration.RealizationLinks)
            CollectionAssert.AreEquivalent(flat.Connections.Single(c => c.Id == connection).NetIds.ToArray(), nets.ToArray());
        CollectionAssert.AreEqual(flat.Statements.Select(s => s.Id).ToArray(), migration.UnclassifiedStatements.ToArray());
        foreach (var layout in migration.Layouts)
            CollectionAssert.AreEqual((layout.Owner is null ? migration.RootChildren : migration.Children[layout.Owner.Value]).ToArray(), layout.Blocks.ToArray());
        CollectionAssert.IsSubsetOf(migration.Layouts.SelectMany(l => l.Blocks).ToArray(), flat.Presentation!.Blocks.Select(b => b.BlockId).ToArray());
        Assert.AreEqual(RequirementRevisionActor.Import, migration.OriginActorKind);
    }

    [TestMethod]
    public void NativeAssertionReportsMissingSheetsWithTheContractCode()
    {
        // An empty snapshot of another root: every expected sheet and the net comparison are categorized differences.
        var design = new SchematicDesign(PsuCpuFixture.Engineering(PsuCpuStage.SheetsOnly), new(), [], []);
        var state = new KiCad.Automation.Protocol.SchematicElectricalState
            { Hierarchy = new() { Data = new() { Document = new() }, Revision = new() { Epoch = "fixture", Sequence = 1 } } };
        var error = Assert.ThrowsExactly<AssertFailedException>(() => PsuCpuFixture.AssertNative(design, state, PsuCpuStage.SheetsOnly));
        Code(error, "psu_cpu_native_mismatch");
        foreach (string expected in Sheets.Select(s => $"sheet: {s} has no sheet binding").Append("net: the native snapshot cannot be compared"))
            Assert.IsTrue(error.Message.Contains(expected, StringComparison.Ordinal), error.Message);
        Assert.IsFalse(error.Message.Contains("symbol:", StringComparison.Ordinal), error.Message);
    }

    [TestMethod]
    public void SymbolCaptureTakesTheFilePinIdentitiesOrReportsTheContractCode()
    {
        string file = PsuCpuFixture.ReadText("lib_symbols.kicad_sexpr");
        var pins = PsuCpuFixtureBuilder.LibraryPins(file);
        // A complete capture: whatever identities the editor reported, every pin gets the file's K21 identity.
        var captured = PsuCpuFixture.CapturePartSymbols(Capture(pins));
        CollectionAssert.AreEqual(PsuCpuFixture.Parts().Select(p => (p.Id, p.CacheKey, p.Library, p.Entry, 1)).ToArray(),
            captured.Select(s => (s.PartId, s.Symbol.CacheKey, s.LibraryId.LibraryNickname, s.LibraryId.EntryName, s.BodyStyle)).ToArray());
        Assert.IsEmpty(PsuCpuFixture.CapturedPinIdentityIssues(captured, file));

        // Must-catch: nothing captured, not exactly the eight cache keys, or no lib_symbols list at all.
        Code(Assert.ThrowsExactly<AssertFailedException>(() => PsuCpuFixture.CapturePartSymbols(new())), "psu_cpu_symbol_capture_incomplete");
        Code(Assert.ThrowsExactly<AssertFailedException>(() => PsuCpuFixture.CapturePartSymbols(
            Capture(pins.Where(p => p.CacheKey != "pic_programmer:24C16")))), "psu_cpu_symbol_capture_incomplete");
        Code(Assert.ThrowsExactly<AssertFailedException>(() => PsuCpuFixtureBuilder.LibraryPins("(foo)")), "psu_cpu_symbol_capture_incomplete");

        // Must-catch for the seed journey's pin check: identities that no longer name the file's pins.
        List<string> Issues(IEnumerable<PsuCpuFixtureBuilder.LibraryPin> reported) =>
            PsuCpuFixture.CapturedPinIdentityIssues(PsuCpuFixture.CapturePartSymbols(Capture(reported)), file);
        void Reports(List<string> issues, params string[] expected)
        {
            foreach (string issue in expected) Assert.IsTrue(issues.Contains(issue), string.Join("; ", issues));
        }
        var first = pins.First(p => p.CacheKey == "Library:F28P659DK8PTPQ1");
        var lost = Issues(pins.Where(p => p != first));
        Reports(lost, $"Library:F28P659DK8PTPQ1 unit {first.Unit} style {first.Style} pin {first.Number} was not captured");
        Assert.IsTrue(lost.Any(i => i.Contains(" has identity ", StringComparison.Ordinal)), "Later identities shift: " + string.Join("; ", lost));
        Reports(Issues(pins.Select(p => p.Style == 0 ? p with { Style = 1 } : p)),
            "pic_programmer:24C16 unit 0 style 1 pin 4 is not in lib_symbols.kicad_sexpr", "pic_programmer:24C16 unit 0 style 0 pin 4 was not captured");
        Reports(Issues(pins.Append(pins[0])), $"{pins[0].CacheKey} unit {pins[0].Unit} style {pins[0].Style} pin {pins[0].Number} is captured twice");

        var swapped = captured.Select(s => s with { Symbol = s.Symbol.Clone() }).ToArray();
        var connector = swapped.Single(s => s.Symbol.CacheKey == "Connector_Generic:Conn_01x02").Symbol.Definition.Items
            .Where(i => i.Item.Is(SchematicPin.Descriptor)).ToArray();
        var (one, two) = (connector[0].Item.Unpack<SchematicPin>(), connector[1].Item.Unpack<SchematicPin>());
        (one.Id, two.Id) = (two.Id, one.Id);
        (connector[0].Item, connector[1].Item) = (Any.Pack(one), Any.Pack(two));
        var exchanged = PsuCpuFixture.CapturedPinIdentityIssues(swapped, file);
        Assert.HasCount(2, exchanged);
        Assert.IsTrue(exchanged.All(i => i.StartsWith("Connector_Generic:Conn_01x02 unit 1 style 1 pin ", StringComparison.Ordinal)
            && i.Contains(" has identity ", StringComparison.Ordinal)), string.Join("; ", exchanged));

        const string Repeated = "(lib_symbols (symbol \"A:B\" (symbol \"B_1_1\" (pin passive line (name \"x\") (number \"1\") (uuid \"u1\"))"
            + " (pin passive line (name \"y\") (number \"1\") (uuid \"u2\")))))";
        Reports(PsuCpuFixture.CapturedPinIdentityIssues([], Repeated), "A:B unit 1 style 1 pin 1 occurs twice in lib_symbols.kicad_sexpr",
            "A:B unit 1 style 1 pin 1 was not captured");
    }

    [TestMethod]
    public void SeedHierarchyCheckAcceptsExactlyTheSeedOrReportsTheContractCode()
    {
        Guid root = Guid.NewGuid();
        string At(params int[] symbols) => string.Join('/', symbols.Select(n => PsuCpuIds.Id(0x20, n).ToString("D")).Prepend(root.ToString("D")));
        Assert.IsEmpty(PsuCpuFixture.SeedHierarchyIssues(PsuCpuFixture.SeedHierarchy(root, PsuCpuSeed.Sheets), PsuCpuSeed.Sheets, root));
        Assert.IsEmpty(PsuCpuFixture.SeedHierarchyIssues(PsuCpuFixture.SeedHierarchy(root, PsuCpuSeed.RootOnly), PsuCpuSeed.RootOnly, root));
        PsuCpuFixture.RequireSeedHierarchy(PsuCpuFixture.SeedHierarchy(root, PsuCpuSeed.Sheets), PsuCpuSeed.Sheets, root);

        void Mismatch(SchematicHierarchyData data, PsuCpuSeed seed, params string[] expected)
        {
            var error = Assert.ThrowsExactly<AssertFailedException>(() => PsuCpuFixture.RequireSeedHierarchy(data, seed, root));
            Code(error, "psu_cpu_seed_hierarchy_mismatch");
            foreach (string issue in expected) Assert.IsTrue(error.Message.Contains(issue, StringComparison.Ordinal), error.Message);
        }
        Mismatch(new(), PsuCpuSeed.Sheets, "0 screens instead of 4", $"ROOT is not loaded at {At()}", $"CPU_POWER is not loaded at {At(3, 4)}");
        Mismatch(new(), PsuCpuSeed.RootOnly, "0 screens instead of 1", $"ROOT is not loaded at {At()}");
        var paper = PsuCpuFixture.SeedHierarchy(root, PsuCpuSeed.Sheets);
        paper.Instances.Single(s => s.Metadata.ScreenId.Value == PsuCpuIds.Id(0x20, 6).ToString("D")).Metadata.Page.PageSize = PageSize.PsA4;
        Mismatch(paper, PsuCpuSeed.Sheets, "CPU paper differs");
        Mismatch(PsuCpuFixture.SeedHierarchy(root, PsuCpuSeed.Sheets), PsuCpuSeed.RootOnly, "4 screens instead of 1");
        Mismatch(PsuCpuFixture.SeedHierarchy(root, PsuCpuSeed.RootOnly), PsuCpuSeed.Sheets, "1 screens instead of 4", $"PSU is not loaded at {At(2)}");
        Mismatch(PsuCpuFixture.SeedHierarchy(Guid.NewGuid(), PsuCpuSeed.Sheets), PsuCpuSeed.Sheets, $"ROOT is not loaded at {At()}");
        var screen = PsuCpuFixture.SeedHierarchy(root, PsuCpuSeed.Sheets);
        screen.Instances.Single(s => s.Metadata.ScreenId.Value == PsuCpuIds.Id(0x20, 7).ToString("D")).Metadata.ScreenId.Value = Guid.NewGuid().ToString("D");
        Mismatch(screen, PsuCpuSeed.Sheets, "CPU_POWER screen identity differs");
        var pinned = PsuCpuFixture.SeedHierarchy(root, PsuCpuSeed.Sheets);
        var psu = pinned.Instances[0].Items[0].Unpack<SheetSymbol>();
        psu.Pins.Add(new SheetPin { Id = new() { Value = Guid.NewGuid().ToString("D") } });
        pinned.Instances[0].Items[0] = Any.Pack(psu);
        Mismatch(pinned, PsuCpuSeed.Sheets, "PSU sheet symbol differs");
    }

    [TestMethod]
    public async Task BaselineResolvesOnlyTheSeededSheetsOrReportsTheContractCode()
    {
        Guid root = Guid.NewGuid();
        var token = CancellationToken.None;
        var sheets = PsuCpuFixture.Baseline(PsuCpuFixture.SeedHierarchy(root, PsuCpuSeed.Sheets), PsuCpuSeed.Sheets, root, token);
        Assert.AreEqual(EngineeringDesignXml.Write(PsuCpuFixture.Engineering(PsuCpuStage.SheetsOnly), []), EngineeringDesignXml.Write(sheets.Engineering, []));
        CollectionAssert.AreEqual(Enumerable.Range(1, 4).Select(n => (PsuCpuIds.Id(0x05, n), string.Join('/', PsuCpuFixture.NativePath(n, root)))).ToArray(),
            sheets.SheetBindings.Select(b => (b.SheetInstanceId, string.Join('/', b.NativePath))).ToArray());
        Assert.IsTrue(sheets.SymbolBindings.Count == 0 && sheets.PartSymbols is null);
        var rootOnly = PsuCpuFixture.Baseline(PsuCpuFixture.SeedHierarchy(root, PsuCpuSeed.RootOnly), PsuCpuSeed.RootOnly, root, token);
        Assert.AreEqual((PsuCpuIds.Id(0x05, 1), root), (rootOnly.SheetBindings.Single().SheetInstanceId, rootOnly.SheetBindings.Single().NativePath.Single()));

        void Unresolved(SchematicHierarchyData native, PsuCpuSeed seed, Guid instance, string expected)
        {
            var error = Assert.ThrowsExactly<AssertFailedException>(() => PsuCpuFixture.Baseline(native, seed, instance, token));
            Code(error, "psu_cpu_baseline_unresolved");
            Assert.IsTrue(error.Message.Contains(expected, StringComparison.Ordinal), error.Message);
        }
        Unresolved(new() { Document = PsuCpuFixture.SeedHierarchy(root, PsuCpuSeed.Sheets).Document }, PsuCpuSeed.Sheets, root, "native_missing_root");
        Unresolved(PsuCpuFixture.SeedHierarchy(root, PsuCpuSeed.RootOnly), PsuCpuSeed.Sheets, root, "missing_native_sheet");
        Unresolved(PsuCpuFixture.SeedHierarchy(root, PsuCpuSeed.Sheets), PsuCpuSeed.RootOnly, root, "unmapped_native_sheet");
        Unresolved(PsuCpuFixture.SeedHierarchy(root, PsuCpuSeed.Sheets), PsuCpuSeed.Sheets, Guid.NewGuid(), "missing_native_sheet");
        Unresolved(PsuCpuFixture.SeedHierarchy(root, PsuCpuSeed.Sheets), PsuCpuSeed.None, root, "Seed None has no native baseline");

        // Seed None prepares no baseline, so neither a desired design nor recovery state can be made from it.
        var none = new PsuCpuNativeContext("unused", new(), root, PsuCpuSeed.None, [], null,
            "hardware.xml", "system.blocks.xml", "design.xml", "flat-structure.engineering.xml");
        Code(Assert.ThrowsExactly<AssertFailedException>(() => PsuCpuFixture.Desired(none, PsuCpuStage.Complete)), "psu_cpu_baseline_unresolved");
        Code(await Assert.ThrowsExactlyAsync<AssertFailedException>(() => PsuCpuFixture.InitializeRecoveryAsync(null!, none, "unused", token)),
            "psu_cpu_baseline_unresolved");

        // With a baseline, Desired keeps its native side and declares exactly the stage's part symbols.
        var symbols = PsuCpuFixture.CapturePartSymbols(Capture(PsuCpuFixtureBuilder.LibraryPins(PsuCpuFixture.ReadText("lib_symbols.kicad_sexpr"))));
        var prepared = none with { Seed = PsuCpuSeed.Sheets, Baseline = sheets, PartSymbols = symbols };
        var desired = PsuCpuFixture.Desired(prepared, PsuCpuStage.PsuComponents);
        Assert.AreEqual(EngineeringDesignXml.Write(PsuCpuFixture.Engineering(PsuCpuStage.PsuComponents), []), EngineeringDesignXml.Write(desired.Engineering, []));
        Assert.IsTrue(ReferenceEquals(sheets.Schematic, desired.Schematic) && ReferenceEquals(sheets.SheetBindings, desired.SheetBindings));
        CollectionAssert.AreEqual(Enumerable.Range(1, 6).Select(n => PsuCpuIds.Id(0x03, n)).ToArray(), desired.PartSymbols!.Select(s => s.PartId).ToArray());
        Assert.IsNull(PsuCpuFixture.Desired(prepared, PsuCpuStage.SheetsOnly).PartSymbols);
    }

    /// <summary>A native-shaped S0 capture holding the given definition pins with the unit and body style
    /// the editor reports, in reverse file order and with identities the editor allocated.</summary>
    private static SchematicHierarchyData Capture(IEnumerable<PsuCpuFixtureBuilder.LibraryPin> pins)
    {
        var screen = new SchematicScreenData();
        foreach (var symbol in Enumerable.Reverse(pins.ToArray()).GroupBy(p => p.CacheKey, StringComparer.Ordinal))
        {
            var definition = new SchematicSymbol();
            foreach (var pin in symbol)
                definition.Items.Add(new SchematicSymbolChild
                {
                    Item = Any.Pack(new SchematicPin { Id = new() { Value = Guid.NewGuid().ToString("D") }, Number = pin.Number, Name = pin.Name }),
                    Unit = new() { Unit = pin.Unit }, BodyStyle = new() { Style = pin.Style }
                });
            screen.CachedSymbols.Add(new SchematicCachedSymbol { CacheKey = symbol.Key, Definition = definition });
        }
        var data = new SchematicHierarchyData();
        data.Instances.Add(screen);
        return data;
    }

    [TestMethod]
    public void LibrarySymbolsHoldTheEightDefinitionsWithNormalizedPinIdentities()
    {
        string text = PsuCpuFixture.ReadText("lib_symbols.kicad_sexpr");
        var list = PsuCpuSexpr.Parse(text);
        Assert.AreEqual("lib_symbols", list.Head);
        var parts = PsuCpuFixture.Parts();
        CollectionAssert.AreEqual(parts.Select(p => p.CacheKey).Order(StringComparer.Ordinal).ToArray(),
            list.Children("symbol").Select(s => s.Value(1)).ToArray());
        Assert.IsFalse(list.Children("symbol").Any(s => s.Children("extends").Any() || s.Children("power").Any()));
        var pins = PsuCpuFixtureBuilder.LibraryPins(text);
        Assert.HasCount(222, pins);
        CollectionAssert.AreEqual(Enumerable.Range(1, 222).Select(k => PsuCpuIds.Id(0x21, k).ToString("D")).ToArray(), pins.Select(p => p.Id).ToArray());
        foreach (var part in parts)
        {
            var own = pins.Where(p => p.CacheKey == part.CacheKey).ToArray();
            CollectionAssert.AreEquivalent(part.Pins.ToArray(), own.Where(p => p.Style is 0 or 1).Select(p => new PartPin(p.Number, p.Name, p.Unit)).ToArray(), part.CacheKey);
            Assert.AreEqual(part.Units, own.Max(p => p.Unit), part.CacheKey);
        }
        Assert.AreEqual(text, PsuCpuFixtureBuilder.NormalizePinIdentities(text));
    }

    [TestMethod]
    public void SeedsDeclareTheExactNativeIdentitiesAndPaths()
    {
        Guid root = Guid.NewGuid();
        string Id(int n) => PsuCpuIds.Id(0x20, n).ToString("D");
        var sheets = PsuCpuFixture.SeedFiles(PsuCpuSeed.Sheets, root).ToDictionary(f => f.Key, f => PsuCpuSexpr.Parse(f.Value));
        CollectionAssert.AreEquivalent(new[] { "fixture.kicad_sch", "psu.kicad_sch", "cpu.kicad_sch", "cpu_power.kicad_sch" }, sheets.Keys.ToArray());
        foreach (var (file, screen, paper, children) in new[] { ("fixture.kicad_sch", 1, "A4", 2), ("psu.kicad_sch", 5, "A4", 0),
                     ("cpu.kicad_sch", 6, "A3", 1), ("cpu_power.kicad_sch", 7, "A4", 0) })
        {
            var document = sheets[file];
            Assert.AreEqual(("20250114", Id(screen), paper), (document.Child("version").Value(1), document.Child("uuid").Value(1), document.Child("paper").Value(1)), file);
            Assert.HasCount(1, document.Child("lib_symbols").Items!, file);
            Assert.HasCount(children, document.Children("sheet").ToArray(), file);
            Assert.IsFalse(document.Children("symbol").Any() || document.Children("label").Any(), file);
        }
        string Sheet(PsuCpuSexpr sheet) => string.Join("|", sheet.Child("at").Value(1), sheet.Child("at").Value(2), sheet.Child("size").Value(1),
            sheet.Child("size").Value(2), sheet.Child("uuid").Value(1), string.Join(",", sheet.Children("property").Select(p => p.Value(1) + "=" + p.Value(2))),
            sheet.Child("instances").Child("project").Child("path").Value(1), sheet.Child("instances").Child("project").Child("path").Child("page").Value(1),
            sheet.Children("pin").Count());
        CollectionAssert.AreEqual(new[] { $"50.8|50.8|38.1|50.8|{Id(2)}|Sheetname=PSU,Sheetfile=psu.kicad_sch|/{root}|2|0",
            $"152.4|50.8|38.1|50.8|{Id(3)}|Sheetname=CPU,Sheetfile=cpu.kicad_sch|/{root}|3|0" },
            sheets["fixture.kicad_sch"].Children("sheet").Select(Sheet).ToArray());
        Assert.AreEqual($"330.2|25.4|38.1|25.4|{Id(4)}|Sheetname=CPU_POWER,Sheetfile=cpu_power.kicad_sch|/{root}/{Id(3)}|4|0",
            Sheet(sheets["cpu.kicad_sch"].Child("sheet")));
        CollectionAssert.AreEqual(new[] { $"{root}", $"{root}/{Id(2)}", $"{root}/{Id(3)}", $"{root}/{Id(3)}/{Id(4)}" },
            Enumerable.Range(1, 4).Select(n => string.Join('/', PsuCpuFixture.NativePath(n, root))).ToArray());

        var rootOnly = PsuCpuFixture.SeedFiles(PsuCpuSeed.RootOnly, root).Single();
        var s2 = PsuCpuSexpr.Parse(rootOnly.Value);
        Assert.AreEqual(("fixture.kicad_sch", Id(1), "A4", 0), (rootOnly.Key, s2.Child("uuid").Value(1), s2.Child("paper").Value(1), s2.Children("sheet").Count()));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PsuCpuFixture.SeedFiles(PsuCpuSeed.None, root));

        string libSymbols = PsuCpuFixture.ReadText("lib_symbols.kicad_sexpr");
        string catalog = PsuCpuFixture.CatalogSchematic(libSymbols, root);
        var s0 = PsuCpuSexpr.Parse(catalog);
        Assert.AreEqual(("A0", Id(1)), (s0.Child("paper").Value(1), s0.Child("uuid").Value(1)));
        Assert.AreEqual(libSymbols, PsuCpuFixtureBuilder.ExtractLibrarySymbols(catalog));
        var placements = s0.Children("symbol").ToArray();
        Assert.HasCount(11, placements);
        foreach (var (placement, index) in placements.Select((p, i) => (p, i)))
        {
            var occurrence = PsuCpuFixtureBuilder.Occurrences[index];
            var component = PsuCpuFixtureBuilder.Components[occurrence.Component - 1];
            var path = placement.Child("instances").Child("project").Child("path");
            Assert.AreEqual((PsuCpuIds.Id(0x20, 0x10 + index).ToString("D"), PsuCpuFixtureBuilder.PartSources[component.Part - 1].CacheKey,
                    occurrence.Unit.ToString(CultureInfo.InvariantCulture), (101.6m * (index % 6 + 1)).ToString(CultureInfo.InvariantCulture),
                    (203.2m * (index / 6 + 1)).ToString(CultureInfo.InvariantCulture), $"/{root}", component.Reference),
                (placement.Child("uuid").Value(1), placement.Child("lib_id").Value(1), placement.Child("unit").Value(1), placement.Child("at").Value(1),
                    placement.Child("at").Value(2), path.Value(1), path.Child("reference").Value(1)));
        }
    }

    // Both kicad-cli checks borrow the "NativeSession" category only as an exclusion marker. The
    // general managed pass (the automation graph's unit check) and this fixture's fixture-contracts
    // check filter on TestCategory!=NativeSession and build no kicad-cli, so without the marker they
    // would run these tests and fail on the missing binary. Giving them their own exclusion would mean
    // changing parent-held filters that this freeze keeps as they were. The tests start no native
    // editor session; each runs only where its own category is selected: PsuCpuFixtureNative in the
    // psu-cpu-fixture graph and PsuCpuFixtureWriterDrift in the psu-cpu-fixture-writer-drift graph.

    /// <summary>GATING (psu-cpu-fixture, symbol-normalization). This fork's eeschema must still read the
    /// pinned sources to exactly the frozen definition pins (symbol, unit, body style, number, name and
    /// K21 identity), and loading and saving the frozen list must keep every one of them. Nothing else
    /// is compared, so a change in how eeschema writes other tokens does not block the lanes; the
    /// byte-for-byte comparison is the non-gating LibrarySymbolsAreByteForByteThisForksEeschemaOutput.
    /// On a pin difference the eeschema reading is written to automation/artifacts/psu-cpu-fixture/native.</summary>
    [TestMethod, TestCategory("NativeSession"), TestCategory("PsuCpuFixtureNative")]
    public async Task LibrarySymbolPinsAreThisForksEeschemaReadingOfThePinnedSources()
    {
        string cli = EeschemaCli();
        string evidence = OutputDirectory("native");
        string work = Directory.CreateTempSubdirectory("psu-cpu-symbols-").FullName;
        try
        {
            string frozen = PsuCpuFixture.ReadText("lib_symbols.kicad_sexpr");
            var failures = new List<string>();
            string normalized = PsuCpuFixtureBuilder.NormalizePinIdentities(
                await SaveWithEeschema(cli, work, evidence, "pinned-sources", PinnedSourceSymbols()));
            if (PinDifferences(frozen, normalized) is { Count: > 0 } read)
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, "lib_symbols.kicad_sexpr"), normalized, new UTF8Encoding(false));
                failures.Add("the pinned sources no longer read as the frozen pins: " + string.Join(", ", read));
            }
            if (PinDifferences(frozen, await SaveWithEeschema(cli, work, evidence, "fixed-point", frozen)) is { Count: > 0 } kept)
                failures.Add("loading and saving the frozen definitions changes their pins: " + string.Join(", ", kept));
            if (failures.Count > 0) throw PsuCpuFixture.Failure("psu_cpu_fixture_builder_drift", string.Join("; ", failures));
        }
        finally { Directory.Delete(work, true); }
    }

    /// <summary>NON-GATING (graph psu-cpu-fixture-writer-drift, which no other graph requires).
    /// lib_symbols.kicad_sexpr must be byte for byte what this fork's eeschema writes for the pinned
    /// sources (identities eeschema allocates for legacy graphics aside), and loading and saving it
    /// must reproduce it exactly. A failure here while the gating pin check passes means only that the
    /// symbol writer changed, for example after a schematic file-format bump. It does not block the
    /// lanes: the parent takes it as the trigger for a version-2 re-freeze, starting from the
    /// replacement written to automation/artifacts/psu-cpu-fixture/writer-drift.</summary>
    [TestMethod, TestCategory("NativeSession"), TestCategory("PsuCpuFixtureWriterDrift")]
    public async Task LibrarySymbolsAreByteForByteThisForksEeschemaOutput()
    {
        string cli = EeschemaCli();
        string evidence = OutputDirectory("writer-drift");
        string work = Directory.CreateTempSubdirectory("psu-cpu-symbols-").FullName;
        try
        {
            string frozen = PsuCpuFixture.ReadText("lib_symbols.kicad_sexpr");
            var failures = new List<string>();
            string normalized = PsuCpuFixtureBuilder.NormalizePinIdentities(
                await SaveWithEeschema(cli, work, evidence, "pinned-sources", PinnedSourceSymbols()));
            if (PsuCpuFixtureBuilder.WithoutAllocatedIdentities(normalized) != PsuCpuFixtureBuilder.WithoutAllocatedIdentities(frozen))
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, "lib_symbols.kicad_sexpr"), normalized, new UTF8Encoding(false));
                failures.Add("lib_symbols.kicad_sexpr is no longer byte for byte this fork's normalization of the pinned sources");
            }
            if (await SaveWithEeschema(cli, work, evidence, "fixed-point", frozen) != frozen)
                failures.Add("loading and saving the frozen definitions changes their bytes");
            if (failures.Count > 0)
                throw PsuCpuFixture.Failure("psu_cpu_fixture_builder_drift", string.Join("; ", failures)
                    + ". The symbol writer changed: the lanes are not blocked, and the parent re-freezes the fixture as version 2.");
        }
        finally { Directory.Delete(work, true); }
    }

    private static string EeschemaCli()
    {
        Assert.IsTrue(OperatingSystem.IsLinux(), "The normalization evidence is Linux evidence.");
        string cli = Path.Combine(PsuCpuFixture.RepositoryRoot, "automation", "artifacts", "native", "kicad", "kicad-cli");
        Assert.IsTrue(File.Exists(cli), "Build kicad-cli and the eeschema kiface first.");
        return cli;
    }

    /// <summary>The eight definitions copied verbatim from their sources at the blobs fixture.json pins.</summary>
    private static string PinnedSourceSymbols() => PsuCpuFixtureBuilder.RawLibrarySymbols(PsuCpuFixture.RepositoryRoot,
        PsuCpuFixture.Manifest.GetProperty("symbols").EnumerateArray()
            .ToDictionary(s => s.GetProperty("cacheKey").GetString()!, s => s.GetProperty("gitBlob").GetString()!));

    /// <summary>Definition pins that differ between two lib_symbols lists, compared only by symbol, unit,
    /// body style, number, name and identity (at most ten from each side); empty when they agree.</summary>
    private static List<string> PinDifferences(string expected, string actual)
    {
        static string[] Pins(string text) => [.. PsuCpuFixtureBuilder.LibraryPins(text)
            .Select(p => $"{p.CacheKey} unit {p.Unit} style {p.Style} pin {p.Number} \"{p.Name}\" {p.Id}")];
        string[] frozen = Pins(expected), observed = Pins(actual);
        if (frozen.SequenceEqual(observed)) return [];
        List<string> differences = [.. frozen.Except(observed).Take(10).Select(p => "lost " + p),
            .. observed.Except(frozen).Take(10).Select(p => "gained " + p)];
        return differences.Count > 0 ? differences : [$"the pin order or count differs ({frozen.Length} frozen, {observed.Length} observed)"];
    }

    private static async Task<string> SaveWithEeschema(string cli, string work, string evidence, string name, string libSymbols)
    {
        string directory = Directory.CreateDirectory(Path.Combine(work, name)).FullName;
        string schematic = Path.Combine(directory, "fixture.kicad_sch");
        await File.WriteAllTextAsync(schematic, PsuCpuFixture.CatalogSchematic(libSymbols, PsuCpuIds.Id(0x20, 1)));
        var start = new ProcessStartInfo(cli) { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(directory, "config");
        start.Environment["XDG_CACHE_HOME"] = Path.Combine(directory, "cache");
        foreach (string arg in new[] { "sch", "upgrade", "--force", schematic }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        await File.WriteAllTextAsync(Path.Combine(evidence, name + ".log"), await output + await error);
        Assert.AreEqual(0, process.ExitCode, await error);
        string saved = await File.ReadAllTextAsync(schematic);
        await File.WriteAllTextAsync(Path.Combine(evidence, name + ".kicad_sch"), saved);
        return PsuCpuFixtureBuilder.ExtractLibrarySymbols(saved);
    }

    private static void Code(Exception error, string code) =>
        Assert.IsTrue(error.Message.StartsWith(code + ":", StringComparison.Ordinal), error.Message);

    /// <summary>A test-owned directory under automation/artifacts/psu-cpu-fixture, emptied first so that a
    /// governed check retains only what this run wrote. Each directory has exactly one writing test.</summary>
    private static string OutputDirectory(string name)
    {
        string directory = Path.Combine(PsuCpuFixture.RepositoryRoot, "automation", "artifacts", "psu-cpu-fixture", name);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        return Directory.CreateDirectory(directory).FullName;
    }

    /// <summary>Every placed pin with its reference and native sheet: a unit's pins lie on its
    /// occurrence's sheet; common unit-0 pins lie on every sheet of the component.</summary>
    private static IEnumerable<(string Reference, PinEndpoint Pin, Guid Sheet)> PlacedPins(Circuit circuit)
    {
        var components = circuit.Components.ToDictionary(c => c.Id);
        var definitions = circuit.Sheets.SelectMany(s => s.Components).ToDictionary(d => d.Id);
        var parts = circuit.Parts.ToDictionary(p => p.Id);
        foreach (var component in circuit.Components)
        {
            var occurrences = circuit.Symbols.Where(s => s.ComponentId == component.Id).ToArray();
            foreach (var pin in parts[definitions[component.DefinitionId].PartId].Pins)
                foreach (var sheet in occurrences.Where(o => pin.Unit == 0 || o.Unit == pin.Unit).Select(o => o.EffectiveSheetInstanceId(component)).Distinct())
                    yield return (component.Reference, new(component.Id, pin.Number), sheet);
        }
    }
}

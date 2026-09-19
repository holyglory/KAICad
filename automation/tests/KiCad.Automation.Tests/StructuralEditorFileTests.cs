using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using P = KiCad.Automation.Protocol.Structural;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class StructuralEditorFileTests
{
    [TestMethod]
    public async Task EditingStructurePreservesTheNativeSchematicAndItsBindings()
    {
        string root = Directory.CreateTempSubdirectory("kicad-structural-files-").FullName;
        try
        {
            var (design, library) = SchematicDesignTests.Fixture();
            string libraryPath = Path.Combine(root, design.Engineering.KnowledgeLibraries.Single().Path);
            Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
            await File.WriteAllTextAsync(libraryPath, ComponentKnowledgeXml.WriteLibrary(library));
            string source = Path.Combine(root, "design.xml");
            string original = SchematicDesignXml.Write(design, [library]);
            await File.WriteAllTextAsync(source, original);
            var request = new P.StructuralFileRequest { SchemaVersion = 1, RepositoryRoot = root, SourcePath = source };
            var read = await StructuralEditorFiles.ExecuteAsync(request);
            Assert.AreEqual(design.Engineering.Structure.Id.ToString("D"), read.DocumentId);
            Assert.AreEqual(design.Engineering.Circuit.Components.Count, read.Components.Count);
            var unchanged = request.Clone(); unchanged.Action = P.StructuralFileAction.SfaSave;
            unchanged.ExpectedSourceToken = read.SourceToken; unchanged.Diagram = read.Diagram.Clone();
            Assert.AreEqual(read.SourceToken, (await StructuralEditorFiles.ExecuteAsync(unchanged)).SourceToken);
            Assert.AreEqual(original, await File.ReadAllTextAsync(source));
            Assert.IsFalse(File.Exists(source + ".sync.lock"));
            unchanged.Diagram.Blocks[0].Name = "Edited block";
            unchanged.Diagram.Blocks[0].Purpose = "Keep near the heat sink.\r\n";
            var saved = await StructuralEditorFiles.ExecuteAsync(unchanged);
            Assert.AreNotEqual(read.SourceToken, saved.SourceToken);
            var restored = SchematicDesignXml.Read(await File.ReadAllTextAsync(source), [library]);
            Assert.AreEqual(design.Schematic, restored.Schematic);
            Assert.AreEqual(CircuitXml.Write(design.Engineering.Circuit), CircuitXml.Write(restored.Engineering.Circuit));
            CollectionAssert.AreEquivalent(design.SymbolBindings.ToArray(), restored.SymbolBindings.ToArray());
            Assert.AreEqual("Edited block", restored.Engineering.Structure.Blocks.Single(b => b.Id.ToString("D") == read.Diagram.Blocks[0].Id).Name);
            var stale = await Assert.ThrowsExactlyAsync<AutomationException>(() => StructuralEditorFiles.ExecuteAsync(unchanged));
            Assert.AreEqual("structural_file_changed", stale.Code);
            Assert.AreEqual(saved.SourceToken, (await StructuralEditorFiles.ExecuteAsync(request)).SourceToken);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TypedBridgePreservesEveryModelFieldAndRejectsUnknownOrMissingData()
    {
        var (circuit, original) = StructuralDiagramTests.Fixture();
        var diagram = original with
        {
            Blocks = original.Blocks.Select(b => b with { Purpose = " exact\r\n purpose " }).ToArray(),
            Properties = [new(original.Id, new(Guid.NewGuid(), "voltage", "electrical", "Declared range", GuidanceStrength.Requirement,
                "startup", [new("brief", "r1", 2, "", "prototype")], Quantity: new(ParameterKind.AbsoluteMaximum, "V",
                    Maximum: 3.300m, Tolerance: new(ToleranceKind.Percent, 1.25m, 2.5m))))],
            Presentation = new([new(original.Blocks[0].Id, -100, 200, 10_000_000, 8_000_000, true, 0xff00)],
                [new(original.Ports[0].Id, StructuralPortSide.Left, 100)],
                [new(original.Connections[0].Id, [new(100, 200)], new(300, 400), true)])
        };
        var message = StructuralEditorCodec.Encode(diagram, circuit);
        Assert.AreEqual(StructuralDiagramXml.Write(diagram, circuit),
            StructuralDiagramXml.Write(StructuralEditorCodec.Decode(message, circuit), circuit));
        var unknown = P.StructuralDiagramData.Parser.ParseFrom([.. message.ToByteArray(), 0xf8, 0x3e, 0x01]);
        Assert.ThrowsExactly<AutomationException>(() => StructuralEditorCodec.Decode(unknown, circuit));
        var missing = message.Clone(); missing.Properties[0].ClearStrength();
        Assert.ThrowsExactly<AutomationException>(() => StructuralEditorCodec.Decode(missing, circuit));
        var rounded = message.Clone(); rounded.Properties[0].Quantity.Maximum = "3.3000000000000000000000000000000000000000001";
        Assert.ThrowsExactly<AutomationException>(() => StructuralEditorCodec.Decode(rounded, circuit));
        var noPosition = message.Clone(); noPosition.Presentation.Blocks[0].Position = null;
        Assert.ThrowsExactly<AutomationException>(() => StructuralEditorCodec.Decode(noPosition, circuit));
    }

    [TestMethod]
    public async Task BadWritesAndCancellationKeepTheOriginalFile()
    {
        string root = Directory.CreateTempSubdirectory("kicad-structural-invalid-").FullName;
        try
        {
            var (circuit, structure) = StructuralDiagramTests.Fixture();
            string original = EngineeringDesignXml.Write(new(circuit, structure, [], []), []);
            string source = Path.Combine(root, "design.xml"); await File.WriteAllTextAsync(source, original);
            var request = new P.StructuralFileRequest { SchemaVersion = 1, RepositoryRoot = root, SourcePath = source };
            var data = await StructuralEditorFiles.ExecuteAsync(request);
            request.Action = P.StructuralFileAction.SfaSave; request.ExpectedSourceToken = data.SourceToken;
            request.Diagram = data.Diagram.Clone(); request.Diagram.Id = Guid.NewGuid().ToString("D");
            Assert.AreEqual("structural_identity_changed", (await Assert.ThrowsExactlyAsync<AutomationException>(
                () => StructuralEditorFiles.ExecuteAsync(request))).Code);
            request.Diagram = data.Diagram.Clone(); request.Diagram.Blocks[0].ParentId = request.Diagram.Blocks[0].Id;
            await Assert.ThrowsExactlyAsync<AutomationException>(() => StructuralEditorFiles.ExecuteAsync(request));
            await Assert.ThrowsAsync<OperationCanceledException>(() => StructuralEditorFiles.ExecuteAsync(request, new(true)));
            Assert.AreEqual(original, await File.ReadAllTextAsync(source));
            request.SourcePath = Path.Combine(Path.GetDirectoryName(root)!, "outside.xml");
            Assert.AreEqual("structural_path_outside_repository", (await Assert.ThrowsExactlyAsync<AutomationException>(
                () => StructuralEditorFiles.ExecuteAsync(request))).Code);
        }
        finally { Directory.Delete(root, true); }
    }
}

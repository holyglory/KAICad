using System.Text.Json.Nodes;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicProjectSkeletonTests
{
    [TestMethod]
    public void ExtractsDerivedProjectEntriesAndCreatesMissingFileWithoutReplacement()
    {
        string directory = Directory.CreateTempSubdirectory("kicad-project-skeleton-").FullName;
        try
        {
            Guid root = Guid.NewGuid(), child = Guid.NewGuid(), screen = Guid.NewGuid();
            var document = new DocumentSpecifier { Type = DocumentType.DoctypeSchematic,
                Project = new ProjectSpecifier { Name = "fixture", Path = directory }, SheetPath = new() };
            document.SheetPath.Path.Add(new KIID { Value = root.ToString("D") });
            var childSheet = new SheetSymbol { Id = new KIID { Value = child.ToString("D") },
                Path = document.SheetPath.Clone(), ChildScreenId = new KIID { Value = screen.ToString("D") },
                NameField = new() { Text = new() { Text_ = "Child" } }, FilenameField = new() { Text = new() { Text_ = "child.kicad_sch" } } };
            var hierarchy = new SchematicHierarchyData { Document = document.Clone() };
            hierarchy.Instances.Add(new SchematicScreenData { Metadata = new() { Document = document.Clone(), ScreenId = new() { Value = Guid.NewGuid().ToString("D") } }, Items = { Any.Pack(childSheet) } });
            var childDocument = document.Clone(); childDocument.SheetPath.Path.Add(new KIID { Value = child.ToString("D") });
            hierarchy.Instances.Add(new SchematicScreenData { Metadata = new() { Document = childDocument, ScreenId = new() { Value = screen.ToString("D") } } });
            var skeleton = SchematicProjectSkeleton.FromHierarchy(hierarchy);
            Assert.AreEqual(Path.Combine(directory, "fixture.kicad_pro"), skeleton.ProjectFile);
            Assert.AreEqual(root, skeleton.RootSheetId);
            Assert.AreSequenceEqual(new[] { "fixture", "Child" }, skeleton.Sheets.Select(s => s.Name).ToArray());
            Assert.IsTrue(skeleton.CreateIfMissing());
            byte[] first = File.ReadAllBytes(skeleton.ProjectFile);
            Assert.IsFalse(skeleton.CreateIfMissing());
            CollectionAssert.AreEqual(first, File.ReadAllBytes(skeleton.ProjectFile));
            var json = JsonNode.Parse(first)!.AsObject();
            Assert.AreEqual("fixture.kicad_pro", json["meta"]!["filename"]!.GetValue<string>());
            Assert.AreEqual(root.ToString("D"), json["schematic"]!["top_level_sheets"]![0]!["uuid"]!.GetValue<string>());
            Assert.HasCount(2, json["sheets"]!.AsArray());
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    [TestMethod]
    public void RejectsMismatchedProjectIdentity()
    {
        string directory = Directory.CreateTempSubdirectory("kicad-project-skeleton-").FullName;
        try
        {
            var document = new DocumentSpecifier { Type = DocumentType.DoctypeSchematic,
                Project = new ProjectSpecifier { Name = "fixture", Path = directory }, SheetPath = new() };
            document.SheetPath.Path.Add(new KIID { Value = Guid.NewGuid().ToString("D") });
            var hierarchy = new SchematicHierarchyData { Document = document.Clone() };
            hierarchy.Instances.Add(new SchematicScreenData { Metadata = new() { Document = document.Clone(), ScreenId = new() { Value = Guid.NewGuid().ToString("D") } } });
            var skeleton = SchematicProjectSkeleton.FromHierarchy(hierarchy);
            Assert.ThrowsExactly<AutomationException>(() => skeleton.CreateIfMissing(Path.Combine(directory, "other.kicad_pro")));
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }
}

using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveBlockDefinitionGuidanceTests
{
    [TestMethod]
    public async Task LibraryFilesReturnActualByteIdentityAndRejectWrongPathsWithoutWriting()
    {
        string root = Directory.CreateTempSubdirectory("kicad-definition-library-").FullName;
        try
        {
            var library = new ComponentKnowledgeLibrary(Guid.NewGuid(), "r1", [new(Guid.NewGuid(), "Fixture class", null, [])]);
            string xml = ComponentKnowledgeXml.WriteLibrary(library); string path = Path.Combine(root, "library.xml");
            await File.WriteAllTextAsync(path, xml);
            var read = await BlockDefinitionLibraries.ReadAsync(root, ["library.xml"]);
            Assert.HasCount(1, read); Assert.AreEqual("library.xml", read[0].RelativePath);
            Assert.AreEqual(library.Id, read[0].Library.Id); Assert.AreEqual("r1", read[0].Library.Revision);
            Assert.AreEqual(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path))), read[0].ContentSha256);
            Assert.AreEqual(xml, await File.ReadAllTextAsync(path));
            Assert.IsEmpty(await BlockDefinitionLibraries.ReadAsync(root, []));
            await Assert.ThrowsExactlyAsync<AutomationException>(() => BlockDefinitionLibraries.ReadAsync(root, ["library.xml", "library.xml"]));
            await Assert.ThrowsExactlyAsync<AutomationException>(() => BlockDefinitionLibraries.ReadAsync(root, ["../library.xml"]));
            await File.WriteAllBytesAsync(path, [0xff, 0xfe, 0xfd]);
            var invalid = await Assert.ThrowsExactlyAsync<AutomationException>(() => BlockDefinitionLibraries.ReadAsync(root, ["library.xml"]));
            Assert.AreEqual("invalid_definition_library_encoding", invalid.Code);
            CollectionAssert.AreEqual(new byte[] { 0xff, 0xfe, 0xfd }, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void AClassResolvesInheritedGuidanceWithoutCreatingAComponentOrChoosingElectricalValues()
    {
        Guid libraryId = Guid.NewGuid(), parent = Guid.NewGuid(), child = Guid.NewGuid();
        var package = new GuidanceStatement(Guid.NewGuid(), "package", "mechanical", "Keep the service area accessible.",
            GuidanceStrength.Requirement, "During service", [new("mechanical-spec", "r1", 2, null, null)]);
        var routing = new GuidanceStatement(Guid.NewGuid(), "routing", "routing", "Keep sensing away from switching.",
            GuidanceStrength.Preference, "", []);
        var library = new ComponentKnowledgeLibrary(libraryId, "r1", [new(parent, "Device", null, [package]), new(child, "LDO", parent, [routing])]);
        var definition = new BlockDefinition(KnowledgeClass: new(DefinitionChoiceState.Selected,
            [new(libraryId, "r1", child)], GuidanceStrength.Information, "", []));
        var result = BlockDefinitionGuidance.Resolve(definition, [library]);
        Assert.IsNotNull(result.Selected); Assert.AreEqual(BlockClassAvailability.Available, result.Selected.Availability);
        Assert.AreEqual("LDO", result.Selected.ClassName); Assert.HasCount(2, result.Selected.Guidance!.Effective);
        Assert.IsTrue(result.Selected.Guidance.Effective.All(g => !g.IsInstance));
        Assert.AreEqual(parent, result.Selected.Guidance.Effective.Single(g => g.Statement.Id == package.Id).OwnerId);
        Assert.AreEqual(VerificationState.Unverified, result.Selected.Guidance.Effective[0].Statement.Verification);
        Assert.AreEqual(DefinitionChoiceState.Unspecified, definition.Get(BlockDefinitionFacet.Model).State);
        Assert.IsNull(BlockDefinitionGuidance.Resolve(BlockDefinition.Empty, [library]).Selected);
    }

    [TestMethod]
    public void CandidatesStaySeparateAndEveryMissingReferenceIsExplicit()
    {
        Guid id = Guid.NewGuid(), type = Guid.NewGuid();
        var library = new ComponentKnowledgeLibrary(id, "r2", [new(type, "Exact type", null, [])]);
        var definition = new BlockDefinition(KnowledgeClass: new(DefinitionChoiceState.Candidates,
            [new(id, "r1", type), new(id, "r2", type), new(id, "r2", Guid.NewGuid()), new(Guid.NewGuid(), "r1", type)],
            GuidanceStrength.Preference, "", []));
        var result = BlockDefinitionGuidance.Resolve(definition, [library]);
        Assert.IsNull(result.Selected); Assert.HasCount(4, result.Choices);
        CollectionAssert.AreEqual(new[] { BlockClassAvailability.MissingRevision, BlockClassAvailability.Available,
            BlockClassAvailability.MissingClass, BlockClassAvailability.MissingLibrary }, result.Choices.Select(c => c.Availability).ToArray());
        Assert.IsTrue(result.Choices.Where(c => c.Availability != BlockClassAvailability.Available).All(c => c.Guidance is null && c.ClassName is null));
        Assert.ThrowsExactly<AutomationException>(() => BlockDefinitionGuidance.Resolve(definition, [library, library]));
    }

    [TestMethod]
    public void ACorruptLibraryDoesNotHideAnotherValidCandidateOrBecomeAnEmptySuccess()
    {
        Guid id = Guid.NewGuid(), type = Guid.NewGuid();
        var invalid = new ComponentKnowledgeLibrary(id, "bad", [new(type, "Broken type", type, [])]);
        var valid = new ComponentKnowledgeLibrary(id, "good", [new(type, "Good type", null, [])]);
        var definition = new BlockDefinition(KnowledgeClass: new(DefinitionChoiceState.Candidates,
            [new(id, "bad", type), new(id, "good", type)], GuidanceStrength.Information, "", []));
        var result = BlockDefinitionGuidance.Resolve(definition, [invalid, valid]);
        Assert.AreEqual(BlockClassAvailability.InvalidLibrary, result.Choices[0].Availability);
        Assert.AreEqual("class_cycle", result.Choices[0].ErrorCode); Assert.IsNull(result.Choices[0].Guidance);
        Assert.AreEqual(BlockClassAvailability.Available, result.Choices[1].Availability);
        Assert.IsNotNull(result.Choices[1].Guidance); Assert.IsEmpty(result.Choices[1].Guidance!.Effective);
    }
}

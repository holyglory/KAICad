using System.Text.Json;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class ComponentPackageMappingTests
{
    private static (Circuit Circuit, ComponentPackageMapping Mapping) Fixture()
    {
        Guid part = Guid.NewGuid(), definition = Guid.NewGuid(), sheet = Guid.NewGuid(), component = Guid.NewGuid();
        Guid rootInstance = Guid.NewGuid();
        var circuit = new Circuit(Guid.NewGuid(), [new(part, "Fixture dual-unit", 2,
            [new("1", "A", 1), new("2", "B", 2)])], [new(sheet, "Root", [new(definition, part, "Fixture")])],
            [new(rootInstance, sheet, null)], [new(component, definition, rootInstance, "U1")], [], []);
        var mapping = new ComponentPackageMapping(part, component, "QFN-2", "package-r3",
            [new("1", "A", true), new("2", "B", true), new("EP", "thermal", false)],
            [new("1", "1", "A", "A"), new("2", "2", "B", "B")], ["EP"]);
        return (circuit, mapping);
    }

    [TestMethod]
    public void ExactPinPadMappingRoundTripsAndRetainsMechanicalPads()
    {
        var f = Fixture(); f.Mapping.Validate(f.Circuit);
        string xml = ComponentPackageMappingXml.Write(f.Mapping, f.Circuit);
        Assert.AreEqual(xml, ComponentPackageMappingXml.Write(ComponentPackageMappingXml.Read(xml, f.Circuit), f.Circuit));
        Assert.AreEqual("EP", ComponentPackageMappingXml.Read(xml, f.Circuit).ExplicitlyUnusedPads.Single());
    }

    [TestMethod]
    public void MissingDuplicatePositionalOrNameGuessedMappingsAreRejected()
    {
        var f = Fixture();
        int index = 0;
        foreach (var invalid in new[]
        {
            f.Mapping with { PinMappings = [f.Mapping.PinMappings[0]] },
            f.Mapping with { PinMappings = [f.Mapping.PinMappings[0], f.Mapping.PinMappings[0] with { PinNumber = "2", PinName = "B", PadNumber = "1", PadName = "A" }] },
            f.Mapping with { PinMappings = [f.Mapping.PinMappings[0], f.Mapping.PinMappings[1] with { PadNumber = "EP", PadName = "thermal" }] },
            f.Mapping with { ExplicitlyUnusedPads = [] },
            f.Mapping with { PinMappings = [f.Mapping.PinMappings[0] with { PinName = "B" }, f.Mapping.PinMappings[1]] },
            f.Mapping with { Pads = [.. f.Mapping.Pads, new("3", "unused", true)] }
        }) Assert.ThrowsExactly<AutomationException>(() => invalid.Validate(f.Circuit), "invalid mapping index " + index++);
        // Package identity is textual evidence and is intentionally not checked
        // against a live package library here; a different pinned package needs
        // its own declared pad mapping rather than a guessed substitution.
    }

    [TestMethod]
    public void CompiledMcpValidatorReturnsTruthfulMappingStatusWithoutNativeCreation()
    {
        var f = Fixture(); var tool = new KnowledgeTools();
        var valid = tool.ValidatePackageMapping(CircuitXml.Write(f.Circuit),
            JsonSerializer.SerializeToElement(f.Mapping, new JsonSerializerOptions(JsonSerializerDefaults.Web)), CancellationToken.None);
        Assert.IsFalse(valid.IsError == true); Assert.IsTrue(valid.StructuredContent!.Value.GetProperty("valid").GetBoolean());
        Assert.IsFalse(valid.StructuredContent.Value.GetProperty("nativeCreation").GetBoolean());
        var invalidMapping = f.Mapping with { PinMappings = [f.Mapping.PinMappings[0]] };
        var invalid = tool.ValidatePackageMapping(CircuitXml.Write(f.Circuit),
            JsonSerializer.SerializeToElement(invalidMapping, new JsonSerializerOptions(JsonSerializerDefaults.Web)), CancellationToken.None);
        Assert.IsTrue(invalid.IsError == true); Assert.AreEqual("invalid_component_package_mapping",
            invalid.StructuredContent!.Value.GetProperty("errorCode").GetString());
    }
}

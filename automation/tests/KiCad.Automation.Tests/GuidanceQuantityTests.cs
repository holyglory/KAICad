using KiCad.Automation.Model;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class GuidanceQuantityTests
{
    private static GuidanceStatement Statement(GuidanceQuantity quantity, string key = "supply-operating") =>
        new(Guid.NewGuid(), key, "power", "Synthetic source claim, not a real component specification.",
            GuidanceStrength.Information, "variant:A; ambient:25 C",
            [new("synthetic-datasheet", "rev-2", 7, "Characteristics", "A")], Quantity: quantity);

    private static (ComponentKnowledgeLibrary Library, ComponentKnowledgeBinding Binding) Fixture(params GuidanceStatement[] statements)
    {
        Guid parent = Guid.NewGuid(), child = Guid.NewGuid(), library = Guid.NewGuid();
        return (new(library, "r1", [new(parent, "Base", null, statements), new(child, "Derived", parent, [])]),
            new(Guid.NewGuid(), library, "r1", child, []));
    }

    [TestMethod]
    public void ClassAndInstanceQuantitiesPreserveUnitsKindsToleranceAndSourceHistory()
    {
        var quantity = new GuidanceQuantity(ParameterKind.OperatingLimit, "V", 3.3m, 3m, 3.6m,
            new(ToleranceKind.Percent, 10m, 10m));
        var original = Statement(quantity);
        var (library, binding) = Fixture(original, Statement(new(ParameterKind.AbsoluteMaximum, "V", Maximum: 5m), "supply-absolute-maximum"));
        var replacement = original with { Id = Guid.NewGuid(), Replaces = original.Id,
            Quantity = quantity with { Nominal = 3.2m }, Text = "Explicit instance selection" };
        binding = binding with { Guidance = [replacement] };
        string before = ComponentKnowledgeXml.WriteLibrary(library);
        var parsedLibrary = ComponentKnowledgeXml.ReadLibrary(before);
        string local = ComponentKnowledgeXml.WriteBinding(binding, library);
        var parsedBinding = ComponentKnowledgeXml.ReadBinding(local, parsedLibrary);
        var resolved = ComponentGuidance.Resolve(parsedLibrary, parsedBinding);
        Assert.AreEqual(quantity, resolved.Replacements.Single().Original.Statement.Quantity);
        Assert.AreEqual(replacement.Quantity, resolved.Replacements.Single().Replacement.Statement.Quantity);
        Assert.AreEqual(original.Sources[0], resolved.Effective.Single(r => r.IsInstance).Statement.Sources[0]);
        Assert.AreEqual(ParameterKind.AbsoluteMaximum, resolved.Effective.Single(r => !r.IsInstance).Statement.Quantity!.Kind);
        Assert.AreEqual(GuidanceStrength.Information, resolved.Effective.Single(r => !r.IsInstance).Statement.Strength);
        Assert.AreEqual(VerificationState.Unverified, resolved.Effective.Single(r => !r.IsInstance).Statement.Verification);
        Assert.AreEqual(0, resolved.QuantityIssues!.Count);
        Assert.AreEqual(before, ComponentKnowledgeXml.WriteLibrary(parsedLibrary));
        Assert.AreEqual(local, ComponentKnowledgeXml.WriteBinding(parsedBinding, parsedLibrary));
    }

    [TestMethod]
    public void UnknownNumbersNeverBecomeZeroAndToleranceKeepsItsExplicitKind()
    {
        var unknown = new GuidanceQuantity(ParameterKind.Unclassified, "pF", UnknownReason: "Package has not been selected");
        var measured = new GuidanceQuantity(ParameterKind.Measurement, "mA", Nominal: 0m,
            Tolerance: new(ToleranceKind.Absolute, 0.1m, 0.2m));
        var (library, binding) = Fixture(Statement(unknown, "capacitance"), Statement(measured, "measured-current"));
        var parsed = ComponentKnowledgeXml.ReadLibrary(ComponentKnowledgeXml.WriteLibrary(library));
        var result = ComponentGuidance.Resolve(parsed, binding);
        Assert.AreEqual(unknown, result.Effective.Single(r => r.Statement.Key == "capacitance").Statement.Quantity);
        Assert.AreEqual(measured, result.Effective.Single(r => r.Statement.Key == "measured-current").Statement.Quantity);
        Assert.AreEqual(0, result.QuantityIssues!.Count);
    }

    [TestMethod]
    public void ContradictoryRangesRemainInspectableWithoutChangingVerificationOrSource()
    {
        var statement = Statement(new(ParameterKind.OperatingLimit, "V", 4m, 5m, 3m));
        var (library, binding) = Fixture(statement);
        string xml = ComponentKnowledgeXml.WriteLibrary(library);
        var parsed = ComponentKnowledgeXml.ReadLibrary(xml);
        var result = ComponentGuidance.Resolve(parsed, binding);
        CollectionAssert.AreEquivalent(new[] { "inverted_range", "nominal_below_minimum", "nominal_above_maximum" },
            result.QuantityIssues!.Select(i => i.Code).ToArray());
        Assert.IsTrue(result.QuantityIssues!.All(i => i.StatementId == statement.Id));
        Assert.AreEqual(statement.Quantity, result.Effective.Single().Statement.Quantity);
        Assert.AreEqual(VerificationState.Unverified, result.Effective.Single().Statement.Verification);
        Assert.AreEqual(xml, ComponentKnowledgeXml.WriteLibrary(parsed));
    }

    [TestMethod]
    public void MalformedShapesAndUnknownXmlFieldsFailWithoutDroppingData()
    {
        foreach (var invalid in new GuidanceQuantity[]
        {
            new((ParameterKind)99, "V", 1m), new(ParameterKind.Nominal, " ", 1m), new(ParameterKind.Nominal, "V"),
            new(ParameterKind.Nominal, "V", UnknownReason: " "),
            new(ParameterKind.Nominal, "V", 1m, Tolerance: new((ToleranceKind)99, 1m, 1m)),
            new(ParameterKind.Nominal, "V", 1m, Tolerance: new(ToleranceKind.Percent, -1m, 1m))
        })
            Assert.ThrowsExactly<AutomationException>(() => ComponentKnowledgeXml.WriteLibrary(Fixture(Statement(invalid)).Library));
        var (library, _) = Fixture(Statement(new(ParameterKind.Nominal, "V", 1m)));
        string xml = ComponentKnowledgeXml.WriteLibrary(library);
        foreach (string invalid in new[] { xml.Replace("<quantity ", "<quantity dropped=\"data\" ", StringComparison.Ordinal),
            xml.Replace("nominal=\"1\"", "nominal=\"NaN\"", StringComparison.Ordinal),
            xml.Replace("nominal=\"1\"", "nominal=\"0.123456789012345678901234567890123\"", StringComparison.Ordinal),
            xml.Replace("nominal=\"1\"", "nominal=\"0.00000000000000000000000000001\"", StringComparison.Ordinal),
            xml.Replace("nominal=\"1\"", "nominal=\"99999999999999999999999999999999999999999999\"", StringComparison.Ordinal) })
            Assert.ThrowsExactly<AutomationException>(() => ComponentKnowledgeXml.ReadLibrary(invalid));
        var equivalent = ComponentKnowledgeXml.ReadLibrary(xml.Replace("nominal=\"1\"", "nominal=\"+0001.00000000000000000000000000000000\"", StringComparison.Ordinal));
        Assert.AreEqual(1m, equivalent.Classes.Single(c => c.Guidance.Count != 0).Guidance.Single().Quantity!.Nominal);
    }

    [TestMethod]
    public void TextOnlyDocumentsStayUnchangedAndIncompatibleUnitsAreNotGuessed()
    {
        var (library, binding) = ComponentGuidanceTests.Fixture();
        string xml = ComponentKnowledgeXml.WriteLibrary(library);
        Assert.IsFalse(xml.Contains("<quantity", StringComparison.Ordinal));
        Assert.AreEqual(xml, ComponentKnowledgeXml.WriteLibrary(ComponentKnowledgeXml.ReadLibrary(xml)));
        var a = Statement(new(ParameterKind.Nominal, "V", 3.3m));
        var b = Statement(new(ParameterKind.Nominal, "mV", 3.3m));
        (library, binding) = Fixture(a);
        binding = binding with { Guidance = [b] };
        var result = ComponentGuidance.Resolve(library, binding);
        Assert.AreEqual(1, result.Conflicts.Count);
        CollectionAssert.AreEquivalent(new[] { "V", "mV" }, result.Effective.Select(r => r.Statement.Quantity!.Unit).ToArray());
    }

    [TestMethod]
    public void CancelledGuidanceResolutionDoesNotReadOrInterpretInputs()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => new KnowledgeTools().Resolve("not xml", "not xml", "not xml", cancellation.Token));
    }

    [TestMethod]
    public void FullEngineeringDesignRetainsLibraryAndLocalNumericalGuidance()
    {
        var (design, library) = EngineeringDesignXmlTests.Fixture();
        library = library with { Classes = library.Classes.Select(c => c with
        {
            Guidance = c.Guidance.Select(s => s.Key == "decoupling"
                ? s with { Quantity = new(ParameterKind.Nominal, "count", Nominal: 30m) } : s).ToArray()
        }).ToArray() };
        var local = Statement(new(ParameterKind.OperatingLimit, "°C", Maximum: 85m), "case-temperature")
            with { Strength = GuidanceStrength.Preference, Text = "Synthetic design-specific thermal target" };
        design = design with { ComponentBindings = design.ComponentBindings.Select(b => b with
            { Guidance = [.. b.Guidance, local] }).ToArray() };
        string xml = EngineeringDesignXml.Write(design, [library]);
        string libraryXml = ComponentKnowledgeXml.WriteLibrary(library);
        var reloadedLibrary = ComponentKnowledgeXml.ReadLibrary(libraryXml);
        var reloaded = EngineeringDesignXml.Read(xml, [reloadedLibrary]);
        var result = reloaded.Validate([reloadedLibrary]).Single().Value;
        Assert.AreEqual(30m, result.Effective.Single(r => r.Statement.Key == "decoupling").Statement.Quantity!.Nominal);
        var actual = result.Effective.Single(r => r.Statement.Id == local.Id);
        Assert.IsTrue(actual.IsInstance);
        Assert.AreEqual(local.Quantity, actual.Statement.Quantity);
        Assert.AreEqual(local.Applicability, actual.Statement.Applicability);
        Assert.AreEqual(local.Sources[0], actual.Statement.Sources[0]);
        Assert.AreEqual(xml, EngineeringDesignXml.Write(reloaded, [reloadedLibrary]));
        Assert.AreEqual(libraryXml, ComponentKnowledgeXml.WriteLibrary(reloadedLibrary));
    }
}

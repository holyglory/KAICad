using System.Text.Json;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class ComponentKnowledgeAuthoringTests
{
    private static (ComponentKnowledgeLibrary Library, Guid ClassId) Fixture()
    {
        Guid library = Guid.NewGuid(), classId = Guid.NewGuid();
        return (new(library, "r1", [new(classId, "Fixture LDO class", null, [])]), classId);
    }

    [TestMethod]
    public void NumericFactsRequireSourceAndKeepAbsoluteMaximumSeparate()
    {
        var (library, classId) = Fixture();
        var statement = new GuidanceStatement(Guid.NewGuid(), "input-voltage", "electrical", "Input supply range.",
            GuidanceStrength.Requirement, "At rated load", [new("datasheet.pdf", "rev-4", 12, "Electrical characteristics", "variant-A")],
            Quantity: new(ParameterKind.OperatingLimit, "V", 3.3m, 3.0m, 3.6m, new(ParameterToleranceKind(), 0, 0)));
        var result = ComponentKnowledgeAuthoring.AddClassGuidance(library, classId, "r2", statement);
        Assert.AreEqual("r2", result.Library.Revision);
        Assert.AreEqual(ParameterKind.OperatingLimit, result.Added.Quantity!.Kind);
        Assert.AreEqual(12, result.Added.Sources[0].Page);
        var absolute = statement with { Id = Guid.NewGuid(), Key = "absolute-input", Quantity = statement.Quantity! with
            { Kind = ParameterKind.AbsoluteMaximum, Nominal = null, Minimum = null, Maximum = 6.0m } };
        var second = ComponentKnowledgeAuthoring.AddClassGuidance(result.Library, classId, "r3", absolute);
        Assert.AreEqual(ParameterKind.AbsoluteMaximum, second.Library.Classes.Single().Guidance[1].Quantity!.Kind);
        Assert.IsEmpty(second.QuantityIssues);
    }

    [TestMethod]
    public void ContradictionsUnknownAndMissingSourcesRemainExplicitFailuresOrIssues()
    {
        var (library, classId) = Fixture();
        var baseStatement = new GuidanceStatement(Guid.NewGuid(), "load", "electrical", "Load expectation.", GuidanceStrength.Information, "", [],
            Quantity: new(ParameterKind.Nominal, "A", 1m, 2m, 0.5m));
        Assert.ThrowsExactly<AutomationException>(() => ComponentKnowledgeAuthoring.AddClassGuidance(library, classId, "r2", baseStatement));
        var contradiction = baseStatement with { Sources = [new("datasheet.pdf", "r1", 3, null, null)] };
        var result = ComponentKnowledgeAuthoring.AddClassGuidance(library, classId, "r2", contradiction);
        Assert.HasCount(3, result.QuantityIssues); Assert.IsTrue(result.QuantityIssues.Any(i => i.Code == "inverted_range"));
        var unknown = contradiction with { Id = Guid.NewGuid(), Quantity = contradiction.Quantity! with
            { Kind = ParameterKind.Unclassified, UnknownReason = "The datasheet did not specify a nominal load." } };
        Assert.ThrowsExactly<AutomationException>(() => ComponentKnowledgeAuthoring.AddClassGuidance(result.Library, classId, "r3", unknown));
        var tools = new KnowledgeTools();
        var tool = tools.ProposeGuidance(ComponentKnowledgeXml.WriteLibrary(library), classId, "r2",
            JsonSerializer.SerializeToElement(contradiction, new JsonSerializerOptions(JsonSerializerDefaults.Web)), CancellationToken.None);
        Assert.IsFalse(tool.IsError == true);
        Assert.AreEqual("r2", tool.StructuredContent!.Value.GetProperty("revision").GetString());
        Assert.IsFalse(tool.StructuredContent.Value.GetProperty("sourceTextInterpreted").GetBoolean());
        Assert.IsFalse(tool.StructuredContent.Value.GetProperty("nativeCreation").GetBoolean());
    }

    private static ToleranceKind ParameterToleranceKind() => ToleranceKind.Percent;
}

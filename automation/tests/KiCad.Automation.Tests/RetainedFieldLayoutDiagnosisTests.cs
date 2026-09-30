using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RetainedFieldLayoutDiagnosisTests
{
    [TestMethod, TestCategory("ExternalIntegration"), TestCategory("RetainedFieldLayoutDiagnosis")]
    public void CompareExactRetainedRecoveryAndPublishedXmlWithoutEditingEither()
    {
        string root = Environment.GetEnvironmentVariable("KICAD_FIELD_LAYOUT_DIAGNOSTIC_EVIDENCE")
            ?? throw new AssertFailedException("Provide the exact materialized field-layout evidence directory.");
        Assert.IsTrue(Path.IsPathFullyQualified(root));
        var inputs = Directory.GetFiles(root, "*-creation-recovery.json").Order(StringComparer.Ordinal).ToArray();
        Assert.HasCount(2, inputs);
        var results = new List<object>(); var failures = new List<string>();
        foreach (string input in inputs)
        {
            string instance = Path.GetFileName(input)[..^"-creation-recovery.json".Length];
            Assert.IsTrue(Guid.TryParseExact(instance, "D", out _));
            var state = new DesignRecoveryStore(input).Read()!.State;
            var actual = SchematicDesignXml.Read(File.ReadAllText(Path.Combine(root, instance + "-created-design.xml")), state.KnowledgeLibraries);
            var operations = SchematicHierarchyDelta.Plan(state.Baseline.Schematic, actual.Schematic);
            bool engineeringEquivalent = EngineeringDesignXml.Write(state.Baseline.Engineering, state.KnowledgeLibraries)
                == EngineeringDesignXml.Write(actual.Engineering, state.KnowledgeLibraries);
            results.Add(new { instance, protobufEqual = state.Baseline.Schematic.Equals(actual.Schematic),
                engineeringEquivalent, operationCount = operations.Count,
                operations = operations.Select(o => new { kind = o.OperationCase.ToString(),
                    sheet = o.TargetDocument is null ? null : string.Join('/', o.TargetDocument.SheetPath.Path.Select(p => p.Value)) }).ToArray() });
            if (operations.Count != 0 || !engineeringEquivalent) failures.Add(instance);
        }
        Console.WriteLine(JsonSerializer.Serialize(results));
        Assert.IsEmpty(failures, "Retained baseline/XML differ semantically for: " + string.Join(',', failures));
    }
}

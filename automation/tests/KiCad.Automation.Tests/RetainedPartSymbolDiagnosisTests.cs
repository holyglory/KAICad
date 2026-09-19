using System.Text.Json;
using System.Xml.Linq;
using Google.Protobuf;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RetainedPartSymbolDiagnosisTests
{
    [TestMethod, TestCategory("ExternalIntegration"), TestCategory("RetainedPartSymbolCacheDiagnosis")]
    public void CompareDeclaredAndNativeCachedDefinitionsWithoutEditingEvidence()
    {
        string path = Environment.GetEnvironmentVariable("KICAD_PART_SYMBOL_DIAGNOSTIC_RECOVERY")
            ?? throw new AssertFailedException("Provide the exact retained recovery path.");
        Assert.IsTrue(Path.IsPathFullyQualified(path));
        var state = new DesignRecoveryStore(path).Read()!.State;
        var source = state.Baseline.PartSymbols!.Single();
        var results = new List<object>();
        var failures = new List<string>();
        foreach (var screen in state.Baseline.Schematic.Instances)
        {
            var cached = screen.CachedSymbols.Single(c => c.CacheKey == source.Symbol.CacheKey);
            var left = cached.Clone(); var right = source.Symbol.Clone();
            string[] leftItems = left.Definition.Items.Select(Key).Order(StringComparer.Ordinal).ToArray();
            string[] rightItems = right.Definition.Items.Select(Key).Order(StringComparer.Ordinal).ToArray();
            left.Definition.Items.Clear(); right.Definition.Items.Clear();
            bool sameProperties = left.Equals(right);
            bool sameItems = leftItems.SequenceEqual(rightItems);
            results.Add(new { screen = screen.Metadata.ScreenId.Value, exact = cached.Equals(source.Symbol),
                sameProperties, sameItems, differences = Differences(cached, source.Symbol).Take(20).ToArray() });
            if (!sameProperties || !sameItems) failures.Add(screen.Metadata.ScreenId.Value);
        }
        Console.WriteLine(JsonSerializer.Serialize(results));
        Assert.IsEmpty(failures, "A cache differs by more than enumeration: " + string.Join(',', failures));

        static string Key(SchematicSymbolChild item) => Convert.ToBase64String(item.ToByteArray());
        static IEnumerable<object> Differences(SchematicCachedSymbol actual, SchematicCachedSymbol expected)
        {
            static Dictionary<string, string> Leaves(SchematicCachedSymbol value) =>
                XElement.Parse(SchematicDataXml.Write(value)).Descendants().Where(e => !e.HasElements).ToDictionary(e =>
                    string.Join('/', e.AncestorsAndSelf().Reverse().Select(n => n.Name.LocalName + "["
                        + n.ElementsBeforeSelf(n.Name).Count() + "]")), e => e.Value);
            var left = Leaves(actual); var right = Leaves(expected);
            return left.Keys.Union(right.Keys).Where(k => left.GetValueOrDefault(k) != right.GetValueOrDefault(k))
                .Select(k => new { path = k, native = left.GetValueOrDefault(k), declared = right.GetValueOrDefault(k) });
        }
    }

    [TestMethod, TestCategory("ExternalIntegration"), TestCategory("RetainedPartSymbolDiagnosis")]
    public void CompareCreatedFieldsAndDefinitionChildrenByExactIdentity()
    {
        string root = Environment.GetEnvironmentVariable("KICAD_PART_SYMBOL_DIAGNOSTIC_DIRECTORY")
            ?? throw new AssertFailedException("Provide the exact retained field-creation evidence directory.");
        const string instance = "c4b39d92-e1dd-45af-ac17-c98ccd60214f";
        var desired = SchematicDesignXml.Read(File.ReadAllText(Path.Combine(root, instance + "-creation-planned.xml")), []).Schematic;
        var actual = (SchematicHierarchyData)SchematicDataXml.Read(File.ReadAllText(Path.Combine(root, instance + "-creation-actual.xml")));
        var results = new List<object>(); var failures = new List<string>();
        foreach (var expectedScreen in desired.Instances)
        {
            var screen = actual.Instances.Single(s => s.Metadata.Document.Equals(expectedScreen.Metadata.Document));
            var actualSymbols = SchematicItemDelta.Index(screen.Items).Values.OfType<SchematicSymbolInstance>().ToDictionary(s => s.Id.Value);
            foreach (var expected in SchematicItemDelta.Index(expectedScreen.Items).Values.OfType<SchematicSymbolInstance>())
            {
                var observed = actualSymbols[expected.Id.Value];
                if (expected.Equals(observed)) continue;
                var left = observed.Clone(); var right = expected.Clone();
                bool childrenEqual = SameChildren(left.Definition, right.Definition);
                left.Definition.Items.Clear(); right.Definition.Items.Clear();
                bool propertiesEqual = left.Equals(right);
                results.Add(new { symbol = expected.Id.Value, childrenEqual, propertiesEqual,
                    differences = Differences(left, right).Take(16).ToArray() });
                if (!childrenEqual || !propertiesEqual) failures.Add(expected.Id.Value);
            }
            foreach (var expected in expectedScreen.CachedSymbols)
            {
                var observed = screen.CachedSymbols.Single(c => c.CacheKey == expected.CacheKey);
                if (expected.Equals(observed)) continue;
                var left = observed.Clone(); var right = expected.Clone();
                bool childrenEqual = SameChildren(left.Definition, right.Definition);
                left.Definition.Items.Clear(); right.Definition.Items.Clear();
                bool propertiesEqual = left.Equals(right);
                results.Add(new { cache = expected.CacheKey, childrenEqual, propertiesEqual,
                    differences = Differences(left, right).Take(16).ToArray() });
                if (!childrenEqual || !propertiesEqual) failures.Add(expected.CacheKey);
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(results));
        Assert.IsEmpty(failures, "Changes other than definition enumeration: " + string.Join(',', failures));

        static bool SameChildren(SchematicSymbol left, SchematicSymbol right) => left.Items.Select(c => Convert.ToBase64String(c.ToByteArray()))
            .Order(StringComparer.Ordinal).SequenceEqual(right.Items.Select(c => Convert.ToBase64String(c.ToByteArray())).Order(StringComparer.Ordinal));
        static IEnumerable<object> Differences(IMessage actual, IMessage expected)
        {
            static Dictionary<string, string> Leaves(IMessage value) =>
                XElement.Parse(SchematicDataXml.Write(value)).Descendants().Where(e => !e.HasElements).ToDictionary(e =>
                    string.Join('/', e.AncestorsAndSelf().Reverse().Select(n => n.Name.LocalName + "["
                        + n.ElementsBeforeSelf(n.Name).Count() + "]")), e => e.Value);
            var left = Leaves(actual); var right = Leaves(expected);
            return left.Keys.Union(right.Keys).Where(k => left.GetValueOrDefault(k) != right.GetValueOrDefault(k))
                .Select(k => new { path = k, native = left.GetValueOrDefault(k), declared = right.GetValueOrDefault(k) });
        }
    }
}

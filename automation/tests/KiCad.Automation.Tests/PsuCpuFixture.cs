using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Any = Google.Protobuf.WellKnownTypes.Any;
using Empty = Google.Protobuf.WellKnownTypes.Empty;

namespace KiCad.Automation.Tests;

internal enum PsuCpuStage { RootOnly, SheetsOnly, PsuComponents, Components, Complete }
internal enum PsuCpuSeed { None, RootOnly, Sheets }

internal sealed record PsuCpuNativeContext(string ProjectDirectory, DocumentSpecifier Root, Guid RootInstanceId,
    PsuCpuSeed Seed, IReadOnlyList<SchematicPartSymbol> PartSymbols, SchematicDesign? Baseline,
    string HardwarePath, string BlocksPath, string DesignPath, string FlatStructurePath);

internal sealed record PsuCpuPartSource(string Path, string GitBlob, string Symbol, int FormatVersion);
internal sealed record PsuCpuPart(Guid Id, int Ordinal, string Name, string CacheKey, string Library, string Entry, int Units,
    PsuCpuPartSource Source, IReadOnlyList<PartPin> Pins);

internal sealed record PsuCpuPinReference(string Reference, string Number);
/// <summary>Pin numbers one unit of a fixture part draws at one point (contract erratum 2026-09-24).</summary>
internal sealed record PsuCpuStackedPins(string CacheKey, int Unit, IReadOnlyList<string> Numbers);
internal sealed record PsuCpuExpectedSheet(string Key, Guid ModelSheetInstance, Guid Definition, string? Parent, string File,
    string? SheetName, Guid? NativeSheetSymbol, Guid NativeScreen, string Page, string Paper);
internal sealed record PsuCpuExpectedSymbol(Guid Component, Guid Occurrence, string Reference, string LibId, string Value, int Unit, string Sheet);
internal sealed record PsuCpuExpectedNet(Guid Net, string Name, IReadOnlyList<PsuCpuPinReference> Pins);
internal sealed record PsuCpuPresentationPolicy(int PageInsetMm, int ReservedBottomMm, bool SymbolBodiesDisjoint);
/// <summary>Expected native schematic for one stage. Label maps are keyed by sheet key; isolated
/// pins are resolved per reference (the file's U5 "allExcept" form is expanded).</summary>
internal sealed record PsuCpuExpectedNative(PsuCpuStage Stage, PsuCpuSeed Seed, IReadOnlyList<PsuCpuExpectedSheet> Sheets,
    IReadOnlyList<PsuCpuExpectedSymbol> Symbols, IReadOnlyList<PsuCpuExpectedNet> Nets,
    IReadOnlyDictionary<string, IReadOnlyList<string>> IsolatedPins, IReadOnlyDictionary<string, IReadOnlyList<string>> HierarchicalLabels,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SheetPins, IReadOnlyDictionary<string, IReadOnlyList<string>> RequiredLocalLabelNames,
    IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedLabelNames, IReadOnlyList<string> Forbidden, string NetNameRule,
    PsuCpuPresentationPolicy Presentation)
{
    /// <summary>Pins a placed symbol's own definition draws at one point while the stage leaves them out of every
    /// model net. KiCad always joins each group into one native net of exactly those pins, so they are not isolated
    /// (contract erratum 2026-09-24): in the Components and PsuComponents stages, U2 pins 1 and 4. Complete keeps
    /// none, because RAIL_B already holds both.</summary>
    public IReadOnlyList<IReadOnlyList<PsuCpuPinReference>> JoinedPins { get; init; } = [];
}
internal sealed record PsuCpuExpectedConnection(Guid Connection, string Owner, string Name, DiagramConnectionKind Kind, string Status,
    IReadOnlyList<Guid> Nets, int? Boundary, IReadOnlyList<Guid> Members, IReadOnlyList<PsuCpuPinReference> Pins);
internal sealed record PsuCpuExpectedRealization(Guid Graph, Guid Circuit, IReadOnlyList<PsuCpuExpectedConnection> Connections);
internal sealed record PsuCpuMigratedBlock(Guid Block, string Name, IReadOnlyList<Guid> Interfaces, IReadOnlyList<Guid> ComponentBindings);
/// <summary>Owner null is the single new root created by conversion.</summary>
internal sealed record PsuCpuMigratedDiagram(Guid? Owner, IReadOnlyList<Guid> Connections, IReadOnlyList<Guid> BoundaryEndpointConnections);
internal sealed record PsuCpuMigratedLayout(Guid? Owner, IReadOnlyList<Guid> Blocks);
internal sealed record PsuCpuExpectedMigration(Guid Structure, IReadOnlyList<Guid> RootChildren,
    IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Children, IReadOnlyList<PsuCpuMigratedBlock> Blocks,
    IReadOnlyList<PsuCpuMigratedDiagram> LocalDiagrams, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> RealizationLinks,
    IReadOnlyList<Guid> CrossLevelConnections, IReadOnlyList<Guid> UnclassifiedStatements, IReadOnlyList<PsuCpuMigratedLayout> Layouts,
    RequirementRevisionActor OriginActorKind);

/// <summary>Deterministic fixture identities: Id(kind, n) = 7e57f1c5-0000-4000-8000-{kind:x4}{n:x8}.</summary>
internal static class PsuCpuIds
{
    public const string Prefix = "7e57f1c5-0000-4000-8000-";
    public static Guid Id(int kind, long n) => Guid.ParseExact($"{Prefix}{kind:x4}{n:x8}", "D");
}

/// <summary>Loader, stage filters, native seeds and assertions for the frozen PSU-CPU fixture,
/// version 1. Files are verified against fixture.json before use; lanes never edit them.</summary>
internal static class PsuCpuFixture
{
    public const int Version = 1;
    public static string RepositoryRoot { get; } = FindRoot();
    public static string Directory { get; } = Path.Combine(RepositoryRoot, "automation", "tests", "fixtures", "psu-cpu");

    /// <summary>Files pinned by fixture.json, in contract order.</summary>
    public static readonly ImmutableArray<string> DataFiles = ["parts.json", "lib_symbols.kicad_sexpr", "hardware.xml", "design.engineering.xml",
        "flat-structure.engineering.xml", "system.blocks.xml", "expected-native.json", "expected-realization.json", "expected-migration.json"];

    private static readonly Lazy<JsonElement> manifest = new(() => ParseJson(File.ReadAllText(PathOf("fixture.json"))));
    private static readonly Lazy<IReadOnlyList<PsuCpuPart>> parts = new(ReadParts);
    private static readonly Lazy<HardwareRepository> hardware = new(() => HardwareRepositoryXml.Read(
        Checked("hardware.xml", HardwareRepositoryXml.Write(PsuCpuFixtureBuilder.Hardware()))));
    private static readonly Lazy<EngineeringDesign> engineering = new(() => EngineeringDesignXml.Read(
        Checked("design.engineering.xml", EngineeringDesignXml.Write(PsuCpuFixtureBuilder.Engineering(Parts()), [])), []));
    private static readonly Lazy<EngineeringDesign> flat = new(() => EngineeringDesignXml.Read(
        Checked("flat-structure.engineering.xml", EngineeringDesignXml.Write(PsuCpuFixtureBuilder.FlatStructure(Parts()), [])), []));
    private static readonly Lazy<RecursiveBlockGraph> graph = new(() => RecursiveBlockGraphXml.Read(
        Checked("system.blocks.xml", RecursiveBlockGraphXml.Write(PsuCpuFixtureBuilder.Graph(), 1))));
    private static readonly Lazy<PsuCpuExpectedNative> expectedNative = new(ReadExpectedNative);
    private static readonly Lazy<IReadOnlyList<PsuCpuStackedPins>> stackedPins = new(ReadStackedPins);
    private static readonly Lazy<PsuCpuExpectedRealization> expectedRealization = new(ReadExpectedRealization);
    private static readonly Lazy<PsuCpuExpectedMigration> expectedMigration = new(ReadExpectedMigration);

    public static string PathOf(string name) => Path.Combine(Directory, name);
    public static JsonElement Manifest => manifest.Value;
    public static HardwareRepository Hardware() => hardware.Value;
    public static EngineeringDesign Engineering(PsuCpuStage stage = PsuCpuStage.Complete) => Stage(engineering.Value, stage);
    public static EngineeringDesign FlatStructure() => flat.Value;
    public static RecursiveBlockGraph Graph() => graph.Value;
    public static IReadOnlyList<PsuCpuPart> Parts() => parts.Value;
    public static PsuCpuExpectedRealization ExpectedRealization() => expectedRealization.Value;
    public static PsuCpuExpectedMigration ExpectedMigration() => expectedMigration.Value;

    internal static AssertFailedException Failure(string code, string message) => new(code + ": " + message);

    /// <summary>Exact bytes of a pinned file; any change is psu_cpu_fixture_file_changed.</summary>
    public static byte[] ReadBytes(string name) => RequireDigest(name, File.ReadAllBytes(PathOf(name)));

    public static string ReadText(string name) => new UTF8Encoding(false, true).GetString(ReadBytes(name));

    internal static byte[] RequireDigest(string name, byte[] bytes) =>
        Manifest.GetProperty("files").TryGetProperty(name, out var digest) && digest.GetString() == Convert.ToHexStringLower(SHA256.HashData(bytes))
            ? bytes : throw Failure("psu_cpu_fixture_file_changed", $"{name} no longer matches fixture.json version {Version}.");

    internal static string RequireBuilt(string name, string text, string built) =>
        text == built ? text : throw Failure("psu_cpu_fixture_builder_drift", $"{name} differs from the PsuCpuFixtureBuilder output.");

    private static string Checked(string name, string built) => RequireBuilt(name, ReadText(name), built);

    /// <summary>Pure identity filters of E1. Every stage keeps circuit K02:1 and structure K02:2.</summary>
    public static EngineeringDesign Stage(EngineeringDesign complete, PsuCpuStage stage)
    {
        var circuit = complete.Circuit;
        Circuit Keep(int[] sheets, int components)
        {
            HashSet<Guid> Ids(int kind, IEnumerable<int> ordinals) => ordinals.Select(n => PsuCpuIds.Id(kind, n)).ToHashSet();
            var range = Enumerable.Range(1, components).ToArray();
            var partIds = Ids(0x03, range.Select(n => PsuCpuFixtureBuilder.Components[n - 1].Part));
            HashSet<Guid> definitions = Ids(0x06, range), instances = Ids(0x07, range), definitionIds = Ids(0x04, sheets), sheetIds = Ids(0x05, sheets);
            var occurrences = Ids(0x09, PsuCpuFixtureBuilder.Occurrences.Select((o, i) => (o, i + 1)).Where(o => o.o.Component <= components).Select(o => o.Item2));
            return circuit with
            {
                Parts = [.. circuit.Parts.Where(p => partIds.Contains(p.Id))],
                Sheets = [.. circuit.Sheets.Where(s => definitionIds.Contains(s.Id)).Select(s => s with { Components = [.. s.Components.Where(c => definitions.Contains(c.Id))] })],
                SheetInstances = [.. circuit.SheetInstances.Where(s => sheetIds.Contains(s.Id))],
                Components = [.. circuit.Components.Where(c => instances.Contains(c.Id))],
                Nets = [], Symbols = [.. circuit.Symbols.Where(s => occurrences.Contains(s.Id))]
            };
        }
        var result = complete with
        {
            Circuit = stage switch
            {
                PsuCpuStage.Complete => circuit,
                PsuCpuStage.Components => circuit with { Nets = [] },
                PsuCpuStage.PsuComponents => Keep([1, 2, 3, 4], 6),
                PsuCpuStage.SheetsOnly => Keep([1, 2, 3, 4], 0),
                PsuCpuStage.RootOnly => Keep([1], 0),
                _ => throw Failure("psu_cpu_unknown_stage", $"Stage {stage} is not part of fixture version {Version}.")
            }
        };
        _ = result.Validate([]);
        return result;
    }

    /// <summary>Expected native result after realizing a stage from S1 (RootOnly: the S2 root).
    /// Only Complete is stored; the other stages follow contract section 1.6.3 and its erratum of
    /// 2026-09-24: pins a placed symbol stacks at one point (<see cref="StackedPins"/>) are joined by
    /// KiCad, so an unwired stage lists them in JoinedPins instead of IsolatedPins.</summary>
    public static PsuCpuExpectedNative ExpectedNative(PsuCpuStage stage)
    {
        var complete = expectedNative.Value;
        var pins = Parts().ToDictionary(p => p.Ordinal, p => (IReadOnlyList<string>)p.Pins.Select(pin => pin.Number).Order(StringComparer.Ordinal).ToArray());
        var cacheKeys = Parts().ToDictionary(p => p.Ordinal, p => p.CacheKey);
        var partOf = PsuCpuFixtureBuilder.Components.ToDictionary(c => c.Reference, c => c.Part);
        IReadOnlyDictionary<string, IReadOnlyList<string>> Lists(IEnumerable<string> keys, Func<string, IReadOnlyList<string>> value) =>
            keys.Distinct().ToDictionary(k => k, value, StringComparer.Ordinal);
        PsuCpuExpectedNative Unwired(IReadOnlyList<PsuCpuExpectedSheet> sheets, IReadOnlyList<PsuCpuExpectedSymbol> symbols, PsuCpuSeed seed)
        {
            // No stage without nets connects a stacked pin, so every stacked group of a placed unit is joined.
            IReadOnlyList<IReadOnlyList<PsuCpuPinReference>> joined = [.. symbols.SelectMany(s => StackedPins()
                    .Where(x => x.CacheKey == cacheKeys[partOf[s.Reference]] && x.Unit == s.Unit)
                    .Select(x => (IReadOnlyList<PsuCpuPinReference>)x.Numbers.Select(n => new PsuCpuPinReference(s.Reference, n)).ToArray()))
                .DistinctBy(g => string.Join(" ", g.Select(p => p.Reference + "." + p.Number)))
                .OrderBy(g => g[0].Reference, StringComparer.Ordinal).ThenBy(g => g[0].Number, StringComparer.Ordinal)];
            var notIsolated = joined.SelectMany(g => g).ToHashSet();
            return complete with
            {
                Stage = stage, Seed = seed, Sheets = sheets, Symbols = symbols, Nets = [],
                IsolatedPins = Lists(symbols.Select(s => s.Reference), r => [.. pins[partOf[r]].Where(n => !notIsolated.Contains(new(r, n)))]),
                JoinedPins = joined,
                HierarchicalLabels = Lists(sheets.Select(s => s.Key), _ => []),
                SheetPins = Lists(sheets.Where(s => s.Parent is not null).Select(s => s.Key), _ => []),
                RequiredLocalLabelNames = Lists([], _ => []), AllowedLabelNames = Lists(sheets.Select(s => s.Key), _ => [])
            };
        }
        return stage switch
        {
            PsuCpuStage.Complete => complete,
            PsuCpuStage.Components => Unwired(complete.Sheets, complete.Symbols, complete.Seed),
            PsuCpuStage.PsuComponents => Unwired(complete.Sheets, [.. complete.Symbols.Where(s => s.Sheet == "PSU")], complete.Seed),
            PsuCpuStage.SheetsOnly => Unwired(complete.Sheets, [], complete.Seed),
            PsuCpuStage.RootOnly => Unwired([.. complete.Sheets.Where(s => s.Parent is null)], [], PsuCpuSeed.RootOnly),
            _ => throw Failure("psu_cpu_unknown_stage", $"Stage {stage} is not part of fixture version {Version}.")
        };
    }

    /// <summary>hardware.xml, system.blocks.xml and flat-structure.engineering.xml; no native state is needed.</summary>
    public static async Task WriteRepositoryFilesAsync(string directory, CancellationToken token)
    {
        System.IO.Directory.CreateDirectory(directory);
        foreach (string name in new[] { "hardware.xml", "system.blocks.xml", "flat-structure.engineering.xml" })
            await File.WriteAllBytesAsync(Path.Combine(directory, name), ReadBytes(name), token);
        // Validate what was written; the builder and codecs define these exact bytes.
        _ = Hardware(); _ = Graph(); _ = FlatStructure();
    }

    // ---- Native seeds (contract section 1.6) ----------------------------------------------

    /// <summary>Native path of each model sheet instance in S1 (K05:n), rooted at the native-created root R.</summary>
    public static IReadOnlyList<Guid> NativePath(int sheetInstance, Guid rootInstance) => sheetInstance switch
    {
        1 => [rootInstance],
        2 => [rootInstance, PsuCpuIds.Id(0x20, 2)],
        3 => [rootInstance, PsuCpuIds.Id(0x20, 3)],
        4 => [rootInstance, PsuCpuIds.Id(0x20, 3), PsuCpuIds.Id(0x20, 4)],
        _ => throw new ArgumentOutOfRangeException(nameof(sheetInstance))
    };

    /// <summary>S0: the temporary catalog used only to capture definitions. One placement per
    /// symbol occurrence (K20:0x10 onward) on a 6-column grid; it is never saved.</summary>
    public static string CatalogSchematic(string libSymbols, Guid rootInstance)
    {
        var text = new StringBuilder(Header(PsuCpuIds.Id(0x20, 1), "A0"));
        foreach (string line in libSymbols.TrimEnd('\n').Split('\n')) text.Append('\t').Append(line).Append('\n');
        foreach (var (occurrence, index) in PsuCpuFixtureBuilder.Occurrences.Select((o, i) => (o, i)))
        {
            var component = PsuCpuFixtureBuilder.Components[occurrence.Component - 1];
            var part = PsuCpuFixtureBuilder.PartSources[component.Part - 1];
            string at = $"(at {Mm(101.6m * (index % 6 + 1))} {Mm(203.2m * (index / 6 + 1))} 0)";
            text.Append($"\t(symbol (lib_id {Q(part.CacheKey)}) {at} (unit {occurrence.Unit}) (uuid {Q(PsuCpuIds.Id(0x20, 0x10 + index))})\n")
                .Append($"\t\t(property \"Reference\" {Q(component.Reference)} {at} (effects (font (size 1.27 1.27))))\n")
                .Append($"\t\t(property \"Value\" {Q(part.Name)} {at} (effects (font (size 1.27 1.27))))\n")
                .Append($"\t\t(instances (project \"fixture\" (path {Q("/" + rootInstance.ToString("D"))} ")
                .Append($"(reference {Q(component.Reference)}) (unit {occurrence.Unit})))))\n");
        }
        return text.Append("\t(sheet_instances (path \"/\" (page \"1\")))\n)\n").ToString();
    }

    /// <summary>S1 "Sheets" and S2 "RootOnly" files for the native-created root instance R.</summary>
    public static IReadOnlyDictionary<string, string> SeedFiles(PsuCpuSeed seed, Guid rootInstance)
    {
        string Id(int n) => PsuCpuIds.Id(0x20, n).ToString("D");
        string Sheet(string x, string y, string height, int id, string name, string file, string path, string page) =>
            $"\t(sheet (at {x} {y}) (size 38.1 {height}) (stroke (width 0) (type default)) (fill (color 0 0 0 0)) (uuid {Q(Id(id))})\n" +
            $"\t\t(property \"Sheetname\" {Q(name)} (at {x} {y} 0) (effects (font (size 1.27 1.27)) (justify left bottom)))\n" +
            $"\t\t(property \"Sheetfile\" {Q(file)} (at {x} {Mm(decimal.Parse(y, CultureInfo.InvariantCulture) + decimal.Parse(height, CultureInfo.InvariantCulture))} 0) " +
            "(effects (font (size 1.27 1.27)) (justify left top)))\n" +
            $"\t\t(instances (project \"fixture\" (path {Q(path)} (page {Q(page)})))))\n";
        string Screen(int id, string paper, string contents = "") => Header(PsuCpuIds.Id(0x20, id), paper) + "\t(lib_symbols)\n" + contents + ")\n";
        string root = "/" + rootInstance.ToString("D");
        const string Pages = "\t(sheet_instances (path \"/\" (page \"1\")))\n";
        return seed switch
        {
            PsuCpuSeed.RootOnly => new Dictionary<string, string> { ["fixture.kicad_sch"] = Screen(1, "A4", Pages) },
            PsuCpuSeed.Sheets => new Dictionary<string, string>
            {
                ["fixture.kicad_sch"] = Screen(1, "A4", Sheet("50.8", "50.8", "50.8", 2, "PSU", "psu.kicad_sch", root, "2")
                    + Sheet("152.4", "50.8", "50.8", 3, "CPU", "cpu.kicad_sch", root, "3") + Pages),
                ["psu.kicad_sch"] = Screen(5, "A4"),
                ["cpu.kicad_sch"] = Screen(6, "A3", Sheet("330.2", "25.4", "25.4", 4, "CPU_POWER", "cpu_power.kicad_sch", root + "/" + Id(3), "4")),
                ["cpu_power.kicad_sch"] = Screen(7, "A4"),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(seed), "Seed None writes repository files only.")
        };
    }

    /// <summary>Write and load the requested seed through the native editor. Unless the seed is
    /// None this captures and normalizes the eight part symbols from S0, verifies the seed's
    /// screens and paths, and writes the repository files and baseline design.xml.</summary>
    public static async Task<PsuCpuNativeContext> PrepareNativeAsync(NativeClient client, DocumentSpecifier emptyRoot,
        string projectDirectory, PsuCpuSeed seed, string evidence, CancellationToken token)
    {
        if (!System.Enum.IsDefined(seed)) throw new ArgumentOutOfRangeException(nameof(seed));
        Guid rootInstance = Guid.ParseExact(emptyRoot.SheetPath.Path[0].Value, "D");
        string File(string name) => Path.Combine(projectDirectory, name);
        System.IO.Directory.CreateDirectory(evidence);
        await WriteRepositoryFilesAsync(projectDirectory, token);
        if (seed == PsuCpuSeed.None)
        {
            var repositoryOnly = new PsuCpuNativeContext(projectDirectory, emptyRoot, rootInstance, seed, [], null,
                File("hardware.xml"), File("system.blocks.xml"), File("design.xml"), File("flat-structure.engineering.xml"));
            await WriteSeedEvidenceAsync(evidence, repositoryOnly, null, token);
            return repositoryOnly;
        }
        string schematic = File("fixture.kicad_sch");
        await System.IO.File.WriteAllTextAsync(schematic, CatalogSchematic(ReadText("lib_symbols.kicad_sexpr"), rootInstance), token);
        var (root, catalog) = await ReloadAsync(client, emptyRoot, schematic, token);
        var partSymbols = CapturePartSymbols(catalog.Data);
        try { SchematicPartSymbols.Validate(new SchematicDesign(Engineering(), catalog.Data, [], [], partSymbols), token); }
        catch (AutomationException error) { throw Failure("psu_cpu_symbol_capture_incomplete", error.Code + ": " + error.Message); }

        if (seed == PsuCpuSeed.RootOnly)
            foreach (string child in new[] { "psu.kicad_sch", "cpu.kicad_sch", "cpu_power.kicad_sch" })
                System.IO.File.Delete(File(child));
        foreach (var (name, content) in SeedFiles(seed, rootInstance))
            await System.IO.File.WriteAllTextAsync(File(name), content, token);
        (root, var loaded) = await ReloadAsync(client, root, schematic, token);
        RequireSeedHierarchy(loaded.Data, seed, rootInstance);

        var state = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        var baseline = Baseline(state.Electrical.Hierarchy.Data, seed, rootInstance, token);
        await System.IO.File.WriteAllTextAsync(File("design.xml"), SchematicDesignXml.Write(baseline, []), token);
        var context = new PsuCpuNativeContext(projectDirectory, root, rootInstance, seed, partSymbols, baseline,
            File("hardware.xml"), File("system.blocks.xml"), File("design.xml"), File("flat-structure.engineering.xml"));
        await WriteSeedEvidenceAsync(evidence, context, loaded.Data, token);
        return context;
    }

    /// <summary>The NativePsuCpuSeed journey body: prepare S1, S2 and None in one project and require
    /// exact captures, hierarchies and baselines, plus byte-identical part-symbol records from the
    /// two S0 captures. Evidence for each seed is written to its own subdirectory.</summary>
    public static async Task VerifySeedsAsync(NativeClient client, DocumentSpecifier emptyRoot, string projectDirectory,
        string evidence, CancellationToken token)
    {
        var records = new List<byte[]>();
        foreach (var seed in new[] { PsuCpuSeed.Sheets, PsuCpuSeed.RootOnly, PsuCpuSeed.None })
        {
            string directory = Path.Combine(evidence, "psu-cpu-seed-" + seed.ToString().ToLowerInvariant());
            var context = await PrepareNativeAsync(client, emptyRoot, projectDirectory, seed, directory, token);
            emptyRoot = context.Root;
            if (seed == PsuCpuSeed.None) { Assert.IsNull(context.Baseline); Assert.IsEmpty(context.PartSymbols); continue; }
            Assert.HasCount(8, context.PartSymbols);
            // Every captured K21 identity must name the same pin as lib_symbols.kicad_sexpr, so lanes can
            // use the file's pin identities against native captures.
            var pinIssues = CapturedPinIdentityIssues(context.PartSymbols, ReadText("lib_symbols.kicad_sexpr"));
            if (pinIssues.Count > 0)
                throw Failure("psu_cpu_symbol_capture_incomplete", string.Join("; ", pinIssues.Take(20))
                    + (pinIssues.Count > 20 ? $"; and {pinIssues.Count - 20} more" : ""));
            records.Add(await System.IO.File.ReadAllBytesAsync(Path.Combine(directory, "psu-cpu-part-symbols.xml"), token));
            var stored = SchematicDesignXml.Read(await System.IO.File.ReadAllTextAsync(context.DesignPath, token), []);
            Assert.AreEqual(SchematicDesignXml.Write(context.Baseline!, []), SchematicDesignXml.Write(stored, []));
        }
        CollectionAssert.AreEqual(records[0], records[1], "Two preparations must capture byte-identical part symbols.");
        foreach (string name in new[] { "hardware.xml", "system.blocks.xml", "flat-structure.engineering.xml" })
            CollectionAssert.AreEqual(ReadBytes(name), await System.IO.File.ReadAllBytesAsync(Path.Combine(projectDirectory, name), token), name);
    }

    /// <summary>The baseline with Engineering set to the requested stage and the part symbols it uses.
    /// Sheet bindings missing from S2 stay absent; supplying them is sheet generation's job.</summary>
    public static SchematicDesign Desired(PsuCpuNativeContext context, PsuCpuStage stage)
    {
        var baseline = context.Baseline ?? throw Failure("psu_cpu_baseline_unresolved", "Seed None has no native baseline.");
        var design = Engineering(stage);
        var present = design.Circuit.Parts.Select(p => p.Id).ToHashSet();
        SchematicPartSymbol[] symbols = [.. context.PartSymbols.Where(s => present.Contains(s.PartId))];
        return baseline with { Engineering = design, PartSymbols = symbols.Length == 0 ? null : symbols };
    }

    /// <summary>Create recovery state for the prepared baseline. The native hierarchy must still be the
    /// captured baseline; a changed editor is reported rather than silently adopted.</summary>
    public static async Task<StoredDesignRecovery> InitializeRecoveryAsync(NativeClient client, PsuCpuNativeContext context,
        string recoveryPath, CancellationToken token)
    {
        var baseline = context.Baseline ?? throw Failure("psu_cpu_baseline_unresolved", "Seed None has no native baseline.");
        var session = await client.InvokeAsync<GetAutomationSession, AutomationSession>(new(), token);
        var state = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = context.Root.Clone(), ProcessEpoch = client.Epoch }, token);
        if (!Equals(state.Electrical.Hierarchy.Data, baseline.Schematic))
            throw Failure("psu_cpu_baseline_unresolved", "The native hierarchy changed after the seed was prepared.");
        byte[] bytes = await System.IO.File.ReadAllBytesAsync(context.DesignPath, token);
        return new DesignRecoveryStore(recoveryPath).Save(new(Guid.NewGuid(), Guid.ParseExact(session.InstanceId, "D"),
            new(state.State.Revision.Epoch, state.State.Revision.Sequence), state.Electrical.Hierarchy.TrackingComplete, baseline, bytes,
            baseline.Schematic.Clone(), [], BaselineElectrical: state.Electrical.Clone(), ObservedElectrical: state.Electrical.Clone()), null);
    }

    /// <summary>Compare a synchronized native result with ExpectedNative(stage): sheets, symbols, nets,
    /// isolated pins, hierarchical labels, sheet pins, labels, forbidden items and net naming.
    /// Presentation needs native geometry and is checked by NativePresentationChecks.</summary>
    public static void AssertNative(SchematicDesign synchronized, SchematicElectricalState state, PsuCpuStage stage)
    {
        var expected = ExpectedNative(stage);
        var differences = new List<string>();
        void Differ(string category, string detail) => differences.Add(category + ": " + detail);
        var data = state.Hierarchy?.Data ?? throw Failure("psu_cpu_native_mismatch", "sheet: no native hierarchy snapshot");
        var screens = data.Instances.ToDictionary(s => string.Join('/', s.Metadata.Document.SheetPath.Path.Select(p => p.Value)), StringComparer.Ordinal);
        var sheetBindings = synchronized.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => b.NativePath);
        var paths = new Dictionary<string, IReadOnlyList<Guid>>(StringComparer.Ordinal);
        string Key(IEnumerable<Guid> path) => string.Join('/', path.Select(p => p.ToString("D")));
        IEnumerable<T> Items<T>(SchematicScreenData screen, Google.Protobuf.Reflection.MessageDescriptor descriptor) where T : Google.Protobuf.IMessage<T>, new() =>
            screen.Items.Where(i => i.Is(descriptor)).Select(i => i.Unpack<T>());
        string Leaf(string name) => name[(name.LastIndexOf('/') + 1)..];

        if (!CircuitXml.Write(synchronized.Engineering.Circuit.WithoutPlacement()).Equals(CircuitXml.Write(Engineering(stage).Circuit), StringComparison.Ordinal))
            Differ("net", $"the synchronized circuit is not the {stage} stage of E1");
        if (data.Instances.Count != expected.Sheets.Count) Differ("sheet", $"{data.Instances.Count} screens loaded, expected {expected.Sheets.Count}");
        foreach (var sheet in expected.Sheets)
        {
            if (!sheetBindings.TryGetValue(sheet.ModelSheetInstance, out var path)) { Differ("sheet", $"{sheet.Key} has no sheet binding"); continue; }
            paths[sheet.Key] = path;
            if (!screens.TryGetValue(Key(path), out var screen)) { Differ("sheet", $"{sheet.Key} is not loaded at {Key(path)}"); continue; }
            if (screen.Metadata.Page?.PageSize.ToString() != "Ps" + sheet.Paper)
                Differ("sheet", $"{sheet.Key} paper {screen.Metadata.Page?.PageSize} is not {sheet.Paper}");
            // S1 identities are exact; sheets generated later (from S2) carry their own new identities.
            if (sheet.NativeSheetSymbol is Guid symbol ? path[^1] == symbol : path.Count == 1)
                if (screen.Metadata.ScreenId?.Value != sheet.NativeScreen.ToString("D")) Differ("sheet", $"{sheet.Key} screen identity changed");
            if (sheet.Parent is null) continue;
            var parent = screens.GetValueOrDefault(Key(path.Take(path.Count - 1)));
            var owner = parent is null ? null : Items<SheetSymbol>(parent, SheetSymbol.Descriptor).SingleOrDefault(s => s.Id.Value == path[^1].ToString("D"));
            if (owner is null) { Differ("sheet", $"{sheet.Key} has no sheet symbol on its parent"); continue; }
            if (owner.NameField?.Text?.Text_ != sheet.SheetName || owner.FilenameField?.Text?.Text_ != sheet.File || owner.PageNumber != sheet.Page)
                Differ("sheet", $"{sheet.Key} sheet symbol name, file or page differs");
            var pinNames = owner.Pins.Select(p => p.Text?.Text_ ?? "").Order(StringComparer.Ordinal).ToArray();
            if (!pinNames.SequenceEqual(expected.SheetPins.GetValueOrDefault(sheet.Key, [])))
                Differ("sheet_pin", $"{sheet.Key} pins [{string.Join(", ", pinNames)}]");
        }

        var symbolBindings = synchronized.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId, b => b.NativeObjectId.ToString("D"));
        foreach (var group in expected.Symbols.GroupBy(s => s.Sheet))
        {
            if (!paths.TryGetValue(group.Key, out var path) || !screens.TryGetValue(Key(path), out var screen)) continue;
            var placed = Items<SchematicSymbolInstance>(screen, SchematicSymbolInstance.Descriptor).ToDictionary(s => s.Id.Value, StringComparer.Ordinal);
            foreach (var symbol in group)
            {
                if (!symbolBindings.TryGetValue(symbol.Occurrence, out var id) || !placed.Remove(id, out var native))
                { Differ("symbol", $"{symbol.Reference} unit {symbol.Unit} is not placed on {group.Key}"); continue; }
                var link = native.LibraryId ?? native.Definition?.Id;
                string libId = native.LibName.Length != 0 ? native.LibName : link is null ? "" : link.LibraryNickname + ":" + link.EntryName;
                if (native.Unit?.Unit != symbol.Unit || native.ReferenceField?.Text?.Text_ != symbol.Reference
                    || native.ValueField?.Text?.Text_ != symbol.Value || libId != symbol.LibId)
                    Differ("symbol", $"{symbol.Reference} unit {symbol.Unit} has a different unit, reference, value or lib_id");
            }
            foreach (var extra in placed.Values) Differ("symbol", $"unexpected symbol {extra.ReferenceField?.Text?.Text_} on {group.Key}");
        }
        foreach (var sheet in expected.Sheets.Where(s => !expected.Symbols.Any(x => x.Sheet == s.Key)))
            if (paths.TryGetValue(sheet.Key, out var path) && screens.TryGetValue(Key(path), out var screen)
                && Items<SchematicSymbolInstance>(screen, SchematicSymbolInstance.Descriptor).Any())
                Differ("symbol", $"{sheet.Key} must hold no symbols");

        foreach (var sheet in expected.Sheets)
        {
            if (!paths.TryGetValue(sheet.Key, out var path) || !screens.TryGetValue(Key(path), out var screen)) continue;
            var hierarchical = Items<HierarchicalLabel>(screen, HierarchicalLabel.Descriptor).Select(l => l.Text?.Text_ ?? "").Distinct().Order(StringComparer.Ordinal).ToArray();
            if (!hierarchical.SequenceEqual(expected.HierarchicalLabels.GetValueOrDefault(sheet.Key, [])))
                Differ("hierarchical_label", $"{sheet.Key} [{string.Join(", ", hierarchical)}]");
            var local = Items<LocalLabel>(screen, LocalLabel.Descriptor).Select(l => l.Text?.Text_ ?? "").ToHashSet(StringComparer.Ordinal);
            var allowed = expected.AllowedLabelNames.GetValueOrDefault(sheet.Key, []);
            foreach (string name in local.Concat(hierarchical).Where(n => !allowed.Contains(n))) Differ("label", $"{name} is not allowed on {sheet.Key}");
            foreach (string name in expected.RequiredLocalLabelNames.GetValueOrDefault(sheet.Key, []).Where(n => !local.Contains(n)))
                Differ("label", $"{name} is missing on {sheet.Key}");
            foreach (var item in screen.Items)
            {
                string? forbidden = item.Is(GlobalLabel.Descriptor) ? "global label" : item.Is(NoConnectMarker.Descriptor) ? "no-connect marker"
                    : item.Is(BusEntry.Descriptor) ? "bus entry" : item.Is(DirectiveLabel.Descriptor) || item.Is(SchematicText.Descriptor)
                        || item.Is(SchematicTextBox.Descriptor) ? "unlisted text"
                    : item.Is(SchematicLine.Descriptor) && item.Unpack<SchematicLine>().Type == SchematicLineType.SltBus ? "bus"
                    : item.Is(SchematicSymbolInstance.Descriptor) && item.Unpack<SchematicSymbolInstance>() is var s
                        && (s.Definition?.Type is SchematicSymbolType.SstGlobalPower or SchematicSymbolType.SstLocalPower
                            || (s.ReferenceField?.Text?.Text_ ?? "").StartsWith('#')) ? "power symbol" : null;
                if (forbidden is not null) Differ("forbidden", $"{forbidden} on {sheet.Key}");
            }
        }

        SchematicElectricalComparisonResult? comparison = null;
        try { comparison = SchematicElectricalComparison.Compare(synchronized, state, []); }
        catch (AutomationException error) { Differ("net", "the native snapshot cannot be compared: " + error.Code); }
        if (comparison is { PinBindingsComplete: false })
            Differ("net", "pin bindings incomplete: " + string.Join(", ", comparison.Issues.Take(5).Select(i => i.Code)));
        else if (comparison is { ConnectivityEquivalent: false })
            foreach (var difference in comparison.Differences.Take(5)) Differ("net", $"{difference.Kind} with {difference.Pins.Count} pins");
        var references = synchronized.Engineering.Circuit.Components.ToDictionary(c => c.Reference, c => c.Id, StringComparer.Ordinal);
        var partitions = comparison?.PinPartitions ?? [];
        var circuitNets = Engineering(stage).Circuit.Nets;
        foreach (var partition in partitions.Where(p => p.Pins.Count > 1))
        {
            var net = circuitNets.SingleOrDefault(n => n.Pins.ToHashSet().SetEquals(partition.Pins));
            if (net is not null && Leaf(partition.NativeName ?? "") != net.Name)
                Differ("naming", $"native net {partition.NativeName} realizes {net.Name}");
            if (partition.Pins.Contains(new PinEndpoint(references.GetValueOrDefault("U6"), "5")))
                Differ("forbidden", "the memory SDA pin U6.5 is connected");
        }
        if (partitions.Any(p => Leaf(p.NativeName ?? "") == "MEM_SDA")) Differ("forbidden", "a MEM_SDA net exists");
        if (partitions.Count > 0)
            foreach (var (reference, numbers) in expected.IsolatedPins)
                foreach (string number in numbers)
                    if (references.TryGetValue(reference, out var component)
                        && partitions.SingleOrDefault(p => p.Pins.Contains(new PinEndpoint(component, number))) is not { Pins.Count: 1 })
                        Differ("isolated_pin", $"{reference}.{number} is not alone in its native net");
        if (partitions.Count > 0)
            foreach (var group in expected.JoinedPins)
            {
                var pins = group.Select(p => new PinEndpoint(references.GetValueOrDefault(p.Reference), p.Number)).ToHashSet();
                if (partitions.SingleOrDefault(p => p.Pins.Contains(pins.First())) is not { } joined || !joined.Pins.ToHashSet().SetEquals(pins))
                    Differ("joined_pin", $"{string.Join(" and ", group.Select(p => p.Reference + "." + p.Number))} are not exactly one native net");
            }
        if (differences.Count > 0)
            throw Failure("psu_cpu_native_mismatch", string.Join("; ", differences.Take(20))
                + (differences.Count > 20 ? $"; and {differences.Count - 20} more" : ""));
    }

    // ---- Native helpers ------------------------------------------------------------------

    private static async Task<(DocumentSpecifier Root, SchematicHierarchyDataSnapshot Snapshot)> ReloadAsync(NativeClient client,
        DocumentSpecifier current, string schematic, CancellationToken token)
    {
        // Loading the written file explicitly; opening again never silently replaces the native document.
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = current.Clone() }, token);
        var opened = await client.OpenRootSchematicAsync(schematic, token);
        var snapshot = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(
            new() { Document = opened.Document }, token);
        return (opened.Document, snapshot);
    }

    /// <summary>Exactly the eight cache keys of lib_symbols.kicad_sexpr, with definition-pin UUIDs
    /// normalized to K21:k in (cache key; unit; body style; pin number) order.</summary>
    internal static SchematicPartSymbol[] CapturePartSymbols(SchematicHierarchyData data)
    {
        var cached = data.Instances.SelectMany(s => s.CachedSymbols).GroupBy(s => s.CacheKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Clone(), StringComparer.Ordinal);
        var keys = Parts().Select(p => p.CacheKey).ToHashSet(StringComparer.Ordinal);
        if (!keys.SetEquals(cached.Keys))
            throw Failure("psu_cpu_symbol_capture_incomplete", "S0 captured [" + string.Join(", ", cached.Keys.Order(StringComparer.Ordinal)) + "]");
        var pins = cached.OrderBy(c => c.Key, StringComparer.Ordinal).SelectMany(c => c.Value.Definition.Items
                .Where(child => child.Item?.Is(SchematicPin.Descriptor) == true)
                .Select(child => (Key: c.Key, Unit: child.Unit?.Unit ?? 0, Style: child.BodyStyle?.Style ?? 0, Child: child, Pin: child.Item.Unpack<SchematicPin>())))
            .OrderBy(p => p.Key, StringComparer.Ordinal).ThenBy(p => p.Unit).ThenBy(p => p.Style).ThenBy(p => p.Pin.Number, StringComparer.Ordinal).ToArray();
        for (int k = 0; k < pins.Length; ++k)
        {
            pins[k].Pin.Id = new() { Value = PsuCpuIds.Id(0x21, k + 1).ToString("D") };
            pins[k].Child.Item = Any.Pack(pins[k].Pin);
        }
        return [.. Parts().Select(p => new SchematicPartSymbol(p.Id,
            new LibraryIdentifier { LibraryNickname = p.Library, EntryName = p.Entry }, cached[p.CacheKey], BodyStyle: 1))];
    }

    /// <summary>Differences between captured definition-pin identities and lib_symbols.kicad_sexpr.
    /// A pin is named by (cache key, unit, body style, number), read exactly as CapturePartSymbols
    /// reads it; that tuple is unique in fixture version 1. CapturePartSymbols numbers K21 in the same
    /// order as PsuCpuFixtureBuilder.NormalizePinIdentities, so an empty result means the capture holds
    /// exactly the file's pins and each captured K21 identity names the same pin as the file.</summary>
    internal static List<string> CapturedPinIdentityIssues(IReadOnlyList<SchematicPartSymbol> captured, string libSymbols)
    {
        var issues = new List<string>();
        static string Name((string CacheKey, int Unit, int Style, string Number) pin) =>
            $"{pin.CacheKey} unit {pin.Unit} style {pin.Style} pin {pin.Number}";
        var expected = new Dictionary<(string CacheKey, int Unit, int Style, string Number), string>();
        foreach (var pin in PsuCpuFixtureBuilder.LibraryPins(libSymbols))
            if (!expected.TryAdd((pin.CacheKey, pin.Unit, pin.Style, pin.Number), pin.Id))
                issues.Add($"{Name((pin.CacheKey, pin.Unit, pin.Style, pin.Number))} occurs twice in lib_symbols.kicad_sexpr");
        var seen = new HashSet<(string CacheKey, int Unit, int Style, string Number)>();
        foreach (var symbol in captured)
            foreach (var child in symbol.Symbol?.Definition?.Items.AsEnumerable() ?? [])
            {
                if (child.Item?.Is(SchematicPin.Descriptor) != true) continue;
                var pin = child.Item.Unpack<SchematicPin>();
                var key = (symbol.Symbol!.CacheKey, child.Unit?.Unit ?? 0, child.BodyStyle?.Style ?? 0, pin.Number);
                if (!seen.Add(key)) issues.Add($"{Name(key)} is captured twice");
                else if (!expected.TryGetValue(key, out string? id)) issues.Add($"{Name(key)} is not in lib_symbols.kicad_sexpr");
                else if (pin.Id?.Value != id) issues.Add($"{Name(key)} has identity {pin.Id?.Value}, not {id}");
            }
        foreach (var key in expected.Keys.Where(k => !seen.Contains(k))) issues.Add($"{Name(key)} was not captured");
        return issues;
    }

    /// <summary>The loaded S1 "Sheets" (or S2 "RootOnly") hierarchy exactly as contract section 1.6.2
    /// declares it, below the native-created root instance. Offline tests build
    /// the seed with this instead of copying it.</summary>
    internal static SchematicHierarchyData SeedHierarchy(Guid root, PsuCpuSeed seed)
    {
        KIID Id(Guid id) => new() { Value = id.ToString("D") };
        KIID Native(int n) => Id(PsuCpuIds.Id(0x20, n));
        var document = new DocumentSpecifier { Type = DocumentType.DoctypeSchematic, SheetPath = new(), Project = new() { Name = "fixture", Path = "/fixture" } };
        document.SheetPath.Path.Add(Id(root));
        var data = new SchematicHierarchyData { Document = document.Clone() };
        SchematicScreenData Screen(KIID[] path, int screen, PageSize paper)
        {
            var target = document.Clone(); target.SheetPath.Path.Clear(); target.SheetPath.Path.Add(path);
            var result = new SchematicScreenData { Metadata = new() { Document = target, ScreenId = Native(screen), Page = new() { PageSize = paper } } };
            data.Instances.Add(result);
            return result;
        }
        SchematicField Field(string text) => new() { Text = new() { Text_ = text, Attributes = new() { Multiline = true } } };
        void Sheet(SchematicScreenData parent, int symbol, int child, string name, string file, string page) =>
            parent.Items.Add(Any.Pack(new SheetSymbol { Id = Native(symbol), ChildScreenId = Native(child), Path = parent.Metadata.Document.SheetPath.Clone(),
                NameField = Field(name), FilenameField = Field(file), PageNumber = page }));
        var top = Screen([Id(root)], 1, PageSize.PsA4);
        if (seed == PsuCpuSeed.RootOnly) return data;
        Sheet(top, 2, 5, "PSU", "psu.kicad_sch", "2");
        Sheet(top, 3, 6, "CPU", "cpu.kicad_sch", "3");
        Screen([Id(root), Native(2)], 5, PageSize.PsA4);
        var cpu = Screen([Id(root), Native(3)], 6, PageSize.PsA3);
        Sheet(cpu, 4, 7, "CPU_POWER", "cpu_power.kicad_sch", "4");
        Screen([Id(root), Native(3), Native(4)], 7, PageSize.PsA4);
        return data;
    }

    /// <summary>psu_cpu_seed_hierarchy_mismatch unless the loaded screens, paths, papers and sheet
    /// symbols are exactly the seed's (contract section 1.6.2).</summary>
    internal static void RequireSeedHierarchy(SchematicHierarchyData data, PsuCpuSeed seed, Guid rootInstance)
    {
        var issues = SeedHierarchyIssues(data, seed, rootInstance);
        if (issues.Count > 0) throw Failure("psu_cpu_seed_hierarchy_mismatch", string.Join("; ", issues));
    }

    /// <summary>The seed's baseline: the SheetsOnly (S1) or RootOnly (S2) stage of E1 over the captured
    /// native hierarchy, with each seeded model sheet bound to its native path. Any identity that does
    /// not resolve is psu_cpu_baseline_unresolved; seed None has no baseline.</summary>
    internal static SchematicDesign Baseline(SchematicHierarchyData native, PsuCpuSeed seed, Guid rootInstance, CancellationToken token)
    {
        var (stage, sheets) = seed switch
        {
            PsuCpuSeed.Sheets => (PsuCpuStage.SheetsOnly, new[] { 1, 2, 3, 4 }),
            PsuCpuSeed.RootOnly => (PsuCpuStage.RootOnly, new[] { 1 }),
            _ => throw Failure("psu_cpu_baseline_unresolved", $"Seed {seed} has no native baseline.")
        };
        var baseline = new SchematicDesign(Engineering(stage), native,
            [.. sheets.Select(n => new SchematicSheetBinding(PsuCpuIds.Id(0x05, n), NativePath(n, rootInstance)))], [], null);
        var report = SchematicDesignBindings.Inspect(baseline, [], token);
        if (!report.IdentitiesResolved)
            throw Failure("psu_cpu_baseline_unresolved", string.Join("; ", report.Issues.Select(i => $"{i.Code} {i.ModelId} {i.NativePath}")));
        return baseline;
    }

    internal static List<string> SeedHierarchyIssues(SchematicHierarchyData data, PsuCpuSeed seed, Guid rootInstance)
    {
        var issues = new List<string>();
        var expected = expectedNative.Value.Sheets.Where(s => seed == PsuCpuSeed.Sheets || s.Parent is null).ToArray();
        var screens = data.Instances.ToDictionary(s => string.Join('/', s.Metadata.Document.SheetPath.Path.Select(p => p.Value)), StringComparer.Ordinal);
        if (screens.Count != expected.Length) issues.Add($"{screens.Count} screens instead of {expected.Length}");
        foreach (var (sheet, ordinal) in expected.Select(s => (s, Array.IndexOf(["ROOT", "PSU", "CPU", "CPU_POWER"], s.Key) + 1)))
        {
            string path = string.Join('/', NativePath(ordinal, rootInstance).Select(p => p.ToString("D")));
            if (!screens.TryGetValue(path, out var screen)) { issues.Add($"{sheet.Key} is not loaded at {path}"); continue; }
            if (screen.Metadata.ScreenId?.Value != sheet.NativeScreen.ToString("D")) issues.Add($"{sheet.Key} screen identity differs");
            if (screen.Metadata.Page?.PageSize.ToString() != "Ps" + sheet.Paper) issues.Add($"{sheet.Key} paper differs");
            if (sheet.NativeSheetSymbol is not Guid symbol) continue;
            var parent = screens.GetValueOrDefault(path[..path.LastIndexOf('/')]);
            var owner = parent?.Items.Where(i => i.Is(SheetSymbol.Descriptor)).Select(i => i.Unpack<SheetSymbol>())
                .SingleOrDefault(s => s.Id.Value == symbol.ToString("D"));
            if (owner is null || owner.NameField?.Text?.Text_ != sheet.SheetName || owner.FilenameField?.Text?.Text_ != sheet.File
                || owner.PageNumber != sheet.Page || owner.Pins.Count != 0)
                issues.Add($"{sheet.Key} sheet symbol differs");
        }
        return issues;
    }

    private static async Task WriteSeedEvidenceAsync(string evidence, PsuCpuNativeContext context, SchematicHierarchyData? loaded,
        CancellationToken token)
    {
        await System.IO.File.WriteAllTextAsync(Path.Combine(evidence, "psu-cpu-seed.json"), JsonSerializer.Serialize(new
        {
            fixture = "psu-cpu", version = Version, seed = context.Seed.ToString(), rootInstance = context.RootInstanceId,
            screens = loaded?.Instances.Select(s => new { path = s.Metadata.Document.SheetPath.Path.Select(p => p.Value).ToArray(),
                screen = s.Metadata.ScreenId?.Value, paper = s.Metadata.Page?.PageSize.ToString() }).ToArray(),
            partSymbols = context.PartSymbols.Select(s => s.Symbol.CacheKey).ToArray(),
            designSha256 = context.Baseline is null ? null
                : Convert.ToHexStringLower(SHA256.HashData(await System.IO.File.ReadAllBytesAsync(context.DesignPath, token))),
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n", token);
        await System.IO.File.WriteAllTextAsync(Path.Combine(evidence, "psu-cpu-part-symbols.xml"), PartSymbolsXml(context.PartSymbols), token);
    }

    /// <summary>Deterministic record of the captured declarations; two preparations must match byte for byte.</summary>
    public static string PartSymbolsXml(IReadOnlyList<SchematicPartSymbol> symbols)
    {
        XNamespace ns = "urn:kicad:automation:psu-cpu-fixture:1";
        return new XElement(ns + "part-symbols", new XAttribute("fixture", "psu-cpu"), new XAttribute("version", Version),
            symbols.OrderBy(s => s.PartId).Select(s => new XElement(ns + "part-symbol", new XAttribute("part", s.PartId),
                new XAttribute("library", s.LibraryId.LibraryNickname), new XAttribute("entry", s.LibraryId.EntryName),
                new XAttribute("body-style", s.BodyStyle), XElement.Parse(SchematicDataXml.Write(s.Symbol))))).ToString() + "\n";
    }

    // ---- Readers --------------------------------------------------------------------------

    private static IReadOnlyList<PsuCpuPart> ReadParts() => [.. ParseJson(ReadText("parts.json")).GetProperty("parts").EnumerateArray().Select(p =>
    {
        var source = p.GetProperty("source");
        return new PsuCpuPart(Id(p, "part"), p.GetProperty("ordinal").GetInt32(), Text(p, "name"), Text(p, "cacheKey"), Text(p, "library"),
            Text(p, "entry"), p.GetProperty("units").GetInt32(),
            new(Text(source, "path"), Text(source, "gitBlob"), Text(source, "symbol"), source.GetProperty("formatVersion").GetInt32()),
            [.. p.GetProperty("pins").EnumerateArray().Select(pin => new PartPin(Text(pin, "number"), Text(pin, "name"), pin.GetProperty("unit").GetInt32()))]);
    })];

    /// <summary>Per fixture part and unit, the pin numbers its own definition draws at one point in body style 1 (or
    /// common to all units or styles), read from the exact geometry of lib_symbols.kicad_sexpr, never from names. A
    /// no-connect pin passes no connection on in KiCad and never joins. KiCad joins each group into one connection
    /// (contract erratum 2026-09-24, decision kicad-stacked-pins-one-node-20260924); for fixture v1 this is exactly
    /// LP3982 pins 1 and 4.</summary>
    public static IReadOnlyList<PsuCpuStackedPins> StackedPins() => stackedPins.Value;

    private static IReadOnlyList<PsuCpuStackedPins> ReadStackedPins()
    {
        var result = new List<PsuCpuStackedPins>();
        foreach (var symbol in PsuCpuSexpr.Parse(ReadText("lib_symbols.kicad_sexpr")).Children("symbol"))
        {
            string key = symbol.Value(1);
            var drawn = new List<(int Unit, string Number, decimal X, decimal Y)>();
            foreach (var body in symbol.Children("symbol"))
            {
                var name = body.Value(1).Split('_');
                int unit = int.Parse(name[^2], CultureInfo.InvariantCulture), style = int.Parse(name[^1], CultureInfo.InvariantCulture);
                if (style is not (0 or 1)) continue;
                foreach (var pin in body.Children("pin").Where(p => p.Value(1) != "no_connect"))
                {
                    var at = pin.Child("at");
                    drawn.Add((unit, pin.Child("number").Value(1), decimal.Parse(at.Value(1), CultureInfo.InvariantCulture),
                        decimal.Parse(at.Value(2), CultureInfo.InvariantCulture)));
                }
            }
            int units = Parts().Single(p => p.CacheKey == key).Units;
            for (int unit = 1; unit <= units; unit++)
                foreach (var group in drawn.Where(p => p.Unit == 0 || p.Unit == unit).GroupBy(p => (p.X, p.Y)).Where(g => g.Count() > 1))
                    result.Add(new(key, unit, [.. group.Select(p => p.Number).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]));
        }
        return result;
    }

    private static PsuCpuExpectedNative ReadExpectedNative()
    {
        var root = ParseJson(ReadText("expected-native.json"));
        var pins = Parts().ToDictionary(p => p.Ordinal, p => p.Pins.Select(pin => pin.Number).ToArray());
        var partOf = PsuCpuFixtureBuilder.Components.ToDictionary(c => c.Reference, c => c.Part);
        IReadOnlyDictionary<string, IReadOnlyList<string>> Map(string name) => root.GetProperty(name).EnumerateObject()
            .ToDictionary(p => p.Name, p => Strings(p.Value), StringComparer.Ordinal);
        var presentation = root.GetProperty("presentation");
        return new(Enum.Parse<PsuCpuStage>(Text(root, "stage")), Enum.Parse<PsuCpuSeed>(Text(root, "seed")),
            [.. root.GetProperty("sheets").EnumerateArray().Select(s => new PsuCpuExpectedSheet(Text(s, "key"), Id(s, "modelSheetInstance"),
                Id(s, "definition"), OptionalText(s, "parent"), Text(s, "file"), OptionalText(s, "sheetName"),
                OptionalText(s, "nativeSheetSymbol") is { } symbol ? Guid.ParseExact(symbol, "D") : null, Id(s, "nativeScreen"),
                Text(s, "page"), Text(s, "paper")))],
            [.. root.GetProperty("symbols").EnumerateArray().Select(s => new PsuCpuExpectedSymbol(Id(s, "component"), Id(s, "occurrence"),
                Text(s, "reference"), Text(s, "libId"), Text(s, "value"), s.GetProperty("unit").GetInt32(), Text(s, "sheet")))],
            [.. root.GetProperty("nets").EnumerateArray().Select(n => new PsuCpuExpectedNet(Id(n, "net"), Text(n, "name"), PinReferences(n.GetProperty("pins"))))],
            root.GetProperty("isolatedPins").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Array
                ? Strings(p.Value) : pins[partOf[p.Name]].Except(Strings(p.Value.GetProperty("allExcept"))).Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal),
            Map("hierarchicalLabels"), Map("sheetPins"), Map("requiredLocalLabelNames"), Map("allowedLabelNames"), Strings(root.GetProperty("forbidden")),
            Text(root, "netNameRule"), new(presentation.GetProperty("pageInsetMm").GetInt32(), presentation.GetProperty("reservedBottomMm").GetInt32(),
                presentation.GetProperty("symbolBodiesDisjoint").GetBoolean()));
    }

    private static PsuCpuExpectedRealization ReadExpectedRealization()
    {
        var root = ParseJson(ReadText("expected-realization.json"));
        return new(Id(root, "graph"), Id(root, "circuit"), [.. root.GetProperty("connections").EnumerateArray().Select(c =>
            new PsuCpuExpectedConnection(Id(c, "connection"), Text(c, "owner"), Text(c, "name"), Enum.Parse<DiagramConnectionKind>(Text(c, "kind")),
                Text(c, "status"), Ids(c.GetProperty("nets")), c.GetProperty("boundary").ValueKind == JsonValueKind.Null ? null : c.GetProperty("boundary").GetInt32(),
                Ids(c.GetProperty("members")), PinReferences(c.GetProperty("pins"))))]);
    }

    private static PsuCpuExpectedMigration ReadExpectedMigration()
    {
        var root = ParseJson(ReadText("expected-migration.json"));
        Guid? Owner(JsonElement e) => Text(e, "owner") == "root" ? null : Id(e, "owner");
        return new(Id(root.GetProperty("source"), "structure"), Ids(root.GetProperty("root").GetProperty("children")),
            root.GetProperty("children").EnumerateArray().ToDictionary(c => Id(c, "block"), c => Ids(c.GetProperty("children"))),
            [.. root.GetProperty("blocks").EnumerateArray().Select(b => new PsuCpuMigratedBlock(Id(b, "block"), Text(b, "name"),
                Ids(b.GetProperty("interfaces")), Ids(b.GetProperty("componentBindings"))))],
            [.. root.GetProperty("localDiagrams").EnumerateArray().Select(d => new PsuCpuMigratedDiagram(Owner(d), Ids(d.GetProperty("connections")),
                Ids(d.GetProperty("boundaryEndpointConnections"))))],
            root.GetProperty("realizationLinks").EnumerateArray().ToDictionary(l => Id(l, "connection"), l => Ids(l.GetProperty("nets"))),
            Ids(root.GetProperty("receipt").GetProperty("cross_level_connection")), Ids(root.GetProperty("receipt").GetProperty("unclassified_statement")),
            [.. root.GetProperty("layouts").EnumerateArray().Select(l => new PsuCpuMigratedLayout(Owner(l), Ids(l.GetProperty("blocks"))))],
            Enum.Parse<RequirementRevisionActor>(Text(root.GetProperty("history"), "originActorKind")));
    }

    internal static JsonElement ParseJson(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
    private static string Text(JsonElement e, string name) => e.GetProperty(name).GetString()
        ?? throw Failure("psu_cpu_fixture_file_changed", $"{name} must be text.");
    private static string? OptionalText(JsonElement e, string name) => e.GetProperty(name).GetString();
    private static Guid Id(JsonElement e, string name) => Guid.ParseExact(Text(e, name), "D");
    private static IReadOnlyList<Guid> Ids(JsonElement e) => [.. e.EnumerateArray().Select(x => Guid.ParseExact(x.GetString()!, "D"))];
    private static IReadOnlyList<string> Strings(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetString()!)];
    private static IReadOnlyList<PsuCpuPinReference> PinReferences(JsonElement e) =>
        [.. e.EnumerateArray().Select(p => new PsuCpuPinReference(p[0].GetString()!, p[1].GetString()!))];

    private static string Header(Guid screen, string paper) =>
        $"(kicad_sch\n\t(version 20250114)\n\t(generator \"eeschema\")\n\t(uuid {Q(screen.ToString("D"))})\n\t(paper {Q(paper)})\n";
    private static string Q(object value) => PsuCpuFixtureBuilder.Quote(Convert.ToString(value, CultureInfo.InvariantCulture)!);
    private static string Mm(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string FindRoot()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (System.IO.File.Exists(Path.Combine(current.FullName, "automation", "KiCad.Automation.slnx"))) return current.FullName;
        throw new InvalidOperationException("No KiCad source checkout was found.");
    }
}

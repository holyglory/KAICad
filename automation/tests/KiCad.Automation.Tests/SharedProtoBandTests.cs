using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

// Phase 2 lanes extend three shared protocol files in parallel. A lane adds
// fields, enum values and oneof members only between its own marker pair,
// numbered inside its band, and new messages only inside its own block at the
// end of a file (automation/design/contracts/psu-cpu-fixture-and-ownership.md,
// section 3). Every other declaration keeps the frozen (name, number) pairs
// recorded as a digest at the end of this class; the parent updates that record
// when it declares a seam field (a failure prints the new digest and pairs).
[TestClass]
public sealed partial class SharedProtoBandTests
{
    private sealed record SharedFile(string Path, string[] LaneBlocks, string Frozen);

    private sealed class Scope(string name, bool isEnum, string? owner)
    {
        public string Name { get; } = name;
        public bool IsEnum { get; } = isEnum;
        // Set for declarations inside a lane message block; the lane owns them whole.
        public string? Owner { get; } = owner;
        public HashSet<string> Lanes { get; } = [];
        public string? OpenLane { get; set; }
        public List<(string Name, int Number, string? Lane, int Line)> Entries { get; } = [];
    }

    private static readonly SharedFile[] Shared =
    [
        new("api/proto/common/types/diagram_revision_types.proto", ["2A", "2B", "2C"], DiagramRevisionTypes),
        new("api/proto/common/commands/automation_commands.proto", ["2A", "2C", "2D"], AutomationCommands),
        new("api/proto/schematic/schematic_types.proto", [], SchematicTypes),
    ];

    private static readonly (string Path, string Package, string Namespace)[] LaneFiles =
    [
        ("api/proto/common/commands/schematic_realization_commands.proto", "kiapi.automation.v1", "KiCad.Automation.Protocol"),
        ("api/proto/common/commands/diagram_canvas_commands.proto", "kiapi.automation.diagrams.v1", "KiCad.Automation.Protocol.Diagrams"),
        ("api/proto/common/commands/schematic_tracking_commands.proto", "kiapi.automation.v1", "KiCad.Automation.Protocol"),
        ("api/proto/common/commands/capability_commands.proto", "kiapi.automation.v1", "KiCad.Automation.Protocol"),
    ];

    // C++ enum values share package scope, so lane-owned types and values carry lane prefixes.
    private static readonly Dictionary<string, (int First, string[] Types, string[] Values)> Lanes = new()
    {
        ["2A"] = (100, ["SchematicWiring", "SchematicRealization", "InterfaceRealizationDerivation", "PinVariant"], ["SWR_", "SRZ_", "IRD_"]),
        ["2B"] = (200, ["DiagramCanvas", "DiagramLayout", "StructuralMigration", "DiagramCreate"], ["DCV_", "DLY_", "SMG_"]),
        ["2C"] = (300, ["SchematicTracking", "SchematicRebuild", "NativeOwnership", "SchematicStateGroup", "DesignAutoSync"],
            ["STK_", "SRB_", "NOW_", "DAS_"]),
        ["2D"] = (400, ["InstanceCapability", "NativeCapability", "CodexRuntime", "NativeCrash"], ["ICP_", "NCP_", "CXR_"]),
    };

    [TestMethod]
    public void SharedProtocolFilesKeepEveryLaneAdditionInsideItsBand()
    {
        string root = FindRoot();
        var violations = Shared.SelectMany(file => Check(file, Read(root, file.Path))).ToList();
        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void LaneProtocolFilesAreBuiltAndSharedFilesNeverImportThem()
    {
        string root = FindRoot();
        string sources = Read(root, "api/CMakeLists.txt");
        var problems = new List<string>();
        foreach (var (path, package, csharp) in LaneFiles)
        {
            if (!File.Exists(Path.Combine(root, path))) { problems.Add(path + " is missing."); continue; }
            string text = Read(root, path);
            if (!text.Contains($"\npackage {package};\n", StringComparison.Ordinal)) problems.Add($"{path} must use package {package}.");
            if (!text.Contains($"\noption csharp_namespace = \"{csharp}\";\n", StringComparison.Ordinal))
                problems.Add($"{path} must use C# namespace {csharp}.");
            if (!Regex.IsMatch(sources, @"^\s*" + Regex.Escape(path["api/proto/".Length..]) + @"\s*$", RegexOptions.Multiline))
                problems.Add($"{path} is not listed in KIAPI_PROTO_SRCS of api/CMakeLists.txt.");
        }
        foreach (var file in Shared) problems.AddRange(LaneImports(file, Read(root, file.Path)));
        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
    }

    [TestMethod]
    public void BandCheckRejectsEachViolationClassAndAcceptsLaneAdditions()
    {
        string root = FindRoot();
        SharedFile revisions = Shared[0], automation = Shared[1];
        var texts = Shared.ToDictionary(file => file, file => Read(root, file.Path));
        const string AnchorFields = "  string power_net = 13;\n";
        const string AnchorHold = "  // 14: held for CN-1 connected realization, declared at a later seam freeze.\n";
        const string AnchorBand = AnchorFields + AnchorHold + "  // -- lane 2A (100-199) --\n";
        const string BelowMaximum = "  kiapi.common.types.ElectricalPinType electrical_type = 11;\n";
        const string ActionBand = "  RFA_DISCOVER_DIAGRAMS = 19;\n  // -- lane 2B (200-299) --\n";
        const string PayloadOpen = "    // -- lane 2C (300-399) --\n";
        const string PayloadClose = "    // -- end lane 2C --\n  }\n  // -- lane 2D (400-499) --\n";
        const string BlockA = "// == lane 2A messages ==\n", BlockC = "// == lane 2C messages ==\n", BlockD = "// == lane 2D messages ==\n";
        var cases = new (string Name, SharedFile File, Func<string, string> Edit, string? Expected)[]
        {
            ("unchanged files", automation, text => text, null),
            ("2A field in its own band", automation, text => Insert(text, AnchorBand, "  bool wiring_anchor_verified = 100;\n"), null),
            ("2B action value in its own band", revisions, text => Insert(text, ActionBand, "  RFA_OPEN_CANVAS = 200;\n"), null),
            ("2C payload member with its message in the 2C block", automation, text => Insert(Replace(text, PayloadOpen + PayloadClose,
                PayloadOpen + "    SchematicTrackingPayload tracking = 300;\n" + PayloadClose), BlockC,
                "message SchematicTrackingPayload { uint64 sequence = 1; }\n"), null),
            ("2D message and enum in the 2D block", automation, text => Insert(text, BlockD,
                "message NativeCrashReport { string reason = 1; }\nenum NativeCapabilityState { NCP_UNKNOWN = 0; NCP_READY = 1; }\n"), null),
            ("field in another lane's band", automation, text => Insert(text, AnchorBand, "  bool wrong_band = 200;\n"), "outside lane 2A's band"),
            ("Phase 3 number inside a lane pair", automation, text => Insert(text, AnchorBand, "  bool phase_three = 500;\n"),
                "outside lane 2A's band"),
            ("next sequential field without a parent re-freeze", automation, text => Insert(text, AnchorFields, "  bool unfrozen = 14;\n"),
                "above the frozen maximum 13"),
            ("band number outside the marker pair", automation, text => Insert(text, AnchorFields, "  bool unmarked = 150;\n"),
                "above the frozen maximum 13"),
            ("frozen field removed", automation, text => Replace(text, AnchorFields, ""), "no longer declares its frozen maximum 13"),
            ("frozen field removed below the maximum", automation, text => Replace(text, BelowMaximum, ""),
                "SchematicPinAnchor changes its frozen numbered declarations"),
            ("frozen field renamed", automation, text => Replace(text, AnchorFields, "  string power_net_name = 13;\n"),
                "SchematicPinAnchor changes its frozen numbered declarations"),
            ("frozen fields swap numbers below the maximum", automation, text => Replace(Replace(text, BelowMaximum,
                "  kiapi.common.types.ElectricalPinType electrical_type = 12;\n"), "  SchematicPinPowerScope power_scope = 12;\n",
                "  SchematicPinPowerScope power_scope = 11;\n"), "SchematicPinAnchor changes its frozen numbered declarations"),
            ("hold comment added", automation, text => Insert(text, AnchorFields, "  // 15: held for a later seam freeze.\n"), null),
            ("lane opens a closed band", automation, text => Insert(text, AnchorFields,
                "  // -- lane 2D (400-499) --\n  // -- end lane 2D --\n"), "band is closed"),
            ("marker pair left open", automation, text => Replace(text, AnchorBand + "  // -- end lane 2A --\n", AnchorBand),
                "not closed"),
            ("marker with the wrong band", automation, text => Replace(text, AnchorBand, AnchorFields + "  // -- lane 2A (100-150) --\n"),
                "must read (100-199)"),
            ("malformed marker", automation, text => Insert(text, AnchorFields, "  // -- lane 2E (500-599) --\n"), "malformed lane marker"),
            ("new shared message outside a lane block", automation, text => Replace(text, BlockA,
                "message SchematicWiringPlan { string id = 1; }\n\n" + BlockA), "not a frozen declaration"),
            ("lane message with another lane's prefix", automation, text => Insert(text, BlockA,
                "message DiagramCanvasState { string id = 1; }\n"), "name prefix"),
            ("lane enum value without the lane prefix", automation, text => Insert(text, BlockA,
                "enum SchematicWiringMode { WIRING_NONE = 0; }\n"), "value prefix"),
            ("shared enum value without the enum's prefix", revisions, text => Insert(text, ActionBand, "  OPEN_CANVAS = 200;\n"),
                "must keep the RFA_ prefix"),
            ("declaration after the lane blocks", automation, text => text + "message SchematicWiringLate { string id = 1; }\n",
                "after the lane message blocks"),
            ("lane block removed", automation, text => Replace(text, BlockC + "// == end ==\n", ""), "lane 2C message block is missing"),
            ("shared file imports a lane file", automation, text => Insert(text, "import \"board/board_types.proto\";\n",
                "import \"common/commands/schematic_tracking_commands.proto\";\n"), "imports lane file"),
        };
        var failures = new List<string>();
        foreach (var (name, file, edit, expected) in cases)
        {
            string text = edit(texts[file]);
            var found = Check(file, text).Concat(LaneImports(file, text)).ToList();
            if (expected is null ? found.Count != 0 : !found.Any(v => v.Contains(expected, StringComparison.Ordinal)))
                failures.Add($"{name}: expected {expected ?? "no violation"}, found {(found.Count == 0 ? "none" : string.Join(" | ", found))}");
        }
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    private static IEnumerable<string> LaneImports(SharedFile file, string text) => LaneFiles
        .Select(lane => lane.Path["api/proto/".Length..])
        .Where(lane => text.Contains($"import \"{lane}\";", StringComparison.Ordinal))
        .Select(lane => $"{file.Path} imports lane file {lane}; shared files never import lane files.");

    private static List<string> Check(SharedFile file, string text)
    {
        var violations = new List<string>();
        var scopes = Parse(file, text, violations);
        var frozen = file.Frozen.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' '))
            .ToDictionary(parts => parts[0], parts => (Max: int.Parse(parts[1], CultureInfo.InvariantCulture), Digest: parts[2],
                Lanes: parts[3..].ToHashSet()));
        foreach (var scope in scopes.Where(s => s.Owner is null))
        {
            if (!frozen.TryGetValue(scope.Name, out var freeze))
            {
                violations.Add($"{file.Path}: {scope.Name} is not a frozen declaration; lanes declare new types only in their message block.");
                continue;
            }
            foreach (string lane in scope.Lanes.Except(freeze.Lanes))
                violations.Add($"{file.Path}: {scope.Name} has a lane {lane} marker pair, but that band is closed here.");
            foreach (string lane in freeze.Lanes.Except(scope.Lanes))
                violations.Add($"{file.Path}: {scope.Name} is missing its frozen lane {lane} marker pair.");
            var outside = scope.Entries.Where(e => e.Lane is null).ToList();
            int max = outside.Count == 0 ? 0 : outside.Max(e => e.Number);
            if (max < freeze.Max)
                violations.Add($"{file.Path}: {scope.Name} no longer declares its frozen maximum {freeze.Max}; existing numbers never change.");
            var pairs = outside.OrderBy(e => e.Number).ThenBy(e => e.Name, StringComparer.Ordinal)
                .Select(e => e.Name + "=" + e.Number.ToString(CultureInfo.InvariantCulture)).ToList();
            string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', pairs))))[..16];
            if (digest != freeze.Digest)
                violations.Add($"{file.Path}: {scope.Name} changes its frozen numbered declarations (digest {digest}, frozen {freeze.Digest}): "
                    + string.Join(' ', pairs) + ". Existing names and numbers never change; the parent re-freezes a seam field.");
            foreach (var entry in outside.Where(e => e.Number > freeze.Max))
                violations.Add($"{file.Path}:{entry.Line}: {scope.Name} number {entry.Number} is outside every lane marker pair and "
                    + $"above the frozen maximum {freeze.Max}.");
            foreach (var entry in scope.Entries.Where(e => e.Lane is not null))
            {
                int first = Lanes[entry.Lane!].First;
                if (entry.Number < first || entry.Number > first + 99)
                    violations.Add($"{file.Path}:{entry.Line}: {scope.Name} number {entry.Number} is outside lane {entry.Lane}'s band "
                        + $"{first}-{first + 99}.");
            }
            // Shared enums keep their value prefix, as RecursiveFileAction keeps RFA_.
            string? prefix = scope.IsEnum ? CommonPrefix(outside.Where(e => e.Name != "reserved").Select(e => e.Name)) : null;
            foreach (var entry in scope.Entries.Where(e => e.Lane is not null && e.Name != "reserved" && prefix is not null
                && !e.Name.StartsWith(prefix, StringComparison.Ordinal)))
                violations.Add($"{file.Path}:{entry.Line}: {scope.Name} value {entry.Name} must keep the {prefix} prefix.");
        }
        foreach (string name in frozen.Keys.Except(scopes.Select(s => s.Name)))
            violations.Add($"{file.Path}: frozen declaration {name} is missing.");
        return violations;
    }

    [GeneratedRegex(@"^\s*// -- lane (2[A-D]) \((\d+)-(\d+)\) --$")] private static partial Regex OpenMarker();
    [GeneratedRegex(@"^\s*// -- end lane (2[A-D]) --$")] private static partial Regex CloseMarker();
    [GeneratedRegex(@"^\s*// == lane (2[A-D]) messages ==$")] private static partial Regex BlockOpen();
    [GeneratedRegex(@"^\s*// == end ==$")] private static partial Regex BlockClose();
    [GeneratedRegex(@"^\s*// (-- (end )?lane|== )")] private static partial Regex MarkerLike();
    [GeneratedRegex(@"@(?<tag>open|close|bopen|bclose) (?<args>[^@]*)@|\b(?<kind>message|enum|oneof)\s+(?<name>\w+)\s*\{|(?<end>\})"
        + @"|\breserved\s+(?<reserved>[^;]*);|(?<field>\w+)\s*=\s*(?<number>\d+)\s*(\[[^\]]*\])?\s*;")]
    private static partial Regex Token();

    // A small declaration scanner: comments are dropped, markers become tokens,
    // and every field, enum value or reserved number is recorded in its scope.
    private static List<Scope> Parse(SharedFile file, string text, List<string> violations)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);
        var code = new List<string>();
        foreach (string source in text.Split('\n'))
        {
            string trimmed = source.TrimEnd();
            if (OpenMarker().Match(trimmed) is { Success: true } opening)
                code.Add($"@open {opening.Groups[1].Value} {opening.Groups[2].Value} {opening.Groups[3].Value}@");
            else if (CloseMarker().Match(trimmed) is { Success: true } closing) code.Add($"@close {closing.Groups[1].Value}@");
            else if (BlockOpen().Match(trimmed) is { Success: true } laneBlock) code.Add($"@bopen {laneBlock.Groups[1].Value}@");
            else if (BlockClose().IsMatch(trimmed)) code.Add("@bclose @");
            else
            {
                if (MarkerLike().IsMatch(trimmed)) violations.Add($"{file.Path}:{code.Count + 1}: malformed lane marker \"{trimmed.Trim()}\".");
                int comment = source.IndexOf("//", StringComparison.Ordinal);
                code.Add(comment < 0 ? source : source[..comment]);
            }
        }
        string joined = string.Join('\n', code);
        var scopes = new List<Scope>();
        var stack = new Stack<Scope>();
        var blocks = new HashSet<string>();
        string? block = null;
        int line = 1, counted = 0;
        foreach (Match token in Token().Matches(joined))
        {
            line += joined.AsSpan(counted, token.Index - counted).Count('\n');
            counted = token.Index;
            string at = $"{file.Path}:{line}";
            if (token.Groups["tag"].Success)
            {
                string[] args = token.Groups["args"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string lane = args.Length > 0 ? args[0] : "";
                switch (token.Groups["tag"].Value)
                {
                    case "open" when stack.Count == 0 || stack.Peek().Owner is not null:
                        violations.Add($"{at}: a lane {lane} marker belongs inside a shared declaration."); break;
                    case "open":
                        int first = Lanes[lane].First;
                        if (args[1] != first.ToString(CultureInfo.InvariantCulture) || args[2] != (first + 99).ToString(CultureInfo.InvariantCulture))
                            violations.Add($"{at}: the marker for lane {lane} must read ({first}-{first + 99}).");
                        if (stack.Peek().OpenLane is not null || !stack.Peek().Lanes.Add(lane))
                            violations.Add($"{at}: {stack.Peek().Name} repeats or nests a lane {lane} marker pair.");
                        stack.Peek().OpenLane = lane; break;
                    case "close" when stack.Count == 0 || stack.Peek().OpenLane != lane:
                        violations.Add($"{at}: an end marker for lane {lane} has no matching opening marker."); break;
                    case "close":
                        stack.Peek().OpenLane = null; break;
                    case "bopen" when stack.Count > 0 || block is not null || !file.LaneBlocks.Contains(lane) || !blocks.Add(lane):
                        violations.Add($"{at}: unexpected lane {lane} message block."); break;
                    case "bopen":
                        block = lane; break;
                    case "bclose" when block is null:
                        violations.Add($"{at}: a lane block end marker has no matching block."); break;
                    case "bclose":
                        block = null; break;
                }
            }
            else if (token.Groups["kind"].Success)
            {
                string name = token.Groups["name"].Value;
                string? owner = stack.Count > 0 ? stack.Peek().Owner : block;
                if (stack.Count == 0 && block is null && blocks.Count > 0)
                    violations.Add($"{at}: {name} is declared after the lane message blocks; shared declarations come first.");
                if (stack.Count == 0 && block is not null && !Lanes[block].Types.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
                    violations.Add($"{at}: the lane {block} message block declares {name}, which lacks the lane's name prefix.");
                var scope = new Scope(stack.Count > 0 ? stack.Peek().Name + "." + name : name, token.Groups["kind"].Value == "enum", owner);
                scopes.Add(scope);
                stack.Push(scope);
            }
            else if (token.Groups["end"].Success)
            {
                if (stack.Count == 0) { violations.Add($"{at}: unbalanced closing brace."); continue; }
                var closed = stack.Pop();
                if (closed.OpenLane is not null)
                    violations.Add($"{at}: the lane {closed.OpenLane} marker pair in {closed.Name} is not closed.");
            }
            else if (stack.Count == 0)
                violations.Add($"{at}: a numbered declaration lies outside every message and enum.");
            else
            {
                var scope = stack.Peek();
                IEnumerable<(string Name, int Number)> numbers = token.Groups["reserved"].Success
                    ? Regex.Matches(token.Groups["reserved"].Value, @"\d+")
                        .Select(m => ("reserved", int.Parse(m.Value, CultureInfo.InvariantCulture)))
                    : [(token.Groups["field"].Value, int.Parse(token.Groups["number"].Value, CultureInfo.InvariantCulture))];
                foreach (var (name, number) in numbers) scope.Entries.Add((name, number, scope.OpenLane, line));
                // Top-level lane enums share package scope with every other value.
                string value = token.Groups["field"].Value;
                if (scope.IsEnum && scope.Owner is not null && !scope.Name.Contains('.', StringComparison.Ordinal)
                    && token.Groups["field"].Success && !Lanes[scope.Owner].Values.Any(p => value.StartsWith(p, StringComparison.Ordinal)))
                    violations.Add($"{at}: {scope.Name} value {value} lacks lane {scope.Owner}'s value prefix.");
            }
        }
        if (stack.Count > 0) violations.Add($"{file.Path}: {stack.Peek().Name} is not closed.");
        if (block is not null) violations.Add($"{file.Path}: the lane {block} message block is not closed.");
        foreach (string lane in file.LaneBlocks.Except(blocks))
            violations.Add($"{file.Path}: the lane {lane} message block is missing.");
        return scopes;
    }

    private static string? CommonPrefix(IEnumerable<string> names)
    {
        var prefixes = names.Select(n => n.IndexOf('_', StringComparison.Ordinal) is > 0 and var i ? n[..(i + 1)] : "").Distinct().ToList();
        return prefixes.Count == 1 && prefixes[0].Length > 0 ? prefixes[0] : null;
    }

    private static string Insert(string text, string anchor, string addition) => Replace(text, anchor, anchor + addition);

    private static string Replace(string text, string anchor, string replacement)
    {
        int index = text.IndexOf(anchor, StringComparison.Ordinal);
        if (index < 0 || text.IndexOf(anchor, index + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("The test anchor must occur exactly once: " + anchor);
        return text[..index] + replacement + text[(index + anchor.Length)..];
    }

    private static string Read(string root, string path) => File.ReadAllText(Path.Combine(root, path)).ReplaceLineEndings("\n");

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "api/proto/common/commands/automation_commands.proto"))) return directory.FullName;
        throw new DirectoryNotFoundException("KiCad source root not found.");
    }

    // Frozen at the Phase 2 freeze: "declaration maximum-number-outside-markers digest open-lanes", where the digest
    // is the first 16 hex digits of SHA-256 over the "name=number" pairs outside the lane marker pairs, sorted by
    // number then name and joined by newlines ("reserved" names a reserved number).
    private const string DiagramRevisionTypes = """
        BlockSelectionData 3 1c280b99c9aba270
        RequirementFieldsData 3 dd04bbc8c79cf717
        DiagramActorKind 4 89f9aefab469e40d 2C 2D
        RequirementFieldKind 3 5f341209705f5d4e
        DiagramRevisionOriginData 6 d822b1a26732937c 2C 2D
        FieldRestorationData 2 b84091af335dfb5b
        RequirementRevisionData 5 5009a4bcf9e12f89
        RequirementHistoryData 4 08e275319ae2532c
        FieldHistoryEntryData 7 3518028bb6604ff4
        FieldHistoryPageData 11 f1983dcb600ecb01
        DiagramHistoryEntryData 9 29425bda4759c3d7 2B
        DiagramHistoryPageData 6 63e771b418de26bd
        DiagramChangeKind 4 b3ab3223020b548e
        DiagramChangeCategory 11 61886d699b435dbc 2A 2B 2C
        DiagramHistoryChangeData 5 3a4024c49776c5c6 2B
        DiagramHistoryComparisonData 7 eb850ddbff81693a
        PrepareDiagramRestorationData 2 c63110320367a1bc
        BlockDesignStateData 6 63c9a7afc661adf9 2B
        ImplementationChangeKind 3 10aca46bcc34253f
        ImplementationChangeData 8 8293268c048e2682
        DefinitionChoiceStateData 3 c527bdaabad7026e
        DefinitionTextChoiceData 7 d9f762677da0890f
        KnowledgeClassReferenceData 3 b915c546bf5f856c
        DefinitionClassChoiceData 7 d9f762677da0890f
        BlockDefinitionData 8 0c03f8c4da19c2c2
        BlockRevisionData 11 adfc01811a0101b9 2A 2B 2C
        ComponentRealizationData 3 192760d17f1c6536
        BlockComponentBindingsData 1 430073a422e73269
        PhysicalAllocationKindData 5 e3ebb82a25c1521c 2B
        PhysicalAllocationStateData 2 cc0482207cf12e55
        PhysicalAllocationTargetData 6 9aac1ce30d864093
        BlockPhysicalAllocationData 3 59f92b77fd94ed83
        DiagramRefinementAttachmentData 7 23aeb30684b7f23b
        DiagramRefinementInputData 8 647fe819a0119460
        RecursiveBlockGraphData 11 7d75ff3ea2ac93fe 2A 2B 2C
        BlockProposalIssueKindData 2 e482192887b345a3
        BlockProposalIssueData 5 a6315f19dd4c6b8a
        BlockProposalRecordData 7 4dd51372dc0ad396
        RecursiveEditorDocument 7 93e4b9a593f865d3 2B 2C
        RecursiveFileAction 19 5ed57e2fe8b13266 2B
        ImplementationActionKind 5 0a33da0d6922ed39
        ManageImplementationData 9 255a22e4b49c59f4
        ConnectionDraftData 13 3ebcd852d2ac8c65 2A 2B
        DiagramAnnotationListData 1 5c078cad2cc95c6a
        SaveConnectionDraftData 11 53118098ccaf59c8 2B 2C
        RequirementConflictData 4 995effd0594bd96b
        RequirementResolutionData 10 8dbd0d3cd95e702c
        RebaseRequirementsRequest 2 281f2991cb9d5371
        RequirementMergeData 9 ea8a03db4bf36358
        BlockDraftData 12 e43ce6a48e525625 2B 2C
        SaveBlockDraftData 7 f39345ff13085d6a 2B 2C
        RecursiveFileRequest 24 1cc046058534517b 2B
        RecursiveFileResult 20 e7ae047d4454c741 2B
        DiagramBoundaryInterfaceData 5 0d2ac2bfcb73d8c3 2A 2B
        BlockLocalDiagramData 5 804cbf12d7a56e7d 2A 2B
        DiagramAnnotationRole 2 c920eb13132515d9
        DiagramAnnotationTargetKind 3 b5cdb4c613bc7e02
        DiagramAnnotationPointData 2 fd7ba212bd8bd319
        DiagramAnnotationStrokeData 1 b61a3ec05418bfd6
        DiagramAnnotationData 10 0db9c975652b3ab8 2B
        ConnectionSelectionData 3 94797395e035ccde
        ConnectionDesignStateData 4 64c1aa4afd9c9535
        DiagramConnectionKind 5 a882efbb55b13b41 2B
        ConnectionRevisionData 11 a529ad3d32b5b6ca 2A 2B 2C
        ConnectionArchiveData 5 7454241aa0799a4d
        DiagramEndpointKind 5 fb301377ab11566a 2B
        DiagramPinTargetData 4 6b0eeb3241cd357e 2A 2C
        DiagramPinSelectorData 4 afffe840f136f4a7
        DiagramEndpointBindingData 7 74cbdba6277ac262 2A 2B 2C
        DiagramDomain 5 1997d1fa2c91a01a
        DiagramInterfaceDirection 3 176bcdf46a539685
        DiagramConnectionDirection 3 1e70b724ff81bf37
        DiagramPortSide 4 3f604870d35469db
        DiagramRealizationState 3 50305a917dfbcf31
        DiagramRectData 4 1b20369a1fcb2e39
        DiagramBlockPlacementData 4 9cfdcc2f56974731 2B
        DiagramPortPlacementData 4 4b052e0369c9183c 2B
        DiagramConnectionRouteData 5 cedc3548cdc86625 2B
        DiagramPresentationViewData 5 ab58e6403d84e769 2B
        InterfaceRealizationTargetKind 3 a8d102a49f4ba546 2B
        InterfaceRealizationTargetData 5 075ac5f3162b634d 2B
        InterfaceRealizationData 5 523deb76131879c6 2B
        InterconnectSegmentKind 5 9d08b026a7aa223b 2B
        InterconnectSegmentData 13 9723760b33c54152 2B
        InterconnectJoinData 2 bacd7d92edd5fb29
        InterconnectRealizationData 5 6e71c1b582f92f33 2B
        NewBlockOccurrenceData 7 383c545e21c32ccc 2B
        NewConnectionData 10 aefd2afe6698b8dd 2B
        LevelDraftData 5 26440dbaece595ba 2B
        RevisionIdAssignmentData 3 d1029773a6271bb8
        SaveLevelDraftData 11 39d210acf498cbc5 2B
        SaveSummaryData 6 9bb8b87c37baabf1 2B
        DiagramErrorDetailData 4 366338f005546a58 2B
        LevelEditCommandKind 3 6d2a456ca6cc3dac 2B
        LevelEditCommandData 9 3844eff96e467faa 2B
        LevelEditEffectKind 8 f97719808d5a53d8 2B
        LevelEditEffectData 4 18850e89673c0757
        LevelEditResultData 2 3cb24608c32d0fe3
        RebaseLevelData 2 281f2991cb9d5371
        LevelConflictKind 14 b7ede58f2a72c8ab 2B
        LevelConflictData 9 74abdda020c0aaba 2B
        PresentationOverrideData 2 9cd12e37d76b744a
        LevelMergeData 9 bc8bb250ff49cd9d 2B
        ReparentBlockData 8 944f700a8bd1e25c 2B
        ReparentPreviewData 3 9d6682e0e908886c 2B
        CreateDiagramData 5 57df190488a5807a 2B
        MigrateFlatDiagramData 8 60ed7aba17e0ec03 2B
        DiscoverDiagramsData 1 e6180ee3463d6a7d 2B
        DiscoveredDiagramStatus 5 e00feb17ce88577e
        DiscoveredDiagramData 11 589cb53abd9ab12a 2B
        FlatDiagramStatus 4 069511b08660ff33
        DiscoveredFlatDiagramData 13 265f592dbac5c515 2B
        DiagramDiscoveryData 9 64ef0d0ce73f27ce 2B
        MigrationItemKind 16 4079167303aad14f 2B
        MigrationOutcome 3 d225cfc417a66000
        MigrationTargetKind 13 92c497f3fff71f7d 2B
        MigrationReason 13 45a8a06824475a02 2B
        MigrationSourceEnvelope 2 b75e743ad7c317bb
        RetainedGuidanceKind 3 878141bfd78fdfbd
        MigrationItemData 9 f2df3fd55addd974 2B
        RetainedGuidanceData 11 1bfa444aa6f610ed 2B
        DiagramMigrationReceiptData 18 55106feda5c1a2bf 2B
        MigrationResultData 9 5b720ce978c05d4e 2B
        """;

    private const string AutomationCommands = """
        ReadPcbDrcState 1 8483e6b336825e83
        ReadSchematicParityNetlist 4 9b2c7ca514fbcf85
        SchematicParityNetlistSnapshot 5 254fb1f4ce04b765
        PcbDrcFinding 4 14b0ab93da6d9a49
        PcbDrcState 7 3e1392e4e13709c3
        StartPcbDrcJob 10 6d65396d64014d20
        ReadPcbDrcJob 3 9b2dfa8bab97d6e1
        CancelPcbDrcJob 3 9b2dfa8bab97d6e1
        StartSimulationJob 4 34d05ca45fb5e16a
        ReadSimulationJob 3 9b2dfa8bab97d6e1
        CancelSimulationJob 3 9b2dfa8bab97d6e1
        SimulationJobStatus 4 9f9bae8d97e0889b
        SimulationVector 3 11faae2a2dc4c896
        SimulationJobState 13 c3fbc16ca7c805ee
        PcbDrcJobStatus 7 9c4536abc6265877
        PcbDrcJobState 19 faeb6234e48abcbe
        StartPcbRoutePreview 9 58ed2613b2471bb8
        PcbRoutePreviewState 10 0534cbcdbc9748ef
        ReadSchematicMetadata 2 c15c833c8e673520
        ReadSchematicSaveState 1 8483e6b336825e83
        ReadDocumentLifecycleState 1 8483e6b336825e83
        DocumentLifecycleScope 2 dac042265f2494b0
        NativeFileBaselineStatus 4 254c2af90b3df529
        NativeFileBaselineState 11 2b746b5f388b72ef 2C
        DocumentLifecycleState 13 fd4f34e8fea06f1b 2C 2D
        CheckedSaveDocument 3 85bdd5ff029b4e3d 2D
        CheckedCloseDocument 3 85bdd5ff029b4e3d 2D
        ReadLifecycleOperation 3 8c11d6117b788005
        LifecycleOperationStatus 5 f110647c1890e0bd
        LifecycleOperationResult 7 f092b9b0d70901e3 2D
        SchematicSaveState 6 79d6837dd0bff864
        SchematicMetadataSnapshot 3 408a9c92528ad15d
        ReadSchematicScreenData 2 c15c833c8e673520
        SchematicScreenDataSnapshot 3 d32efe34d214b4d8
        ReadSchematicHierarchyData 2 c15c833c8e673520
        SchematicHierarchyDataSnapshot 3 d32efe34d214b4d8 2C
        ReadSchematicElectricalState 3 c350168734e91b48
        SchematicElectricalState 3 6ea4180e1016dc15 2C
        CaptureSchematicObservation 2 c15c833c8e673520
        SchematicObservation 2 ee7267d9562db405
        GetAutomationSession 0 e3b0c44298fc1c14
        AutomationSession 7 af5a9730eb28d24d 2C 2D
        AutomationEvent 5 0b16f3b2d92e2bb5 2D
        AutomationEvent.payload 7 c3354ea1d60a3990 2C
        AutomationHeartbeat 0 e3b0c44298fc1c14
        SchematicCommitNotification 4 987d7fc8a4a9d044 2C
        DocumentRevision 2 5f3acf95ed2247c0
        CaptureSchematicPreview 1 8483e6b336825e83
        ActivateSchematicSheet 1 8483e6b336825e83
        SchematicPreview 7 22f9f7f1a752efbe
        SchematicViewport 8 dbd7e0b94c4611df
        RenderSchematicViews 3 9e6f1052e80a95ed
        SchematicRenderView 5 88e93e8d8331d23a
        SchematicRenderLayer 2 8eb993af4fef5788
        RenderedSchematicView 2 e5a5106b55a34ba2
        SchematicViewSet 4 c14d120cb39f3340
        MeasureSchematicPlacement 5 46c094f7b9fad2e9 2A
        SchematicPlacementGeometry 9 2a80ddae53c43de5 2A
        SchematicPlacementBounds 4 d17aeb7de908d51e 2A
        SchematicPinGeometryIncompleteReason 3 b56836e20cb334bc
        SchematicSymbolPinGeometry 4 415d77fdab89cac1 2A
        SchematicPinPowerScope 2 239fa6ee37976a21
        SchematicPinAnchor 13 a4b0b09f27af2ebb 2A
        ReadSchematicPresentationFacts 1 8483e6b336825e83 2A
        SchematicPresentationFacts 8 355813d62fe890d3 2A
        SchematicPresentationWire 4 ba4ed018258746fa 2A
        SchematicPresentationObject 9 ddfded756401f328 2A
        SchematicPresentationObject.Kind 4 9d8abf1826b9c225
        ReadSchematicChangeJournal 3 50b10d536eea1048 2C
        SchematicChangeJournal 5 c2eb3a6b4fb07a19 2C
        SchematicChange 5 ffe1b6c2673ca40c 2C
        SchematicChange.Kind 3 60cc53a29b5b040d 2C
        ApplySchematicItemBatch 8 efecc5154e08db80 2A 2C
        CheckedSchematicBatch 2 0d2bc3db33ca0eb2 2A 2C
        ReadCheckedSchematicState 2 868a35eb1959ed09
        CheckedSchematicState 2 fe1b80cb51218434 2C
        ReadCheckedSchematicBatchReceipt 4 3a51c98a9d2beba5
        CheckedSchematicBatchStatus 4 c684f62e4a24be94
        CheckedSchematicBatchReceipt 10 c5c5707b42631b85 2C 2D
        SchematicItemOperation 8 8c5c85d0688e975b
        SchematicItemOperation.operation 27 cd1d9f4a6d236192 2A 2C
        SchematicConnectedSymbolMove 2 de775889c555492e
        SchematicConnectedTransformKind 4 e4a1065e5a558baf
        SchematicConnectedSymbolTransform 3 f0af41b6d3194f83
        SchematicSymbolLocks 2 0bbcefbd0daf8660
        SchematicConnectivityAssertion 2 6933a248c3660807 2A 2C
        SchematicPinGroup 1 8f961be1ce464053
        SchematicLibraryCacheState 2 ec4b0591706bf7a8 2C
        SchematicEmbeddedFileState 2 3dffc5ad75d45690
        SchematicBusAliasState 1 6eefc78f195e8668
        SchematicTextVariableState 1 ffac67100c41b7e6
        SchematicNetChainState 1 0501351c6823ff33 2C
        SchematicVariantRegistryState 1 2e2fb88b29ac4835
        SchematicItemBatchResult 25 eb533bc3d6f40c2d 2A 2C
        InspectSchematicOperation 4 f4511343b7bd9da1
        SchematicOperationReceipt 8 dd4b229204b3dfe3 2C 2D
        SchematicOperationReceipt.State 4 2da28953fefd8eb4
        """;

    private const string SchematicTypes = """
        SchematicScreenData 4 d710cd36350609df 2C
        SchematicHierarchyData 2 7c85f8afd787d90e 2C
        SchematicUnrepresentedItem 3 0302a7b6bf9b416a
        SchematicBusAlias 2 565ffcebc8736d3d
        SchematicNetChainTerminal 2 35aeccb08726c7e5
        SchematicNetChainDefinition 8 10ac1e622e632600 2C
        SchematicNetChainExclusions 2 7a5c1e147393756a
        SchematicNetChainPinAnchor 2 617f9fe8fbc72667
        SchematicMetadata 24 1dd9d57620d0ce53 2C
        SchematicNetClassNames 1 fc9e4a07e4d5a1a4
        SchematicNetClassPattern 2 f345d367f49f5562
        SchematicNetSettings 6 584acac27dce2806 2C
        SchematicBomField 4 710fca47d2d247ed
        SchematicBomFilterScope 3 01a9ff8cad941e40
        SchematicBomView 9 1b228c6ba3c4311c
        SchematicBomFormat 8 a6f88642a0b14834
        SchematicBomSettings 5 5e49fef8f9e807a2 2C
        SchematicFieldTemplate 3 7b93a38d03ed9969
        SchematicFieldTemplates 1 f8d873619c6d8c72 2C
        SchematicSymbolComparisonSettings 12 6b02f4d2e5ad421b 2C
        SchematicReferenceInventory 1 53c5bfedd18c2fe7 2C
        SchematicAnnotationSettings 4 277bfc18c2437370 2C
        SchematicAnnotationOrder 2 e10039d30af5e217
        SchematicAnnotationMethod 3 c0244932ea94316c
        SchematicNetChainClassState 2 0d874f7b1ad5fc08 2C
        SchematicErcSettings 3 323b839a9bb2ab97 2C
        SchematicErcPinConflict 4 f75cec39a3bfb8f3
        SchematicErcPinRule 3 1cd2e59ec3357880
        SchematicFormattingSettings 14 e3605428f2402010 2C
        SchematicUnitReferenceFormatting 2 69541500333e3c21
        SchematicOperatingPointFormatting 4 73a79517bb29f647
        SchematicDrawingRatios 5 64dad7102410c41d 2C
        SchematicRootInstance 1 c8e0757772e8e621 2C
        SchematicField 8 7e83a567738ed715
        SchematicLineType 3 be35cdd088173258
        SchematicLine 9 a96a45c53a3907d9
        Junction 6 7229f5505a808cef
        NoConnectMarker 5 ed83572461bf9055
        BusEntryType 2 f30dc8d493781a14
        BusEntry 7 07af150829b53dc6
        SchematicText 5 4c51001d5f5b58a2
        SchematicTextBox 10 267444228d646ca6
        SchematicGraphicShape 4 c7e08dbbfbfb17ed
        SchematicImage 7 16fda2b97e2f9f57
        SchematicLabelShape 9 5ff6774efcbf8fcf
        SchematicLabelSpinStyle 4 1be65873f846c1e3
        LocalLabel 8 a49683b195a983ff 2A
        GlobalLabel 10 4e923c16f64d3d72
        HierarchicalLabel 9 70d87f8da62bc85e 2A
        DirectiveLabel 11 7ceddba9b21ae50a
        Group 6 806fa15178a0585a
        SchematicRuleArea 8 35a8330fa1918693
        SheetSide 4 f2b830842f4967d9
        SheetPin 7 18fa337422237e94 2A
        SheetSymbol 21 58212a28b3b75b95 2A
        SheetPlacementRecord 4 5be5c9050cfb6e12
        SheetPlacementRecords 1 56861273d3192cd9
        SchematicSymbolType 3 c594222e36f77319
        SchematicSymbolOrientation 4 ffcee63ca1d10173
        SchematicPinOrientation 4 4d0b5f83e0551a41
        SchematicPinShape 9 d52fd379627e27dc
        SchematicPinAlternate 3 9a4a3637e1fd4241
        SchematicPin 15 438dd4909f9c668f 2A
        SchematicSymbolUnit 1 5f34e2d38d64f910
        SchematicSymbolBodyStyle 1 cfd31f892d51ea9a
        SchematicSymbolChild 4 d54860af5ccce389
        SchematicSymbolAttributes 5 e4ab9cd70ae38723
        SchematicSymbolVariant 6 3594285493a7c541
        SchematicSymbolVariants 1 d8fad6ecafa35687
        SheetVariant 6 6e4830fec08a3d9f
        SheetVariants 1 d8fad6ecafa35687
        PinMapEntry 2 d7be854356da7070
        PinMap 2 d74730d0da77820f
        AssociatedFootprint 2 4cf550c6635df05e
        SymbolPinMaps 2 c23f4b5aed1e9cee
        PinMapOverrideMode 4 67de7e233bec3e81
        PinMapInstanceOverride 3 0b41b5e10809350f
        SchematicPassthroughMode 3 f930ea49030347ab
        JumperGroup 1 d19596bf8a0975c5
        JumperSettings 2 b2e449370222b0fb
        SchematicUnitDisplayName 2 fa36ce84ed1e218a
        SchematicBodyStyle 1 b3be53dd7b40e928
        SchematicSymbol 21 6a7ba500042cc28c 2A
        SchematicSymbolTransform 3 99ecde2c745f8afb
        SchematicCachedSymbol 5 e67b7b8b641ca9f5 2A 2C
        SymbolSheetRecord 5 f4a635536aa8bf9b
        SymbolSheetRecords 1 56861273d3192cd9
        SchematicSymbolInstance 29 bcdaafc21263fb32 2A
        SheetInstance 5 b3cd2f9188dcf786
        SchematicNetSheetContents 2 4294fa8698adf3f0
        SchematicNet 2 39a9cbb7a1e3a3da
        SchematicTableCell 4 ba90c4efc01c8fb5
        TableStrokeMode 2 21ca27ec93313756
        SchematicTable 13 69df04984c90d311
        """;
}

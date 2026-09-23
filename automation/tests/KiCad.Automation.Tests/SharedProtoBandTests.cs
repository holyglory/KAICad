using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

// Phase 2 lanes extend three shared protocol files in parallel. A lane adds
// fields, enum values and oneof members only between its own marker pair,
// numbered inside its band, and new messages only inside its own block at the
// end of a file (automation/design/contracts/psu-cpu-fixture-and-ownership.md,
// section 3). Every other declaration keeps the frozen numbers recorded at the
// end of this class; the parent updates that record when it declares a seam field.
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
        const string AnchorBand = "  string power_net = 13;\n  // -- lane 2A (100-199) --\n";
        const string AnchorFields = "  string power_net = 13;\n";
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
            .ToDictionary(parts => parts[0], parts => (Max: int.Parse(parts[1], CultureInfo.InvariantCulture), Lanes: parts[2..].ToHashSet()));
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

    // Frozen at the Phase 2 freeze: "declaration maximum-number-outside-markers open-lanes".
    private const string DiagramRevisionTypes = """
        BlockSelectionData 3
        RequirementFieldsData 3
        DiagramActorKind 4 2C 2D
        RequirementFieldKind 3
        DiagramRevisionOriginData 6 2C 2D
        FieldRestorationData 2
        RequirementRevisionData 5
        RequirementHistoryData 4
        FieldHistoryEntryData 7
        FieldHistoryPageData 11
        DiagramHistoryEntryData 9 2B
        DiagramHistoryPageData 6
        DiagramChangeKind 4
        DiagramChangeCategory 11 2A 2B 2C
        DiagramHistoryChangeData 5 2B
        DiagramHistoryComparisonData 7
        PrepareDiagramRestorationData 2
        BlockDesignStateData 6 2B
        ImplementationChangeKind 3
        ImplementationChangeData 8
        DefinitionChoiceStateData 3
        DefinitionTextChoiceData 7
        KnowledgeClassReferenceData 3
        DefinitionClassChoiceData 7
        BlockDefinitionData 8
        BlockRevisionData 11 2A 2B 2C
        ComponentRealizationData 3
        BlockComponentBindingsData 1
        PhysicalAllocationKindData 5 2B
        PhysicalAllocationStateData 2
        PhysicalAllocationTargetData 6
        BlockPhysicalAllocationData 3
        DiagramRefinementAttachmentData 7
        DiagramRefinementInputData 8
        RecursiveBlockGraphData 11 2A 2B 2C
        BlockProposalIssueKindData 2
        BlockProposalIssueData 5
        BlockProposalRecordData 7
        RecursiveEditorDocument 7 2B 2C
        RecursiveFileAction 19 2B
        ImplementationActionKind 5
        ManageImplementationData 9
        ConnectionDraftData 13 2A 2B
        DiagramAnnotationListData 1
        SaveConnectionDraftData 11 2B 2C
        RequirementConflictData 4
        RequirementResolutionData 10
        RebaseRequirementsRequest 2
        RequirementMergeData 9
        BlockDraftData 12 2B 2C
        SaveBlockDraftData 7 2B 2C
        RecursiveFileRequest 24 2B
        RecursiveFileResult 20 2B
        DiagramBoundaryInterfaceData 5 2A 2B
        BlockLocalDiagramData 5 2A 2B
        DiagramAnnotationRole 2
        DiagramAnnotationTargetKind 3
        DiagramAnnotationPointData 2
        DiagramAnnotationStrokeData 1
        DiagramAnnotationData 10 2B
        ConnectionSelectionData 3
        ConnectionDesignStateData 4
        DiagramConnectionKind 5 2B
        ConnectionRevisionData 11 2A 2B 2C
        ConnectionArchiveData 5
        DiagramEndpointKind 5 2B
        DiagramPinTargetData 4 2A 2C
        DiagramPinSelectorData 4
        DiagramEndpointBindingData 7 2A 2B 2C
        DiagramDomain 5
        DiagramInterfaceDirection 3
        DiagramConnectionDirection 3
        DiagramPortSide 4
        DiagramRealizationState 3
        DiagramRectData 4
        DiagramBlockPlacementData 4 2B
        DiagramPortPlacementData 4 2B
        DiagramConnectionRouteData 5 2B
        DiagramPresentationViewData 5 2B
        InterfaceRealizationTargetKind 3 2B
        InterfaceRealizationTargetData 5 2B
        InterfaceRealizationData 5 2B
        InterconnectSegmentKind 5 2B
        InterconnectSegmentData 13 2B
        InterconnectJoinData 2
        InterconnectRealizationData 5 2B
        NewBlockOccurrenceData 7 2B
        NewConnectionData 10 2B
        LevelDraftData 5 2B
        RevisionIdAssignmentData 3
        SaveLevelDraftData 11 2B
        SaveSummaryData 6 2B
        DiagramErrorDetailData 4 2B
        LevelEditCommandKind 3 2B
        LevelEditCommandData 9 2B
        LevelEditEffectKind 8 2B
        LevelEditEffectData 4
        LevelEditResultData 2
        RebaseLevelData 2
        LevelConflictKind 14 2B
        LevelConflictData 9 2B
        PresentationOverrideData 2
        LevelMergeData 9 2B
        ReparentBlockData 8 2B
        ReparentPreviewData 3 2B
        CreateDiagramData 5 2B
        MigrateFlatDiagramData 8 2B
        DiscoverDiagramsData 1 2B
        DiscoveredDiagramStatus 5
        DiscoveredDiagramData 11 2B
        FlatDiagramStatus 4
        DiscoveredFlatDiagramData 13 2B
        DiagramDiscoveryData 9 2B
        MigrationItemKind 16 2B
        MigrationOutcome 3
        MigrationTargetKind 13 2B
        MigrationReason 13 2B
        MigrationSourceEnvelope 2
        RetainedGuidanceKind 3
        MigrationItemData 9 2B
        RetainedGuidanceData 11 2B
        DiagramMigrationReceiptData 18 2B
        MigrationResultData 9 2B
        """;

    private const string AutomationCommands = """
        ReadPcbDrcState 1
        ReadSchematicParityNetlist 4
        SchematicParityNetlistSnapshot 5
        PcbDrcFinding 4
        PcbDrcState 7
        StartPcbDrcJob 10
        ReadPcbDrcJob 3
        CancelPcbDrcJob 3
        StartSimulationJob 4
        ReadSimulationJob 3
        CancelSimulationJob 3
        SimulationJobStatus 4
        SimulationVector 3
        SimulationJobState 13
        PcbDrcJobStatus 7
        PcbDrcJobState 19
        StartPcbRoutePreview 9
        PcbRoutePreviewState 10
        ReadSchematicMetadata 2
        ReadSchematicSaveState 1
        ReadDocumentLifecycleState 1
        DocumentLifecycleScope 2
        NativeFileBaselineStatus 4
        NativeFileBaselineState 11 2C
        DocumentLifecycleState 13 2C 2D
        CheckedSaveDocument 3 2D
        CheckedCloseDocument 3 2D
        ReadLifecycleOperation 3
        LifecycleOperationStatus 5
        LifecycleOperationResult 7 2D
        SchematicSaveState 6
        SchematicMetadataSnapshot 3
        ReadSchematicScreenData 2
        SchematicScreenDataSnapshot 3
        ReadSchematicHierarchyData 2
        SchematicHierarchyDataSnapshot 3 2C
        ReadSchematicElectricalState 3
        SchematicElectricalState 3 2C
        CaptureSchematicObservation 2
        SchematicObservation 2
        GetAutomationSession 0
        AutomationSession 7 2C 2D
        AutomationEvent 5 2D
        AutomationEvent.payload 7 2C
        AutomationHeartbeat 0
        SchematicCommitNotification 4 2C
        DocumentRevision 2
        CaptureSchematicPreview 1
        ActivateSchematicSheet 1
        SchematicPreview 7
        SchematicViewport 8
        RenderSchematicViews 3
        SchematicRenderView 5
        SchematicRenderLayer 2
        RenderedSchematicView 2
        SchematicViewSet 4
        MeasureSchematicPlacement 5 2A
        SchematicPlacementGeometry 9 2A
        SchematicPlacementBounds 4 2A
        SchematicPinGeometryIncompleteReason 3
        SchematicSymbolPinGeometry 4 2A
        SchematicPinPowerScope 2
        SchematicPinAnchor 13 2A
        ReadSchematicPresentationFacts 1 2A
        SchematicPresentationFacts 8 2A
        SchematicPresentationWire 4 2A
        SchematicPresentationObject 9 2A
        SchematicPresentationObject.Kind 4
        ReadSchematicChangeJournal 3 2C
        SchematicChangeJournal 5 2C
        SchematicChange 5 2C
        SchematicChange.Kind 3 2C
        ApplySchematicItemBatch 8 2A 2C
        CheckedSchematicBatch 2 2A 2C
        ReadCheckedSchematicState 2
        CheckedSchematicState 2 2C
        ReadCheckedSchematicBatchReceipt 4
        CheckedSchematicBatchStatus 4
        CheckedSchematicBatchReceipt 10 2C 2D
        SchematicItemOperation 8
        SchematicItemOperation.operation 27 2A 2C
        SchematicConnectedSymbolMove 2
        SchematicConnectedTransformKind 4
        SchematicConnectedSymbolTransform 3
        SchematicSymbolLocks 2
        SchematicConnectivityAssertion 2 2A 2C
        SchematicPinGroup 1
        SchematicLibraryCacheState 2 2C
        SchematicEmbeddedFileState 2
        SchematicBusAliasState 1
        SchematicTextVariableState 1
        SchematicNetChainState 1 2C
        SchematicVariantRegistryState 1
        SchematicItemBatchResult 25 2A 2C
        InspectSchematicOperation 4
        SchematicOperationReceipt 8 2C 2D
        SchematicOperationReceipt.State 4
        """;

    private const string SchematicTypes = """
        SchematicScreenData 4 2C
        SchematicHierarchyData 2 2C
        SchematicUnrepresentedItem 3
        SchematicBusAlias 2
        SchematicNetChainTerminal 2
        SchematicNetChainDefinition 8 2C
        SchematicNetChainExclusions 2
        SchematicNetChainPinAnchor 2
        SchematicMetadata 24 2C
        SchematicNetClassNames 1
        SchematicNetClassPattern 2
        SchematicNetSettings 6 2C
        SchematicBomField 4
        SchematicBomFilterScope 3
        SchematicBomView 9
        SchematicBomFormat 8
        SchematicBomSettings 5 2C
        SchematicFieldTemplate 3
        SchematicFieldTemplates 1 2C
        SchematicSymbolComparisonSettings 12 2C
        SchematicReferenceInventory 1 2C
        SchematicAnnotationSettings 4 2C
        SchematicAnnotationOrder 2
        SchematicAnnotationMethod 3
        SchematicNetChainClassState 2 2C
        SchematicErcSettings 3 2C
        SchematicErcPinConflict 4
        SchematicErcPinRule 3
        SchematicFormattingSettings 14 2C
        SchematicUnitReferenceFormatting 2
        SchematicOperatingPointFormatting 4
        SchematicDrawingRatios 5 2C
        SchematicRootInstance 1 2C
        SchematicField 8
        SchematicLineType 3
        SchematicLine 9
        Junction 6
        NoConnectMarker 5
        BusEntryType 2
        BusEntry 7
        SchematicText 5
        SchematicTextBox 10
        SchematicGraphicShape 4
        SchematicImage 7
        SchematicLabelShape 9
        SchematicLabelSpinStyle 4
        LocalLabel 8 2A
        GlobalLabel 10
        HierarchicalLabel 9 2A
        DirectiveLabel 11
        Group 6
        SchematicRuleArea 8
        SheetSide 4
        SheetPin 7 2A
        SheetSymbol 21 2A
        SheetPlacementRecord 4
        SheetPlacementRecords 1
        SchematicSymbolType 3
        SchematicSymbolOrientation 4
        SchematicPinOrientation 4
        SchematicPinShape 9
        SchematicPinAlternate 3
        SchematicPin 15 2A
        SchematicSymbolUnit 1
        SchematicSymbolBodyStyle 1
        SchematicSymbolChild 4
        SchematicSymbolAttributes 5
        SchematicSymbolVariant 6
        SchematicSymbolVariants 1
        SheetVariant 6
        SheetVariants 1
        PinMapEntry 2
        PinMap 2
        AssociatedFootprint 2
        SymbolPinMaps 2
        PinMapOverrideMode 4
        PinMapInstanceOverride 3
        SchematicPassthroughMode 3
        JumperGroup 1
        JumperSettings 2
        SchematicUnitDisplayName 2
        SchematicBodyStyle 1
        SchematicSymbol 21 2A
        SchematicSymbolTransform 3
        SchematicCachedSymbol 5 2A 2C
        SymbolSheetRecord 5
        SymbolSheetRecords 1
        SchematicSymbolInstance 29 2A
        SheetInstance 5
        SchematicNetSheetContents 2
        SchematicNet 2
        SchematicTableCell 4
        TableStrokeMode 2
        SchematicTable 13
        """;
}

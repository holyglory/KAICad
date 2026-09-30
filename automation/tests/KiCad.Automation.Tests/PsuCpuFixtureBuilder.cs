using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KiCad.Automation.Model;

namespace KiCad.Automation.Tests;

/// <summary>Builds the frozen PSU-CPU models (H1, E1, F1, G1) from the contract tables and
/// parts.json, and derives part provenance from pinned repository blobs. Test data only:
/// pin choices are declared, not engineering recommendations, and no value is invented.</summary>
internal static partial class PsuCpuFixtureBuilder
{
    public static readonly RequirementRevisionOrigin Origin = new(RequirementRevisionActor.User, "PSU-CPU fixture",
        new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero), "Frozen PSU-CPU acceptance fixture v1", [], []);

    /// <summary>Contract section 1.3, in part order: cache key, source entry, part name and source path.</summary>
    public static readonly ImmutableArray<(string CacheKey, string SourceSymbol, string Name, string SourcePath)> PartSources =
    [
        ("Connector_Generic:Conn_01x02", "Connector_Generic:Conn_01x02", "Conn_01x02", "qa/data/pcbnew/issue21739/topology_mismatch.kicad_sch"),
        ("Regulator_Switching:LM2595S-ADJ", "Regulator_Switching:LM2595S-ADJ", "LM2595S-ADJ", "qa/data/pcbnew/issue23658/issue23658.kicad_sch"),
        ("Device:R", "R", "R", "qa/data/libraries/Device.kicad_sym"),
        ("Regulator_Linear:LP3982ILD-3.3", "Regulator_Linear:LP3982ILD-3.3", "LP3982ILD-3.3", "qa/data/eeschema/issue6588.kicad_sch"),
        ("Battery_Management:LTC2959", "Battery_Management:LTC2959", "LTC2959", "qa/data/pcbnew/issue24474/issue24474.kicad_sch"),
        ("MCU_ST_STM32C0:STM32C011J_4-6_Mx", "MCU_ST_STM32C0:STM32C011J_4-6_Mx", "STM32C011J_4-6_Mx", "qa/data/eeschema/api_kitchen_sink.kicad_sch"),
        ("Library:F28P659DK8PTPQ1", "Library:F28P659DK8PTPQ1", "F28P659DK8PTPQ1", "qa/data/eeschema/issue19646/MCU.kicad_sch"),
        ("pic_programmer:24C16", "pic_programmer:24C16", "24C16", "qa/data/cli/variants/pic_sockets.kicad_sch"),
    ];

    /// <summary>Components 1-8 (kinds 06/07): reference, part ordinal and owning sheet instance ordinal.</summary>
    public static readonly ImmutableArray<(string Reference, int Part, int Sheet)> Components =
        [("J1", 1, 2), ("U1", 2, 2), ("R1", 3, 2), ("U2", 4, 2), ("U3", 5, 2), ("U4", 6, 2), ("U5", 7, 3), ("U6", 8, 3)];

    /// <summary>Symbol occurrences 1-11 (kind 09). Only U5 unit 4 overrides its sheet (CPU_POWER).</summary>
    public static readonly ImmutableArray<(int Component, int Unit, int? Sheet)> Occurrences =
        [(1, 1, null), (2, 1, null), (3, 1, null), (4, 1, null), (5, 1, null), (6, 1, null),
         (7, 1, null), (7, 2, null), (7, 3, null), (7, 4, 4), (8, 1, null)];

    /// <summary>Nets 1-11 (kind 08) as "Ref.number" pins; 41 connected pins in total.</summary>
    public static readonly ImmutableArray<(string Name, string Pins)> Nets =
    [
        ("VIN", "J1.1 U1.2"),
        ("GND", "J1.2 U1.3 U2.3 U2.9 U3.10 U4.3 U5.177 U6.1 U6.2 U6.3 U6.4 U6.7"),
        ("DCDC_OUT", "U1.1 U1.4 R1.1 U3.1 U3.2"),
        ("RAIL_A", "R1.2 U2.2 U2.7 U3.5 U4.2 U5.3"),
        ("RAIL_B", "U2.1 U2.4 U3.8 U6.8"),
        ("LDO_FAULT", "U2.8 U4.4"),
        ("PSU_SCL", "U3.6 U4.5"),
        ("PSU_SDA", "U3.7 U4.6"),
        ("TELEM_MCU_TO_CPU", "U4.8 U5.74"),
        ("TELEM_CPU_TO_MCU", "U4.1 U5.73"),
        ("MEM_SCL", "U5.161 U6.6"),
    ];

    /// <summary>Blocks 1-9 (kinds 11-14): children, boundary interfaces, bound components and requirements.</summary>
    public static readonly ImmutableArray<(string Name, int[] Children, int[] Interfaces, int[] Components, DiagramRequirements Requirements)> Blocks =
    [
        ("System", [2, 3], [0x01], [], DiagramRequirements.Empty),
        ("PSU", [4, 5, 6, 7], [0x02, 0x03, 0x04], [1], new("Supply CPU power and report rail status.",
            "Separate power conversion and telemetry.", "Keep high-current paths away from sensing.")),
        ("CPU", [8, 9], [0x05, 0x06], [], DiagramRequirements.Empty),
        ("DC-DC", [], [0x07, 0x08, 0x09], [2, 3], DiagramRequirements.Empty),
        ("LDO", [], [0x0a, 0x0b, 0x0c], [4], DiagramRequirements.Empty),
        ("Telemetry ADC", [], [0x0d, 0x0e, 0x0f], [5], DiagramRequirements.Empty),
        ("Telemetry MCU", [], [0x10, 0x11, 0x12], [6], DiagramRequirements.Empty),
        ("Processor", [], [0x13, 0x14, 0x15], [7], DiagramRequirements.Empty),
        ("Memory", [], [0x16, 0x17], [8], DiagramRequirements.Empty),
    ];

    /// <summary>Boundary interfaces 0x01-0x17 (kind 15), indexed by ordinal - 1.</summary>
    public static readonly ImmutableArray<string> Interfaces =
    [
        "DC input", "DC input", "Power", "Telemetry", "Power", "Telemetry", "Input", "Output", "Sense", "Input", "Output", "Fault",
        "Rail A sense", "Rail B sense", "Measurements", "Measurements", "Fault", "Telemetry", "Power", "Telemetry", "Memory", "Power", "Data",
    ];

    /// <summary>Connections 0x01-0x1f (kinds 16-19). Endpoints: "If:block:iface", "Pin:block:iface:Ref.p", "Sel:block:iface".</summary>
    public static readonly ImmutableArray<(int Owner, string Name, DiagramConnectionKind Kind, string A, string B, int[] Members)> Connections =
    [
        (1, "DC input", DiagramConnectionKind.Interface, "If:1:01", "If:2:02", []),
        (1, "Power", DiagramConnectionKind.Interface, "If:2:03", "If:3:05", [0x03, 0x04, 0x05]),
        (1, "Rail A", DiagramConnectionKind.Signal, "If:2:03", "If:3:05", []),
        (1, "Rail B", DiagramConnectionKind.Signal, "If:2:03", "If:3:05", []),
        (1, "Return", DiagramConnectionKind.Signal, "If:2:03", "If:3:05", []),
        (1, "Telemetry", DiagramConnectionKind.Interface, "If:2:04", "If:3:06", [0x07, 0x08]),
        (1, "MCU to CPU", DiagramConnectionKind.Signal, "If:2:04", "If:3:06", []),
        (1, "CPU to MCU", DiagramConnectionKind.Signal, "If:2:04", "If:3:06", []),
        (2, "DC input", DiagramConnectionKind.Signal, "Pin:2:02:J1.1", "Pin:4:07:U1.2", []),
        (2, "Rail A", DiagramConnectionKind.Signal, "Pin:4:08:R1.2", "If:2:03", []),
        (2, "LDO supply", DiagramConnectionKind.Signal, "Pin:4:08:R1.2", "Pin:5:0a:U2.2", []),
        (2, "Rail B", DiagramConnectionKind.Signal, "Pin:5:0b:U2.1", "If:2:03", []),
        (2, "Rail A sense", DiagramConnectionKind.SignalGroup, "If:4:09", "If:6:0d", [0x0e, 0x0f]),
        (2, "Sense+", DiagramConnectionKind.Signal, "Pin:4:09:U1.1", "Pin:6:0d:U3.2", []),
        (2, "Sense-", DiagramConnectionKind.Signal, "Pin:4:09:R1.2", "Pin:6:0d:U3.5", []),
        (2, "Rail B sense", DiagramConnectionKind.Signal, "Pin:5:0b:U2.1", "Pin:6:0e:U3.8", []),
        (2, "Measurements", DiagramConnectionKind.Interface, "If:6:0f", "If:7:10", [0x12, 0x13]),
        (2, "I2C SCL", DiagramConnectionKind.Signal, "Pin:6:0f:U3.6", "Pin:7:10:U4.5", []),
        (2, "I2C SDA", DiagramConnectionKind.Signal, "Pin:6:0f:U3.7", "Pin:7:10:U4.6", []),
        (2, "Fault", DiagramConnectionKind.Signal, "Pin:5:0c:U2.8", "Pin:7:11:U4.4", []),
        (2, "Telemetry", DiagramConnectionKind.Interface, "If:7:12", "If:2:04", [0x16, 0x17]),
        (2, "MCU to CPU", DiagramConnectionKind.Signal, "Pin:7:12:U4.8", "If:2:04", []),
        (2, "CPU to MCU", DiagramConnectionKind.Signal, "Pin:7:12:U4.1", "If:2:04", []),
        (3, "Rail A", DiagramConnectionKind.Signal, "If:3:05", "Pin:8:13:U5.3", []),
        (3, "Rail B", DiagramConnectionKind.Signal, "If:3:05", "Pin:9:16:U6.8", []),
        (3, "Supply status / control", DiagramConnectionKind.Interface, "If:3:06", "If:8:14", [0x1b, 0x1c]),
        (3, "MCU to CPU", DiagramConnectionKind.Signal, "If:3:06", "Pin:8:14:U5.74", []),
        (3, "CPU to MCU", DiagramConnectionKind.Signal, "If:3:06", "Pin:8:14:U5.73", []),
        (3, "Memory interface", DiagramConnectionKind.Interface, "If:8:15", "If:9:17", [0x1e, 0x1f]),
        (3, "I2C SCL", DiagramConnectionKind.Signal, "Pin:8:15:U5.161", "Pin:9:17:U6.6", []),
        (3, "I2C SDA", DiagramConnectionKind.Signal, "Sel:8:15", "Pin:9:17:U6.5", []),
    ];

    /// <summary>Top-level connections of each local diagram; members are reached through Members.</summary>
    public static readonly IReadOnlyDictionary<int, int[]> TopLevelConnections = new Dictionary<int, int[]>
    {
        [1] = [0x01, 0x02, 0x06], [2] = [0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x10, 0x11, 0x14, 0x15], [3] = [0x18, 0x19, 0x1a, 0x1d],
    };

    public static readonly DiagramRequirements MemoryInterfaceRequirements = new("Provide memory access; choose a compatible interface.",
        "Keep interface signals grouped and readable.", "Leave pin assignment open until placement.");

    private static Guid Id(int kind, long n) => PsuCpuIds.Id(kind, n);

    public static HardwareRepository Hardware() => new(Id(0x01, 1), "PSU-CPU acceptance fixture",
        [new(Id(0x01, 2), "Controller", "fixture.kicad_pro", "design.xml",
            [new(Id(0x01, 3), "DC input", "External supply connector J1 (fixture data).")])], [], [], []);

    public static Circuit Circuit(IReadOnlyList<PsuCpuPart> parts)
    {
        var byOrdinal = parts.ToDictionary(p => p.Ordinal);
        ComponentDefinition Definition(int n) => new(Id(0x06, n), Id(0x03, Components[n - 1].Part), byOrdinal[Components[n - 1].Part].Name);
        var references = Components.Select((c, i) => (c.Reference, Ordinal: i + 1)).ToDictionary(c => c.Reference, c => c.Ordinal);
        return new(Id(0x02, 1),
            parts.OrderBy(p => p.Ordinal).Select(p => new PartDefinition(p.Id, p.Name, p.Units, p.Pins)).ToArray(),
            [new(Id(0x04, 1), "System", []), new(Id(0x04, 2), "PSU", Enumerable.Range(1, 6).Select(Definition).ToArray()),
             new(Id(0x04, 3), "CPU", [Definition(7), Definition(8)]), new(Id(0x04, 4), "CPU_POWER", [])],
            [new(Id(0x05, 1), Id(0x04, 1), null), new(Id(0x05, 2), Id(0x04, 2), Id(0x05, 1)),
             new(Id(0x05, 3), Id(0x04, 3), Id(0x05, 1)), new(Id(0x05, 4), Id(0x04, 4), Id(0x05, 3))],
            Components.Select((c, i) => new ComponentInstance(Id(0x07, i + 1), Id(0x06, i + 1), Id(0x05, c.Sheet), c.Reference)).ToArray(),
            Nets.Select((net, i) => new CircuitNet(Id(0x08, i + 1), net.Name, net.Pins.Split(' ').Select(pin =>
                new PinEndpoint(Id(0x07, references[pin[..pin.IndexOf('.')]]), pin[(pin.IndexOf('.') + 1)..])).ToArray())).ToArray(),
            Occurrences.Select((o, i) => new SymbolOccurrence(Id(0x09, i + 1), Id(0x07, o.Component), o.Unit, null,
                o.Sheet is int sheet ? Id(0x05, sheet) : null)).ToArray());
    }

    /// <summary>E1 with the empty structure K02:2, written as design.engineering.xml.</summary>
    public static EngineeringDesign Engineering(IReadOnlyList<PsuCpuPart> parts) =>
        new(Circuit(parts), new StructuralDiagram(Id(0x02, 2), [], [], [], []), [], []);

    /// <summary>F1: the E1 circuit with the legacy flat structure. It reuses the G1 block,
    /// port and connection identities so conversion can be checked by identity.</summary>
    public static EngineeringDesign FlatStructure(IReadOnlyList<PsuCpuPart> parts)
    {
        var circuit = Circuit(parts);
        Guid B(int n) => Id(0x11, n); Guid P(int i) => Id(0x15, i); Guid C(int c) => Id(0x16, c); Guid Part(int n) => Id(0x07, n);
        Guid Net(string name) => circuit.Nets.Single(n => n.Name == name).Id;
        const long M = 1_000_000;
        const StructuralConnectionKind Power = StructuralConnectionKind.Power;
        var structure = new StructuralDiagram(Id(0x30, 1),
            [new(B(2), "PSU", null, [Part(1)], "Supply CPU power and report rail status."), new(B(3), "CPU", null, []),
             new(B(4), "DC-DC", B(2), [Part(2), Part(3)]), new(B(5), "LDO", B(2), [Part(4)]),
             new(B(6), "Telemetry ADC", B(2), [Part(5)]), new(B(7), "Telemetry MCU", B(2), [Part(6)]),
             new(B(8), "Processor", B(3), [Part(7)]), new(B(9), "Memory", B(3), [Part(8)])],
            [new(P(0x02), B(2), "DC input"), new(P(0x03), B(2), "Power"), new(P(0x04), B(2), "Telemetry"),
             new(P(0x05), B(3), "Power"), new(P(0x06), B(3), "Telemetry"), new(P(0x07), B(4), "Input"),
             new(P(0x08), B(4), "Output"), new(P(0x0a), B(5), "Input")],
            [new(C(0x02), P(0x03), P(0x05), Power, "Power", [Net("RAIL_A"), Net("RAIL_B"), Net("GND")], StructuralConnectionDirection.FirstToSecond),
             new(C(0x06), P(0x04), P(0x06), StructuralConnectionKind.Data, "Telemetry", [Net("TELEM_MCU_TO_CPU"), Net("TELEM_CPU_TO_MCU")],
                 StructuralConnectionDirection.Bidirectional),
             new(C(0x09), P(0x02), P(0x07), Power, "DC input", [Net("VIN")], StructuralConnectionDirection.FirstToSecond),
             new(C(0x0a), P(0x08), P(0x03), Power, "Rail A", [Net("RAIL_A")]),
             new(C(0x0b), P(0x08), P(0x0a), Power, "LDO supply", [Net("RAIL_A")]),
             // Deliberately cross-level: DC-DC (inside PSU) to CPU. Conversion leaves it out and records it.
             new(Id(0x30, 0x10), P(0x08), P(0x05), Power, "Direct supply", [Net("RAIL_A")])],
            [new(Id(0x30, 0x20), B(2), EngineeringStatementRole.Intent, GuidanceStrength.Preference,
                "Keep high-current paths away from sensing.", null, [], [])],
            Presentation: new(
                [new(B(2), 10 * M, 10 * M, 60 * M, 40 * M), new(B(3), 100 * M, 10 * M, 60 * M, 40 * M),
                 new(B(4), 10 * M, 70 * M, 30 * M, 20 * M), new(B(5), 50 * M, 70 * M, 30 * M, 20 * M),
                 new(B(6), 10 * M, 100 * M, 30 * M, 20 * M), new(B(7), 50 * M, 100 * M, 30 * M, 20 * M),
                 new(B(8), 100 * M, 70 * M, 30 * M, 20 * M), new(B(9), 140 * M, 70 * M, 30 * M, 20 * M)],
                [new(P(0x03), StructuralPortSide.Right, 10 * M), new(P(0x04), StructuralPortSide.Right, 30 * M),
                 new(P(0x05), StructuralPortSide.Left, 10 * M), new(P(0x06), StructuralPortSide.Left, 30 * M),
                 new(P(0x02), StructuralPortSide.Left, 20 * M), new(P(0x07), StructuralPortSide.Left, 10 * M),
                 new(P(0x08), StructuralPortSide.Right, 10 * M), new(P(0x0a), StructuralPortSide.Left, 10 * M)],
                [new(C(0x02), [new(70 * M, 20 * M), new(100 * M, 20 * M)])]));
        return new(circuit, structure, [], []);
    }

    /// <summary>G1: coordinate-free recursive block graph, schema v1. Every block and connection
    /// has one state, one parentless revision and one requirement revision; definitions and
    /// physical allocations stay unspecified.</summary>
    public static RecursiveBlockGraph Graph()
    {
        Guid document = Id(0x10, 1);
        BlockSelection Block(int n) => new(Id(0x11, n), Id(0x12, n), Id(0x13, n));
        ConnectionSelection Link(int c) => new(Id(0x16, c), Id(0x17, c), Id(0x18, c));
        DiagramEndpointBinding Endpoint(string text)
        {
            string[] parts = text.Split(':');
            Guid block = Id(0x11, int.Parse(parts[1])), boundary = Id(0x15, Convert.ToInt32(parts[2], 16));
            if (parts[0] == "If") return new(DiagramEndpointKind.Interface, block, boundary, "", null, [], null);
            if (parts[0] == "Sel") return new(DiagramEndpointKind.Compatible, block, boundary, "",
                new("I2C SDA", "I2C", ["I2C_SDA"], []), [], null);
            string reference = parts[3][..parts[3].IndexOf('.')];
            int component = Components.Select((c, i) => (c.Reference, i)).Single(c => c.Reference == reference).i;
            // The path names the component's owning sheet instance, even for U5 unit-4 pins on CPU_POWER.
            return new(DiagramEndpointKind.Pin, block, boundary, "", null, [], new(Id(0x01, 2), Id(0x07, component + 1),
                [Id(0x05, 1), Id(0x05, Components[component].Sheet)], parts[3][(parts[3].IndexOf('.') + 1)..]));
        }
        var archives = Connections.Select((c, i) => (Ordinal: i + 1, Connection: c)).GroupBy(c => c.Connection.Owner).Select(group =>
            new DiagramConnectionArchive(document, Id(0x11, group.Key),
                group.Select(c => new ConnectionDesignState(Id(0x17, c.Ordinal), Id(0x16, c.Ordinal), "Initial interface", Id(0x18, c.Ordinal))),
                group.Select(c => new DiagramConnectionRevision(Link(c.Ordinal), null, c.Connection.Name, c.Connection.Kind,
                    [Endpoint(c.Connection.A), Endpoint(c.Connection.B)], Id(0x19, c.Ordinal),
                    c.Connection.Members.Select(Link).ToImmutableArray(), Origin)),
                group.Select(c => new DiagramRequirementHistory(new(document, Id(0x16, c.Ordinal), Id(0x17, c.Ordinal)),
                    [new(Id(0x19, c.Ordinal), null, c.Ordinal == 0x1d ? MemoryInterfaceRequirements : DiagramRequirements.Empty,
                        Origin, [])])))).ToArray();
        DiagramAnnotation Note(int n, string text, DiagramAnnotationTarget target, DiagramAnnotationPoint? position) =>
            new(Id(0x1a, n), DiagramAnnotationRole.Comment, text, target, position, [], Origin);
        var canvas = new DiagramAnnotationTarget(DiagramAnnotationTargetKind.Canvas, null);
        var notes = new Dictionary<int, ImmutableArray<DiagramAnnotation>>
        {
            [1] = [Note(1, "Keep PSU replaceable as a unit.", canvas, new(40, 260)),
                   Note(2, "Explore a quieter supply.", new(DiagramAnnotationTargetKind.Block, Id(0x11, 2)), null)],
            [2] = [Note(3, "Keep sensing away from switching nodes.", canvas, new(40, 300))],
            [3] = [Note(4, "Compare memory-interface implementations.", canvas, new(40, 300)),
                   Note(5, "Keep pin choices open for placement.", new(DiagramAnnotationTargetKind.Connection, Id(0x16, 0x1d)), null)],
        };
        var revisions = Blocks.Select((b, i) => new RecursiveBlockRevision(Block(i + 1), null, b.Name, Id(0x14, i + 1),
            b.Children.Select(Block).ToImmutableArray(), Origin,
            Diagram: new([.. b.Interfaces.Select(f => new DiagramBoundaryInterface(Id(0x15, f), Interfaces[f - 1], ""))],
                [.. TopLevelConnections.GetValueOrDefault(i + 1, []).Select(Link)], notes.GetValueOrDefault(i + 1, [])),
            ComponentBindings: b.Components.Length == 0 ? null
                : new([.. b.Components.Select(c => new ComponentRealization(Id(0x01, 2), Id(0x02, 1), Id(0x07, c)))])));
        return new(document, Block(1),
            Blocks.Select((b, i) => new BlockDesignState(Id(0x12, i + 1), Id(0x11, i + 1), "Initial approach", Id(0x13, i + 1))),
            revisions,
            Blocks.Select((b, i) => new DiagramRequirementHistory(new(document, Id(0x11, i + 1), Id(0x12, i + 1)),
                [new(Id(0x14, i + 1), null, b.Requirements, Origin, [])])),
            archives);
    }

    /// <summary>The checked-in bytes of every builder-owned file, keyed by file name.</summary>
    public static IReadOnlyDictionary<string, string> Files(IReadOnlyList<PsuCpuPart> parts) => new Dictionary<string, string>
    {
        ["hardware.xml"] = HardwareRepositoryXml.Write(Hardware()),
        ["design.engineering.xml"] = EngineeringDesignXml.Write(Engineering(parts), []),
        ["flat-structure.engineering.xml"] = EngineeringDesignXml.Write(FlatStructure(parts), []),
        ["system.blocks.xml"] = RecursiveBlockGraphXml.Write(Graph(), 1),
    };

    /// <summary>Git's blob identity of exact file bytes.</summary>
    public static string GitBlob(byte[] bytes)
    {
        byte[] header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        return Convert.ToHexStringLower(SHA1.HashData(header.Concat(bytes).ToArray()));
    }

    /// <summary>Read a source at its pinned blob. The working-tree file is used when it still has
    /// that identity; otherwise the blob is read from Git history. A missing blob is a changed fixture.</summary>
    public static string ReadPinnedSource(string root, string path, string blob)
    {
        byte[] bytes = File.Exists(Path.Combine(root, path)) ? File.ReadAllBytes(Path.Combine(root, path)) : [];
        if (GitBlob(bytes) != blob)
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-C", root, "cat-file", "blob", blob }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            using var buffer = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(buffer);
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit();
            bytes = buffer.ToArray();
            if (process.ExitCode != 0 || GitBlob(bytes) != blob)
                throw PsuCpuFixture.Failure("psu_cpu_fixture_file_changed", $"The pinned source blob {blob} for {path} is unavailable.");
        }
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    /// <summary>Derive part rows from the pinned source definitions: body style 1 and the common
    /// style-0 pins. This fork reads a lone "~" pin name as empty before format 20250318.</summary>
    public static IReadOnlyList<PsuCpuPart> DeriveParts(string root, IReadOnlyDictionary<string, string> blobs)
    {
        var result = new List<PsuCpuPart>();
        foreach (var (source, index) in PartSources.Select((s, i) => (s, i)))
        {
            string blob = blobs[source.CacheKey];
            var definition = SourceDefinition(root, source.SourcePath, blob, source.SourceSymbol, out int version);
            var pins = new List<(int Unit, int Style, PartPin Pin)>();
            foreach (var body in definition.Children("symbol"))
            {
                var (unit, style) = UnitAndStyle(body.Value(1));
                if (style is not (0 or 1)) continue;
                foreach (var pin in body.Children("pin"))
                {
                    string name = pin.Child("name").Value(1);
                    if (version < 20250318 && name == "~") name = "";
                    pins.Add((unit, style, new(pin.Child("number").Value(1), name, unit)));
                }
            }
            string[] key = source.CacheKey.Split(':');
            result.Add(new(Id(0x03, index + 1), index + 1, source.Name, source.CacheKey, key[0], key[1], pins.Max(p => p.Unit),
                new(source.SourcePath, blob, source.SourceSymbol, version),
                pins.OrderBy(p => p.Unit).ThenBy(p => p.Style).ThenBy(p => p.Pin.Number, StringComparer.Ordinal).Select(p => p.Pin).ToArray()));
        }
        return result;
    }

    /// <summary>The raw catalog: the eight definitions copied verbatim from their pinned sources,
    /// with the Device library entry R renamed to its cache name. This is the input that this
    /// fork's eeschema normalizes once into lib_symbols.kicad_sexpr.</summary>
    public static string RawLibrarySymbols(string root, IReadOnlyDictionary<string, string> blobs)
    {
        var text = new StringBuilder("(lib_symbols");
        foreach (var source in PartSources.OrderBy(s => s.CacheKey, StringComparer.Ordinal))
        {
            string file = ReadPinnedSource(root, source.SourcePath, blobs[source.CacheKey]);
            var definition = SourceDefinition(file, source.SourcePath, source.SourceSymbol, out _);
            string copied = file[definition.Start..definition.End];
            var name = definition.Items![1];
            if (source.SourceSymbol != source.CacheKey)
                copied = copied[..(name.Start - definition.Start)] + Quote(source.CacheKey) + copied[(name.End - definition.Start)..];
            text.Append("\n\t").Append(copied);
        }
        return text.Append("\n)\n").ToString();
    }

    /// <summary>Replace every definition-pin UUID with K21:k, numbered from 1 in the order
    /// (cache key, ordinal; unit; body style; pin number, ordinal). Other identities are kept.</summary>
    public static string NormalizePinIdentities(string libSymbols)
    {
        var pins = LibraryPins(libSymbols);
        var text = new StringBuilder(libSymbols);
        foreach (var (pin, index) in pins.Select((p, i) => (p, i)).OrderByDescending(p => p.p.IdStart))
            text.Remove(pin.IdStart, pin.IdEnd - pin.IdStart).Insert(pin.IdStart, Quote(PsuCpuIds.Id(0x21, index + 1).ToString("D")));
        return text.ToString();
    }

    public sealed record LibraryPin(string CacheKey, int Unit, int Style, string Number, string Name, string Id, int IdStart, int IdEnd);

    /// <summary>Every pin of a lib_symbols list, sorted by the K21 normalization order.</summary>
    public static IReadOnlyList<LibraryPin> LibraryPins(string libSymbols)
    {
        var list = PsuCpuSexpr.Parse(libSymbols);
        if (list.Head != "lib_symbols") throw PsuCpuFixture.Failure("psu_cpu_symbol_capture_incomplete", "Expected one lib_symbols list.");
        var pins = new List<LibraryPin>();
        foreach (var symbol in list.Children("symbol"))
            foreach (var body in symbol.Children("symbol"))
            {
                var (unit, style) = UnitAndStyle(body.Value(1));
                foreach (var pin in body.Children("pin"))
                {
                    var id = pin.Children("uuid").SingleOrDefault()?.Items![1]
                        ?? throw PsuCpuFixture.Failure("psu_cpu_symbol_capture_incomplete", "A normalized definition pin has no identity.");
                    pins.Add(new(symbol.Value(1), unit, style, pin.Child("number").Value(1), pin.Child("name").Value(1), id.Atom!, id.Start, id.End));
                }
            }
        return pins.OrderBy(p => p.CacheKey, StringComparer.Ordinal).ThenBy(p => p.Unit).ThenBy(p => p.Style)
            .ThenBy(p => p.Number, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The lib_symbols list of a saved schematic, moved out one indentation level.</summary>
    public static string ExtractLibrarySymbols(string schematic)
    {
        var list = PsuCpuSexpr.Parse(schematic).Child("lib_symbols");
        string[] lines = schematic[list.Start..list.End].Split('\n');
        return string.Join('\n', lines.Select((line, i) => i > 0 && line.StartsWith('\t') ? line[1..] : line)) + "\n";
    }

    /// <summary>Compare two normalized lists without the identities eeschema allocates for legacy
    /// graphics and texts; definition-pin identities (K21) remain significant.</summary>
    public static string WithoutAllocatedIdentities(string libSymbols) => AllocatedIdentity().Replace(libSymbols, match =>
        match.Groups[1].Value.StartsWith(PsuCpuIds.Prefix + "0021", StringComparison.Ordinal) ? match.Value : "(uuid \"*\")");

    [GeneratedRegex("\\(uuid \"([0-9a-f-]{36})\"\\)")]
    private static partial Regex AllocatedIdentity();

    private static PsuCpuSexpr SourceDefinition(string root, string path, string blob, string symbol, out int version) =>
        SourceDefinition(ReadPinnedSource(root, path, blob), path, symbol, out version);

    private static PsuCpuSexpr SourceDefinition(string file, string path, string symbol, out int version)
    {
        var document = PsuCpuSexpr.Parse(file);
        version = int.Parse(document.Child("version").Value(1));
        var container = path.EndsWith(".kicad_sym", StringComparison.Ordinal) ? document : document.Child("lib_symbols");
        var matches = container.Children("symbol").Where(s => s.Value(1) == symbol).ToArray();
        if (matches.Length != 1 || matches[0].Children("extends").Any())
            throw PsuCpuFixture.Failure("psu_cpu_fixture_file_changed", $"{path} must hold exactly one self-contained {symbol}.");
        return matches[0];
    }

    private static (int Unit, int Style) UnitAndStyle(string name)
    {
        var match = BodyName().Match(name);
        if (!match.Success) throw PsuCpuFixture.Failure("psu_cpu_symbol_capture_incomplete", $"Unexpected unit body name {name}.");
        return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
    }

    [GeneratedRegex("_(\\d+)_(\\d+)$")]
    private static partial Regex BodyName();

    internal static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

/// <summary>Minimal KiCad S-expression reader that keeps exact source spans, so definitions are
/// copied verbatim and identities replaced in place. It is not a schematic loader.</summary>
internal sealed class PsuCpuSexpr(string? atom, bool quoted, IReadOnlyList<PsuCpuSexpr>? items, int start, int end)
{
    public string? Atom { get; } = atom;
    public bool Quoted { get; } = quoted;
    public IReadOnlyList<PsuCpuSexpr>? Items { get; } = items;
    public int Start { get; } = start;
    public int End { get; } = end;
    public string? Head => Items is { Count: > 0 } list && list[0] is { Quoted: false, Atom: { } head } ? head : null;
    public IEnumerable<PsuCpuSexpr> Children(string head) => (Items ?? []).Where(i => i.Head == head);
    public PsuCpuSexpr Child(string head) => Children(head).Single();
    public string Value(int index) => Items![index].Atom ?? throw new FormatException("Expected an atom at position " + index + ".");

    public static PsuCpuSexpr Parse(string text)
    {
        var open = new Stack<(int Start, List<PsuCpuSexpr> Items)>();
        PsuCpuSexpr? root = null;
        for (int i = 0; i < text.Length;)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { ++i; continue; }
            if (c == '(') { open.Push((i++, [])); continue; }
            PsuCpuSexpr node;
            if (c == ')')
            {
                if (!open.TryPop(out var list)) throw new FormatException("Unbalanced closing parenthesis at " + i + ".");
                node = new(null, false, list.Items, list.Start, ++i);
            }
            else if (c == '"')
            {
                int start = i++; var value = new StringBuilder();
                while (true)
                {
                    if (i >= text.Length) throw new FormatException("Unterminated string at " + start + ".");
                    char d = text[i++];
                    if (d == '"') break;
                    if (d == '\\' && i < text.Length)
                        d = text[i++] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', var escaped => escaped };
                    value.Append(d);
                }
                node = new(value.ToString(), true, null, start, i);
            }
            else
            {
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('(' or ')' or '"')) ++i;
                node = new(text[start..i], false, null, start, i);
            }
            if (open.TryPeek(out var parent)) parent.Items.Add(node);
            else if (root is null && node.Items is not null) root = node;
            else throw new FormatException("Expected exactly one top-level list.");
        }
        return open.Count == 0 && root is not null ? root : throw new FormatException("Unbalanced S-expression.");
    }
}

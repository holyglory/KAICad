using System.ComponentModel;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

public sealed record GuidanceToolResult(bool Valid, Guid? ComponentInstanceId, GuidanceResolution? Resolution,
                                       string? ErrorCode, string? ErrorMessage);

public sealed record EngineeringDesignToolResult(bool ModelValid, Guid? DesignId,
    IReadOnlyDictionary<Guid, GuidanceResolution>? Guidance, IReadOnlyList<Guid>? UnrealizedConnections,
    string? ErrorCode, string? ErrorMessage, IReadOnlyList<UnresolvedNetBinding>? UnresolvedNetBindings = null,
    IReadOnlyList<UnresolvedComponentReference>? UnresolvedComponentReferences = null,
    IReadOnlyList<UnresolvedGuidanceBinding>? UnresolvedGuidanceBindings = null)
{
    public bool NetBindingsResolved => ModelValid && UnresolvedNetBindings is not { Count: > 0 };
    public bool ComponentReferencesResolved => ModelValid && UnresolvedComponentReferences is not { Count: > 0 }
        && UnresolvedGuidanceBindings is not { Count: > 0 };
}

public sealed record DesignBindingToolResult(bool DocumentParsed, SchematicBindingReport? BindingReport,
    string? ErrorCode, string? ErrorMessage);

public sealed record DesignProjectionToolResult(string? CandidateEngineeringXml,
    IReadOnlyList<SchematicProjectionConflict> Conflicts, IReadOnlyList<SchematicBindingIssue> BindingIssues,
    bool UnprojectedSnapshotChanges, IReadOnlyList<HierarchyCoverageGap> CoverageGaps,
    string? ErrorCode, string? ErrorMessage);

[McpServerToolType]
public sealed class KnowledgeTools
{
    [McpServerTool(Name = "kicad_design_connectivity_compare", ReadOnly = true, UseStructuredContent = true),
     Description("Compare the supplied design:1 engineering model with one native SchematicElectricalState protobuf-JSON snapshot using exact sheet/symbol/placed-pin bindings. Returns missing or ambiguous pin bindings and split/joined pin partitions. Net names are not identity matches, and snapshot net indexes are not persistent IDs. Requires exact declared knowledge-library XML. Does not read a live editor, assign successor nets, transfer requirements, change either design, or establish complete native coverage or mutation admission.")]
    public SchematicElectricalComparisonResult CompareConnectivity(string designXml, string electricalStateJson,
        string[] knowledgeLibraryXml, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var libraries = knowledgeLibraryXml.Select(ComponentKnowledgeXml.ReadLibrary).ToArray();
            return SchematicElectricalComparison.Compare(SchematicDesignXml.Read(designXml, libraries),
                SchematicJson.Parser.Parse<KiCad.Automation.Protocol.SchematicElectricalState>(electricalStateJson), libraries, cancellationToken);
        }
        catch (Exception error) when (error is AutomationException or Google.Protobuf.InvalidJsonException or Google.Protobuf.InvalidProtocolBufferException)
        { return new(false, false, [], [], [], error is AutomationException known ? known.Code : "invalid_electrical_snapshot", error.Message); }
    }

    [McpServerTool(Name = "kicad_design_reconcile_properties", ReadOnly = true, UseStructuredContent = true),
     Description("Prepare a three-way reverse projection from supplied baseline design:1 XML, desired engineering-design:1 XML and observed typed schematic hierarchy XML. Requires declared knowledge libraries. Reconciles native reference/value/unit/placement changes through explicit bindings while preserving desired instructions and reporting competing edits. Candidate XML is only an engineering-model proposal: unprojected native snapshot changes and coverage gaps remain explicit. Does not read or write files, access a live editor, compare connectivity, verify live revisions, advance synchronization state or apply changes. No candidate is returned for property conflicts or unresolved identity.")]
    public DesignProjectionToolResult ReconcileProperties(string baselineDesignXml, string desiredEngineeringXml,
        string observedHierarchyXml, string[] knowledgeLibraryXml, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var libraries = new List<ComponentKnowledgeLibrary>();
            foreach (string xml in knowledgeLibraryXml)
            {
                cancellationToken.ThrowIfCancellationRequested();
                libraries.Add(ComponentKnowledgeXml.ReadLibrary(xml));
            }
            var baseline = SchematicDesignXml.Read(baselineDesignXml, libraries);
            var desired = EngineeringDesignXml.Read(desiredEngineeringXml, libraries);
            var observed = SchematicDataXml.Read(observedHierarchyXml) as Kiapi.Schematic.Types.SchematicHierarchyData
                ?? throw new AutomationException("invalid_design_xml", "Observed data must be a typed schematic hierarchy.");
            var result = SchematicModelProjection.Reconcile(baseline, desired, observed, libraries, cancellationToken);
            return new(result.Candidate is null ? null : EngineeringDesignXml.Write(result.Candidate, libraries),
                result.Conflicts, result.BindingIssues, result.UnprojectedSnapshotChanges, result.CoverageGaps, result.ErrorCode, result.ErrorMessage);
        }
        catch (AutomationException error)
        {
            return new(null, [], [], true, [], error.Code, error.Message);
        }
    }

    [McpServerTool(Name = "kicad_design_bindings_inspect", ReadOnly = true, UseStructuredContent = true),
     Description("Inspect supplied design:1 XML containing engineering intent, typed native schematic hierarchy and explicit sheet/symbol identity bindings. Requires the declared knowledge libraries as XML. Reports missing/ambiguous model-to-native links, reference/value/unit differences and native serializer coverage gaps. Repeated native object UUIDs are distinguished by their sheet paths; names and positions never identify objects. Does not access a running editor, compare pin connectivity or all persisted properties, infer links, authorize mutations, reconstruct files or perform synchronization. Identifies unresolved links without discarding the supplied versions.")]
    public DesignBindingToolResult InspectBindings(string designXml, string[] knowledgeLibraryXml,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var libraries = new List<ComponentKnowledgeLibrary>();
            foreach (string xml in knowledgeLibraryXml)
            {
                cancellationToken.ThrowIfCancellationRequested();
                libraries.Add(ComponentKnowledgeXml.ReadLibrary(xml));
            }
            var design = SchematicDesignXml.Read(designXml, libraries);
            var report = SchematicDesignBindings.Inspect(design, libraries, cancellationToken);
            return new(true, report, null, null);
        }
        catch (AutomationException error)
        {
            return new(false, null, error.Code, error.Message);
        }
    }

    [McpServerTool(Name = "kicad_engineering_design_validate", ReadOnly = true, UseStructuredContent = true),
     Description("Validate supplied engineering-design:1 XML and its exact declared knowledge-library XML documents. Checks electrical/structural references and resolves inherited versus local component guidance without changing either. Reports retained unresolved component/pin/guidance references separately; detached guidance is not reported as assigned to a live component. Returns conflicting named-property assignments and abstract connections with no explicit net realization. Does not read files, infer prose contradictions, verify electrical performance, compare a live schematic, generate native files or synchronize edits. This engineering intent model is not a complete native schematic snapshot.")]
    public EngineeringDesignToolResult ValidateDesign(string designXml, string[] knowledgeLibraryXml,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var libraries = new List<ComponentKnowledgeLibrary>();
            foreach (string xml in knowledgeLibraryXml)
            {
                cancellationToken.ThrowIfCancellationRequested();
                libraries.Add(ComponentKnowledgeXml.ReadLibrary(xml));
            }
            EngineeringDesign design = EngineeringDesignXml.Read(designXml, libraries);
            cancellationToken.ThrowIfCancellationRequested();
            return new(true, design.Circuit.Id, design.Validate(libraries),
                design.Structure.Connections.Where(c => c.NetIds.Count == 0).Select(c => c.Id).Order().ToArray(), null, null,
                design.Structure.UnresolvedNetBindings, design.Structure.UnresolvedComponentReferences, design.UnresolvedGuidanceBindings);
        }
        catch (AutomationException error)
        {
            return new(false, null, null, null, error.Code, error.Message);
        }
    }

    [McpServerTool(Name = "kicad_component_guidance_resolve", ReadOnly = true),
     Description("Validate supplied electrical circuit, component knowledge library and instance-binding XML, then resolve inherited and local text/numerical guidance for that exact component. Returns explicit quantity kinds and units, sources, replacement history, competing named-property assignments and numerical range issues. Valid means structurally valid input, not verified engineering claims or absence of conflicts. Does not infer values from prose, turn absolute maxima into operating limits, or apply placement/routing/native edits. Inputs use circuit:1 and knowledge:1 namespaces, not a complete design.xml.")]
    public GuidanceToolResult Resolve(string circuitXml, string libraryXml, string bindingXml,
                                      CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Circuit circuit = CircuitXml.Read(circuitXml);
            cancellationToken.ThrowIfCancellationRequested();
            ComponentKnowledgeLibrary library = ComponentKnowledgeXml.ReadLibrary(libraryXml);
            ComponentKnowledgeBinding binding = ComponentKnowledgeXml.ReadBinding(bindingXml, library);
            cancellationToken.ThrowIfCancellationRequested();
            GuidanceResolution resolution = ComponentGuidance.Resolve(circuit, library, binding);
            return new(true, binding.ComponentInstanceId, resolution, null, null);
        }
        catch (AutomationException error)
        {
            return new(false, null, null, error.Code, error.Message);
        }
    }
}

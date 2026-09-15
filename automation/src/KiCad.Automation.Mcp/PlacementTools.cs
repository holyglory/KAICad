using System.ComponentModel;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

public sealed record ConnectedPlacementProposal(string DocumentJson, IReadOnlyList<string> SymbolIds,
    long DeltaXNm, long DeltaYNm);

public sealed record ConnectedTransformProposal(string DocumentJson, IReadOnlyList<string> SymbolIds,
    SchematicConnectedTransformKind Transform, long PivotXNm, long PivotYNm);

public sealed record PlacementPlanToolResult(string? ReconciledEngineeringXml,
    IReadOnlyList<ConnectedPlacementProposal> Moves, IReadOnlyList<PlacementPlanIssue> Issues,
    IReadOnlyList<SchematicProjectionConflict> Conflicts, IReadOnlyList<SchematicBindingIssue> BindingIssues,
    bool UnprojectedSnapshotChanges, IReadOnlyList<HierarchyCoverageGap> CoverageGaps,
    string? ErrorCode, string? ErrorMessage, IReadOnlyList<ConnectedTransformProposal>? Transforms = null);

[McpServerToolType]
public sealed class PlacementTools
{
    [McpServerTool(Name = "kicad_design_plan_placement", ReadOnly = true, UseStructuredContent = true),
     Description("Prepare connected-symbol placement from baseline design:1 XML, desired engineering-design:1 XML, observed typed hierarchy XML and declared knowledge libraries. Apply the ordered Transforms first, then Moves; equal displacements on one sheet are grouped. Uses exact sheet paths and native identities, moving shared geometry once. Equivalent angle/mirror encodings do not request edits. Preserves coordinate-free intent, conflicts, native coverage gaps and unprojected changes. Lock and electrical changes require their own operations. Proposals only: does not access editors, verify live revisions, apply edits, advance synchronization or certify connectivity. Native application requires explicit instance/document guards and operation identity, including for non-displayed sheets.")]
    public PlacementPlanToolResult PlanPlacement(string baselineDesignXml, string desiredEngineeringXml,
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
            var observed = SchematicDataXml.Read(observedHierarchyXml) as SchematicHierarchyData
                ?? throw new AutomationException("invalid_design_xml", "Observed data must be a typed schematic hierarchy.");
            var result = SchematicPlacementPlan.Plan(baseline, desired, observed, libraries, cancellationToken);
            var moves = new List<ConnectedPlacementProposal>();
            var transforms = new List<ConnectedTransformProposal>();
            foreach (var operation in result.Operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (operation.TransformConnectedSymbols is { } transform)
                {
                    transforms.Add(new(SchematicJson.Formatter.Format(operation.TargetDocument),
                        transform.Symbols.Select(s => s.Value).ToArray(), transform.Kind, transform.Pivot.XNm, transform.Pivot.YNm));
                    continue;
                }
                moves.Add(new(SchematicJson.Formatter.Format(operation.TargetDocument),
                    operation.MoveConnectedSymbols.Symbols.Select(s => s.Value).ToArray(),
                    operation.MoveConnectedSymbols.Delta.XNm, operation.MoveConnectedSymbols.Delta.YNm));
            }
            return new(result.ReconciledModel is null ? null : EngineeringDesignXml.Write(result.ReconciledModel, libraries),
                moves, result.Issues, result.Conflicts, result.BindingIssues, result.UnprojectedSnapshotChanges,
                result.CoverageGaps, null, null, transforms);
        }
        catch (AutomationException error)
        {
            return new(null, [], [], [], [], true, [], error.Code, error.Message, []);
        }
    }
}

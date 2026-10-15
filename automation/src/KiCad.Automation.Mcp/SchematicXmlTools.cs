using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class SchematicXmlTools
{
    [McpServerTool(Name = "kicad_schematic_hierarchy_reconcile", ReadOnly = true),
     Description("Three-way reconcile supplied baseline, desired and current native typed hierarchy XML by exact sheet-instance paths and object identities. Combines supported independent sheet edits and branch changes, preserving project-wide setting ownership. Returns merged typed XML and one ordered hierarchy delta, or conflicts with all three sheet versions and no partial operations. Delete-versus-edit, competing additions, conflicting shared screens and invalid combined topology require resolution; reparenting with concurrent child changes may still conflict. Coverage gaps remain explicit. Does not read files, edit KiCad, advance a synchronization baseline, resolve electrical bindings or authorize live mutations.")]
    public CallToolResult ReconcileHierarchy(string baselineXml, string desiredXml, string nativeXml,
        CancellationToken cancellationToken,
        [Description("Optional whole-sheet choices keyed by an exact returned conflict instancePath: xml, native or baseline. A choice replaces the entire sheet version, including otherwise independent changes. All choices require expectedSnapshotToken; invalid topology or inconsistent shared contents remain rejected.")]
        Dictionary<string, string>? choices = null, string? expectedSnapshotToken = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SchematicHierarchyData Read(string xml) => SchematicDataXml.Read(xml) as SchematicHierarchyData
                ?? throw new AutomationException("invalid_schematic_xml", "Expected typed schematic hierarchy XML.");
            var baseline = Read(baselineXml); var desired = Read(desiredXml); var native = Read(nativeXml);
            string snapshotToken = SchematicHierarchyMerge.SnapshotToken(baseline, desired, native);
            var selected = new Dictionary<string, SchematicConflictChoice>(StringComparer.Ordinal);
            foreach (var (path, choice) in choices ?? [])
                selected.Add(path, choice switch
                {
                    "xml" => SchematicConflictChoice.Xml, "native" => SchematicConflictChoice.Native,
                    "baseline" => SchematicConflictChoice.Baseline,
                    _ => throw new AutomationException("invalid_hierarchy_resolution", "Choose xml, native or baseline.")
                });
            var plan = choices is null ? SchematicHierarchyMerge.Plan(baseline, desired, native, cancellationToken)
                : SchematicHierarchyMerge.Resolve(baseline, desired, native,
                    expectedSnapshotToken ?? throw new AutomationException("missing_hierarchy_snapshot_token", "Supply the token returned with these conflicts."),
                    selected, cancellationToken);
            var structured = JsonSerializer.SerializeToElement(new
            {
                canPlan = plan.CanApply, snapshotToken, liveMutationAuthorized = false, completeReconstructionProven = false,
                mergedXml = plan.Merged is null ? null : SchematicDataXml.Write(plan.Merged),
                operations = plan.NativeOperations.Select(o => JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(o))).ToArray(),
                conflicts = plan.Conflicts.Select(c => new
                {
                    instancePath = c.InstancePath, reason = c.Reason,
                    baselineXml = c.Baseline is null ? null : SchematicDataXml.Write(c.Baseline),
                    desiredXml = c.Xml is null ? null : SchematicDataXml.Write(c.Xml),
                    nativeXml = c.Native is null ? null : SchematicDataXml.Write(c.Native),
                    items = c.Items.Select(i => new { objectId = i.ObjectId, reason = i.Reason, cacheKey = i.CacheKey }).ToArray()
                }).ToArray(),
                coverageGaps = plan.CoverageGaps.Select(g => new { instancePath = g.InstancePath, reason = g.Reason }).ToArray(),
                errorCode = plan.ErrorCode, errorMessage = plan.ErrorMessage
            });
            return new() { Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured };
        }
        catch (AutomationException error)
        {
            return new() { IsError = true, Content = [new TextContentBlock
                { Text = JsonSerializer.Serialize(new { code = error.Code, message = error.Message }) }] };
        }
    }

    [McpServerTool(Name = "kicad_schematic_hierarchy_plan", ReadOnly = true),
     Description("Compare supplied current and desired typed hierarchy XML and return explicitly sheet-targeted operations for loaded sheets, supported new nested sheets, shared-screen instances, subtree removal and reparenting. Shared instances and moved subtrees require consistent contents and explicit placement records. New sheets require represented contents and supported settings; parent creation precedes child edits. Removed subtrees are detached through their parent reference without deleting their contents or files. Rejects conflicting shared edits/assets and unsupported settings. Does not read files, edit KiCad, merge concurrent changes, admit revisions or prove complete schematic fidelity.")]
    public CallToolResult PlanHierarchy(string currentXml, string desiredXml, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SchematicHierarchyData Read(string xml) => SchematicDataXml.Read(xml) as SchematicHierarchyData
                ?? throw new AutomationException("invalid_schematic_xml", "Expected typed schematic hierarchy XML.");
            var current = Read(currentXml); var desired = Read(desiredXml);
            var operations = SchematicHierarchyDelta.Plan(current, desired, cancellationToken);
            var coverage = SchematicHierarchyTopology.Inspect(desired, cancellationToken).CoverageGaps;
            var structured = JsonSerializer.SerializeToElement(new
            {
                canPlan = true, liveMutationAuthorized = false, completeReconstructionProven = false,
                operations = operations.Select(o => JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(o))).ToArray(),
                coverageGaps = coverage.Select(g => new { instancePath = g.InstancePath, reason = g.Reason }).ToArray()
            });
            return new() { Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured };
        }
        catch (AutomationException error)
        {
            return new() { IsError = true, Content = [new TextContentBlock
                { Text = JsonSerializer.Serialize(new { code = error.Code, message = error.Message }) }] };
        }
    }

    [McpServerTool(Name = "kicad_schematic_hierarchy_validate", ReadOnly = true),
     Description("Check supplied typed hierarchy XML for missing child contents, orphan/duplicate instances, incorrect child identities, parent paths, recursive screens and inconsistent shared-screen child references. Reports topology validity separately from explicit serializer coverage gaps. Does not inspect files, compare shared electrical/layout contents, verify connectivity, restore a design or authorize mutations.")]
    public CallToolResult ValidateHierarchy(string hierarchyXml, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = SchematicDataXml.Read(hierarchyXml) as SchematicHierarchyData
                ?? throw new AutomationException("invalid_schematic_xml", "Expected typed schematic hierarchy XML.");
            var report = SchematicHierarchyTopology.Inspect(data, cancellationToken);
            var structured = JsonSerializer.SerializeToElement(new
            {
                topologyValid = report.IsValid, instanceCount = report.InstanceCount, screenCount = report.ScreenCount,
                completeReconstructionProven = false, liveMutationAuthorized = false,
                issues = report.Issues.Select(i => new { code = i.Code, instancePath = i.InstancePath, objectId = i.ObjectId, message = i.Message }),
                coverageGaps = report.CoverageGaps.Select(g => new { instancePath = g.InstancePath, reason = g.Reason })
            });
            return new() { Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured };
        }
        catch (AutomationException error)
        {
            return new() { IsError = true, Content = [new TextContentBlock
                { Text = JsonSerializer.Serialize(new { code = error.Code, message = error.Message }) }] };
        }
    }

    [McpServerTool(Name = "kicad_schematic_xml_plan", ReadOnly = true),
     Description("Compare supplied baseline, desired and observed native typed schematic XML for the same explicit sheet. Plans supported item changes without reading files or touching an editor. Returns merged XML, native operations or all competing versions, plus a snapshotToken binding any later choice to these exact snapshots. Optional choices map conflicted object UUIDs to xml, native or baseline; netChainChoices maps exact conflicted net-chain names to the same choices; variantChoices and cacheChoices bind the corresponding project records. When any choice map is supplied, expectedSnapshotToken is required and must match the returned token, otherwise the choice is refused as stale. Choices apply only to these supplied snapshots; this is not live conflict resolution, complete hierarchy generation or safe mutation admission. Settings, hierarchy/group changes and unknown objects may be unsupported.")]
    public CallToolResult Plan(string baselineXml, string desiredXml, string nativeXml,
        CancellationToken cancellationToken, Dictionary<string, string>? choices = null,
        Dictionary<string, string>? netChainChoices = null,
        [Description("Map exact conflicting project variant names to xml, native or baseline. Choices refer only to the supplied snapshots.")]
        Dictionary<string, string>? variantChoices = null,
        [Description("Map exact conflicting cache-definition keys to xml, native or baseline. Choices refer only to the supplied snapshots.")]
        Dictionary<string, string>? cacheChoices = null,
        [Description("The snapshotToken returned for these exact baseline, desired and native snapshots. Required whenever any choice map is supplied.")]
        string? expectedSnapshotToken = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseline = Screen(baselineXml); var desired = Screen(desiredXml); var native = Screen(nativeXml);
            cancellationToken.ThrowIfCancellationRequested();
            string snapshotToken = SnapshotToken(baseline, desired, native);
            bool hasChoices = choices is not null || netChainChoices is not null || variantChoices is not null || cacheChoices is not null;
            if (hasChoices)
            {
                if (string.IsNullOrWhiteSpace(expectedSnapshotToken))
                    throw new AutomationException("missing_sync_snapshot_token", "Supply the snapshotToken returned with these conflicts before choosing a version.");
                if (!string.Equals(expectedSnapshotToken, snapshotToken, StringComparison.Ordinal))
                    throw new AutomationException("stale_sync_snapshot", "The supplied conflict choice belongs to different XML snapshots; replan and choose again.");
            }
            var selected = new Dictionary<Guid, SchematicConflictChoice>();
            foreach (var (key, value) in choices ?? [])
            {
                if (!Guid.TryParseExact(key, "D", out var id) || id == Guid.Empty)
                    throw new AutomationException("invalid_sync_resolution", "Conflict choices require an exact object UUID.");
                var choice = value switch
                {
                    "xml" => SchematicConflictChoice.Xml,
                    "native" => SchematicConflictChoice.Native,
                    "baseline" => SchematicConflictChoice.Baseline,
                    _ => throw new AutomationException("invalid_sync_resolution", "Choose xml, native or baseline.")
                };
                if (!selected.TryAdd(id, choice))
                    throw new AutomationException("invalid_sync_resolution", "Duplicate conflict identity.");
            }
            var selectedChains = new Dictionary<string, SchematicConflictChoice>(StringComparer.Ordinal);
            foreach (var (name, value) in netChainChoices ?? [])
                selectedChains.Add(name, value switch
                {
                    "xml" => SchematicConflictChoice.Xml,
                    "native" => SchematicConflictChoice.Native,
                    "baseline" => SchematicConflictChoice.Baseline,
                    _ => throw new AutomationException("invalid_sync_resolution", "Choose xml, native or baseline.")
                });
            var selectedVariants = new Dictionary<string, SchematicConflictChoice>(StringComparer.Ordinal);
            foreach (var (name, value) in variantChoices ?? [])
                selectedVariants.Add(name, value switch
                {
                    "xml" => SchematicConflictChoice.Xml,
                    "native" => SchematicConflictChoice.Native,
                    "baseline" => SchematicConflictChoice.Baseline,
                    _ => throw new AutomationException("invalid_sync_resolution", "Choose xml, native or baseline.")
                });
            var selectedCaches = new Dictionary<string, SchematicConflictChoice>(StringComparer.Ordinal);
            foreach (var (key, value) in cacheChoices ?? [])
                selectedCaches.Add(key, value switch
                {
                    "xml" => SchematicConflictChoice.Xml,
                    "native" => SchematicConflictChoice.Native,
                    "baseline" => SchematicConflictChoice.Baseline,
                    _ => throw new AutomationException("invalid_sync_resolution", "Choose xml, native or baseline.")
                });
            var plan = choices is null && netChainChoices is null && variantChoices is null && cacheChoices is null
                ? SchematicItemMerge.Plan(baseline, desired, native)
                : SchematicItemMerge.Resolve(baseline, desired, native, selected, selectedChains, selectedVariants, selectedCaches);
            cancellationToken.ThrowIfCancellationRequested();
            var structured = JsonSerializer.SerializeToElement(new
            {
                canPlan = plan.CanApply, snapshotToken, liveMutationAuthorized = false,
                mergedXml = plan.Merged is null ? null : SchematicDataXml.Write(plan.Merged),
                operations = plan.NativeOperations.Select(o => JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(o))).ToArray(),
                conflicts = plan.Conflicts.Select(c => new
                {
                    objectId = c.ObjectId, reason = c.Reason, cacheKey = c.CacheKey,
                    baseline = c.Baseline is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(c.Baseline)),
                    xml = c.Xml is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(c.Xml)),
                    native = c.Native is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(c.Native))
                }).ToArray(),
                unsupportedChange = plan.UnsupportedChange
            });
            return new() { Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured };
        }
        catch (AutomationException error)
        {
            return new() { IsError = true, Content = [new TextContentBlock
                { Text = JsonSerializer.Serialize(new { code = error.Code, message = error.Message }) }] };
        }
    }

    private static SchematicScreenData Screen(string xml) => SchematicDataXml.Read(xml) as SchematicScreenData
        ?? throw new AutomationException("invalid_schematic_xml", "Expected typed schematic screen XML with an explicit sheet target.");

    private static string SnapshotToken(SchematicScreenData baseline, SchematicScreenData desired, SchematicScreenData native)
    {
        // Hash canonical typed XML in a length-delimited JSON array so a choice is
        // bound to all three snapshots without ambiguity from separators or input
        // formatting. This token is a planning checkpoint, not native revision proof.
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            SchematicDataXml.Write(baseline), SchematicDataXml.Write(desired), SchematicDataXml.Write(native)
        });
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }
}

using System.ComponentModel;
using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class RecoveryTools
{
    [McpServerTool(Name = "kicad_design_sync_plan", ReadOnly = true),
     Description("Prepare one full typed design candidate by reconciling saved XML intent, hierarchy, native properties and captured pin connectivity. Requires an explicit saved instance/recovery path and current recovery revision token. Conflicts or unresolved property projection return no partial candidate. Preserves textual requirements and unresolved net bindings. Returns candidate XML and proposed native operations, with coverage gaps and a flag requiring native connectivity validation. This is preparation only: it does not contact KiCad, prove live freshness, write design files, apply edits or advance synchronization.")]
    public CallToolResult PlanSynchronization(string instanceId, string recoveryPath, string expectedRevisionToken,
        CancellationToken cancellationToken) => Execute(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (store, saved) = Read(instanceId, recoveryPath);
        if (saved.RevisionToken != expectedRevisionToken)
            throw new AutomationException("design_recovery_changed", "Recovery changed; inspect the current record before planning.");
        var plan = SchematicSynchronizationPlanner.Plan(saved.State, cancellationToken);
        var data = JsonSerializer.SerializeToElement(new
        {
            instanceId = saved.State.InstanceId, recoveryRevisionToken = saved.RevisionToken,
            savedNativeRevision = saved.State.NativeRevision, trackingComplete = saved.State.TrackingComplete,
            liveMutationAuthorized = false, designFileWritten = false, baselineAdvanced = false,
            canPrepare = plan.CanPrepare, candidateDesignXml = plan.CandidateXml,
            nativeOperationsJson = plan.NativeOperations.Select(operation => SchematicJson.Formatter.Format(operation)).ToArray(),
            nativeConnectivityValidationRequired = plan.NativeConnectivityValidationRequired,
            hierarchyConflicts = plan.Hierarchy?.Conflicts.Select(x => new { x.InstancePath, x.Reason }),
            electricalConflicts = plan.Electrical?.Conflicts, netChanges = plan.Electrical?.NetChanges,
            propertyConflicts = plan.Properties?.Conflicts, bindingIssues = plan.BindingIssues,
            electricalBindingIssues = plan.Electrical?.BindingIssues, propertyBindingIssues = plan.Properties?.BindingIssues,
            projectionDifferences = plan.ProjectionDifferences, observedConnectivity = plan.ObservedConnectivity,
            coverageGaps = plan.CoverageGaps, errorCode = plan.ErrorCode, errorMessage = plan.ErrorMessage
        });
        cancellationToken.ThrowIfCancellationRequested();
        if (store.Read()?.RevisionToken != saved.RevisionToken)
            throw new AutomationException("design_recovery_changed", "Recovery changed while planning; reload it.");
        return new() { IsError = !plan.CanPrepare, Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_design_nets_reconcile", ReadOnly = true),
     Description("Plan three-way pin connectivity reconciliation from an explicit instance's saved recovery record and its expected revision token. Combines independent or matching XML/native net edits through exact pin identities. Contradictory changes return conflicts and no candidate; ambiguous requirement bindings remain unresolved. Returns candidate engineering XML only, not a reconstructed schematic or an applied synchronization. Requires matched baseline/current electrical checkpoints and unchanged component, sheet, unit and pin ownership. Does not contact KiCad, establish live freshness, write files, edit native objects or advance the baseline.")]
    public CallToolResult ReconcileNets(string instanceId, string recoveryPath, string expectedRevisionToken,
        CancellationToken cancellationToken) => Execute(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (store, saved) = Read(instanceId, recoveryPath);
        if (saved.RevisionToken != expectedRevisionToken)
            throw new AutomationException("design_recovery_changed", "Recovery changed; reload the saved record before planning.");
        var plan = SchematicNetReconciliation.Plan(saved.State, cancellationToken);
        var data = JsonSerializer.SerializeToElement(new
        {
            instanceId = saved.State.InstanceId, recoveryRevisionToken = saved.RevisionToken,
            savedNativeRevision = saved.State.NativeRevision, trackingComplete = saved.State.TrackingComplete,
            liveMutationAuthorized = false, canPlan = plan.Candidate is not null,
            candidateEngineeringXml = plan.Candidate is null ? null : EngineeringDesignXml.Write(plan.Candidate, saved.State.KnowledgeLibraries),
            conflicts = plan.Conflicts, netChanges = plan.NetChanges,
            unresolvedNetBindings = plan.Candidate?.Structure.UnresolvedNetBindings?.Count,
            bindingIssues = plan.BindingIssues, coverageGaps = plan.CoverageGaps,
            errorCode = plan.ErrorCode, errorMessage = plan.ErrorMessage
        });
        cancellationToken.ThrowIfCancellationRequested();
        if (store.Read()?.RevisionToken != saved.RevisionToken)
            throw new AutomationException("design_recovery_changed", "Recovery changed during reconciliation; reload it.");
        return new() { IsError = plan.ErrorCode is not null,
            Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_design_recovery_plan", ReadOnly = true),
     Description("Inspect a saved recovery record at an absolute path for an explicit instance ID. Plans hierarchy reconciliation from the saved baseline, desired design XML and saved native observation, including retained choices. Does not contact KiCad or prove that an instance is live or the observation is current. Returns record and snapshot tokens, conflicts and selected sheet versions. Invalid desired XML is reported without changing its bytes. No native edit, design-file write or baseline advancement occurs.")]
    public CallToolResult Plan(string instanceId, string recoveryPath, CancellationToken cancellationToken) => Execute(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (store, saved) = Read(instanceId, recoveryPath);
        var result = Describe(saved);
        cancellationToken.ThrowIfCancellationRequested();
        if (store.Read()?.RevisionToken != saved.RevisionToken)
            throw new AutomationException("design_recovery_changed", "Recovery changed during inspection; reload it.");
        return result;
    });

    [McpServerTool(Name = "kicad_design_recovery_resolve", ReadOnly = false),
     Description("Persist reviewed whole-sheet conflict choices in one explicitly identified saved recovery record. Requires its current recovery revision token and hierarchy snapshot token from kicad_design_recovery_plan. choices maps exact conflict instance paths to xml, native or baseline; selecting a whole sheet may discard independent changes within that sheet. Existing choices are retained unless explicitly replaced. The only write is the recovery record: baseline, desired file bytes and pending native command remain unchanged. Does not contact KiCad, apply a native batch, advance synchronization or write design files. Stale inputs and invalid choices fail without replacing the record; incomplete or incompatible choices can remain saved but are not an applicable plan.")]
    public CallToolResult Resolve(string instanceId, string recoveryPath, string expectedRevisionToken,
        string expectedSnapshotToken, Dictionary<string, string> choices, CancellationToken cancellationToken) => Execute(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (store, saved) = Read(instanceId, recoveryPath);
        if (saved.RevisionToken != expectedRevisionToken)
            throw new AutomationException("design_recovery_changed", "Recovery changed; inspect the current conflicts before choosing.");
        var selected = choices.ToDictionary(p => p.Key, p => p.Value switch
        {
            "xml" => SchematicConflictChoice.Xml, "native" => SchematicConflictChoice.Native,
            "baseline" => SchematicConflictChoice.Baseline,
            _ => throw new AutomationException("invalid_hierarchy_resolution", "Choose xml, native or baseline.")
        }, StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        var written = store.ResolveHierarchy(expectedRevisionToken, expectedSnapshotToken, selected);
        // Do not report cancellation after the atomic write as though nothing was saved.
        return Describe(written);
    });

    private static (DesignRecoveryStore Store, StoredDesignRecovery Saved) Read(string instanceId, string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new AutomationException("invalid_recovery_path", "Specify the absolute recovery-record path.");
        if (!Guid.TryParseExact(instanceId, "D", out var id) || id == Guid.Empty)
            throw new AutomationException("invalid_instance_id", "Specify the saved instance ID.");
        var store = new DesignRecoveryStore(path);
        var saved = store.Read() ?? throw new AutomationException("missing_design_recovery", "No saved recovery record exists.");
        if (saved.State.InstanceId != id)
            throw new AutomationException("recovery_instance_mismatch", "The record belongs to a different instance.");
        return (store, saved);
    }

    private static CallToolResult Describe(StoredDesignRecovery saved)
    {
        var plan = DesignRecoveryStore.PlanHierarchy(saved.State);
        var data = JsonSerializer.SerializeToElement(new
        {
            instanceId = saved.State.InstanceId, recoveryRevisionToken = saved.RevisionToken,
            snapshotToken = DesignRecoveryStore.HierarchySnapshotToken(saved.State),
            savedNativeRevision = saved.State.NativeRevision, trackingComplete = saved.State.TrackingComplete,
            pendingOperationId = saved.State.PendingMutation?.OperationId,
            electricalBaselineAvailable = saved.State.BaselineElectrical is not null,
            electricalObservationAvailable = saved.State.ObservedElectrical is not null,
            liveMutationAuthorized = false, canPlan = plan.CanApply,
            choices = saved.State.HierarchyResolution?.Choices.ToDictionary(p => p.Key, p => p.Value.ToString().ToLowerInvariant()),
            mergedXml = plan.Merged is null ? null : SchematicDataXml.Write(plan.Merged),
            conflicts = plan.Conflicts.Select(c => new
            {
                instancePath = c.InstancePath, reason = c.Reason,
                baselineXml = c.Baseline is null ? null : SchematicDataXml.Write(c.Baseline),
                desiredXml = c.Xml is null ? null : SchematicDataXml.Write(c.Xml),
                nativeXml = c.Native is null ? null : SchematicDataXml.Write(c.Native)
            }).ToArray(),
            coverageGaps = plan.CoverageGaps, errorCode = plan.ErrorCode, errorMessage = plan.ErrorMessage
        });
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    }

    private static CallToolResult Execute(Func<CallToolResult> action)
    {
        try { return action(); }
        catch (Exception error) when (error is AutomationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            var data = JsonSerializer.SerializeToElement(new
                { errorCode = error is AutomationException a ? a.Code : "design_recovery_io", errorMessage = error.Message });
            return new() { IsError = true, Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
        }
    }
}

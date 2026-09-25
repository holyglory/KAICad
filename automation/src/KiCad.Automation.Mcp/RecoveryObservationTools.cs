using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class RecoveryObservationTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_design_electrical_baseline_initialize"),
     Description("Initialize only a missing electrical recovery baseline for one explicitly attached native instance and absolute recovery path. Requires the exact recovery revision token, no pending native operation, unchanged baseline hierarchy, and matching exact model/native pin connectivity. Preserves desired XML and requirements; never writes native design files, edits KiCad or replaces an established baseline. Old recovery files without electrical checkpoints remain readable. Full revision admission and automatic synchronization are still separate.")]
    public Task<CallToolResult> InitializeElectricalBaseline(string instanceId, string recoveryPath,
        string expectedRevisionToken, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(recoveryPath))
            throw new AutomationException("invalid_recovery_path", "Provide the absolute design recovery path.");
        var store = new DesignRecoveryStore(recoveryPath);
        var saved = store.Read() ?? throw new AutomationException("missing_design_recovery", "Create design recovery state first.");
        if (saved.State.InstanceId.ToString("D") != instanceId)
            throw new AutomationException("recovery_instance_mismatch", "The recovery record belongs to another instance.");
        var initialized = await DesignRecoveryInspector.InitializeElectricalBaselineAsync(store, registry.Client(instanceId), expectedRevisionToken, cancellationToken);
        var data = JsonSerializer.SerializeToElement(new { instanceId, recoveryRevisionToken = initialized.RevisionToken,
            nativeRevision = initialized.State.NativeRevision, electricalBaselineInitialized = true,
            netCount = initialized.State.BaselineElectrical!.Nets.Count, trackingComplete = initialized.State.TrackingComplete,
            liveMutationAuthorized = false });
        return new CallToolResult { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_design_recovery_refresh"),
     Description("Capture and persist the current native hierarchy into an existing recovery record for an explicitly attached instance; a record that keeps electrical checkpoints also gets the current pin connectivity, so it can be planned again. Requires the absolute recovery path and exact expected recovery revision token. Preserves baseline, desired XML bytes and libraries. Rejects unconfirmed pending mutations; use recovery observation and reconciliation first. Changed native data/revision invalidates saved hierarchy choices. A concurrent recovery write rejects the refresh. Does not modify KiCad, write design XML, advance the baseline, or imply complete tracking or automatic synchronization.")]
    public Task<CallToolResult> RefreshRecovery(string instanceId, string recoveryPath,
        string expectedRevisionToken, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(recoveryPath))
            throw new AutomationException("invalid_recovery_path", "Specify the absolute saved design recovery path.");
        var store = new DesignRecoveryStore(recoveryPath);
        var saved = store.Read() ?? throw new AutomationException("missing_design_recovery", "No saved design recovery state exists.");
        if (saved.State.InstanceId.ToString("D") != instanceId)
            throw new AutomationException("recovery_instance_mismatch", "The recovery record belongs to a different instance.");
        if (saved.RevisionToken != expectedRevisionToken)
            throw new AutomationException("design_recovery_changed", "Recovery state changed; reload it before refresh.");
        // A record that keeps electrical checkpoints is planned against current pin connectivity, so capture it with the
        // hierarchy; a hierarchy-only refresh would leave that record unplannable (missing_electrical_observation).
        bool electrical = saved.State.BaselineElectrical is not null || saved.State.ObservedElectrical is not null;
        var refreshed = await DesignRecoveryInspector.RefreshAsync(store, registry.Client(instanceId), expectedRevisionToken, cancellationToken,
            includeElectrical: electrical);
        var structured = JsonSerializer.SerializeToElement(new
        {
            instanceId, recoveryRevisionToken = refreshed.RevisionToken,
            nativeRevision = refreshed.State.NativeRevision, trackingComplete = refreshed.State.TrackingComplete,
            choicesInvalidated = saved.State.HierarchyResolution is not null && refreshed.State.HierarchyResolution is null,
            changed = saved.RevisionToken != refreshed.RevisionToken, liveMutationAuthorized = false
        });
        return new CallToolResult { Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured };
    });

    [McpServerTool(Name = "kicad_design_recovery_reattach"),
     Description("Explicitly adopt a checked native observation after reloading a schematic. Requires the exact saved instance, absolute recovery path, current recovery token and newly observed document epoch. Rejects pending old-session operations, wrong documents and concurrent writes. Preserves desired XML, baseline, libraries and historical receipts; invalidates old conflict choices. Does not edit or save KiCad, write XML, or resolve intervening edits. Inspect the synchronization plan before restarting automatic synchronization.")]
    public Task<CallToolResult> ReattachRecovery(string instanceId, string recoveryPath, string expectedRevisionToken,
        string expectedDocumentEpoch, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(recoveryPath))
            throw new AutomationException("invalid_recovery_path", "Specify the absolute saved design recovery path.");
        var store = new DesignRecoveryStore(recoveryPath);
        var saved = store.Read() ?? throw new AutomationException("missing_design_recovery", "No saved design recovery state exists.");
        if (saved.State.InstanceId.ToString("D") != instanceId)
            throw new AutomationException("recovery_instance_mismatch", "The recovery record belongs to a different instance.");
        var attached = await DesignRecoveryReattachment.ReattachAsync(store, registry.Client(instanceId),
            expectedRevisionToken, expectedDocumentEpoch, cancellationToken);
        var data = JsonSerializer.SerializeToElement(new { instanceId, recoveryRevisionToken = attached.RevisionToken,
            previousRevision = saved.State.NativeRevision, nativeRevision = attached.State.NativeRevision,
            snapshotChanged = !Equals(saved.State.Observed, attached.State.Observed),
            electricalSnapshotChanged = !Equals(saved.State.ObservedElectrical, attached.State.ObservedElectrical),
            baselineAdvanced = false, designFileWritten = false, liveMutationAuthorized = false });
        return new CallToolResult { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_design_recovery_observe", ReadOnly = true),
     Description("Observe a saved full-design recovery record against its explicitly attached KiCad instance. recoveryPath is an absolute DesignRecoveryStore file path; optional expectedRevisionToken rejects a changed recovery record. Verifies the exact pending command receipt, then reads the current native hierarchy with matching instance, root, epoch and non-regressing revision. Returns the original receipt separately from current state, which may include later edits or undo. Preserves invalid desired XML, baseline, pending operation and files. Does not resubmit edits, resolve conflicts, advance synchronization or imply full native coverage. The pending target (or saved root when none) must be readable by the native editor.")]
    public Task<CallToolResult> ObserveRecovery(string instanceId, string recoveryPath,
        CancellationToken cancellationToken, string? expectedRevisionToken = null) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(recoveryPath))
            throw new AutomationException("invalid_recovery_path", "Specify the absolute saved design recovery path.");
        var store = new DesignRecoveryStore(recoveryPath);
        var saved = store.Read() ?? throw new AutomationException("missing_design_recovery", "No saved design recovery state exists.");
        if (saved.State.InstanceId.ToString("D") != instanceId)
            throw new AutomationException("recovery_instance_mismatch", "The recovery record belongs to a different instance.");
        if (expectedRevisionToken is not null && saved.RevisionToken != expectedRevisionToken)
            throw new AutomationException("design_recovery_changed", "Recovery state changed; reload it before observation.");
        var observed = await DesignRecoveryInspector.ObserveAsync(store, registry.Client(instanceId), cancellationToken);
        if (observed.Inspection.RevisionToken != saved.RevisionToken)
            throw new AutomationException("design_recovery_changed", "Recovery state changed during observation; reload it before continuing.");
        var structured = JsonSerializer.SerializeToElement(new
        {
            instanceId, recoveryRevisionToken = observed.Inspection.RevisionToken,
            electricalBaselineAvailable = saved.State.BaselineElectrical is not null,
            electricalObservationAvailable = saved.State.ObservedElectrical is not null,
            pendingPublicationOperationId = saved.State.PendingPublication?.OperationId,
            pendingPublicationPhase = saved.State.PendingPublication?.Phase.ToString(),
            disposition = observed.Inspection.Disposition.ToString(), trackingComplete = observed.Snapshot.TrackingComplete,
            receipt = observed.Inspection.Receipt is null ? (JsonElement?)null :
                JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(observed.Inspection.Receipt)),
            checkedReceipt = observed.Inspection.CheckedReceipt is null ? (JsonElement?)null :
                JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(observed.Inspection.CheckedReceipt)),
            saveReceipt = observed.Inspection.SaveReceipt is null ? (JsonElement?)null :
                JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(observed.Inspection.SaveReceipt)),
            snapshot = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(observed.Snapshot)),
            xml = SchematicDataXml.Write(observed.Snapshot.Data)
        });
        return new CallToolResult { Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured };
    });

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> operation)
    {
        try { return await operation(); }
        catch (Exception error) when (error is AutomationException or NativeApiException
                                     or InvalidProtocolBufferException or InvalidJsonException)
        {
            string code = error switch
            {
                AutomationException automation => automation.Code,
                NativeApiException native => "native_status_" + native.Status,
                _ => "invalid_document"
            };
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { code, message = error.Message }) }]
            };
        }
    }
}

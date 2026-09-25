using System.ComponentModel;
using System.Text;
using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

public interface IExecutionCheckpoint
{
    Task WaitAsync(string reached, CancellationToken token);
}

/// <summary>The handshake each attached KiCad instance returned when it was attached. Reading it never
/// contacts KiCad, so a preview stays offline (CN-1 I10; decision n39ac0ccc5c9270f2).</summary>
public interface IAttachedHandshakes
{
    KiCad.Automation.Protocol.AutomationSession? Handshake(string instanceId);
}

/// <summary>Adapts a registry lookup of the handshake recorded at attach. Every read is a copy.</summary>
public sealed class AttachedHandshakes(Func<string, KiCad.Automation.Protocol.AutomationSession?> recorded) : IAttachedHandshakes
{
    public KiCad.Automation.Protocol.AutomationSession? Handshake(string instanceId) => recorded(instanceId)?.Clone();
}

[McpServerToolType]
public sealed class RecoveryTools
{
    private readonly InstanceRegistry? registry;
    private readonly IExecutionCheckpoint? checkpoint;
    private readonly IAttachedHandshakes? handshakes;
    public RecoveryTools() { }
    public RecoveryTools(InstanceRegistry registry, IExecutionCheckpoint? checkpoint = null, IAttachedHandshakes? handshakes = null)
    { this.registry = registry; this.checkpoint = checkpoint; this.handshakes = handshakes; }

    [McpServerTool(Name = "kicad_design_sync_apply", ReadOnly = false),
     Description("Apply a prepared XML synchronization candidate to one explicit native KiCad instance, then publish the native files and XML through the revision-safe journal. Requires the exact recovery token, absolute XML destination and caller-stable operation ID. Replays an identical completed operation from its receipt; stale instances, changed files, conflicts and interrupted phases remain explicit recovery results. Native connectivity validation, dirty-session preservation and retained prior XML are returned separately. This is not an automatic AI request and does not run from Save/Decline.")]
    public async Task<CallToolResult> ApplySynchronizationTool(string instanceId, string recoveryPath, string designPath,
        string expectedRevisionToken, string operationId, CancellationToken cancellationToken)
    {
        var result = await ApplySynchronization(instanceId, recoveryPath, designPath, expectedRevisionToken, operationId,
            cancellationToken, checkpoint is null ? null : checkpoint.WaitAsync);
        if (!(result.IsError ?? false) && checkpoint is not null) await checkpoint.WaitAsync("completed", cancellationToken);
        return result;
    }

    [McpServerTool(Name = "kicad_design_candidate_commit", ReadOnly = false),
     Description("Store a complete validated schematic design XML as the desired recovery candidate for one explicit attached instance. Requires the exact recovery token and candidate SHA256. The candidate must retain the native hierarchy root identity and declared library dependencies. This only advances the local recovery desired bytes; it does not write the native schematic, mutate KiCad, advance the baseline, or certify connectivity. Inspect with kicad_design_sync_plan before any separately authorized native application.")]
    public CallToolResult CommitCandidate(string instanceId, string recoveryPath, string expectedRevisionToken,
        string candidateXml, string expectedCandidateSha256, string operationId, CancellationToken cancellationToken) => Execute(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (store, saved) = Read(instanceId, recoveryPath);
        if (saved.RevisionToken != expectedRevisionToken)
            throw new AutomationException("design_recovery_changed", "Recovery changed; inspect the latest desired and native snapshots before committing a candidate.");
        if (!Guid.TryParseExact(operationId, "D", out var operation) || operation == Guid.Empty || operation.ToString("D") != operationId)
            throw new AutomationException("invalid_operation_id", "Provide a canonical operation UUID for this candidate commit.");
        if (candidateXml is null) throw new AutomationException("invalid_candidate_xml", "Supply the complete typed schematic design XML.");
        byte[] bytes;
        try { bytes = new System.Text.UTF8Encoding(false, true).GetBytes(candidateXml); }
        catch (Exception error) when (error is EncoderFallbackException or ArgumentException)
        { throw new AutomationException("invalid_candidate_encoding", error.Message); }
        string actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        if (actual != expectedCandidateSha256)
            throw new AutomationException("candidate_hash_mismatch", "The candidate bytes do not match the observed candidate hash.");
        var candidate = SchematicDesignXml.Read(candidateXml, saved.State.KnowledgeLibraries);
        if (!candidate.Schematic.Document.Equals(saved.State.Baseline.Schematic.Document))
            throw new AutomationException("candidate_root_mismatch", "A candidate cannot change the native schematic hierarchy root through recovery state.");
        cancellationToken.ThrowIfCancellationRequested();
        var next = saved.State with { DesiredFileBytes = bytes };
        var committed = store.Save(next, expectedRevisionToken);
        var data = JsonSerializer.SerializeToElement(new
        {
            instanceId, operationId = operation, recoveryRevisionToken = committed.RevisionToken,
            candidateSha256 = actual, desiredCandidateStored = true, designFileWritten = false,
            nativeMutationCommitted = false, baselineAdvanced = false, liveMutationAuthorized = false
        });
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });
    // Advertised through ApplySynchronizationTool. Interruption across creation,
    // removal and placement still needs native evidence (p7e712f1bb764e327).
    internal async Task<CallToolResult> ApplySynchronization(string instanceId, string recoveryPath,
        string designPath, string expectedRevisionToken, string operationId, CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? checkpoint = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (store, _) = Read(instanceId, recoveryPath);
            if (!Guid.TryParseExact(operationId, "D", out var id) || id == Guid.Empty || id.ToString("D") != operationId)
                throw new AutomationException("invalid_operation_id", "Provide a caller-stable canonical operation UUID.");
            if (registry is null)
                throw new AutomationException("instance_registry_unavailable", "The synchronization executor requires the service instance registry.");
            var result = await SchematicSynchronizationExecutor.ApplyAsync(store, registry.Client(instanceId),
                designPath, expectedRevisionToken, id, cancellationToken, checkpoint);
            var data = JsonSerializer.SerializeToElement(new
            {
                instanceId, recoveryRevisionToken = result.RecoveryRevisionToken,
                designFileSha256 = result.DesignFileSha256, nativeRevision = result.NativeRevision,
                nativeMutationCommitted = result.NativeMutationCommitted, nativeFilesSaved = result.NativeFilesSaved,
                synchronizationCommitted = result.SynchronizationCommitted,
                operationId = result.PublicationId, replayed = result.Replayed, liveStateStillCurrent = false,
                previousXmlPath = result.PreviousXmlPath,
                retainedXml = result.RetainedXml,
                nativeReceipt = result.NativeReceipt is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(result.NativeReceipt))
            });
            return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
        }
        catch (Exception error) when (error is AutomationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            var data = JsonSerializer.SerializeToElement(new { instanceId,
                errorCode = error is AutomationException known ? known.Code : "design_sync_io", errorMessage = error.Message });
            return new() { IsError = true, Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
        }
    }

    [McpServerTool(Name = "kicad_design_sync_plan", ReadOnly = true),
     Description("Prepare one full typed design candidate by reconciling saved XML intent, hierarchy, native properties and captured pin connectivity. Requires an explicit saved instance/recovery path and current recovery revision token. Content-verified retained XML can recover exact deleted owners restored by native undo; newer instructions remain current. Conflicts or unresolved property projection return no partial candidate. Returns candidate XML, restored identities and proposed native operations, with coverage gaps and a flag requiring native connectivity validation. When the saved XML adds connections (between pins already drawn, or to parts it also adds) and the instance's handshake recorded at attach advertises connection realization, connectionRealizationRequired is true, candidateDesignXml is null (the drawing is measured and made in KiCad during apply) and connectionIntent summarizes the planned connections: each net with its scope and global name, each sheet's island with its label text, members, roles, stub and join needs, the sheet ports, the number of native pin groups apply must prove and the symbols it creates. This is preparation only: it does not contact KiCad, prove live freshness, write design files, apply edits or advance synchronization.")]
    public Task<CallToolResult> PlanSynchronization(string instanceId, string recoveryPath, string expectedRevisionToken,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (store, saved) = Read(instanceId, recoveryPath);
        if (saved.RevisionToken != expectedRevisionToken)
            throw new AutomationException("design_recovery_changed", "Recovery changed; inspect the current record before planning.");
        // The preview classifies exactly as apply and the automatic worker do, from the handshake the
        // recorded instance gave at attach. No handshake (for example no attachment) keeps today's plan.
        var plan = await SchematicSynchronizationPlanner.PlanWithHistoryAsync(store, saved,
            handshakes?.Handshake(saved.State.InstanceId.ToString("D")), cancellationToken);
        var data = JsonSerializer.SerializeToElement(new
        {
            instanceId = saved.State.InstanceId, recoveryRevisionToken = saved.RevisionToken,
            savedNativeRevision = saved.State.NativeRevision, trackingComplete = saved.State.TrackingComplete,
            liveMutationAuthorized = false, designFileWritten = false, baselineAdvanced = false,
            canPrepare = plan.CanPrepare, candidateDesignXml = plan.CandidateXml,
            nativeOperationsJson = plan.NativeOperations.Select(operation => SchematicJson.Formatter.Format(operation)).ToArray(),
            nativeConnectivityValidationRequired = plan.NativeConnectivityValidationRequired,
            // CN-1 §9.5: a realization plan has no publishable XML; its preview is the planned connections.
            connectionRealizationRequired = plan.NativeConnectionRealizationRequired,
            connectionIntent = plan.Connections is { } connections ? SchematicConnectionIntentBuilder.Summary(connections) : null,
            hierarchyConflicts = plan.Hierarchy?.Conflicts.Select(x => new { x.InstancePath, x.Reason }),
            electricalConflicts = plan.Electrical?.Conflicts, netChanges = plan.Electrical?.NetChanges,
            restoredSymbolOccurrences = plan.Electrical?.RestoredSymbolOccurrences, restoredNetIds = plan.Electrical?.RestoredNetIds,
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

    [McpServerTool(Name = "kicad_design_owner_history_inspect", ReadOnly = true),
     Description("Inspect content-verified historical ownership choices for this exact saved instance and recovery revision. Returns a snapshot token and the component/unit mappings each eligible history would restore. Does not contact KiCad, write XML or authorize a live mutation.")]
    public Task<CallToolResult> InspectOwnershipHistory(string instanceId, string recoveryPath, string expectedRevisionToken,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        var (store, saved) = ReadAtRevision(instanceId, recoveryPath, expectedRevisionToken);
        var inspection = await SchematicOwnershipResolutionService.InspectAsync(store, saved, cancellationToken);
        return OwnershipResult(saved, inspection, false);
    });

    [McpServerTool(Name = "kicad_design_owner_history_resolve", Destructive = false),
     Description("Record one explicit verified history mapping against an inspected snapshot token and current recovery revision. This changes only the saved recovery choice, not XML or native editors. The normal synchronization planner revalidates the choice and content before use.")]
    public Task<CallToolResult> ResolveOwnershipHistory(string instanceId, string recoveryPath, string expectedRevisionToken,
        string expectedSnapshotToken, string historyOperationId, CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        var (store, saved) = ReadAtRevision(instanceId, recoveryPath, expectedRevisionToken);
        if (!Guid.TryParseExact(historyOperationId, "D", out var operation) || operation == Guid.Empty)
            throw new AutomationException("invalid_history_choice", "Select one exact history operation from inspection.");
        var written = await SchematicOwnershipResolutionService.ResolveAsync(store, saved, expectedSnapshotToken, operation, cancellationToken);
        return OwnershipResult(written, null, true);
    });

    [McpServerTool(Name = "kicad_design_owner_history_clear", Destructive = false),
     Description("Clear a recorded history choice using the current recovery revision. Does not change XML, native editors or retained history. Pending synchronization must be recovered before its choice can change.")]
    public CallToolResult ClearOwnershipHistory(string instanceId, string recoveryPath, string expectedRevisionToken,
        CancellationToken cancellationToken) => Execute(() =>
    {
        var (store, saved) = ReadAtRevision(instanceId, recoveryPath, expectedRevisionToken);
        return OwnershipResult(SchematicOwnershipResolutionService.Clear(store, saved, cancellationToken), null, true);
    });

    private static CallToolResult OwnershipResult(StoredDesignRecovery saved, SchematicOwnershipChoices? inspection, bool written)
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            instanceId = saved.State.InstanceId, recoveryRevisionToken = saved.RevisionToken,
            snapshotToken = inspection?.SnapshotToken, choices = inspection?.Choices, selection = saved.State.OwnershipResolution,
            recoveryChoiceWritten = written, designFileWritten = false, nativeMutationAuthorized = false
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    }

    private static (DesignRecoveryStore Store, StoredDesignRecovery Saved) ReadAtRevision(string instanceId, string path, string token)
    {
        var value = Read(instanceId, path);
        if (value.Saved.RevisionToken != token)
            throw new AutomationException("design_recovery_changed", "Recovery changed; inspect the current record before choosing.");
        return value;
    }

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
            pendingSaveOperationId = saved.State.PendingNativeSave?.OperationId,
            pendingLayout = saved.State.PendingLayout is not { } layout ? null : new
            {
                operationId = layout.OperationId, designPath = layout.DesignPath,
                requestedRecoveryRevisionToken = layout.RequestedRecoveryRevisionToken,
                expectedSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(layout.ExpectedFileBytes)),
                plannedDesignSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(layout.PlannedDesignFileBytes)),
                geometryResolved = false, liveFilesVerified = false
            },
            pendingPublication = saved.State.PendingPublication is not { } publication ? null : new
            {
                operationId = publication.OperationId, phase = publication.Phase.ToString(),
                designPath = publication.DesignPath, stagedPath = publication.StagedPath, previousPath = publication.PreviousPath,
                expectedSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(publication.ExpectedFileBytes)),
                candidateSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(publication.CandidateFileBytes)),
                liveFilesVerified = false
            },
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

    private static async Task<CallToolResult> ExecuteAsync(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            var data = JsonSerializer.SerializeToElement(new
                { errorCode = error is AutomationException a ? a.Code : "design_recovery_io", errorMessage = error.Message });
            return new() { IsError = true, Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
        }
    }
}

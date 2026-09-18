using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class SchematicViewTools(InstanceRegistry registry)
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

    [McpServerTool(Name = "kicad_schematic_electrical_state", ReadOnly = true),
     Description("Capture the loaded schematic hierarchy and native scalar-net memberships together at one stable revision, for an explicit attached instance and loaded sheet target. Does not navigate the human view, save, annotate or repair the design. requestJson is ReadSchematicElectricalState protobuf JSON with document and optional expectedRevision. Net names are labels, not persistent net identities; membership uses exact sheet paths and UUIDs. Returns typed hierarchy XML, native revision, memberships and explicit coverage limitations. This is not full reconstruction, simulation or mutation admission.")]
    public Task<CallToolResult> ReadElectricalState(string instanceId, string requestJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        var request = SchematicJson.Parser.Parse<ReadSchematicElectricalState>(requestJson);
        var observed = await registry.Client(instanceId).InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(request, cancellationToken);
        var data = JsonSerializer.SerializeToElement(new { instanceId,
            state = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(observed)),
            xml = SchematicDataXml.Write(observed.Hierarchy.Data), liveMutationAuthorized = false });
        return new CallToolResult { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_design_recovery_refresh"),
     Description("Capture and persist the current native hierarchy into an existing recovery record for an explicitly attached instance. Requires the absolute recovery path and exact expected recovery revision token. Preserves baseline, desired XML bytes and libraries. Rejects unconfirmed pending mutations; use recovery observation and reconciliation first. Changed native data/revision invalidates saved hierarchy choices. A concurrent recovery write rejects the refresh. Does not modify KiCad, write design XML, advance the baseline, or imply complete tracking or automatic synchronization.")]
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
        var refreshed = await DesignRecoveryInspector.RefreshAsync(store, registry.Client(instanceId), expectedRevisionToken, cancellationToken);
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

    [McpServerTool(Name = "kicad_design_propose_initial_layout", ReadOnly = true),
     Description("Propose positions for coordinate-free additions of known, unconnected parts in an explicitly attached schematic. Requires the absolute recovery path and exact recovery token, nanometre grid/clearance/inset, and one usable-page region with title-block/artwork reservations per affected physical screen UUID. Uses revision-bound native body/visible-field measurements, combines repeated sheet envelopes and preserves existing or explicitly positioned symbols. Returns desired XML plus scoped visual-refinement context; no-space returns issues without a partial candidate. Does not write XML/recovery, edit KiCad, generate wiring or certify readability. Review and publish the proposed XML through normal revision-safe synchronization, then verify native connectivity and rendered presentation.")]
    public Task<CallToolResult> ProposeInitialLayout(string instanceId, string recoveryPath,
        string expectedRevisionToken, long gridNm, long clearanceNm, long pageInsetNm,
        SchematicLayoutRegion[] regions, string userInstructions, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(recoveryPath))
            throw new AutomationException("invalid_recovery_path", "Specify the absolute saved design recovery path.");
        var store = new DesignRecoveryStore(recoveryPath);
        var saved = store.Read() ?? throw new AutomationException("missing_design_recovery", "No saved design recovery state exists.");
        if (saved.State.InstanceId.ToString("D") != instanceId)
            throw new AutomationException("recovery_instance_mismatch", "The recovery record belongs to a different instance.");
        if (saved.RevisionToken != expectedRevisionToken)
            throw new AutomationException("design_recovery_changed", "Reload the recovery record before proposing layout.");
        var proposal = await SchematicInitialLayoutPlanner.ProposeAsync(registry.Client(instanceId), saved.State,
            new(gridNm, clearanceNm, pageInsetNm), regions, userInstructions, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (store.Read()?.RevisionToken != saved.RevisionToken)
            throw new AutomationException("design_recovery_changed", "The recovery record changed during layout measurement; request a fresh proposal.");
        var data = JsonSerializer.SerializeToElement(new { instanceId, recoveryRevisionToken = saved.RevisionToken,
            nativeRevision = saved.State.NativeRevision, canPropose = proposal.CanPropose, desiredXml = proposal.DesiredXml,
            layout = proposal.Layout, refinement = proposal.Refinement, limitations = proposal.Limitations,
            requiresVisualReview = true, requiresNativeConnectivityValidation = true,
            nativeDocumentEdited = false, designFileWritten = false, recoveryWritten = false });
        return new CallToolResult { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_design_propose_field_layout", ReadOnly = true),
     Description("Propose field-only layout changes in a synchronized design recovery snapshot. Requires its absolute path, exact instance ID and recovery token. Each change addresses an exact model symbol occurrence, standard field slot or custom-field index plus expected name, nanometre position, cardinal text rotation and native alignment. Preserves text, visibility, circuit meaning, symbol placement and locks. Repeated sheet instances share field geometry; conflicting alias proposals fail. Returns desired XML only, without writing files or editing KiCad. Apply through normal revision-safe synchronization and then verify native connectivity and rendered readability; the proposal itself is not a readability certificate.")]
    public Task<CallToolResult> ProposeFieldLayout(string instanceId, string recoveryPath,
        string expectedRevisionToken, SchematicFieldPlacement[] changes, CancellationToken cancellationToken) => Execute(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(recoveryPath))
            throw new AutomationException("invalid_recovery_path", "Specify the absolute saved design recovery path.");
        var store = new DesignRecoveryStore(recoveryPath);
        var saved = store.Read() ?? throw new AutomationException("missing_design_recovery", "No saved design recovery state exists.");
        if (saved.State.InstanceId.ToString("D") != instanceId)
            throw new AutomationException("recovery_instance_mismatch", "The recovery record belongs to a different instance.");
        if (saved.RevisionToken != expectedRevisionToken)
            throw new AutomationException("design_recovery_changed", "Reload the recovery record before proposing field layout.");
        var proposal = SchematicFieldLayoutPlanner.Propose(saved.State, changes, cancellationToken);
        if (store.Read()?.RevisionToken != saved.RevisionToken)
            throw new AutomationException("design_recovery_changed", "The recovery record changed during layout preparation.");
        cancellationToken.ThrowIfCancellationRequested();
        var data = JsonSerializer.SerializeToElement(new { instanceId, recoveryRevisionToken = saved.RevisionToken,
            nativeRevision = saved.State.NativeRevision, desiredXml = proposal.DesiredXml, affectedSymbols = proposal.AffectedSymbols,
            nativeOperationCount = proposal.Operations.Count, requiresNativeConnectivityValidation = true, requiresRenderedReview = true,
            nativeDocumentEdited = false, designFileWritten = false, recoveryWritten = false });
        return Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data });
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

    [McpServerTool(Name = "kicad_schematic_render_views", ReadOnly = true),
     Description("Render independent regions and artwork layers of an explicitly targeted loaded schematic sheet without navigating or changing the human canvas. The target may be a non-visible or repeated sheet instance. requestJson is RenderSchematicViews protobuf JSON: document plus one to four views, each with a distinct key, region (position/size in nm), widthPixels and heightPixels (64..2048), and optional nativeLayers. Empty layers select all supported artwork, including drawing-sheet frame/title block and page boundary; the response includes their layer IDs. Maximum total 8388608 pixels. Image blocks follow view order and share one supported snapshot. Printing-style images omit transient overlays. PCB/3D views, complete serializer coverage and complete revision tracking remain unfinished.")]
    public Task<CallToolResult> RenderViews(string instanceId, string requestJson, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var request = SchematicJson.Parser.Parse<RenderSchematicViews>(requestJson);
            var views = await registry.Client(instanceId).InvokeAsync<RenderSchematicViews, SchematicViewSet>(
                request, cancellationToken);
            var content = new List<ContentBlock>();
            foreach (var view in views.Views)
            {
                content.Add(ImageContentBlock.FromBytes(view.Preview.Png.Memory, "image/png"));
                view.Preview.Png = ByteString.Empty;
            }
            var structured = JsonSerializer.SerializeToElement(new
            {
                instanceId,
                trackingComplete = views.Snapshot.TrackingComplete && views.Views.All(v => v.Preview.TrackingComplete),
                viewSet = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(views)),
                xml = SchematicDataXml.Write(views.Snapshot.Data)
            });
            content.Add(new TextContentBlock { Text = structured.GetRawText() });
            return new CallToolResult { Content = content, StructuredContent = structured };
        });

    [McpServerTool(Name = "kicad_schematic_observe", ReadOnly = true),
     Description("Capture the current sheet PNG, viewport and supported schematic objects together in one native dispatch. Returns explicit target, typed XML and incomplete coverage/tracking flags. Rejects supported-content changes across rendering. This is a preliminary current-canvas observation, not complete revision-safe editing, independent offscreen views or whole-hierarchy capture.")]
    public Task<CallToolResult> Observe(string instanceId, string documentJson, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var observation = await registry.Client(instanceId).InvokeAsync<CaptureSchematicObservation, SchematicObservation>(
                new() { Document = ParseDocument(documentJson) }, cancellationToken);
            var image = ImageContentBlock.FromBytes(observation.Preview.Png.Memory, "image/png");
            observation.Preview.Png = ByteString.Empty;
            var structured = JsonSerializer.SerializeToElement(new
            {
                instanceId,
                trackingComplete = observation.Snapshot.TrackingComplete && observation.Preview.TrackingComplete,
                observation = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(observation)),
                xml = SchematicDataXml.Write(observation.Snapshot.Data)
            });
            return new CallToolResult
            {
                Content = [image, new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured
            };
        });

    [McpServerTool(Name = "kicad_schematic_data", ReadOnly = true),
     Description("Read supported schematic objects and metadata together from one native dispatch for an explicit currently displayed sheet. Includes typed editable XML, object UUIDs, exact sheet-instance target, native revision and explicit unsupported-state entries. This is one sheet, not the complete hierarchy. Incomplete revision tracking is reported and must not be used as safe mutation admission. No files are written and no design is changed.")]
    public Task<CallToolResult> ReadData(string instanceId, string documentJson, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var snapshot = await registry.Client(instanceId).InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(
                new() { Document = ParseDocument(documentJson) }, cancellationToken);
            var structured = JsonSerializer.SerializeToElement(new
            {
                instanceId, trackingComplete = snapshot.TrackingComplete,
                snapshot = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(snapshot)),
                xml = SchematicDataXml.Write(snapshot.Data)
            });
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured
            };
        });

    [McpServerTool(Name = "kicad_schematic_hierarchy_data", ReadOnly = true),
     Description("Read every loaded schematic sheet instance and its supported objects/settings in one native dispatch, without navigating the visible editor. Supply an explicit loaded sheet target; it need not be currently displayed. Repeated instances retain separate paths and shared screen identities. Returns typed XML and explicit unsupported-state entries; incomplete tracking and serializer coverage are not full reconstruction or mutation-admission proof. No files or designs are changed.")]
    public Task<CallToolResult> ReadHierarchyData(string instanceId, string documentJson, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var snapshot = await registry.Client(instanceId).InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(
                new() { Document = ParseDocument(documentJson) }, cancellationToken);
            var structured = JsonSerializer.SerializeToElement(new
            {
                instanceId, trackingComplete = snapshot.TrackingComplete,
                snapshot = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(snapshot)),
                xml = SchematicDataXml.Write(snapshot.Data)
            });
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured
            };
        });

    [McpServerTool(Name = "kicad_schematic_save_state", ReadOnly = true),
     Description("Read native unsaved-schematic flags and modified sheet-instance IDs for an explicit loaded schematic target, without changing the visible sheet or saving. Covers loaded schematic screens, not project-settings persistence or external file changes. Revision tracking is incomplete; a clean flag is not permission to discard or close a session.")]
    public Task<CallToolResult> ReadSaveState(string instanceId, string documentJson, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var state = await registry.Client(instanceId).InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
                new() { Document = ParseDocument(documentJson) }, cancellationToken);
            var structured = JsonSerializer.SerializeToElement(new
            {
                instanceId, state = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(state))
            });
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured
            };
        });

    [McpServerTool(Name = "kicad_schematic_metadata", ReadOnly = true),
     Description("Read typed metadata for an explicit currently displayed schematic sheet: screen identity, page/title settings, project text variables, bus aliases and embedded-file records. Returns explicit unrepresented-state entries and incomplete revision tracking. This is a partial read-only snapshot, not a complete reconstruction or restore operation; embedded-file restoration is not implemented.")]
    public Task<CallToolResult> ReadMetadata(string instanceId, string documentJson, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var snapshot = await registry.Client(instanceId).InvokeAsync<ReadSchematicMetadata, SchematicMetadataSnapshot>(
                new() { Document = ParseDocument(documentJson) }, cancellationToken);
            var structured = JsonSerializer.SerializeToElement(new
            {
                instanceId, trackingComplete = snapshot.TrackingComplete,
                snapshot = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(snapshot))
            });
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured
            };
        });

    [McpServerTool(Name = "kicad_schematic_operation_inspect", ReadOnly = true),
     Description("Inspect a native schematic batch operation by its exact document target, document epoch and operation ID. Optionally supply expectedRequestJson containing the original ApplySchematicItemBatch to verify its complete payload; require expectedRequestVerified before using a found receipt for interrupted-edit recovery. Returns the recorded result or rejection, not-found, or an indeterminate outcome. Does not apply or retry edits. Receipts belong to the current native document session; this is not a complete revision-safe mutation API.")]
    public Task<CallToolResult> InspectOperation(string instanceId, string documentJson,
        string documentEpoch, string operationId, CancellationToken cancellationToken, string? expectedRequestJson = null) =>
        Execute(async () =>
        {
            var receipt = await registry.Client(instanceId).InvokeAsync<InspectSchematicOperation, SchematicOperationReceipt>(
                new() { Document = ParseDocument(documentJson), DocumentEpoch = documentEpoch, OperationId = operationId,
                    ExpectedRequest = expectedRequestJson is null ? null : SchematicJson.Parser.Parse<ApplySchematicItemBatch>(expectedRequestJson) },
                cancellationToken);
            var structured = JsonSerializer.SerializeToElement(new
            {
                instanceId,
                receipt = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(receipt))
            });
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured
            };
        });

    [McpServerTool(Name = "kicad_schematic_check_presentation", ReadOnly = true),
     Description("Check current-sheet native geometry for page overflow, image bounds, font-size policy, reference visibility and more than two unrelated signal crossings. Supply explicit minimum/maximum text height in millimetres. Returns object-linked findings and repair targets. Partial coverage: full glyph clipping/occlusion, independent sheet contexts and complete revision tracking remain unfinished; missing wire bindings are reported. Never interpret this partial report as a complete verification pass.")]
    public Task<CallToolResult> CheckPresentation(string instanceId, string documentJson,
        decimal minimumTextHeightMm, decimal maximumTextHeightMm, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var check = await NativePresentationChecks.CheckAsync(registry.Client(instanceId), ParseDocument(documentJson),
                new(minimumTextHeightMm, maximumTextHeightMm), cancellationToken);
            var result = JsonSerializer.SerializeToElement(new { instanceId, check },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Converters = { new JsonStringEnumConverter() } });
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = result.GetRawText() }], StructuredContent = result
            };
        });

    [McpServerTool(Name = "kicad_schematic_preview", ReadOnly = true),
     Description("Capture the currently displayed schematic sheet as PNG with native viewport metadata. Supply the exact document JSON returned by document discovery, including sheet_path. Does not navigate. The journal cursor is diagnostic-only: trackingComplete=false is NOT a safe mutation precondition or complete synchronized observation.")]
    public Task<CallToolResult> Preview(string instanceId, string documentJson, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var preview = await registry.Client(instanceId).InvokeAsync<CaptureSchematicPreview, SchematicPreview>(
                new() { Document = ParseDocument(documentJson) }, cancellationToken);
            var image = ImageContentBlock.FromBytes(preview.Png.Memory, "image/png");
            preview.Png = ByteString.Empty;
            string metadata = JsonFormatter.Default.Format(preview);
            // Include false/default values explicitly: lack of complete tracking
            // must not disappear through protobuf's default-value omission.
            var structured = JsonSerializer.SerializeToElement(new
            {
                instanceId,
                trackingComplete = preview.TrackingComplete,
                native = JsonSerializer.Deserialize<JsonElement>(metadata)
            });
            return new CallToolResult
            {
                Content = [image, new TextContentBlock { Text = structured.GetRawText() }],
                StructuredContent = structured
            };
        });

    [McpServerTool(Name = "kicad_schematic_sheet_activate"),
     Description("Display an exact schematic sheet instance in the attached native editor. Supply its native document JSON including the full sheet-instance path. Navigation preserves design undo history; rejects pending edits and invalid paths. It changes the visible sheet, not the design.")]
    public Task<CallToolResult> Activate(string instanceId, string documentJson, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var document = await registry.Client(instanceId).InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(
                new() { Document = ParseDocument(documentJson) }, cancellationToken);
            return new CallToolResult { Content = [new TextContentBlock { Text = JsonFormatter.Default.Format(document) }] };
        });

    private static DocumentSpecifier ParseDocument(string json)
    {
        var document = JsonParser.Default.Parse<DocumentSpecifier>(json);
        if (document.SheetPath is null || document.SheetPath.Path.Count == 0)
            throw new AutomationException("invalid_document", "An explicit sheet-instance path is required.");
        return document;
    }

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

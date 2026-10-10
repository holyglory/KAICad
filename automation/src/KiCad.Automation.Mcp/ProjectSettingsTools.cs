using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Kiapi.Common.Commands;
using Kiapi.Common.Project;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>Revision-paired read-only observations of project-wide settings.</summary>
[McpServerToolType]
public sealed class ProjectSettingsTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_project_net_classes", ReadOnly = true),
     Description("Read the native project netclass definitions at one exact lifecycle checkpoint. documentJson identifies an open schematic or PCB in the project; expectedStateJson must be the matching kicad_document_state observation with project-settings coverage and process epoch. The common native API is project-wide, so the returned definitions are paired with the requested document only as an identity and revision guard. This never changes design data. If the connected native peer cannot expose project netclasses, the result says supported=false instead of fabricating a snapshot."),
     KiCadCapability("project-settings", "compiled-mcp plus native-common-api", "explicit project document, process epoch, lifecycle revision and project-settings coverage"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NetClassesRoundTripThroughXmlAndNativeEdits")]
    public Task<CallToolResult> NetClasses(string instanceId, string documentJson,
        string expectedStateJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_project_net_classes_target");
        DocumentStateTools.ValidateTarget(document);
        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson,
            "invalid_project_net_classes_state");
        ValidateExpected(document, expected);

        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("project_settings_instance_changed",
                "The expected project state belongs to another native process epoch.");

        DocumentLifecycleState before = await ReadState(client, document, cancellationToken);
        ValidateObserved(document, before);
        if (!before.Equals(expected))
            throw new AutomationException("project_settings_state_changed",
                "The project changed before netclasses were read; inspect a fresh lifecycle state.");

        NetClassesResponse response;
        try
        {
            response = await client.InvokeAsync<GetNetClasses, NetClassesResponse>(new(), cancellationToken);
        }
        catch (NativeApiException error) when (error.Status == (int)Kiapi.Common.ApiStatusCode.AsUnhandled
            || error.Status == (int)Kiapi.Common.ApiStatusCode.AsUnimplemented)
        {
            // Older/headless peers can have the lifecycle endpoint without the common project
            // settings command. Preserve the identity guard and say exactly what is unavailable.
            DocumentLifecycleState unsupportedAfter = await ReadState(client, document, cancellationToken);
            ValidateObserved(document, unsupportedAfter);
            if (!unsupportedAfter.Equals(expected))
                throw new AutomationException("project_settings_state_changed",
                    "The project changed while checking project netclass support; inspect a fresh lifecycle state.");
            return Data(new
            {
                instanceId,
                processEpoch = session.Epoch,
                document,
                query = "project-net-classes",
                beforeState = SchematicJson.Formatter.Format(before),
                afterState = SchematicJson.Formatter.Format(unsupportedAfter),
                supported = false,
                unsupportedReason = "The connected native peer does not provide the project netclass command.",
                response = (JsonElement?)null,
                nativeQuery = false,
                boardMutation = false
            });
        }

        if (response.NetClasses.Count > 4096)
            throw new AutomationException("project_net_classes_oversized",
                "The native project returned more than 4096 netclass definitions; narrow the project before retrying.");

        var names = response.NetClasses.Select(netClass => netClass.Name).ToArray();
        if (names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new AutomationException("invalid_project_net_classes_response",
                "The native project returned unnamed or duplicate netclass definitions.");

        DocumentLifecycleState after = await ReadState(client, document, cancellationToken);
        ValidateObserved(document, after);
        if (!after.Equals(expected))
            throw new AutomationException("project_settings_state_changed",
                "The project changed while netclasses were read; discard the result.");

        using JsonDocument parsed = JsonDocument.Parse(SchematicJson.Formatter.Format(response));
        return Data(new
        {
            instanceId,
            processEpoch = session.Epoch,
            document,
            query = "project-net-classes",
            beforeState = SchematicJson.Formatter.Format(before),
            afterState = SchematicJson.Formatter.Format(after),
            supported = true,
            unsupportedReason = (string?)null,
            response = (JsonElement?)parsed.RootElement.Clone(),
            netClassesSha256 = NetClassesDigest(response),
            nativeQuery = true,
            boardMutation = false
        });
    });

    [McpServerTool(Name = "kicad_project_net_classes_set", ReadOnly = false),
     Description("Replace or merge the declared project netclasses through the existing checked schematic net-settings transaction. documentJson must identify the explicit schematic root, and expectedStateJson must be the complete kicad_schematic_checked_state result from that same root. netClassesJson is protobuf JSON for NetClassesResponse; mergeMode is MMM_MERGE or MMM_REPLACE; operationId is a new canonical UUID; expectedNetClassesSha256 must match the declared netclasses in the supplied checked state. The wrapper preserves every other project net-setting, uses the native checked receipt/replay path, and leaves saving explicit through kicad_document_save. PCB-only documents are refused because no equivalent checked project-settings transaction exists.") ,
     KiCadCapability("project-settings", "checked-native-schematic-net-settings", "explicit schematic root, complete checked state, netclass digest, operation ID"),
     KiCadVerification(KiCadVerificationLevel.InProcess, "ProjectSettingsToolTests.SetNetClassesRequiresSchematicCheckedState")]
    public Task<CallToolResult> SetNetClasses(string instanceId, string documentJson,
        string expectedStateJson, string netClassesJson, string mergeMode, Guid operationId,
        string expectedNetClassesSha256, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operationId == Guid.Empty)
            throw new AutomationException("invalid_operation_id", "Provide a nonempty operation UUID for a project netclass mutation.");

        DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson,
            "invalid_project_net_classes_target");
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypeSchematic || document.SheetPath?.Path.Count != 1)
            throw new AutomationException("project_net_classes_schematic_required",
                "Project netclass mutation requires the explicit schematic root; PCB-only project settings have no checked mutation path.");

        CheckedSchematicState expected = Parse<CheckedSchematicState>(expectedStateJson,
            "invalid_project_net_classes_state");
        CheckedSchematicContract.ValidateObservation(expected, document, expected.State.ProcessEpoch);
        SchematicMetadata[] metadataMatches = expected.Electrical.Hierarchy.Data.Instances
            .Select(screen => screen.Metadata)
            .Where(value => value is not null && value.Document.Equals(document))
            .Select(value => value!)
            .ToArray();
        if (metadataMatches.Length != 1)
            throw new AutomationException("invalid_project_net_classes_state",
                "The complete checked state must contain exactly one metadata record for the explicit schematic root.");
        SchematicMetadata metadata = metadataMatches[0];
        if (metadata.NetSettings is null)
            throw new AutomationException("project_net_classes_unavailable",
                "The checked state does not include declared project net settings.");

        NetClassesResponse requested = Parse<NetClassesResponse>(netClassesJson,
            "invalid_project_net_classes_request");
        ValidateNetClasses(requested, "request");
        if (mergeMode is not ("MMM_MERGE" or "MMM_REPLACE"))
            throw new AutomationException("invalid_project_net_classes_merge_mode",
                "mergeMode must be MMM_MERGE or MMM_REPLACE.");
        if (!IsNetClassDigest(expectedNetClassesSha256))
            throw new AutomationException("invalid_project_net_classes_revision",
                "expectedNetClassesSha256 must be a lowercase SHA-256 digest.");
        string observedDigest = NetClassesDigest(ToNetClasses(metadata.NetSettings));
        if (!string.Equals(observedDigest, expectedNetClassesSha256, StringComparison.Ordinal))
            throw new AutomationException("stale_project_net_classes",
                "The supplied netclass digest does not match the complete checked schematic observation.");

        SchematicNetSettings next = MergeNetClasses(metadata.NetSettings, requested, mergeMode);
        CallToolResult native = await new SchematicMetadataMutationTools(registry).SetNetSettings(
            instanceId, documentJson, expectedStateJson, SchematicJson.Formatter.Format(next),
            operationId.ToString("D"), cancellationToken);
        return WrapNetClassesResult(native, instanceId, operationId, expectedNetClassesSha256, requested);
    });

    private static void ValidateNetClasses(NetClassesResponse response, string name)
    {
        if (response is null || response.NetClasses.Count > 4096
            || response.NetClasses.Any(netClass => string.IsNullOrWhiteSpace(netClass.Name)
                || netClass.Name.Contains('\0') || netClass.Type != NetClassType.NctExplicit
                || netClass.Constituents.Count != 0)
            || response.NetClasses.Select(netClass => netClass.Name).Distinct(StringComparer.Ordinal).Count()
                != response.NetClasses.Count)
            throw new AutomationException("invalid_project_net_classes_response",
                $"Native checked netclass mutation returned an invalid {name} list.");
    }

    private static SchematicNetSettings MergeNetClasses(SchematicNetSettings baseline,
        NetClassesResponse requested, string mergeMode)
    {
        var result = baseline.Clone();
        var classes = result.Classes.ToDictionary(netClass => netClass.Name, netClass => netClass.Clone(), StringComparer.Ordinal);
        if (mergeMode == "MMM_REPLACE")
            classes.Clear();

        foreach (var netClass in requested.NetClasses)
        {
            if (netClass.Name == "Default")
                result.DefaultClass = netClass.Clone();
            else
                classes[netClass.Name] = netClass.Clone();
        }

        result.Classes.Clear();
        result.Classes.Add(classes.Values.OrderBy(netClass => netClass.Name, StringComparer.Ordinal));
        return result;
    }

    private static NetClassesResponse ToNetClasses(SchematicNetSettings settings)
    {
        var result = new NetClassesResponse();
        result.NetClasses.Add(settings.DefaultClass.Clone());
        result.NetClasses.Add(settings.Classes.Select(netClass => netClass.Clone()));
        return result;
    }

    private static bool IsNetClassDigest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

    private static string NetClassesDigest(NetClassesResponse response)
    {
        var bytes = new List<byte>();
        foreach (var netClass in response.NetClasses.OrderBy(netClass => netClass.Name, StringComparer.Ordinal))
        {
            byte[] name = Encoding.UTF8.GetBytes(netClass.Name);
            byte[] serialized = netClass.ToByteArray();
            bytes.AddRange(Encoding.UTF8.GetBytes(name.Length.ToString())); bytes.Add((byte)'\n');
            bytes.AddRange(name);
            bytes.AddRange(Encoding.UTF8.GetBytes(serialized.Length.ToString())); bytes.Add((byte)'\n');
            bytes.AddRange(serialized);
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }

    private static CallToolResult WrapNetClassesResult(CallToolResult native, string instanceId,
        Guid operationId, string expectedDigest, NetClassesResponse requested)
    {
        if (native.StructuredContent is not JsonElement data)
            return native;

        JsonElement? receipt = data.TryGetProperty("receipt", out var receiptValue)
            ? receiptValue.Clone() : null;
        string? errorCode = data.TryGetProperty("errorCode", out var codeValue)
            ? codeValue.GetString() : null;
        string? errorMessage = data.TryGetProperty("errorMessage", out var messageValue)
            ? messageValue.GetString() : null;
        bool submitted = data.TryGetProperty("mutationSubmitted", out var submittedValue)
            && submittedValue.ValueKind == JsonValueKind.True;
        bool replayed = data.TryGetProperty("replayed", out var replayedValue)
            && replayedValue.ValueKind == JsonValueKind.True;
        var wrapped = JsonSerializer.SerializeToElement(new
        {
            instanceId,
            operationId = operationId.ToString("D"),
            expectedNetClassesSha256 = expectedDigest,
            requestedNetClassCount = requested.NetClasses.Count,
            mutationSubmitted = submitted,
            replayed,
            designFilesSaved = false,
            receipt,
            errorCode,
            errorMessage,
            nativeResult = data
        });
        return new CallToolResult
        {
            IsError = native.IsError,
            StructuredContent = wrapped,
            Content = [new TextContentBlock { Text = wrapped.GetRawText() }]
        };
    }

    [McpServerTool(Name = "kicad_project_text_variables", ReadOnly = true),
     Description("Read the native project text-variable map at one exact lifecycle checkpoint. project identifies the loaded project; document and expectedStateJson identify an open schematic or PCB in that project and its project-settings revision coverage. The native common API is project-wide, so the result is paired with the requested document only as an identity and revision guard. This never changes design data and reports supported=false when the connected peer lacks the command."),
     KiCadCapability("project-settings", "compiled-mcp plus native-common-api", "explicit project, lifecycle document, process epoch, revision and project-settings coverage"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> TextVariables(string instanceId, NativeProjectInput project,
        NativeDocumentInput document, string expectedStateJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentSpecifier projectDocument = ProjectDocument(project);
        DocumentSpecifier target = NativeDocumentInput.ToNative(document);
        ValidateProjectContext(projectDocument, target, "invalid_project_text_variables_target");
        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson,
            "invalid_project_text_variables_state");
        ValidateTextVariableExpected(target, expected);

        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("project_settings_instance_changed",
                "The expected project state belongs to another native process epoch.");

        DocumentLifecycleState before = await ReadState(client, target, cancellationToken);
        ValidateTextVariableObserved(target, before);
        if (!before.Equals(expected))
            throw new AutomationException("project_settings_state_changed",
                "The project changed before text variables were read; inspect a fresh lifecycle state.");

        Kiapi.Common.Project.TextVariables response;
        try
        {
            response = await client.InvokeAsync<GetTextVariables, Kiapi.Common.Project.TextVariables>(
                new() { Document = projectDocument }, cancellationToken);
        }
        catch (NativeApiException error) when (error.Status == (int)Kiapi.Common.ApiStatusCode.AsUnhandled
            || error.Status == (int)Kiapi.Common.ApiStatusCode.AsUnimplemented)
        {
            DocumentLifecycleState unsupportedAfter = await ReadState(client, target, cancellationToken);
            ValidateTextVariableObserved(target, unsupportedAfter);
            if (!unsupportedAfter.Equals(expected))
                throw new AutomationException("project_settings_state_changed",
                    "The project changed while checking text-variable support; inspect a fresh lifecycle state.");
            return Data(new
            {
                instanceId, processEpoch = session.Epoch, project = projectDocument, document = target,
                beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(unsupportedAfter),
                supported = false, unsupportedReason = "The connected native peer does not provide project text variables.",
                variables = (Dictionary<string, string>?)null, nativeQuery = false, mutation = false
            });
        }

        if (response.Variables.Count > 4096 || response.Variables.Keys.Any(string.IsNullOrWhiteSpace)
            || response.Variables.Keys.Distinct(StringComparer.Ordinal).Count() != response.Variables.Count)
            throw new AutomationException("invalid_project_text_variables_response",
                "The native project returned unnamed, duplicate or oversized text variables.");

        DocumentLifecycleState after = await ReadState(client, target, cancellationToken);
        ValidateTextVariableObserved(target, after);
        if (!after.Equals(expected))
            throw new AutomationException("project_settings_state_changed",
                "The project changed while text variables were read; discard the result.");

        return Data(new
        {
            instanceId, processEpoch = session.Epoch, project = projectDocument, document = target,
            beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
            supported = true, unsupportedReason = (string?)null,
            variables = response.Variables.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            nativeQuery = true, mutation = false
        });
    });

    [McpServerTool(Name = "kicad_project_text_variables_set", ReadOnly = false),
     Description("Set project text variables with a native map-digest admission, process-owned operation receipt and save/readback outcome. The legacy SetTextVariables command remains unchanged; this typed path refuses stale maps, mismatched projects and operation reuse."),
     KiCadCapability("project-settings", "checked-native-common-api", "explicit project, lifecycle document, expected revision, operation ID and variable map"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> SetTextVariables(string instanceId, NativeProjectInput project,
        NativeDocumentInput document, string expectedStateJson, Dictionary<string, string> variables,
        string mergeMode, Guid operationId, CancellationToken cancellationToken,
        string? expectedVariablesSha256 = null) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operationId == Guid.Empty)
            throw new AutomationException("invalid_operation_id", "Provide a nonempty operation UUID for a project text-variable mutation.");
        DocumentSpecifier projectDocument = ProjectDocument(project);
        DocumentSpecifier target = NativeDocumentInput.ToNative(document);
        ValidateProjectContext(projectDocument, target, "invalid_project_text_variables_target");
        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson,
            "invalid_project_text_variables_state");
        ValidateTextVariableExpected(target, expected);
        if (mergeMode is not ("MMM_MERGE" or "MMM_REPLACE"))
            throw new AutomationException("invalid_project_text_variables_merge_mode",
                "mergeMode must be MMM_MERGE or MMM_REPLACE.");
        if (variables is null)
            throw new AutomationException("invalid_project_text_variables_request",
                "A text-variable map is required, even when it is intentionally empty.");
        if (variables.Count > 4096 || variables.Keys.Any(string.IsNullOrWhiteSpace)
            || variables.Keys.Distinct(StringComparer.Ordinal).Count() != variables.Count
            || variables.Values.Any(value => value is null || value.Contains('\0')))
            throw new AutomationException("invalid_project_text_variables_request",
                "The requested text-variable map is unnamed, duplicated, oversized or contains a NUL value.");
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("project_settings_instance_changed",
                "The expected project state belongs to another native process epoch.");
        DocumentLifecycleState before = await ReadState(client, target, cancellationToken);
        ValidateTextVariableObserved(target, before);
        // The native checked command admits the write against the complete
        // project-variable digest. A changed lifecycle checkpoint is allowed
        // here so an operation ID can replay its original receipt after the
        // successful write advanced the document observation.
        Kiapi.Common.Project.TextVariables current = await client.InvokeAsync<GetTextVariables,
            Kiapi.Common.Project.TextVariables>(new() { Document = projectDocument }, cancellationToken);
        ValidateTextVariableMap(current, "current");
        string expectedVariablesDigest = string.IsNullOrWhiteSpace(expectedVariablesSha256)
            ? Digest(current.Variables) : expectedVariablesSha256;
        if (expectedVariablesDigest.Length != 64 || !expectedVariablesDigest.All(char.IsAsciiHexDigitLower))
            throw new AutomationException("invalid_project_text_variables_revision",
                "expectedVariablesSha256 must be a lowercase SHA-256 digest when supplied.");
        SetTextVariablesCheckedResponse response;
        try
        {
            response = await client.InvokeAsync<SetTextVariablesChecked, SetTextVariablesCheckedResponse>(new()
            {
                Project = projectDocument,
                Document = target,
                OperationId = operationId.ToString("D"),
                ProcessEpoch = session.Epoch,
                ExpectedVariablesSha256 = expectedVariablesDigest,
                Variables = new Kiapi.Common.Project.TextVariables { Variables = { variables } },
                MergeMode = mergeMode == "MMM_REPLACE" ? MapMergeMode.MmmReplace : MapMergeMode.MmmMerge
            }, cancellationToken);
        }
        catch (NativeApiException error) when (error.Status == (int)Kiapi.Common.ApiStatusCode.AsUnhandled
            || error.Status == (int)Kiapi.Common.ApiStatusCode.AsUnimplemented)
        {
            throw new AutomationException("project_text_variables_mutation_unavailable",
                "The connected native peer does not provide checked project text-variable mutation.");
        }
        string normalizedOperationId = operationId.ToString("D");
        ValidateTextVariableMutationResponse(response, projectDocument, target, normalizedOperationId,
            session.Epoch, expectedVariablesDigest, variables, mergeMode);
        var structured = new
        {
            instanceId, processEpoch = session.Epoch, project = projectDocument, document = target,
            operationId = response.OperationId, committed = response.Committed, replayed = response.Replayed,
            rollbackCompleted = response.RollbackCompleted, beforeVariablesSha256 = response.BeforeVariablesSha256,
            afterVariablesSha256 = response.AfterVariablesSha256,
            // Keep the owner admission digest under an explicit name as well as
            // the historical beforeVariablesSha256 field. Callers can retain
            // the exact digest used by the native project owner when replaying
            // an operation after a lost MCP reply.
            ownerDigest = response.BeforeVariablesSha256,
            expectedVariablesSha256 = expectedVariablesDigest,
            revisionTracked = response.RevisionTracked, completeLifecycle = response.CompleteLifecycle,
            beforeVariables = response.BeforeVariables.Variables.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            afterVariables = response.AfterVariables.Variables.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            errorCode = response.ErrorCode, errorMessage = response.ErrorMessage
        };
        return Data(structured, !response.Committed);
    });

    private static void ValidateTextVariableMutationResponse(SetTextVariablesCheckedResponse response,
        DocumentSpecifier project, DocumentSpecifier document, string operationId, string processEpoch,
        string expectedVariablesDigest, IReadOnlyDictionary<string, string> requestedVariables, string mergeMode)
    {
        if (response.Project is null || !response.Project.Equals(project)
            || response.Document is null || !response.Document.Equals(document)
            || response.OperationId != operationId || response.ProcessEpoch != processEpoch
            || response.BeforeVariablesSha256 != expectedVariablesDigest)
            throw new AutomationException("invalid_project_text_variables_response",
                "Native checked text-variable mutation did not preserve its project, document, operation or owner digest.");

        ValidateTextVariableMap(response.BeforeVariables, "beforeVariables");
        ValidateTextVariableMap(response.AfterVariables, "afterVariables");
        string beforeDigest = Digest(response.BeforeVariables.Variables);
        string afterDigest = Digest(response.AfterVariables.Variables);
        if (beforeDigest != response.BeforeVariablesSha256 || !IsDigest(response.AfterVariablesSha256)
            || afterDigest != response.AfterVariablesSha256)
            throw new AutomationException("invalid_project_text_variables_response",
                "Native checked text-variable mutation returned a map whose owner digest does not match its contents.");

        var requestedAfter = mergeMode == "MMM_REPLACE"
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : response.BeforeVariables.Variables.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var pair in requestedVariables)
            requestedAfter[pair.Key] = pair.Value;
        if (response.Committed && (requestedAfter.Count != response.AfterVariables.Variables.Count
                || requestedAfter.Any(pair => !response.AfterVariables.Variables.TryGetValue(pair.Key, out string? value)
                    || !string.Equals(pair.Value, value, StringComparison.Ordinal))))
            throw new AutomationException("invalid_project_text_variables_response",
                "Native checked text-variable commit did not return the complete requested merge or replacement map.");

        // A committed receipt is the only success state. Rollback is a
        // failure-path fact; reporting both committed and rollbackCompleted
        // would claim two incompatible outcomes. Replays must preserve the
        // original successful outcome and cannot become a new failure.
        if (response.Committed && response.RollbackCompleted)
            throw new AutomationException("invalid_project_text_variables_response",
                "Native checked text-variable mutation reported both commit and rollback completion.");
        if (response.Committed && (!string.IsNullOrWhiteSpace(response.ErrorCode)
                || !string.IsNullOrWhiteSpace(response.ErrorMessage)))
            throw new AutomationException("invalid_project_text_variables_response",
                "Native checked text-variable mutation reported an error alongside a committed receipt.");
        if (response.Replayed && !response.Committed)
            throw new AutomationException("invalid_project_text_variables_response",
                "Native checked text-variable replay did not preserve its committed receipt.");
        if (!response.Committed && string.IsNullOrWhiteSpace(response.ErrorCode))
            throw new AutomationException("invalid_project_text_variables_response",
                "Native checked text-variable failure did not include an error code.");
    }

    private static void ValidateTextVariableMap(Kiapi.Common.Project.TextVariables variables, string name)
    {
        if (variables is null || variables.Variables.Count > 4096
            || variables.Variables.Keys.Any(string.IsNullOrWhiteSpace)
            || variables.Variables.Values.Any(value => value is null || value.Contains('\0')))
            throw new AutomationException("invalid_project_text_variables_response",
                $"Native checked text-variable mutation returned an invalid {name} map.");
    }

    private static bool IsDigest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

    private static string Digest(IReadOnlyDictionary<string, string> variables)
    {
        var bytes = new List<byte>();
        foreach (var pair in variables.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            byte[] key = Encoding.UTF8.GetBytes(pair.Key); byte[] value = Encoding.UTF8.GetBytes(pair.Value);
            bytes.AddRange(Encoding.UTF8.GetBytes(key.Length.ToString())); bytes.Add((byte)'\n'); bytes.AddRange(key);
            bytes.AddRange(Encoding.UTF8.GetBytes(value.Length.ToString())); bytes.Add((byte)'\n'); bytes.AddRange(value);
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }

    private static DocumentSpecifier ProjectDocument(NativeProjectInput project)
    {
        if (project is null || string.IsNullOrWhiteSpace(project.Name) || string.IsNullOrWhiteSpace(project.Path)
            || !Path.IsPathFullyQualified(project.Path))
            throw new AutomationException("invalid_project_text_variables_target",
                "project requires a nonempty name and absolute path.");
        return new DocumentSpecifier
        {
            Type = DocumentType.DoctypeProject,
            Project = new() { Name = project.Name, Path = project.Path }
        };
    }

    private static void ValidateProjectContext(DocumentSpecifier project, DocumentSpecifier document, string code)
    {
        if (document.Project is null || !string.Equals(document.Project.Name, project.Project.Name, StringComparison.Ordinal)
            || !string.Equals(Path.GetFullPath(document.Project.Path), Path.GetFullPath(project.Project.Path), StringComparison.Ordinal)
            || document.Type is not (DocumentType.DoctypeSchematic or DocumentType.DoctypePcb))
            throw new AutomationException(code,
                "The lifecycle document must be a schematic or PCB in the explicitly named project.");
    }

    private static void ValidateTextVariableExpected(DocumentSpecifier document, DocumentLifecycleState expected)
    {
        if (expected.Document is null || !expected.Document.Equals(document) || string.IsNullOrWhiteSpace(expected.ProcessEpoch)
            || expected.Scope != Scope(document) || !expected.ProjectSettingsIncluded)
            throw new AutomationException("invalid_project_text_variables_state",
                "expectedStateJson must identify the same project document, native process epoch and project-settings coverage.");
    }

    private static void ValidateTextVariableObserved(DocumentSpecifier document, DocumentLifecycleState state)
    {
        if (state.Document is null || !state.Document.Equals(document) || state.Scope != Scope(document)
            || string.IsNullOrWhiteSpace(state.ProcessEpoch) || !state.ProjectSettingsIncluded
            || state.StateSha256.Length != 64 || !state.StateSha256.All(char.IsAsciiHexDigitLower)
            || !Guid.TryParseExact(state.NativeIdentity, "D", out _)
            || state.Revision is null || !Guid.TryParseExact(state.Revision.Epoch, "D", out _))
            throw new AutomationException("invalid_project_text_variables_state",
                "Native lifecycle state did not provide the requested document, revision, digest or project-settings coverage.");
    }

    private static async Task<DocumentLifecycleState> ReadState(NativeClient client,
        DocumentSpecifier document, CancellationToken cancellationToken) =>
        await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);

    private static void ValidateExpected(DocumentSpecifier document, DocumentLifecycleState expected)
    {
        if (expected.Document is null || !expected.Document.Equals(document) || string.IsNullOrWhiteSpace(expected.ProcessEpoch)
            || expected.Scope != Scope(document))
            throw new AutomationException("invalid_project_net_classes_state",
                "expectedStateJson must identify the same project document and native process epoch.");
        if (!expected.ProjectSettingsIncluded)
            throw new AutomationException("project_settings_unavailable",
                "The lifecycle checkpoint does not include project-settings coverage; no netclass snapshot was claimed.");
    }

    private static void ValidateObserved(DocumentSpecifier document, DocumentLifecycleState state)
    {
        if (state.Document is null || !state.Document.Equals(document) || state.Scope != Scope(document)
            || string.IsNullOrWhiteSpace(state.ProcessEpoch) || !state.ProjectSettingsIncluded
            || state.StateSha256.Length != 64 || !state.StateSha256.All(char.IsAsciiHexDigitLower)
            || !Guid.TryParseExact(state.NativeIdentity, "D", out _)
            || state.Revision is null || !Guid.TryParseExact(state.Revision.Epoch, "D", out _))
            throw new AutomationException("invalid_project_net_classes_state",
                "Native lifecycle state did not provide the requested document, revision, digest or project-settings coverage.");
    }

    private static DocumentLifecycleScope Scope(DocumentSpecifier document) =>
        document.Type == DocumentType.DoctypeSchematic
            ? DocumentLifecycleScope.DlsSchematicHierarchy
            : DocumentLifecycleScope.DlsPcb;

    private static T Parse<T>(string json, string code)
        where T : class, IMessage<T>, new()
    {
        try
        {
            return SchematicJson.Parser.Parse<T>(json);
        }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException
            or InvalidOperationException or ArgumentException)
        {
            throw new AutomationException(code, "The supplied protobuf JSON is invalid: " + error.Message);
        }
    }

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException or ArgumentException)
        {
            string code = error is AutomationException known
                ? known.Code
                : error is NativeApiException native ? "native_status_" + native.Status
                : error is NngException ? "native_transport_unavailable" : "project_net_classes_failed";
            JsonElement structured = JsonSerializer.SerializeToElement(new { errorCode = code, errorMessage = error.Message });
            return new CallToolResult
            {
                IsError = true,
                StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }]
            };
        }
    }

    private static CallToolResult Data(object value, bool error = false)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new CallToolResult
        {
            IsError = error,
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }]
        };
    }
}

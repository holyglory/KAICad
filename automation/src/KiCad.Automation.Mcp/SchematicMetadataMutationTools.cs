using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Typed project-level schematic metadata edits.  Bus aliases and the variant
/// description registry are owned by the project/root document even though
/// every loaded screen carries a copy in its observation.  These tools turn
/// the complete checked observation into the existing native batch operation;
/// they do not introduce another transaction or revision protocol.
/// </summary>
[McpServerToolType]
public sealed class SchematicMetadataMutationTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_schematic_replace_bus_aliases", ReadOnly = false,
        UseStructuredContent = true),
     Description("Replace the complete project bus-alias catalogue in one checked native undo step. Supply the explicit schematic root documentJson, the complete structuredContent returned by kicad_schematic_checked_state, aliases as typed name/member entries, and a new canonical operationId. Names and members must be nonempty, trimmed, NUL-free and alias names unique. The native batch is admitted only while process epoch, native identity, complete hierarchy, revision and file baselines still match. Reuse the identical arguments and operationId after a lost reply; the retained receipt is returned without a second edit. The edit is not saved to disk; use kicad_document_save after observing the result. A stale or rejected request does not change the project.")]
    public Task<CallToolResult> ReplaceBusAliases(string instanceId, string documentJson,
        string expectedStateJson, SchematicBusAliasInput[] aliases, string operationId,
        CancellationToken cancellationToken) => Execute(instanceId, documentJson, expectedStateJson,
            operationId, MetadataMutation.BusAliases, aliases, null, null, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_replace_variant_registry", ReadOnly = false,
        UseStructuredContent = true),
     Description("Replace the complete project variant-description registry in one checked native undo step. Supply the explicit schematic root documentJson, the complete structuredContent returned by kicad_schematic_checked_state, entries as typed name/description records, and a new canonical operationId. Variant names must be nonempty, trimmed and NUL-free; descriptions may be empty but cannot contain NUL, and names must be unique. The native batch is admitted only while process epoch, native identity, complete hierarchy, revision and file baselines still match. Reuse the identical arguments and operationId after a lost reply; the retained receipt is returned without a second edit. The edit is not saved to disk; use kicad_document_save after observing the result. This updates project descriptions only; per-symbol and per-sheet variant declarations remain separate checked data.")]
    public Task<CallToolResult> ReplaceVariantRegistry(string instanceId, string documentJson,
        string expectedStateJson, SchematicVariantDescriptionInput[] entries, string operationId,
        CancellationToken cancellationToken) => Execute(instanceId, documentJson, expectedStateJson,
            operationId, MetadataMutation.VariantRegistry, null, entries, null, cancellationToken);

    [McpServerTool(Name = "kicad_schematic_set_net_settings", ReadOnly = false,
        UseStructuredContent = true),
     Description("Replace the complete project schematic net settings through the checked native batch path. netSettingsJson is a typed SchematicNetSettings payload with an explicit Default class; documentJson and expectedStateJson bind the explicit root revision, and operationId provides receipt replay. Map keys must be nonempty, NUL-free and bounded. The edit is undoable and remains unsaved until kicad_document_save.")]
    public Task<CallToolResult> SetNetSettings(string instanceId, string documentJson,
        string expectedStateJson, string netSettingsJson, string operationId,
        CancellationToken cancellationToken) => Execute(instanceId, documentJson, expectedStateJson,
            operationId, MetadataMutation.NetSettings, null, null, netSettingsJson, cancellationToken);

    public sealed record SchematicBusAliasInput(string Name, string[] Members);
    public sealed record SchematicVariantDescriptionInput(string Name, string Description);

    private enum MetadataMutation
    {
        BusAliases,
        VariantRegistry,
        NetSettings
    }

    private sealed record ExpectedObservation(DocumentLifecycleState State,
        SchematicElectricalState Electrical);

    private async Task<CallToolResult> Execute(string instanceId, string documentJson,
        string expectedStateJson, string operationId, MetadataMutation mutation,
        SchematicBusAliasInput[]? aliases, SchematicVariantDescriptionInput[]? entries,
        string? netSettingsJson,
        CancellationToken cancellationToken)
    {
        bool submitted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_checked_document");
            ValidateRootDocument(document);
            ExpectedObservation expected = ParseExpectedObservation(expectedStateJson, document);
            ValidateIdentity(operationId, "operationId");
            SchematicItemOperation operation = BuildOperation(mutation, aliases, entries, netSettingsJson);

            NativeClient client = registry.Client(instanceId);
            AutomationSession session = await client.HandshakeAsync(cancellationToken);
            ValidateProcessEpoch(expected.State, session.Epoch);
            CheckedSchematicBatch request = BuildRequest(expected.State, operation, operationId,
                mutation);
            CheckedSchematicTools.ValidateRequest(request, session.Epoch);

            // Receipt inspection precedes a live capture. A completed request has
            // advanced the revision, so recapturing before checking the receipt would
            // turn a safe exact replay into an avoidable stale-state failure.
            CheckedSchematicBatchReceipt prior = await client.InvokeAsync<ReadCheckedSchematicBatchReceipt,
                CheckedSchematicBatchReceipt>(new()
                {
                    Document = request.Batch.Document.Clone(), ProcessEpoch = session.Epoch,
                    OperationId = operationId, ExpectedRequest = request.Clone()
                }, cancellationToken);
            CheckedSchematicTools.ValidateResult(request, prior, inspect: true);
            if (prior.Status != CheckedSchematicBatchStatus.CsbsNotFound)
                return Reply(instanceId, operationId, mutation, prior, submitted: false, replayed: true);

            CheckedSchematicState observed = await client.InvokeAsync<ReadCheckedSchematicState,
                CheckedSchematicState>(new() { Document = document.Clone(), ProcessEpoch = session.Epoch },
                cancellationToken);
            CheckedSchematicContract.ValidateObservation(observed, document, session.Epoch);
            if (!observed.State.Equals(expected.State) || !observed.Electrical.Equals(expected.Electrical))
                throw new AutomationException("stale_document_state",
                    "The schematic changed after the checked observation; inspect a fresh checked state and retry.");

            // The caller's complete expected state remains the admission guard. The
            // native checked controller will reject a change between this read and the
            // commit atomically; no unguarded fallback exists.
            cancellationToken.ThrowIfCancellationRequested();
            submitted = true;
            CheckedSchematicBatchReceipt receipt = await client.InvokeAsync<CheckedSchematicBatch,
                CheckedSchematicBatchReceipt>(request, cancellationToken);
            CheckedSchematicTools.ValidateResult(request, receipt, inspect: false);
            return Reply(instanceId, operationId, mutation, receipt, submitted, replayed: false);
        }
        catch (Exception error) when (error is AutomationException or NativeApiException
            or InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException
            or JsonException or NngException)
        {
            string code = error switch
            {
                AutomationException known => known.Code,
                NativeApiException native => "native_status_" + native.Status,
                NngException transport => "transport_status_" + transport.ErrorCode,
                _ => "invalid_schematic_metadata"
            };
            JsonElement data = JsonSerializer.SerializeToElement(new
            {
                instanceId, operationId, metadata = mutation.ToString(),
                mutationSubmitted = submitted,
                outcome = submitted ? "not_confirmed" : "not_submitted",
                errorCode = code, errorMessage = error.Message
            });
            return new()
            {
                IsError = true, StructuredContent = data,
                Content = [new TextContentBlock { Text = data.GetRawText() }]
            };
        }
    }

    private static SchematicItemOperation BuildOperation(MetadataMutation mutation,
        SchematicBusAliasInput[]? aliases, SchematicVariantDescriptionInput[]? entries,
        string? netSettingsJson)
    {
        return mutation switch
        {
            MetadataMutation.BusAliases => new SchematicItemOperation
            {
                TargetDocument = null,
                ReplaceBusAliases = BuildBusAliases(aliases)
            },
            MetadataMutation.VariantRegistry => new SchematicItemOperation
            {
                TargetDocument = null,
                ReplaceVariantRegistry = BuildVariantRegistry(entries)
            },
            MetadataMutation.NetSettings => new SchematicItemOperation
            {
                TargetDocument = null,
                SetNetSettings = BuildNetSettings(netSettingsJson)
            },
            _ => throw new AutomationException("invalid_schematic_metadata", "The metadata operation is unsupported.")
        };
    }

    private const int MaximumNetSettingsMapEntries = 4096;

    private static SchematicNetSettings BuildNetSettings(string? json)
    {
        if (json is null)
            throw new AutomationException("invalid_net_settings", "netSettingsJson is required.");

        SchematicNetSettings value = Parse<SchematicNetSettings>(json, "invalid_net_settings");
        if (value.DefaultClass is null || value.DefaultClass.Name != "Default")
            throw new AutomationException("invalid_net_settings",
                "Net settings require an explicit Default class.");

        ValidateNetSettingsMap(value.LabelAssignments, "labelAssignments");
        ValidateNetSettingsMap(value.NetColors, "netColors");
        ValidateNetSettingsMap(value.ChainNetclasses, "chainNetclasses");
        try
        {
            SchematicNetSettingsState.Validate(value);
        }
        catch (AutomationException error)
        {
            throw new AutomationException("invalid_net_settings", error.Message);
        }
        return value;
    }

    private static void ValidateNetSettingsMap<T>(IReadOnlyDictionary<string, T> map, string name)
    {
        if (map is null || map.Count > MaximumNetSettingsMapEntries
            || map.Keys.Any(key => string.IsNullOrEmpty(key) || key.Contains('\0')))
            throw new AutomationException("invalid_net_settings",
                $"{name} must contain at most {MaximumNetSettingsMapEntries} entries with nonempty, NUL-free keys.");
    }

    private static SchematicBusAliasState BuildBusAliases(SchematicBusAliasInput[]? aliases)
    {
        if (aliases is null)
            throw new AutomationException("invalid_bus_aliases", "aliases is required and may be empty only when clearing the catalogue.");
        var state = new SchematicBusAliasState();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (SchematicBusAliasInput? input in aliases)
        {
            if (input is null)
                throw new AutomationException("invalid_bus_aliases", "Every alias entry is required.");
            if (!ValidText(input.Name) || !names.Add(input.Name))
                throw new AutomationException("invalid_bus_aliases",
                    "Bus alias names must be unique, trimmed, nonempty and NUL-free.");
            if (input.Members is null || input.Members.Any(member => !ValidText(member)))
                throw new AutomationException("invalid_bus_aliases",
                    "Bus alias members must be trimmed, nonempty and NUL-free.");
            var alias = new SchematicBusAlias { Name = input.Name };
            alias.Members.Add(input.Members);
            // Run the same serialization validation used by native hierarchy planning
            // so a typed call has identical acceptance rules to a checked batch.
            SchematicDataXml.Write(alias);
            state.Aliases.Add(alias);
        }
        return state;
    }

    private static SchematicVariantRegistryState BuildVariantRegistry(
        SchematicVariantDescriptionInput[]? entries)
    {
        if (entries is null)
            throw new AutomationException("invalid_variant_registry", "entries is required and may be empty only when clearing the registry.");
        var state = new SchematicVariantRegistryState();
        foreach (SchematicVariantDescriptionInput? input in entries)
        {
            if (input is null || !ValidText(input.Name) || input.Description is null
                || input.Description.Contains('\0'))
                throw new AutomationException("invalid_variant_registry",
                    "Variant names must be trimmed, nonempty and NUL-free; descriptions may be empty but cannot contain NUL.");
            if (state.Descriptions.ContainsKey(input.Name))
                throw new AutomationException("invalid_variant_registry", "Variant names must be unique.");
            state.Descriptions.Add(input.Name, input.Description);
        }
        return state;
    }

    private static bool ValidText(string? value) => !string.IsNullOrEmpty(value)
        && value == value.Trim() && !value.Contains('\0');

    private static CheckedSchematicBatch BuildRequest(DocumentLifecycleState state,
        SchematicItemOperation operation, string operationId, MetadataMutation mutation)
    {
        var batch = new ApplySchematicItemBatch
        {
            Document = state.Document.Clone(),
            DocumentEpoch = state.Revision.Epoch,
            ExpectedRevision = state.Revision.Clone(),
            OperationId = operationId,
            Description = mutation switch
            {
                MetadataMutation.BusAliases => "Replace checked schematic bus aliases",
                MetadataMutation.VariantRegistry => "Replace checked schematic variant registry",
                MetadataMutation.NetSettings => "Set checked schematic net settings",
                _ => "Apply checked schematic metadata mutation"
            }
        };
        batch.Operations.Add(operation);
        return new CheckedSchematicBatch { Batch = batch, ExpectedState = state.Clone() };
    }

    private static ExpectedObservation ParseExpectedObservation(string json,
        DocumentSpecifier document)
    {
        try
        {
            using JsonDocument root = JsonDocument.Parse(json);
            if (!root.RootElement.TryGetProperty("state", out _))
                throw new AutomationException("invalid_checked_state",
                    "expectedStateJson must be the complete structuredContent returned by kicad_schematic_checked_state.");
            CheckedSchematicState result = Parse<CheckedSchematicState>(json, "invalid_checked_state");
            if (result.State is null || result.Electrical?.Hierarchy?.Data?.Document is null)
                throw new AutomationException("invalid_checked_state",
                    "The checked state must include lifecycle and electrical hierarchy observations.");
            CheckedSchematicContract.ValidateObservation(result, document, result.State.ProcessEpoch);
            return new(result.State.Clone(), result.Electrical.Clone());
        }
        catch (AutomationException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidProtocolBufferException
            or InvalidJsonException or InvalidOperationException)
        {
            throw new AutomationException("invalid_checked_state",
                "expectedStateJson is not a complete checked schematic observation: " + error.Message);
        }
    }

    private static void ValidateRootDocument(DocumentSpecifier document)
    {
        DocumentStateTools.ValidateTarget(document);
        if ((int)document.Type != 1 || document.SheetPath?.Path.Count != 1)
            throw new AutomationException("invalid_checked_document",
                "Select the explicit schematic root for project metadata replacement.");
    }

    private static void ValidateProcessEpoch(DocumentLifecycleState state, string processEpoch)
    {
        if (state.ProcessEpoch != processEpoch)
            throw new AutomationException("stale_process_epoch",
                "The checked state belongs to another native process epoch.");
    }

    private static void ValidateIdentity(string value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty
            || id.ToString("D") != value)
            throw new AutomationException("invalid_identity",
                $"{name} must be a canonical nonempty UUID.");
    }

    private static T Parse<T>(string json, string code) where T : IMessage<T>, new()
    {
        try { return SchematicJson.Parser.Parse<T>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException
            or InvalidOperationException or JsonException)
        { throw new AutomationException(code, "The request is not valid protobuf JSON: " + error.Message); }
    }

    private static CallToolResult Reply(string instanceId, string operationId,
        MetadataMutation mutation, CheckedSchematicBatchReceipt receipt, bool submitted,
        bool replayed)
    {
        string json = SchematicJson.Formatter.Format(receipt);
        using JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement data = JsonSerializer.SerializeToElement(new
        {
            instanceId, operationId, metadata = mutation.ToString(),
            receipt = parsed.RootElement.Clone(), mutationSubmitted = submitted,
            replayed, designFilesSaved = false
        });
        return new()
        {
            IsError = !replayed && receipt.Status != CheckedSchematicBatchStatus.CsbsCompleted,
            StructuredContent = data,
            Content = [new TextContentBlock { Text = data.GetRawText() }]
        };
    }
}

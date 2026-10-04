using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
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
            nativeQuery = true,
            boardMutation = false
        });
    });

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

    private static CallToolResult Data(object value)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new CallToolResult
        {
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }]
        };
    }
}

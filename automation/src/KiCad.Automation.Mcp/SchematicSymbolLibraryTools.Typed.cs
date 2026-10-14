using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Structured admission for the existing symbol-library handlers.
///
/// The legacy handlers intentionally remain string based because their
/// protobuf-JSON contracts are already used by native journeys.  This
/// boundary gives an MCP client a real document identity and protobuf
/// objects, then serializes the validated objects at the last hand-off to the
/// established execution and receipt authority.
/// </summary>
public sealed partial class SchematicSymbolLibraryTools
{
    [McpServerTool(Name = "kicad_symbol_library_mutation_typed", ReadOnly = false),
     Description("Read or mutate one project symbol-library entry from a structured input. input.document is an explicit project-bound symbol document (library nickname and entry name), input.definition is the SchematicSymbol protobuf object, and input.expectedState is the complete checked direct-file state for writeChecked. write, writeChecked and delete require a canonical operation UUID; native calls retain the legacy receipt/replay and post-readback guarantees. The existing JSON-string symbol-library tools remain available and are not weakened."),
     KiCadCapability("symbol-library", "compiled-mcp plus-native-symbol-library-adapter", "typed project/library document, SchematicSymbol protobuf definition, operation UUID and checked snapshot state"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "SymbolLibraryTypedMcpProcessTests.TypedSymbolLibrarySchemaAndMalformedAdmissionAreProvenOverStdio")]
    public async Task<CallToolResult> MutateTyped(string instanceId, SymbolLibraryTypedInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Execute(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                SymbolLibraryTypedPayload payload = input?.ToPayload()
                    ?? throw InvalidTyped("input is required.");

                switch (payload.Action)
                {
                    case SymbolLibraryTypedAction.Validate:
                        return Validate(payload.LibraryNickname!,
                            SchematicJson.Formatter.Format(payload.Definition!), cancellationToken);

                    case SymbolLibraryTypedAction.Read:
                        return await Read(instanceId, payload.ProcessEpoch!, payload.LibraryNickname!,
                            payload.SymbolName!, cancellationToken);

                    case SymbolLibraryTypedAction.Write:
                        return await Write(instanceId, payload.ProcessEpoch!, payload.LibraryNickname!,
                            SchematicJson.Formatter.Format(payload.Definition!), payload.OperationId!,
                            cancellationToken, payload.Overwrite, payload.ExpectedModifyHash ?? "",
                            payload.PinDisplay is null ? "" : SchematicJson.Formatter.Format(payload.PinDisplay));

                    case SymbolLibraryTypedAction.WriteChecked:
                        return await WriteChecked(instanceId, payload.ProcessEpoch!,
                            SchematicJson.Formatter.Format(payload.Document),
                            SchematicJson.Formatter.Format(payload.Definition!), payload.OperationId!,
                            payload.ExpectedSnapshotSha256!, cancellationToken, payload.Overwrite,
                            payload.PinDisplay is null ? "" : SchematicJson.Formatter.Format(payload.PinDisplay),
                            SchematicJson.Formatter.Format(payload.ExpectedState!));

                    case SymbolLibraryTypedAction.Delete:
                        return await Delete(instanceId, payload.ProcessEpoch!, payload.LibraryNickname!,
                            payload.SymbolName!, payload.OperationId!, cancellationToken,
                            payload.ExpectedModifyHash ?? "");

                    default:
                        throw InvalidTyped("action must be validate, read, write, writeChecked or delete.");
                }
            });
        }
        catch (JsonException error)
        {
            return Data(new
            {
                errorCode = "invalid_symbol_library_typed_input",
                errorMessage = error.Message,
                nativeContacted = false,
                nativeMutation = false
            }, true);
        }
    }

    private static AutomationException InvalidTyped(string message) =>
        new("invalid_symbol_library_typed_input", message);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SymbolLibraryTypedInput : NativeDocumentInputObject
{
    private SymbolLibraryTypedAction action;
    private NativeDocumentInput? document;
    private string? processEpoch;
    private string? operationId;
    private string? symbolName;
    private SchematicSymbol? definition;
    private NativeCapabilitySymbolLibraryPinDisplay? pinDisplay;
    private NativeCapabilitySymbolLibraryCheckedState? expectedState;
    private string? expectedSnapshotSha256;
    private string? expectedModifyHash;
    private bool overwrite = true;

    public SymbolLibraryTypedAction Action { get => action; set => action = BindOnce(value); }
    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }
    public string? ProcessEpoch { get => processEpoch; set => processEpoch = BindOnce(value); }
    public string? OperationId { get => operationId; set => operationId = BindOnce(value); }
    public string? SymbolName { get => symbolName; set => symbolName = BindOnce(value); }
    public SchematicSymbol? Definition { get => definition; set => definition = BindOnce(value); }
    public NativeCapabilitySymbolLibraryPinDisplay? PinDisplay { get => pinDisplay; set => pinDisplay = BindOnce(value); }
    public NativeCapabilitySymbolLibraryCheckedState? ExpectedState { get => expectedState; set => expectedState = BindOnce(value); }
    public string? ExpectedSnapshotSha256 { get => expectedSnapshotSha256; set => expectedSnapshotSha256 = BindOnce(value); }
    public string? ExpectedModifyHash { get => expectedModifyHash; set => expectedModifyHash = BindOnce(value); }
    public bool Overwrite { get => overwrite; set => overwrite = BindOnce(value); }

    internal SymbolLibraryTypedPayload ToPayload()
    {
        if (!Enum.IsDefined(Action))
            throw Invalid("action must be validate, read, write, writeChecked or delete.");
        if (Document is null)
            throw Invalid("document is required and must identify a project-bound symbol library entry.");

        DocumentSpecifier nativeDocument;
        try
        {
            nativeDocument = NativeDocumentInput.ToNative(Document);
        }
        catch (AutomationException error)
        {
            throw Invalid(error.Message);
        }

        if (nativeDocument.Type != DocumentType.DoctypeSymbol || nativeDocument.Project is null
            || nativeDocument.LibId is null)
            throw Invalid("document must identify a project-bound symbol library entry.");
        if (string.IsNullOrWhiteSpace(nativeDocument.LibId.LibraryNickname)
            || string.IsNullOrWhiteSpace(nativeDocument.LibId.EntryName))
            throw Invalid("document.libId must carry both libraryNickname and entryName.");

        string libraryNickname = nativeDocument.LibId.LibraryNickname;
        string documentEntryName = nativeDocument.LibId.EntryName;
        SchematicSymbol? definition = Definition;
        string? requestedName = string.IsNullOrWhiteSpace(SymbolName) ? null : SymbolName;

        if (Action == SymbolLibraryTypedAction.Validate)
        {
            if (definition is null)
                throw Invalid("validate requires definition.");
            EnsureDefinitionIdentity(definition, libraryNickname, requestedName);
            if (!string.Equals(definition.Id!.EntryName, documentEntryName, StringComparison.Ordinal))
                throw Invalid("definition.id.entryName must match document.libId.entryName.");
            if (ProcessEpoch is not null || OperationId is not null || ExpectedState is not null
                || ExpectedSnapshotSha256 is not null)
                throw Invalid("validate is read-only and does not accept process, operation or checked-state fields.");
            return new(SymbolLibraryTypedAction.Validate, nativeDocument, libraryNickname,
                definition.Id.EntryName, definition, null, null, null, null, null, Overwrite, null);
        }

        RequireCanonicalUuid(ProcessEpoch, "processEpoch");
        if (Action is SymbolLibraryTypedAction.Write or SymbolLibraryTypedAction.WriteChecked
            or SymbolLibraryTypedAction.Delete)
            RequireCanonicalUuid(OperationId, "operationId");
        else if (OperationId is not null)
            throw Invalid("read does not accept operationId; use a mutation action for a replayable write/delete.");

        if (Action is SymbolLibraryTypedAction.Read or SymbolLibraryTypedAction.Delete)
        {
            if (definition is not null)
                throw Invalid("read and delete do not accept definition; provide symbolName.");
            if (string.IsNullOrWhiteSpace(requestedName))
                throw Invalid("read and delete require symbolName.");
            if (!string.Equals(requestedName, documentEntryName, StringComparison.Ordinal))
                throw Invalid("symbolName must match document.libId.entryName.");
            ValidateOptionalModifyHash(ExpectedModifyHash);
            if (ExpectedState is not null || ExpectedSnapshotSha256 is not null || PinDisplay is not null)
                throw Invalid("read and delete do not accept definition display or checked-state fields.");
            return new(Action, nativeDocument, libraryNickname, requestedName, null, null, null,
                ProcessEpoch, null, ExpectedModifyHash, Overwrite, OperationId);
        }

        if (definition is null)
            throw Invalid("write and writeChecked require definition.");
        EnsureDefinitionIdentity(definition, libraryNickname, requestedName);
        string symbolName = definition.Id!.EntryName;
        if (!string.Equals(symbolName, documentEntryName, StringComparison.Ordinal))
            throw Invalid("definition.id.entryName must match document.libId.entryName.");
        if (Action == SymbolLibraryTypedAction.Write)
        {
            ValidateOptionalModifyHash(ExpectedModifyHash);
            if (ExpectedState is not null || ExpectedSnapshotSha256 is not null)
                throw Invalid("write uses expectedModifyHash; use writeChecked with expectedState and expectedSnapshotSha256 for a complete checked admission.");
            return new(Action, nativeDocument, libraryNickname, symbolName, definition, PinDisplay,
                null, ProcessEpoch, null, ExpectedModifyHash, Overwrite, OperationId);
        }

        if (PinDisplay is null)
            throw Invalid("writeChecked requires pinDisplay so the typed request retains complete display state.");
        if (ExpectedState is null)
            throw Invalid("writeChecked requires expectedState.");
        ValidateSnapshot(ExpectedState, nativeDocument, ProcessEpoch!, ExpectedSnapshotSha256);
        return new(Action, nativeDocument, libraryNickname, symbolName, definition, PinDisplay,
            ExpectedState, ProcessEpoch, ExpectedSnapshotSha256, null, Overwrite, OperationId);
    }

    private static void EnsureDefinitionIdentity(SchematicSymbol definition, string libraryNickname,
        string? requestedName)
    {
        if (definition.Id is null || string.IsNullOrWhiteSpace(definition.Id.LibraryNickname)
            || string.IsNullOrWhiteSpace(definition.Id.EntryName))
            throw Invalid("definition.id must carry libraryNickname and entryName.");
        if (!string.Equals(definition.Id.LibraryNickname, libraryNickname, StringComparison.Ordinal))
            throw Invalid("definition.id.libraryNickname must match document.libId.libraryNickname.");
        if (requestedName is not null && !string.Equals(requestedName, definition.Id.EntryName, StringComparison.Ordinal))
            throw Invalid("symbolName must match definition.id.entryName.");
    }

    private static void ValidateSnapshot(NativeCapabilitySymbolLibraryCheckedState state,
        DocumentSpecifier document, string processEpoch, string? expectedSnapshotSha256)
    {
        RequireCanonicalUuid(processEpoch, "processEpoch");
        ValidateDigest(expectedSnapshotSha256, "expectedSnapshotSha256");
        if (state.Document is null || !state.Document.Equals(document))
            throw Invalid("expectedState.document must match document.");
        if (state.ProcessEpoch != processEpoch || state.Revision is null || state.Revision.Epoch != processEpoch)
            throw Invalid("expectedState must carry the same process epoch in processEpoch and revision.epoch.");
        ValidateDigest(state.StateSha256, "expectedState.stateSha256");
        if (!string.Equals(state.StateSha256, expectedSnapshotSha256, StringComparison.Ordinal))
            throw Invalid("expectedSnapshotSha256 must equal expectedState.stateSha256.");
        if (!string.Equals(SymbolLibraryCheckedContract.ComputeStateDigest(state), state.StateSha256,
                StringComparison.Ordinal))
            throw Invalid("expectedState.stateSha256 does not match the canonical checked-state digest.");

        // Reuse the receipt validator's complete identity checks before a
        // typed request reaches the native writer. A digest over an aliased
        // or incomplete dependency set cannot stand in for the table and
        // expanded-file baselines required by the direct-file owner.
        try
        {
            SymbolLibraryCheckedContract.ValidateExpectedState(state, document, processEpoch);
        }
        catch (AutomationException error)
        {
            throw Invalid(error.Message);
        }
    }

    private static void ValidateOptionalDigest(string? value, string name)
    {
        if (value is null or "") return;
        ValidateDigest(value, name);
    }

    private static void ValidateOptionalModifyHash(string? value)
    {
        if (value is null or "") return;
        if (!ulong.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            throw Invalid("expectedModifyHash must be an unsigned decimal native modify hash.");
    }

    private static void ValidateDigest(string? value, string name)
    {
        if (value is null || value.Length != 64 || !value.All(char.IsAsciiHexDigitLower))
            throw Invalid(name + " must be a lowercase SHA-256 digest.");
    }

    private static void RequireCanonicalUuid(string? value, string name)
    {
        if (value is null || !Guid.TryParseExact(value, "D", out Guid parsed)
            || parsed == Guid.Empty || !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
            throw Invalid(name + " must be a canonical lowercase UUID.");
    }

    private static AutomationException Invalid(string message) =>
        new("invalid_symbol_library_typed_input", message);
}

[JsonConverter(typeof(JsonStringEnumConverter<SymbolLibraryTypedAction>))]
public enum SymbolLibraryTypedAction
{
    [JsonStringEnumMemberName("validate")] Validate = 1,
    [JsonStringEnumMemberName("read")] Read = 2,
    [JsonStringEnumMemberName("write")] Write = 3,
    [JsonStringEnumMemberName("writeChecked")] WriteChecked = 4,
    [JsonStringEnumMemberName("delete")] Delete = 5
}

internal sealed record SymbolLibraryTypedPayload(
    SymbolLibraryTypedAction Action,
    DocumentSpecifier Document,
    string? LibraryNickname,
    string? SymbolName,
    SchematicSymbol? Definition,
    NativeCapabilitySymbolLibraryPinDisplay? PinDisplay,
    NativeCapabilitySymbolLibraryCheckedState? ExpectedState,
    string? ProcessEpoch,
    string? ExpectedSnapshotSha256,
    string? ExpectedModifyHash,
    bool Overwrite,
    string? OperationId);

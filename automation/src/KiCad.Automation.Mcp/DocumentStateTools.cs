using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class DocumentStateTools(InstanceRegistry registry)
{
    public Task<CallToolResult> Read(string instanceId, string documentJson, CancellationToken cancellationToken) =>
        InstanceToolBoundary.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentSpecifier document;
            try { document = SchematicJson.Parser.Parse<DocumentSpecifier>(documentJson); }
            catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
            { throw new AutomationException("invalid_document_target", "Provide a valid native document descriptor: " + error.Message); }
            ValidateTarget(document);

            var state = await registry.Client(instanceId).InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = document }, cancellationToken);
            var scope = (int)document.Type == 1 ? DocumentLifecycleScope.DlsSchematicHierarchy : DocumentLifecycleScope.DlsPcb;
            if (!document.Equals(state.Document) || state.StateSha256.Length != 64
                || !state.StateSha256.All(char.IsAsciiHexDigitLower) || state.Scope != scope
                || !Guid.TryParseExact(state.NativeIdentity, "D", out _)
                || state.Revision is null || !Guid.TryParseExact(state.Revision.Epoch, "D", out _))
                throw new AutomationException("invalid_native_state", "Native state did not match the requested document or digest contract.");
            return SchematicJson.Formatter.Format(state);
        });

    [McpServerTool(Name = "kicad_document_state", ReadOnly = true),
     Description("Read full native-writer state for exactly one explicit document descriptor: documentJson (legacy protobuf JSON) or document (the structured native document identifier). Returns native identity, revision, content digest, native dirty flag, file names, loaded/written versus current file comparisons and explicit coverage flags without saving or changing the design. Unknown file baselines remain explicit. Complete lifecycle protection is not yet qualified; this observation is not authorization to save, close or discard a document."),
     KiCadCapability("document-lifecycle", "native-api", "typed or legacy explicit instance ID and document"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NetClassesRoundTripThroughXmlAndNativeEdits")]
    public Task<CallToolResult> ReadStructured(string instanceId, CancellationToken cancellationToken,
        string? documentJson = null, NativeDocumentInput? document = null) =>
        RunAdmission(cancellationToken, () => Read(instanceId, ResolveDocumentJson(documentJson, document), cancellationToken),
            "invalid_document_input");

    private static async Task<CallToolResult> RunAdmission(Func<Task<CallToolResult>> operation, string invalidCode)
    {
        try { return await operation(); }
        catch (AutomationException error) { return AdmissionError(error.Code, error.Message); }
        catch (JsonException error) { return AdmissionError(invalidCode, error.Message); }
        catch (ArgumentException error) { return AdmissionError(invalidCode, error.Message); }
    }

    private static Task<CallToolResult> RunAdmission(CancellationToken cancellationToken,
        Func<Task<CallToolResult>> operation, string invalidCode)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RunAdmission(operation, invalidCode);
    }

    private static CallToolResult AdmissionError(string code, string message)
    {
        var json = JsonSerializer.SerializeToElement(new { code, message });
        return new CallToolResult
        {
            IsError = true,
            StructuredContent = json,
            Content = [new TextContentBlock { Text = json.GetRawText() }]
        };
    }

    private static string ResolveDocumentJson(string? legacyJson, NativeDocumentInput? typed)
    {
        bool legacySupplied = legacyJson is not null;
        if (legacySupplied && typed is not null)
            throw new AutomationException("invalid_document_input",
                "Provide exactly one document descriptor: documentJson or document.");
        if (typed is null && (!legacySupplied || string.IsNullOrWhiteSpace(legacyJson)))
            throw new AutomationException("invalid_document_input",
                "Provide exactly one document descriptor: documentJson or document.");
        if (typed is not null)
            return SchematicJson.Formatter.Format(NativeDocumentInput.ToNative(typed));
        return legacyJson!;
    }

    internal static void ValidateTarget(DocumentSpecifier? document)
    {
        if (document?.Project is null || string.IsNullOrWhiteSpace(document.Project.Name)
            || !Path.IsPathFullyQualified(document.Project.Path))
            throw new AutomationException("invalid_document_target", "An explicit project name and absolute project directory are required.");
        if ((int)document.Type == 1)
        {
            if (document.SheetPath is null || document.SheetPath.Path.Count == 0
                || document.SheetPath.Path.Any(id => !Guid.TryParseExact(id.Value, "D", out _)))
                throw new AutomationException("invalid_document_target", "An exact native sheet-instance path is required.");
        }
        else if ((int)document.Type == 3)
        {
            if (string.IsNullOrWhiteSpace(document.BoardFilename)
                || Path.GetFileName(document.BoardFilename) != document.BoardFilename
                || Path.GetExtension(document.BoardFilename) != ".kicad_pcb")
                throw new AutomationException("invalid_document_target", "The native board filename is required.");
        }
        else throw new AutomationException("unsupported_document_type", "This state query currently supports schematic and PCB documents.");
    }
}

using System.ComponentModel;
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
    [McpServerTool(Name = "kicad_document_state", ReadOnly = true),
     Description("Read full native-writer state for an explicitly identified schematic hierarchy or PCB. Returns native identity, revision, content digest, native dirty flag, file names and explicit coverage flags without saving or changing the design. Complete change tracking and loaded-file baselines are not yet qualified; this observation is not authorization to save, close or discard a document.")]
    public Task<CallToolResult> Read(string instanceId, string documentJson, CancellationToken cancellationToken) =>
        InstanceToolBoundary.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentSpecifier document;
            try { document = SchematicJson.Parser.Parse<DocumentSpecifier>(documentJson); }
            catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
            { throw new AutomationException("invalid_document_target", "Provide a valid native document descriptor: " + error.Message); }
            if (document.Project is null || string.IsNullOrWhiteSpace(document.Project.Name)
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
}

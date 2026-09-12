using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task<DocumentLifecycleState> ObserveLifecycleState(NativeClient client,
        DocumentSpecifier document, CancellationToken token)
    {
        var query = new ReadDocumentLifecycleState { Document = document.Clone() };
        var state = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(query, token);
        Assert.AreEqual(document, state.Document);
        Assert.IsTrue(Guid.TryParse(state.NativeIdentity, out _));
        Assert.IsTrue(Guid.TryParse(state.Revision.Epoch, out _));
        Assert.HasCount(64, state.StateSha256);
        Assert.IsTrue(state.StateSha256.All(character => char.IsAsciiHexDigitLower(character)));
        Assert.IsTrue(state.ProjectSettingsIncluded);
        Assert.IsFalse(state.CompleteChangeTracking);
        Assert.IsFalse(state.DiskBaselineChecked, "A content observation must not pretend to know loaded-file baselines.");
        Assert.IsNotEmpty(state.NativeFiles);
        Assert.IsTrue(state.NativeFiles.All(Path.IsPathFullyQualified));
        Assert.AreEqual(state, await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(query, token),
            "Reading the same native state must be deterministic and non-mutating.");
        return state;
    }

    private static async Task VerifyLifecycleStateThroughMcp(NativeClient client, DocumentSpecifier document,
        string evidence, CancellationToken token)
    {
        string statePath = Directory.CreateTempSubdirectory("kicad-state-mcp-").FullName;
        try
        {
            var expected = await ObserveLifecycleState(client, document, token);
            string instanceId = (await client.HandshakeAsync(token)).InstanceId;
            await using (var mcp = await StdioMcpFixture.StartAsync(statePath,
                Path.Combine(evidence, "state-mcp-" + Guid.NewGuid().ToString("N") + ".stderr.log"), token))
            {
                var attached = await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
                Assert.IsFalse(attached.TryGetProperty("isError", out var attachError) && attachError.GetBoolean());
                var observed = await mcp.Tool("kicad_document_state", new
                    { instanceId, documentJson = SchematicJson.Formatter.Format(document) });
                Assert.IsFalse(observed.TryGetProperty("isError", out var error) && error.GetBoolean(), observed.GetRawText());
                string json = observed.GetProperty("content").EnumerateArray()
                    .Single(item => item.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;
                Assert.AreEqual(expected, SchematicJson.Parser.Parse<DocumentLifecycleState>(json));
                var malformed = await mcp.Tool("kicad_document_state", new { instanceId, documentJson = "{}" });
                Assert.IsTrue(malformed.GetProperty("isError").GetBoolean());
                var recovered = await mcp.Tool("kicad_document_state", new
                    { instanceId, documentJson = SchematicJson.Formatter.Format(document) });
                Assert.IsFalse(recovered.TryGetProperty("isError", out error) && error.GetBoolean());
            }
            Assert.AreEqual(expected, await ObserveLifecycleState(client, document, token),
                "MCP state reads, rejected input and EOF must leave the native document unchanged.");
        }
        finally { Directory.Delete(statePath, true); }
    }
}

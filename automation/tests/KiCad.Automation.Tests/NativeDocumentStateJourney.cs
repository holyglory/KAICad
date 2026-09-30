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
        Assert.IsFalse(state.DiskBaselineChecked, "Per-file comparisons do not imply qualified lifecycle mutation guards.");
        Assert.IsNotEmpty(state.NativeFiles);
        Assert.IsTrue(state.NativeFiles.All(Path.IsPathFullyQualified));
        CollectionAssert.AreEquivalent(state.NativeFiles.ToArray(), state.FileBaselines.Select(file => file.Path).ToArray());
        foreach (var file in state.FileBaselines)
        {
            if (file.BaselineKnown && file.BaselineExists)
            {
                Assert.HasCount(64, file.BaselineSha256);
                Assert.IsTrue(file.BaselineSha256.All(char.IsAsciiHexDigitLower));
            }
            if (file.CurrentKnown && file.CurrentExists)
            {
                Assert.HasCount(64, file.CurrentSha256);
                Assert.IsTrue(file.CurrentSha256.All(char.IsAsciiHexDigitLower));
            }
        }
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
                var savedFiles = expected.FileBaselines.Where(file => file.BaselineKnown && file.BaselineExists
                    && file.Status == NativeFileBaselineStatus.NfbsUnchanged).ToArray();
                Assert.IsNotEmpty(savedFiles, "This journey must exercise a real loaded/written file baseline.");
                foreach (var file in savedFiles)
                {
                    byte[] original = await File.ReadAllBytesAsync(file.Path, token);
                    try
                    {
                        // Isolated fixture only: valid trailing whitespace changes the exact disk version.
                        await File.AppendAllTextAsync(file.Path, "\n ", token);
                        var changedReply = await mcp.Tool("kicad_document_state", new
                            { instanceId, documentJson = SchematicJson.Formatter.Format(document) });
                        Assert.IsFalse(changedReply.TryGetProperty("isError", out error) && error.GetBoolean());
                        string changedJson = changedReply.GetProperty("content").EnumerateArray()
                            .Single(item => item.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;
                        var changed = SchematicJson.Parser.Parse<DocumentLifecycleState>(changedJson);
                        var changedFile = changed.FileBaselines.Single(item => item.Path == file.Path);
                        Assert.AreEqual(NativeFileBaselineStatus.NfbsChanged, changedFile.Status);
                        Assert.AreEqual(file.BaselineSha256, changedFile.BaselineSha256);
                        Assert.AreNotEqual(file.CurrentSha256, changedFile.CurrentSha256);
                        Assert.AreEqual(expected.StateSha256, changed.StateSha256);
                        Assert.AreEqual(expected.NativeContentDirty, changed.NativeContentDirty);
                    }
                    finally { await File.WriteAllBytesAsync(file.Path, original, CancellationToken.None); }
                }
            }
            Assert.AreEqual(expected, await ObserveLifecycleState(client, document, token),
                "MCP state reads, rejected input and EOF must leave the native document unchanged.");
        }
        finally { Directory.Delete(statePath, true); }
    }
}

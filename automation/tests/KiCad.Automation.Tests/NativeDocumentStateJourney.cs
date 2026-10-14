using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task<DocumentLifecycleState> ObserveLifecycleState(NativeClient client,
        DocumentSpecifier document, CancellationToken token, bool retryOnTransition = false)
    {
        var query = new ReadDocumentLifecycleState { Document = document.Clone() };
        async Task<DocumentLifecycleState> ReadAndValidate()
        {
            var state = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(query, token);
            Assert.AreEqual(document, state.Document);
            Assert.IsTrue(Guid.TryParse(state.NativeIdentity, out _));
            Assert.IsTrue(Guid.TryParse(state.Revision.Epoch, out _));
            Assert.HasCount(64, state.StateSha256);
            Assert.IsTrue(state.StateSha256.All(character => char.IsAsciiHexDigitLower(character)));
            Assert.IsTrue(state.ProjectSettingsIncluded);
            if (document.Type == DocumentType.DoctypePcb)
            {
                Assert.IsFalse(state.CompleteChangeTracking, "The numeric PCB revision remains a partial cache cursor; checked route admission uses the full state digest separately.");
                Assert.AreEqual(CheckedSchematicContract.FileCoverage(state), state.DiskBaselineChecked,
                    "The disk flag describes only the listed native file comparisons.");
            }
            else
            {
                Assert.IsFalse(state.CompleteChangeTracking);
                Assert.IsFalse(state.DiskBaselineChecked, "Schematic per-file comparisons do not imply qualified lifecycle mutation guards.");
            }
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
            return state;
        }

        while (true)
        {
            DocumentLifecycleState state = await ReadAndValidate();
            DocumentLifecycleState repeated = await ReadAndValidate();
            if (state.Equals(repeated)) return state;
            if (!retryOnTransition)
            {
                Assert.AreEqual(state, repeated,
                    "Reading the same native state must be deterministic and non-mutating.");
            }
            await Task.Delay(50, token);
        }
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
                        // PCB lifecycle state includes the custom-rules sidecar bytes.  A
                        // direct edit to that sidecar therefore changes the state digest,
                        // while editing the board/project files above only changes their
                        // disk baselines and leaves the native design digest stable.
                        if (Path.GetExtension(file.Path).Equals(".kicad_dru", StringComparison.OrdinalIgnoreCase))
                            Assert.AreNotEqual(expected.StateSha256, changed.StateSha256);
                        else
                            Assert.AreEqual(expected.StateSha256, changed.StateSha256);
                        Assert.AreEqual(expected.NativeContentDirty, changed.NativeContentDirty);
                    }
                    finally { await File.WriteAllBytesAsync(file.Path, original, CancellationToken.None); }
                }

                if (document.Type == DocumentType.DoctypePcb)
                {
                    // A missing .kicad_dru is still a known, tracked baseline.  Exercise
                    // the external-create path and then restore the original absence.  The
                    // editor timestamp must remain an in-memory cursor: observing a newly
                    // created sidecar cannot make a checked mutation appear safe.
                    var rulesFile = expected.FileBaselines.SingleOrDefault(file =>
                        file.BaselineKnown && !file.BaselineExists
                        && Path.GetExtension(file.Path).Equals(".kicad_dru", StringComparison.OrdinalIgnoreCase));
                    Assert.IsNotNull(rulesFile, "PCB lifecycle state must track the absent custom-rules sidecar.");
                    string rulesPath = rulesFile!.Path;
                    Assert.IsFalse(File.Exists(rulesPath));
                    ulong expectedSequence = expected.Revision.Sequence;
                    string expectedEpoch = expected.Revision.Epoch;
                    try
                    {
                        await File.WriteAllTextAsync(rulesPath, "(version 1)\n", token);
                        var createdReply = await mcp.Tool("kicad_document_state", new
                            { instanceId, documentJson = SchematicJson.Formatter.Format(document) });
                        Assert.IsFalse(createdReply.TryGetProperty("isError", out var createdError)
                            && createdError.GetBoolean(), createdReply.GetRawText());
                        string createdJson = createdReply.GetProperty("content").EnumerateArray()
                            .Single(item => item.GetProperty("type").GetString() == "text")
                            .GetProperty("text").GetString()!;
                        var created = SchematicJson.Parser.Parse<DocumentLifecycleState>(createdJson);
                        var createdRules = created.FileBaselines.Single(file => file.Path == rulesPath);
                        Assert.AreEqual(NativeFileBaselineStatus.NfbsChanged, createdRules.Status);
                        Assert.AreEqual(expectedSequence, created.Revision.Sequence,
                            "An external rules-file edit must not advance the native board timestamp.");
                        Assert.AreEqual(expectedEpoch, created.Revision.Epoch);
                        Assert.AreNotEqual(expected.StateSha256, created.StateSha256,
                            "The rules sidecar bytes must participate in the lifecycle digest.");
                        Assert.IsFalse(created.CompleteChangeTracking,
                            "A changed sidecar must never claim universal event tracking.");
                        Assert.IsFalse(created.DiskBaselineChecked,
                            "A changed sidecar must fail disk-baseline coverage.");

                        File.Delete(rulesPath);
                        var restoredReply = await mcp.Tool("kicad_document_state", new
                            { instanceId, documentJson = SchematicJson.Formatter.Format(document) });
                        Assert.IsFalse(restoredReply.TryGetProperty("isError", out var restoredError)
                            && restoredError.GetBoolean(), restoredReply.GetRawText());
                        string restoredJson = restoredReply.GetProperty("content").EnumerateArray()
                            .Single(item => item.GetProperty("type").GetString() == "text")
                            .GetProperty("text").GetString()!;
                        var restored = SchematicJson.Parser.Parse<DocumentLifecycleState>(restoredJson);
                        var restoredRules = restored.FileBaselines.Single(file => file.Path == rulesPath);
                        Assert.AreEqual(NativeFileBaselineStatus.NfbsUnchanged, restoredRules.Status,
                            "Deleting the externally created sidecar must restore its absent baseline.");
                        Assert.AreEqual(expected.StateSha256, restored.StateSha256);
                        Assert.AreEqual(expectedSequence, restored.Revision.Sequence);
                        Assert.AreEqual(expectedEpoch, restored.Revision.Epoch);
                        Assert.IsFalse(restored.CompleteChangeTracking);
                        Assert.AreEqual(expected.DiskBaselineChecked, restored.DiskBaselineChecked);
                    }
                    finally
                    {
                        if (File.Exists(rulesPath)) File.Delete(rulesPath);
                    }
                }
            }
            Assert.AreEqual(expected, await ObserveLifecycleState(client, document, token),
                "MCP state reads, rejected input and EOF must leave the native document unchanged.");
        }
        finally { Directory.Delete(statePath, true); }
    }
}

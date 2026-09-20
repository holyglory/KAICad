using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class CandidateRecoveryCommitTests
{
    [TestMethod]
    public async Task StoresValidatedDesiredXmlWithoutWritingNativeDesignOrAdvancingBaseline()
    {
        string root = Directory.CreateTempSubdirectory("candidate-recovery-").FullName;
        try
        {
            var state = SchematicSynchronizationPlanTests.Fixture();
            string recovery = Path.Combine(root, "recovery.json"); var store = new DesignRecoveryStore(recovery); var saved = store.Save(state, null);
            string candidateXml = CandidateXml(state); byte[] candidate = Encoding.UTF8.GetBytes(candidateXml);
            string hash = Convert.ToHexStringLower(SHA256.HashData(candidate)); string operation = Guid.NewGuid().ToString("D");
            var result = new RecoveryTools().CommitCandidate(state.InstanceId.ToString("D"), recovery, saved.RevisionToken,
                candidateXml, hash, operation, CancellationToken.None);
            Assert.IsFalse(result.IsError == true);
            var data = JsonSerializer.SerializeToElement(result).GetProperty("structuredContent");
            Assert.IsTrue(data.GetProperty("desiredCandidateStored").GetBoolean());
            Assert.IsFalse(data.GetProperty("designFileWritten").GetBoolean());
            Assert.IsFalse(data.GetProperty("nativeMutationCommitted").GetBoolean());
            Assert.IsFalse(data.GetProperty("baselineAdvanced").GetBoolean());
            var committed = store.Read()!;
            CollectionAssert.AreEqual(candidate, committed.State.DesiredFileBytes);
            Assert.AreEqual(SchematicDesignXml.Write(saved.State.Baseline, saved.State.KnowledgeLibraries),
                SchematicDesignXml.Write(committed.State.Baseline, committed.State.KnowledgeLibraries));
            Assert.IsFalse(File.Exists(Path.Combine(root, "design.xml")));
            var stale = new RecoveryTools().CommitCandidate(state.InstanceId.ToString("D"), recovery, saved.RevisionToken,
                candidateXml, hash, Guid.NewGuid().ToString("D"), CancellationToken.None);
            Assert.IsTrue(stale.IsError == true);
            Assert.AreEqual("design_recovery_changed", JsonSerializer.SerializeToElement(stale).GetProperty("structuredContent").GetProperty("errorCode").GetString());
            var mismatch = new RecoveryTools().CommitCandidate(state.InstanceId.ToString("D"), recovery, committed.RevisionToken,
                candidateXml, new string('a', 64), Guid.NewGuid().ToString("D"), CancellationToken.None);
            Assert.IsTrue(mismatch.IsError == true);
            Assert.AreEqual("candidate_hash_mismatch", JsonSerializer.SerializeToElement(mismatch).GetProperty("structuredContent").GetProperty("errorCode").GetString());
            var wrongSchematic = state.Baseline.Schematic.Clone();
            wrongSchematic.Document = wrongSchematic.Document.Clone();
            wrongSchematic.Document.SheetPath.Path[0].Value = Guid.NewGuid().ToString("D");
            var wrongRoot = SchematicDesignXml.Write(state.Baseline with { Schematic = wrongSchematic }, state.KnowledgeLibraries);
            var wrong = new RecoveryTools().CommitCandidate(state.InstanceId.ToString("D"), recovery, committed.RevisionToken,
                wrongRoot, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(wrongRoot))), Guid.NewGuid().ToString("D"), CancellationToken.None);
            Assert.IsTrue(wrong.IsError == true);
            Assert.AreEqual("candidate_root_mismatch", JsonSerializer.SerializeToElement(wrong).GetProperty("structuredContent").GetProperty("errorCode").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CandidateXml(DesignRecoveryState state)
    {
        var schematic = state.Baseline.Schematic.Clone();
        var screen = schematic.Instances[0]; screen.Metadata.TextVariables["CANDIDATE_NOTE"] = "Stored as desired XML only";
        return SchematicDesignXml.Write(state.Baseline with { Schematic = schematic }, state.KnowledgeLibraries);
    }
}

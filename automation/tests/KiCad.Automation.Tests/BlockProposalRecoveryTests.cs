using System.Security.Cryptography;
using System.Text;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class BlockProposalRecoveryTests
{
    private static (RecursiveBlockGraph Before, RecursiveBlockGraph After, BlockProposal Proposal,
        string BeforeXml, string AfterXml, string BeforeHash, string AfterHash, string Fingerprint) Fixture(string root)
    {
        var f = RecursiveBlockProposalTests.Fixture();
        var prepared = BlockProposalCompiler.Prepare(f.Graph, f.Proposal);
        var fingerprint = BlockProposalFiles.Fingerprint(f.Proposal);
        var record = new BlockProposalRecord(f.Proposal.Id, f.Proposal.InputId, fingerprint, f.Proposal.BasePath,
            f.Proposal.Candidate, f.Proposal.Issues, prepared.Graph.Inspect(f.Proposal.Candidate).Origin);
        var after = prepared.Graph.WithProposal(record);
        string beforeXml = RecursiveBlockGraphXml.Write(f.Graph); string afterXml = RecursiveBlockGraphXml.Write(after);
        return (f.Graph, after, f.Proposal, beforeXml, afterXml, Hash(beforeXml), Hash(afterXml), fingerprint);
    }

    private static BlockProposalPublicationReceipt Receipt((RecursiveBlockGraph Before, RecursiveBlockGraph After, BlockProposal Proposal,
        string BeforeXml, string AfterXml, string BeforeHash, string AfterHash, string Fingerprint) f, string path, Guid operation,
        BlockProposalOperationStage stage, string? staged = null, string? retained = null) => new(
        2, f.Proposal.Id, operation, BlockProposalOperationKind.Publish, path, f.Fingerprint, f.BeforeHash, f.AfterHash,
        stage, staged, retained, stage == BlockProposalOperationStage.Published ? DateTimeOffset.UtcNow : null,
        f.Before.DocumentId, f.AfterXml);

    [TestMethod]
    public async Task PreparedReceiptResumesAndKeepsTheOriginalXmlRetained()
    {
        string root = Directory.CreateTempSubdirectory("proposal-recovery-prepared-").FullName;
        try
        {
            var f = Fixture(root); string path = Path.Combine(root, "diagram.xml"), state = Path.Combine(root, "state");
            await File.WriteAllTextAsync(path, f.BeforeXml); Guid operation = Guid.NewGuid();
            var store = new BlockProposalReceipts(state); store.Write(Receipt(f, path, operation, BlockProposalOperationStage.Prepared));
            var result = await BlockProposalRecovery.ResumeAsync(root, path, f.Before.DocumentId, operation, state);
            Assert.AreEqual(BlockProposalRecoveryDisposition.CompletedPreviously, result.Disposition);
            Assert.AreEqual(BlockProposalOperationStage.Published, result.Receipt.Stage);
            Assert.AreEqual(f.AfterXml, await File.ReadAllTextAsync(path));
            Assert.AreEqual(f.BeforeXml, await File.ReadAllTextAsync(result.Receipt.RetainedPath!));
            var retry = await BlockProposalRecovery.ResumeAsync(root, path, f.Before.DocumentId, operation, state);
            Assert.AreEqual(result.Receipt, retry.Receipt);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ReplacingReceiptCompletesOnlyWhenBothSidesProveTheExchange()
    {
        string root = Directory.CreateTempSubdirectory("proposal-recovery-replacing-").FullName;
        try
        {
            var f = Fixture(root); string path = Path.Combine(root, "diagram.xml"), state = Path.Combine(root, "state");
            string staged = path + ".sync-existing"; await File.WriteAllTextAsync(path, f.BeforeXml); await File.WriteAllTextAsync(staged, f.AfterXml);
            Guid operation = Guid.NewGuid(); var store = new BlockProposalReceipts(state);
            store.Write(Receipt(f, path, operation, BlockProposalOperationStage.Prepared));
            store.Write(Receipt(f, path, operation, BlockProposalOperationStage.Replacing, staged, staged));
            var resumed = await BlockProposalRecovery.ResumeAsync(root, path, f.Before.DocumentId, operation, state);
            Assert.AreEqual(BlockProposalRecoveryDisposition.CompletedPreviously, resumed.Disposition);
            Assert.AreEqual(f.AfterXml, await File.ReadAllTextAsync(path));
            Assert.AreEqual(f.BeforeXml, await File.ReadAllTextAsync(staged));
            // A second operation with current postimage/retained preimage confirms
            // the phase without performing another exchange.
            Guid confirmOperation = Guid.NewGuid(); var confirm = new BlockProposalReceipts(state);
            confirm.Write(Receipt(f, path, confirmOperation, BlockProposalOperationStage.Prepared));
            confirm.Write(Receipt(f, path, confirmOperation, BlockProposalOperationStage.Replacing, staged, staged));
            var observed = await BlockProposalRecovery.InspectAsync(root, path, f.Before.DocumentId, confirmOperation, state);
            Assert.AreEqual(BlockProposalRecoveryDisposition.ConfirmPublication, observed.Disposition);
            var confirmed = await BlockProposalRecovery.ResumeAsync(root, path, f.Before.DocumentId, confirmOperation, state);
            Assert.AreEqual(BlockProposalRecoveryDisposition.CompletedPreviously, confirmed.Disposition);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ConflictingCurrentXmlAndCancellationNeverOverwriteEitherVersion()
    {
        string root = Directory.CreateTempSubdirectory("proposal-recovery-conflict-").FullName;
        try
        {
            var f = Fixture(root); string path = Path.Combine(root, "diagram.xml"), state = Path.Combine(root, "state");
            string conflict = f.BeforeXml + "\n<!-- another editor -->\n"; await File.WriteAllTextAsync(path, conflict);
            string staged = path + ".sync-conflict"; await File.WriteAllTextAsync(staged, f.AfterXml);
            Guid operation = Guid.NewGuid(); var store = new BlockProposalReceipts(state);
            store.Write(Receipt(f, path, operation, BlockProposalOperationStage.Prepared));
            store.Write(Receipt(f, path, operation, BlockProposalOperationStage.Replacing, staged, staged));
            var inspect = await BlockProposalRecovery.InspectAsync(root, path, f.Before.DocumentId, operation, state);
            Assert.AreEqual(BlockProposalRecoveryDisposition.NeedsReview, inspect.Disposition);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => BlockProposalRecovery.ResumeAsync(root, path, f.Before.DocumentId, operation, state, canceled.Token));
            Assert.AreEqual(conflict, await File.ReadAllTextAsync(path)); Assert.AreEqual(f.AfterXml, await File.ReadAllTextAsync(staged));
            await Assert.ThrowsExactlyAsync<AutomationException>(() => BlockProposalRecovery.ResumeAsync(root, path, f.Before.DocumentId, Guid.NewGuid(), state));
            Assert.ThrowsExactly<AutomationException>(() => store.Write(Receipt(f, path, operation, BlockProposalOperationStage.Replacing,
                staged + "-other", staged + "-other")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Hash(string xml) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(xml)));
}

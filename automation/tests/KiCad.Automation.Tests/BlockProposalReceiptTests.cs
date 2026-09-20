using KiCad.Automation.Native;
using KiCad.Automation.Model;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class BlockProposalReceiptTests
{
    [TestMethod]
    public void PublicationReceiptKeepsExactProposalOperationAndOrderedPhases()
    {
        string root = Directory.CreateTempSubdirectory("proposal-receipts-").FullName;
        try
        {
            Guid proposal = Guid.NewGuid(), operation = Guid.NewGuid();
            string design = Path.Combine(root, "diagram.xml"); string staged = design + ".sync-one";
            string before = new('a', 64), after = new('b', 64), request = new('c', 64);
            var store = new BlockProposalReceipts(root);
            var prepared = new BlockProposalPublicationReceipt(1, proposal, operation, BlockProposalOperationKind.Publish,
                design, request, before, after, BlockProposalOperationStage.Prepared, null, null);
            store.Write(prepared); store.Write(prepared);
            Assert.AreEqual(prepared, store.Read(operation));
            var replacing = prepared with { Stage = BlockProposalOperationStage.Replacing, StagedPath = staged, RetainedPath = staged };
            store.Write(replacing); store.Write(replacing);
            Assert.AreEqual(replacing, store.Read(operation));
            Assert.ThrowsExactly<AutomationException>(() => store.Write(prepared));
            Assert.ThrowsExactly<AutomationException>(() => store.Write(replacing with { StagedPath = staged + "-other", RetainedPath = staged + "-other" }));
            var published = replacing with { Stage = BlockProposalOperationStage.Published, ConfirmedAt = DateTimeOffset.UtcNow };
            store.Write(published); store.Write(published);
            Assert.AreEqual(published, new BlockProposalReceipts(root).Read(operation));
            Assert.ThrowsExactly<AutomationException>(() => store.Write(published with { RequestSha256 = new('d', 64) }));
            File.WriteAllText(Path.Combine(root, "block-proposal-operations", operation.ToString("N") + ".json"), "{\"Version\":1,\"Version\":1}");
            Assert.ThrowsExactly<AutomationException>(() => store.Read(operation));
        }
        finally { Directory.Delete(root, true); }
    }
}

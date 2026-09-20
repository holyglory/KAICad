using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveBlockReceiptStoreTests
{
    [TestMethod]
    public void ReceiptProgressionSurvivesReopenAndRejectsConflictingOrOutOfOrderWriters()
    {
        string root = Directory.CreateTempSubdirectory("refinement-receipts-").FullName;
        try
        {
            var graph = LinkedDiagramFixture.Create().Graph;
            var input = new DiagramRefinementInput(Guid.NewGuid(), graph.DocumentId, new string('a', 64), [graph.SelectedRoot], [],
                "Retain this original request.", RecursiveBlockFixture.Origin(), []);
            var prepared = new RefinementInputPublicationReceipt(1, input.Id, Path.Combine(root, "design.xml"),
                DiagramRefinementInputXml.Write(input), input.SourceSha256, new string('b', 64), RefinementPublicationStage.Prepared, null, null);
            var store = new RefinementInputReceipts(root);
            using (store.Acquire(input.Id))
            {
                Assert.ThrowsExactly<AutomationException>(() => new RefinementInputReceipts(root).Acquire(input.Id));
                using var independent = store.Acquire(Guid.NewGuid());
            }
            store.Write(prepared); store.Write(prepared);
            Assert.AreEqual(prepared, new RefinementInputReceipts(root).Read(input.Id));
            string staged = prepared.DesignPath + ".sync-fixture";
            var replacing = prepared with { Stage = RefinementPublicationStage.Replacing, StagedPath = staged, RetainedPath = staged };
            var published = replacing with { Stage = RefinementPublicationStage.Published, ConfirmedAt = DateTimeOffset.UtcNow };
            Assert.ThrowsExactly<AutomationException>(() => store.Write(published));
            store.Write(replacing); store.Write(replacing);
            Assert.ThrowsExactly<AutomationException>(() => store.Write(replacing with { StagedPath = staged + "2", RetainedPath = staged + "2" }));
            Assert.ThrowsExactly<AutomationException>(() => store.Write(prepared));
            store.Write(published); store.Write(published);
            Assert.AreEqual(published, new RefinementInputReceipts(root).Read(input.Id));
            Assert.ThrowsExactly<AutomationException>(() => store.Write(published with { ConfirmedAt = published.ConfirmedAt!.Value.AddSeconds(1) }));
            Assert.ThrowsExactly<AutomationException>(() => store.Write(published with { DesignPath = Path.Combine(root, "another.xml") }));
            string receiptPath = Path.Combine(root, "refinement-input-operations", input.Id.ToString("N") + ".json");
            string original = File.ReadAllText(receiptPath);
            File.WriteAllText(receiptPath, original.Replace("\"Version\":1", "\"Version\":1,\"Version\":1", StringComparison.Ordinal));
            Assert.ThrowsExactly<AutomationException>(() => store.Read(input.Id));
            File.WriteAllText(receiptPath, original); Assert.AreEqual(published, store.Read(input.Id));
            Assert.ThrowsExactly<AutomationException>(() => store.Write(prepared with { InputId = Guid.NewGuid() }));
        }
        finally { Directory.Delete(root, true); }
    }
}

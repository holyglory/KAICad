using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicSynchronizationAdmissionTests
{
    [TestMethod]
    public async Task WrongInstanceIsRejectedBeforeReadingOrEditingEitherDesign()
    {
        string directory = Directory.CreateTempSubdirectory("sync-wrong-instance-").FullName;
        try
        {
            string path = Path.Combine(directory, "design.xml");
            var store = new DesignRecoveryStore(Path.Combine(directory, "recovery.json"));
            var state = DesignRecoveryStoreTests.Fixture();
            var saved = store.Save(state, null);
            var peer = new NativeClientTests.FixtureTransport();
            var client = new NativeClient(peer, NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")));
            var failure = await Assert.ThrowsExactlyAsync<AutomationException>(() =>
                SchematicSynchronizationExecutor.ApplyAsync(store, client, path, saved.RevisionToken));
            Assert.AreEqual("recovery_instance_mismatch", failure.Code);
            Assert.IsTrue(peer.LastRequest!.Message.Is(KiCad.Automation.Protocol.GetAutomationSession.Descriptor));
            Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
            Assert.IsFalse(File.Exists(path), "An unrelated native connection must not create or replace design XML.");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task StaleRecoveryAndCancellationDoNotContactKiCad()
    {
        string directory = Directory.CreateTempSubdirectory("sync-preflight-").FullName;
        try
        {
            string path = Path.Combine(directory, "design.xml");
            var store = new DesignRecoveryStore(Path.Combine(directory, "recovery.json"));
            var saved = store.Save(DesignRecoveryStoreTests.Fixture(), null);
            var peer = new NativeClientTests.FixtureTransport();
            var client = new NativeClient(peer, NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "native.sock")));
            Assert.AreEqual("design_recovery_changed", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
                SchematicSynchronizationExecutor.ApplyAsync(store, client, path, "stale"))).Code);
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                SchematicSynchronizationExecutor.ApplyAsync(store, client, path, saved.RevisionToken, new CancellationToken(true)));
            Assert.IsNull(peer.LastRequest);
            Assert.AreEqual(saved.RevisionToken, store.Read()!.RevisionToken);
        }
        finally { Directory.Delete(directory, true); }
    }
}

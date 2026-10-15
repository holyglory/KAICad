using System.Text.Json;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecoveryToolsSynchronizationBoundaryTests
{
    [TestMethod]
    public async Task NativeHandshakeFailureIsReturnedAsTypedSynchronizationError()
    {
        string root = Directory.CreateTempSubdirectory("sync-tool-boundary-").FullName;
        try
        {
            var transport = new NativeClientTests.FixtureTransport();
            var state = DesignRecoveryStoreTests.Fixture() with { InstanceId = Guid.Parse(transport.InstanceId) };
            string recoveryPath = Path.Combine(root, "recovery.json");
            var saved = new DesignRecoveryStore(recoveryPath).Save(state, null);
            var registry = new InstanceRegistry(transport, root);
            await registry.AttachAsync(NativeIpcEndpoint.FromSocketPath(Path.Combine(root, "native.sock")), transport.InstanceId);
            transport.Status = 7;

            var result = await new RecoveryTools(registry).ApplySynchronization(transport.InstanceId, recoveryPath,
                Path.Combine(root, "design.xml"), saved.RevisionToken, Guid.NewGuid().ToString("D"), CancellationToken.None);

            Assert.IsTrue(result.IsError);
            JsonElement data = result.StructuredContent!.Value;
            Assert.AreEqual("native_status_7", data.GetProperty("errorCode").GetString());
            Assert.AreEqual(saved.RevisionToken, new DesignRecoveryStore(recoveryPath).Read()!.RevisionToken,
                "A failed native admission must leave the recovery record available for retry.");
        }
        finally { Directory.Delete(root, true); }
    }
}

using System.Text.Json;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbArtworkContractTests
{
    [TestMethod]
    public void ReceiptIdentityRequiresStoredInstanceAndProcessEpoch()
    {
        const string instanceId = "instance-a";
        const string processEpoch = "11111111-1111-1111-1111-111111111111";
        JsonElement payload = JsonSerializer.SerializeToElement(new { instanceId, processEpoch });

        PcbArtworkContract.ValidateReceiptIdentity(payload, instanceId, processEpoch);

        Reject(() => PcbArtworkContract.ValidateReceiptIdentity(payload, instanceId,
            "22222222-2222-2222-2222-222222222222"), "stale_process_epoch");
        Reject(() => PcbArtworkContract.ValidateReceiptIdentity(payload, "instance-b", processEpoch),
            "wrong_instance_receipt");
    }

    [TestMethod]
    public void MissingReceiptIdentityFailsClosed()
    {
        const string instanceId = "instance-a";
        const string processEpoch = "11111111-1111-1111-1111-111111111111";

        JsonElement missingEpoch = JsonSerializer.SerializeToElement(new { instanceId });
        Reject(() => PcbArtworkContract.ValidateReceiptIdentity(missingEpoch, instanceId, processEpoch),
            "stale_process_epoch");

        JsonElement missingInstance = JsonSerializer.SerializeToElement(new { processEpoch });
        Reject(() => PcbArtworkContract.ValidateReceiptIdentity(missingInstance, instanceId, processEpoch),
            "wrong_instance_receipt");
    }

    private static void Reject(Action action, string expectedCode)
    {
        AutomationException error = Assert.ThrowsExactly<AutomationException>(action);
        Assert.AreEqual(expectedCode, error.Code);
    }
}

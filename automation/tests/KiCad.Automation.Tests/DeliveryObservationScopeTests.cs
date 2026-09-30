using System.Text.Json;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DeliveryObservationScopeTests
{
    private static JsonElement State(bool current, bool owned = true, string service = "running",
        string health = "healthy", int generation = 12, string deployment = "d-fixture") => JsonSerializer.SerializeToElement(new
    {
        deployment_id = deployment, state = "running", @public = true, current_generation = 12, route_component = "files",
        readiness = new { ready = current, pending_apply = !current },
        components = new[] { new { name = "files", state = service, health, owned, generation } }
    });

    [TestMethod]
    public void FrozenPlatformAvailabilityDoesNotClaimCurrentWebSource()
    {
        var staleSource = State(false);
        DeliveryReceiptTests.ValidateDeployment(staleSource, "d-fixture", requireCurrentSource: false);
        Assert.ThrowsExactly<AssertFailedException>(() => DeliveryReceiptTests.ValidateDeployment(staleSource, "d-fixture", true));
        DeliveryReceiptTests.ValidateDeployment(State(true), "d-fixture", true);
        Assert.AreEqual("package-route.observation.json", DeliveryReceiptTests.RouteObservationFile(true));
        Assert.AreEqual("package-route", DeliveryReceiptTests.RouteObservationKind(true));
        Assert.AreEqual("web.verification.json", DeliveryReceiptTests.RouteObservationFile(false));
        Assert.AreEqual("web-deployment", DeliveryReceiptTests.RouteObservationKind(false));
    }

    [TestMethod]
    public void AnUnhealthyUnownedStoppedOrDifferentGenerationNeverPassesEitherMode()
    {
        foreach (var invalid in new[] { State(true, owned: false), State(true, service: "stopped"),
                     State(true, health: "unhealthy"), State(true, generation: 11), State(true, deployment: "another") })
        foreach (bool requireCurrent in new[] { false, true })
            Assert.ThrowsExactly<AssertFailedException>(() => DeliveryReceiptTests.ValidateDeployment(invalid, "d-fixture", requireCurrent));
    }
}

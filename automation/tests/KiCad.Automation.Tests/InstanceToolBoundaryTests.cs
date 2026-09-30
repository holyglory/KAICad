using System.Text.Json;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class InstanceToolBoundaryTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public async Task DomainFailuresArePublicButCancellationAndUnexpectedFailuresAreUnchanged()
    {
        var expected = new AutomationException("instance_mismatch", "The endpoint belongs to another instance.");
        var error = await InstanceToolBoundary.Run<int>(() => throw expected);
        Assert.IsTrue(error.IsError);
        using var message = JsonDocument.Parse(((TextContentBlock)error.Content.Single()).Text);
        Assert.AreEqual(expected.Code, message.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(expected.Message, message.RootElement.GetProperty("message").GetString());
        var native = await InstanceToolBoundary.Run<int>(() => throw new NativeApiException(3, "Wrong document target."));
        Assert.IsTrue(native.IsError);
        using var nativeMessage = JsonDocument.Parse(((TextContentBlock)native.Content.Single()).Text);
        Assert.AreEqual("native_status_3", nativeMessage.RootElement.GetProperty("code").GetString());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => InstanceToolBoundary.Run(() => Task.FromCanceled<int>(cancelled.Token)));
        var internalFailure = new InvalidOperationException("Do not publish internal diagnostics.");
        Assert.AreSame(internalFailure, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => InstanceToolBoundary.Run<int>(() => throw internalFailure)));
        var success = await InstanceToolBoundary.Run(() => Task.FromResult(new { value = 42 }));
        Assert.AreEqual("{\"value\":42}", ((TextContentBlock)success.Content.Single()).Text);
        var nativeJson = await InstanceToolBoundary.Run(() => Task.FromResult("{\"sheetPath\":{}}"));
        Assert.AreEqual("{\"sheetPath\":{}}", ((TextContentBlock)nativeJson.Content.Single()).Text);
    }

    [TestMethod]
    public async Task WrongInstanceReturnsItsReasonWithoutReplacingTheAttachedSession()
    {
        string root = Directory.CreateTempSubdirectory("kicad-instance-boundary-").FullName;
        try
        {
            // The fixture answers like a released KiCad built before handled_requests: feature
            // contracts only, no list of the requests it handles.
            string[] features = ["session.info", "version.read"];
            var transport = new NativeClientTests.FixtureTransport { ProjectPath = Path.Combine(root, "fixture.kicad_pro"), Features = features };
            var registry = new InstanceRegistry(transport, root);
            var tools = new InstanceTools(registry);
            string endpoint = NativeIpcEndpoint.FromSocketPath(Path.Combine(Path.GetTempPath(), "instance-boundary.sock"));
            var attached = await tools.Attach(endpoint, transport.InstanceId, default);
            Assert.IsFalse(attached.IsError ?? false);
            var original = registry.Get(transport.InstanceId);
            // Inspecting that older build reports its features and unknown request coverage, never
            // an empty or invented list of handlers.
            var inspected = await tools.Inspect(transport.InstanceId, default);
            Assert.IsFalse(inspected.IsError ?? false, ((TextContentBlock)inspected.Content.Single()).Text);
            using (var inspection = JsonDocument.Parse(((TextContentBlock)inspected.Content.Single()).Text))
            {
                var instance = inspection.RootElement;
                Assert.AreEqual(transport.InstanceId, instance.GetProperty("instanceId").GetString());
                Assert.AreEqual("isolated-protocol-fixture", instance.GetProperty("nativeVersion").GetString());
                CollectionAssert.AreEqual(features, instance.GetProperty("nativeCapabilities").EnumerateArray().Select(n => n.GetString()!).ToArray());
                Assert.AreEqual("unknown", instance.GetProperty("nativeRequestCoverage").GetString());
                Assert.AreEqual(JsonValueKind.Null, instance.GetProperty("nativeRequests").ValueKind, instance.GetRawText());
                Assert.AreEqual(instance.GetRawText(), inspected.StructuredContent!.Value.GetRawText());
            }
            var wrong = await tools.Attach(endpoint, Guid.NewGuid().ToString("D"), default);
            Assert.IsTrue(wrong.IsError);
            using var message = JsonDocument.Parse(((TextContentBlock)wrong.Content.Single()).Text);
            Assert.AreEqual("instance_mismatch", message.RootElement.GetProperty("code").GetString());
            Assert.AreEqual(original, registry.Get(transport.InstanceId)); Assert.AreEqual(1, registry.List().Count);
            var invalidEndpoint = await tools.Attach("tcp://localhost:1234", transport.InstanceId, default);
            Assert.IsTrue(invalidEndpoint.IsError);
            using var endpointMessage = JsonDocument.Parse(((TextContentBlock)invalidEndpoint.Content.Single()).Text);
            Assert.AreEqual("invalid_argument", endpointMessage.RootElement.GetProperty("code").GetString());
            var unavailable = await InstanceToolBoundary.Run<int>(() => throw new NngException(5, "Native diagnostic fixture."));
            Assert.IsTrue(unavailable.IsError);
            using var transportMessage = JsonDocument.Parse(((TextContentBlock)unavailable.Content.Single()).Text);
            Assert.AreEqual("native_transport_unavailable", transportMessage.RootElement.GetProperty("code").GetString());
            Assert.IsFalse(transportMessage.RootElement.GetProperty("message").GetString()!.Contains("Native diagnostic fixture."));
            Assert.IsFalse((await tools.Attach(endpoint, transport.InstanceId, default)).IsError ?? false);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CompiledStdioReturnsActionableUnknownInstanceErrorAndRecovers()
    {
        string root = Directory.CreateTempSubdirectory("kicad-instance-errors-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), deadline.Token);
            var error = await mcp.Tool("kicad_instance_inspect", new { instanceId = Guid.NewGuid().ToString("D") });
            Assert.IsTrue(error.GetProperty("isError").GetBoolean());
            TestContext.WriteLine(error.GetRawText());
            using var detail = JsonDocument.Parse(error.GetProperty("content").EnumerateArray().Single(x => x.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!);
            Assert.AreEqual("unknown_instance", detail.RootElement.GetProperty("code").GetString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(detail.RootElement.GetProperty("message").GetString()));
            var recovered = await mcp.Tool("kicad_instances_list", new { });
            Assert.IsFalse(recovered.TryGetProperty("isError", out var value) && value.GetBoolean());
        }
        finally
        {
            string log = Path.Combine(root, "stderr.log");
            if (File.Exists(log))
            {
                string retained = Path.Combine(TestContext.TestResultsDirectory!, "instance-error-mcp.log");
                File.Copy(log, retained, true); TestContext.AddResultFile(retained);
            }
            Directory.Delete(root, true);
        }
    }
}

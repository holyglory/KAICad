using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class InstanceReplacementTests
{
    [TestMethod]
    public async Task AdoptionReplacesOnePinnedClientAndSurvivesServiceRestart()
    {
        string state = Directory.CreateTempSubdirectory("kicad-replacement-").FullName;
        try
        {
            var original = new NativeClientTests.FixtureTransport { Epoch = "original", ProjectPath = Path.Combine(state, "one.kicad_pro") };
            var other = new NativeClientTests.FixtureTransport { Epoch = "other", ProjectPath = Path.Combine(state, "two.kicad_pro") };
            var peers = new Peers(); string oldEndpoint = Endpoint("old"), nextEndpoint = Endpoint("next"), otherEndpoint = Endpoint("other");
            peers.Values.Add(oldEndpoint, original); peers.Values.Add(nextEndpoint, original); peers.Values.Add(otherEndpoint, other);
            var registry = new InstanceRegistry(peers, state);
            var previous = await registry.AttachAsync(oldEndpoint, original.InstanceId);
            var second = await registry.AttachAsync(otherEndpoint, other.InstanceId);
            var oldClient = registry.Client(previous.InstanceId); var otherClient = registry.Client(second.InstanceId);
            byte[] otherSaved = await File.ReadAllBytesAsync(Path.Combine(state, second.InstanceId + ".json"));
            // Each attachment records the handshake that verified it; reads are copies (decision n39ac0ccc5c9270f2).
            Assert.AreEqual("original", registry.AttachedHandshake(previous.InstanceId)!.Epoch);
            registry.AttachedHandshake(previous.InstanceId)!.Epoch = "altered copy";
            Assert.AreEqual("original", registry.AttachedHandshake(previous.InstanceId)!.Epoch);
            Assert.IsNull(registry.AttachedHandshake(Guid.NewGuid().ToString("D")));
            original.Epoch = "replacement"; original.Features = ["replacement.feature"]; Guid operation = Guid.NewGuid();
            var result = await registry.AdoptVerifiedReplacementAsync(previous, nextEndpoint, original.Epoch, 42, operation);
            // The adopted replacement's own handshake replaces the recorded one; the other instance keeps its own.
            Assert.AreEqual("replacement", registry.AttachedHandshake(previous.InstanceId)!.Epoch);
            CollectionAssert.AreEqual(new[] { "replacement.feature" }, registry.AttachedHandshake(previous.InstanceId)!.Capabilities.ToArray());
            Assert.AreEqual("other", registry.AttachedHandshake(second.InstanceId)!.Epoch);
            Assert.IsFalse(result.Reused); Assert.AreEqual(original.Epoch, result.Instance.Epoch);
            Assert.AreEqual(nextEndpoint, registry.Client(previous.InstanceId).Endpoint);
            Assert.AreNotSame(oldClient, registry.Client(previous.InstanceId));
            Assert.AreSame(otherClient, registry.Client(second.InstanceId)); Assert.AreEqual(second, registry.Get(second.InstanceId));
            CollectionAssert.AreEqual(otherSaved, await File.ReadAllBytesAsync(Path.Combine(state, second.InstanceId + ".json")));
            await Assert.ThrowsExactlyAsync<AutomationException>(() => oldClient.HandshakeAsync());
            var restarted = new InstanceRegistry(peers, state);
            Assert.IsNull(restarted.AttachedHandshake(previous.InstanceId), "A saved registration is not a recorded handshake.");
            var reused = await restarted.AdoptVerifiedReplacementAsync(previous, nextEndpoint, original.Epoch, 42, operation);
            Assert.IsTrue(reused.Reused); Assert.AreEqual(result.Instance, reused.Instance);
            Assert.AreEqual("replacement", restarted.AttachedHandshake(previous.InstanceId)!.Epoch, "Reusing a receipt records the live handshake.");
            var reattached = await new InstanceRegistry(peers, state).ReattachAsync(previous.InstanceId);
            Assert.AreEqual(result.Instance.Epoch, reattached.Epoch);
            Assert.AreEqual(result.Instance.Endpoint, reattached.Endpoint);
            Assert.AreEqual(result.Instance.ProcessId, reattached.ProcessId);
            await Assert.ThrowsExactlyAsync<AutomationException>(() => restarted.AdoptVerifiedReplacementAsync(previous, nextEndpoint, original.Epoch, 42, Guid.NewGuid()));
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task FailedAndStaleAdoptionsPreserveRegistrationAndReceipt()
    {
        string state = Directory.CreateTempSubdirectory("kicad-replacement-").FullName;
        try
        {
            var peer = new NativeClientTests.FixtureTransport { ProjectPath = Path.Combine(state, "one.kicad_pro") };
            var registry = new InstanceRegistry(peer, state);
            var previous = await registry.AttachAsync(Endpoint("old"), peer.InstanceId);
            var client = registry.Client(peer.InstanceId); string saved = Path.Combine(state, peer.InstanceId + ".json");
            byte[] before = await File.ReadAllBytesAsync(saved); peer.Epoch = "next";
            using (var held = new FileStream(Path.Combine(state, "metadata.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var cancel = new CancellationTokenSource(100))
                await Assert.ThrowsAsync<OperationCanceledException>(() => registry.AdoptVerifiedReplacementAsync(previous, Endpoint("next"), peer.Epoch, 42, Guid.NewGuid(), cancel.Token));
            Assert.AreSame(client, registry.Client(peer.InstanceId));
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(saved));
            Assert.IsFalse(Directory.Exists(Path.Combine(state, "replacements")));
            var wrong = previous with { ProjectPath = Path.Combine(state, "wrong.kicad_pro") };
            await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.AdoptVerifiedReplacementAsync(wrong, Endpoint("next"), peer.Epoch, 42, Guid.NewGuid()));
            await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.AdoptVerifiedReplacementAsync(previous, Endpoint("next"), "wrong-epoch", 42, Guid.NewGuid()));
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(saved));
            // A new service cannot overwrite a saved binding through ordinary attach.
            await Assert.ThrowsExactlyAsync<AutomationException>(() => new InstanceRegistry(peer, state).AttachAsync(Endpoint("next"), peer.InstanceId));
            Guid operation = Guid.NewGuid();
            var adopted = await registry.AdoptVerifiedReplacementAsync(previous, Endpoint("next"), peer.Epoch, 42, operation);
            byte[] committed = await File.ReadAllBytesAsync(saved);
            await Assert.ThrowsExactlyAsync<AutomationException>(() => registry.AdoptVerifiedReplacementAsync(previous, Endpoint("next"), peer.Epoch, 99, operation));
            CollectionAssert.AreEqual(committed, await File.ReadAllBytesAsync(saved));
            Assert.AreEqual(adopted.Instance, registry.Get(peer.InstanceId));
        }
        finally { Directory.Delete(state, true); }
    }

    private static string Endpoint(string name) => NativeIpcEndpoint.FromSocketPath(Path.Combine(Path.GetTempPath(), "replacement-" + name + ".sock"));

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CompetingMcpRegistriesCommitOnlyOneReplacementIntent(bool sameOperation)
    {
        string state = Directory.CreateTempSubdirectory("kicad-replacement-race-").FullName;
        try
        {
            var peer = new NativeClientTests.FixtureTransport { ProjectPath = Path.Combine(state, "one.kicad_pro") };
            var first = new InstanceRegistry(peer, state); var second = new InstanceRegistry(peer, state);
            var previous = await first.AttachAsync(Endpoint("old"), peer.InstanceId);
            peer.Epoch = "replacement"; Guid operation = Guid.NewGuid();
            var attempts = await Task.WhenAll(Attempt(first, operation), Attempt(second, sameOperation ? operation : Guid.NewGuid()));
            Assert.AreEqual(sameOperation ? 2 : 1, attempts.Count(value => value.Result is not null));
            Assert.AreEqual(sameOperation ? 1 : 0, attempts.Count(value => value.Result?.Reused == true));
            if (!sameOperation) Assert.AreEqual("instance_changed", attempts.Single(value => value.Error is not null).Error);
            Assert.AreEqual(1, Directory.GetFiles(Path.Combine(state, "replacements"), "*.json").Length);
            var saved = (await new InstanceRegistry(peer, state).SavedSessionsAsync()).Single();
            Assert.AreEqual(peer.Epoch, saved.Epoch); Assert.AreEqual(Endpoint("new"), saved.Endpoint);

            async Task<(InstanceReplacementResult? Result, string? Error)> Attempt(InstanceRegistry registry, Guid id)
            {
                try { return (await registry.AdoptVerifiedReplacementAsync(previous, Endpoint("new"), peer.Epoch, 42, id), null); }
                catch (AutomationException error) { return (null, error.Code); }
            }
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task InterruptedPublicationPreservesBothIdentitiesAndRetriesExactlyOnce()
    {
        string state = Directory.CreateTempSubdirectory("kicad-replacement-").FullName;
        try
        {
            var peer = new NativeClientTests.FixtureTransport { ProjectPath = Path.Combine(state, "one.kicad_pro") };
            var registry = new InstanceRegistry(peer, state);
            var previous = await registry.AttachAsync(Endpoint("old"), peer.InstanceId);
            byte[] before = await File.ReadAllBytesAsync(Path.Combine(state, peer.InstanceId + ".json"));
            peer.Epoch = "replacement"; Guid operation = Guid.NewGuid();
            await Assert.ThrowsExactlyAsync<IOException>(() => registry.AdoptVerifiedReplacementAsync(previous,
                Endpoint("next"), peer.Epoch, 42, operation, default, () => throw new IOException("Synthetic persistence interruption.")));
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(Path.Combine(state, peer.InstanceId + ".json")));
            Assert.AreEqual(previous, registry.Get(peer.InstanceId));
            string intentPath = Path.Combine(state, "replacements", operation.ToString("D") + ".json");
            byte[] intent = await File.ReadAllBytesAsync(intentPath);
            var fresh = new InstanceRegistry(peer, state);
            var result = await fresh.AdoptVerifiedReplacementAsync(previous, Endpoint("next"), peer.Epoch, 42, operation);
            Assert.IsFalse(result.Reused);
            Assert.IsTrue((await fresh.AdoptVerifiedReplacementAsync(previous, Endpoint("next"), peer.Epoch, 42, operation)).Reused);
            CollectionAssert.AreEqual(intent, await File.ReadAllBytesAsync(intentPath));
            Assert.AreEqual(1, Directory.GetFiles(Path.Combine(state, "replacements")).Length);
        }
        finally { Directory.Delete(state, true); }
    }

    private sealed class Peers : INativeTransport
    {
        internal Dictionary<string, NativeClientTests.FixtureTransport> Values { get; } = new();
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Values[endpoint].ExchangeAsync(endpoint, request, timeout, cancellationToken);
    }
}

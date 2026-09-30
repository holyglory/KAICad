using System.Security.Cryptography;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveBlockRefinementAssetTests
{
    [TestMethod]
    public async Task OriginalBytesSurviveSourceReplacementDeletionAndConcurrentCapture()
    {
        string root = Directory.CreateTempSubdirectory("kicad-refinement-assets-").FullName;
        try
        {
            byte[] bytes = Enumerable.Range(0, 180000).Select(i => (byte)(i % 251)).ToArray();
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); Guid id = Guid.NewGuid();
            string original = Path.Combine(root, "original.bin"); await File.WriteAllBytesAsync(original, bytes);
            var source = new SourceReference("source-document", "revision A", 4, "Table 7", "Package B");
            Task<DiagramRefinementAttachment> Capture() => RefinementAssetFiles.CaptureAsync(root, "original.bin", "assets/refinement", id,
                hash, bytes.Length, "application/octet-stream", source);
            var captured = await Task.WhenAll(Capture(), Capture()); Assert.AreEqual(captured[0], captured[1]);
            var attachment = captured[0]; Assert.AreEqual(source, attachment.Source); Assert.AreEqual("original.bin", attachment.OriginalName);
            string asset = Path.Combine(root, attachment.AssetPath);
            Assert.IsTrue(bytes.SequenceEqual(await Read(asset)));
            await File.WriteAllTextAsync(original, "Changed source, not the original input.");
            Assert.AreEqual(attachment, await Capture()); File.Delete(original);
            Assert.AreEqual(attachment, await Capture(), "Existing verified original bytes do not depend on a still-present mutable source.");
            Assert.HasCount(1, Directory.GetFiles(Path.Combine(root, "assets/refinement")));
            var observed = await RefinementAssetFiles.InspectAsync(root, attachment);
            Assert.AreEqual(RefinementAssetStatus.Available, observed.Status);
            Assert.AreEqual(hash, observed.ObservedSha256); Assert.AreEqual(bytes.LongLength, observed.ObservedByteCount);
            await File.WriteAllTextAsync(asset, "Deliberately corrupt isolated fixture asset.");
            var changed = await RefinementAssetFiles.InspectAsync(root, attachment);
            Assert.AreEqual(RefinementAssetStatus.Changed, changed.Status); Assert.AreNotEqual(hash, changed.ObservedSha256);
            await Assert.ThrowsExactlyAsync<AutomationException>(async () => await Capture());
            Assert.AreEqual("Deliberately corrupt isolated fixture asset.", await File.ReadAllTextAsync(asset));
            await File.WriteAllBytesAsync(asset, bytes); Assert.AreEqual(attachment, await Capture());
            File.Delete(asset); Assert.AreEqual(RefinementAssetStatus.Missing, (await RefinementAssetFiles.InspectAsync(root, attachment)).Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task StaleInvalidCancelledAndRedirectedSourcesDoNotPublishAnAsset()
    {
        string root = Directory.CreateTempSubdirectory("kicad-refinement-rejection-").FullName;
        try
        {
            byte[] bytes = [1, 2, 3, 4]; string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            await File.WriteAllBytesAsync(Path.Combine(root, "source.bin"), bytes);
            Task<DiagramRefinementAttachment> Capture(string path, string archive, long count = 4, CancellationToken token = default) =>
                RefinementAssetFiles.CaptureAsync(root, path, archive, Guid.NewGuid(), hash, count, "application/octet-stream", token: token);
            await Assert.ThrowsExactlyAsync<AutomationException>(async () => await Capture("source.bin", "assets/first", 5));
            Assert.IsEmpty(Directory.GetFiles(Path.Combine(root, "assets/first")));
            await Assert.ThrowsExactlyAsync<AutomationException>(async () => await Capture("../source.bin", "assets/outside"));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "assets/outside")));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await Capture("source.bin", "assets/cancelled", token: cancelled.Token));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "assets/cancelled")));
            await File.WriteAllTextAsync(Path.Combine(root, "not-a-directory"), "Keep this file.");
            await Assert.ThrowsExactlyAsync<AutomationException>(async () => await Capture("source.bin", "not-a-directory"));
            Assert.AreEqual("Keep this file.", await File.ReadAllTextAsync(Path.Combine(root, "not-a-directory")));
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(Path.Combine(root, "linked.bin"), "source.bin");
                await Assert.ThrowsExactlyAsync<AutomationException>(async () => await Capture("linked.bin", "assets/linked-source"));
                Directory.CreateSymbolicLink(Path.Combine(root, "linked-archive"), "assets");
                await Assert.ThrowsExactlyAsync<AutomationException>(async () => await Capture("source.bin", "linked-archive"));
            }
            Assert.IsTrue(bytes.SequenceEqual(await Read(Path.Combine(root, "source.bin"))));
        }
        finally { Directory.Delete(root, true); }
    }

    // Materialize before SequenceEqual so no span is held across an await boundary.
    private static async Task<IEnumerable<byte>> Read(string path) => await File.ReadAllBytesAsync(path);
}

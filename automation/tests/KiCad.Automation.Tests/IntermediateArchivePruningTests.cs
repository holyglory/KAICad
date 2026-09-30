using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KiCad.Automation.Distribution;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class IntermediateArchivePruningTests
{
    [TestMethod]
    public async Task RemovesOnlyVerifiedCopiesAndRetainsMetadataAndRecoveryBytes()
    {
        using var fixture = new Fixture();
        byte[] catalogue = File.ReadAllBytes(Path.Combine(fixture.Source, "downloads.json"));
        var receipt = await IntermediateArchivePruning.RunAsync(fixture.Request, fixture.Receipt);
        Assert.AreEqual("completed", receipt.Status); Assert.AreEqual(fixture.Content.LongLength, receipt.RemovedLogicalBytes);
        Assert.HasCount(1, receipt.Archives); Assert.IsTrue(receipt.Archives[0].Removed);
        Assert.IsFalse(File.Exists(fixture.SourceFile));
        CollectionAssert.AreEqual(fixture.Content, File.ReadAllBytes(fixture.RetainedFile));
        CollectionAssert.AreEqual(catalogue, File.ReadAllBytes(Path.Combine(fixture.Source, "downloads.json")));
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(fixture.Source, "metadata.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Source, "updates", "preview.json")));
        var retry = await IntermediateArchivePruning.RunAsync(fixture.Request, fixture.Receipt + ".retry.json");
        Assert.AreEqual(0, retry.RemovedLogicalBytes); Assert.IsFalse(retry.Archives[0].Removed);
        File.Copy(receipt.Archives[0].RetainedCopy, fixture.SourceFile);
        CollectionAssert.AreEqual(fixture.Content, File.ReadAllBytes(fixture.SourceFile), "The retained copy must restore the removed file exactly.");
    }

    [TestMethod]
    public async Task MissingOrChangedInputsFailBeforeAnyRemoval()
    {
        foreach (int fault in Enumerable.Range(0, 5))
        {
            using var fixture = new Fixture(); var request = fixture.Request;
            switch (fault)
            {
                case 0: request = request with { Retained = request.Retained with { CatalogueSha256 = new('0', 64) } }; break;
                case 1: File.Delete(fixture.RetainedFile); break;
                case 2: File.WriteAllText(fixture.RetainedFile, "changed replica"); break;
                case 3: File.WriteAllText(fixture.SourceFile, "unique changed source"); break;
                case 4: File.AppendAllText(Path.Combine(fixture.Source, "downloads.json"), " "); break;
            }
            byte[] before = File.ReadAllBytes(fixture.SourceFile);
            await Assert.ThrowsAsync<InvalidDataException>(() => IntermediateArchivePruning.RunAsync(request, fixture.Receipt));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.SourceFile));
            Assert.IsFalse(File.Exists(fixture.Receipt));
        }
    }

    [TestMethod]
    public async Task MissingLaterReplicaFailsPreflightWithoutRemovingEarlierFiles()
    {
        using var fixture = new Fixture(); var request = fixture.AddSecondArchive();
        File.Delete(fixture.SecondRetainedFile);
        await Assert.ThrowsAsync<InvalidDataException>(() => IntermediateArchivePruning.RunAsync(request, fixture.Receipt));
        Assert.IsTrue(File.Exists(fixture.SourceFile)); Assert.IsTrue(File.Exists(fixture.SecondSourceFile));
        Assert.IsFalse(File.Exists(fixture.Receipt));
    }

    [TestMethod]
    public async Task InterruptedCleanupRetainsExactPartialReceiptAndCanRecover()
    {
        using var fixture = new Fixture(); var request = fixture.AddSecondArchive();
        await Assert.ThrowsAsync<InvalidDataException>(() => IntermediateArchivePruning.RunAsync(request, fixture.Receipt, default,
            index => { if (index == 1) File.Delete(fixture.SecondRetainedFile); }));
        using (var failed = JsonDocument.Parse(File.ReadAllBytes(fixture.Receipt)))
        {
            Assert.AreEqual("failed", failed.RootElement.GetProperty("status").GetString());
            Assert.AreEqual(1, failed.RootElement.GetProperty("archives").GetArrayLength());
            Assert.IsTrue(failed.RootElement.GetProperty("archives")[0].GetProperty("removed").GetBoolean());
        }
        Assert.IsFalse(File.Exists(fixture.SourceFile)); Assert.IsTrue(File.Exists(fixture.SecondSourceFile));
        File.Copy(fixture.SecondSourceFile, fixture.SecondRetainedFile);
        var recovered = await IntermediateArchivePruning.RunAsync(request, fixture.Receipt + ".retry.json");
        Assert.AreEqual(1, recovered.Archives.Count(a => a.Removed));
        foreach (var archive in recovered.Archives) File.Copy(archive.RetainedCopy, Path.Combine(archive.Directory, archive.FileName));
        CollectionAssert.AreEqual(fixture.Content, File.ReadAllBytes(fixture.SourceFile));
        Assert.IsTrue(File.Exists(fixture.SecondSourceFile));
    }

    [TestMethod]
    public async Task CancellationAfterPreflightWritesAReceiptWithoutRemovingFiles()
    {
        using var fixture = new Fixture(); using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => IntermediateArchivePruning.RunAsync(fixture.Request,
            fixture.Receipt, cancellation.Token, _ => cancellation.Cancel()));
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(fixture.Receipt));
        Assert.AreEqual("cancelled", receipt.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(0, receipt.RootElement.GetProperty("archives").GetArrayLength());
        Assert.IsTrue(File.Exists(fixture.SourceFile));
    }

    [TestMethod]
    public async Task SameNestedDuplicateOrRelativeRootsAndPreCancelledWorkAreRejected()
    {
        using var fixture = new Fixture();
        foreach (var request in new[]
        {
            fixture.Request with { CompletedImports = [fixture.Request.Retained] },
            fixture.Request with { CompletedImports = [fixture.Request.CompletedImports[0], fixture.Request.CompletedImports[0]] },
            fixture.Request with { CompletedImports = [new("relative", fixture.Request.CompletedImports[0].CatalogueSha256)] },
            fixture.Request with { CompletedImports = [] }
        })
            await Assert.ThrowsAsync<ArgumentException>(() => IntermediateArchivePruning.RunAsync(request, fixture.Receipt));
        await Assert.ThrowsAsync<ArgumentException>(() => IntermediateArchivePruning.RunAsync(fixture.Request, Path.Combine(fixture.Source, "receipt.json")));
        await Assert.ThrowsAsync<OperationCanceledException>(() => IntermediateArchivePruning.RunAsync(fixture.Request, fixture.Receipt, new(true)));
        Assert.IsTrue(File.Exists(fixture.SourceFile)); Assert.IsFalse(File.Exists(fixture.Receipt));
    }

    [TestMethod]
    public async Task LinkedFilesAndDirectoriesCannotRedirectCleanup()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        File.Delete(fixture.SourceFile); File.CreateSymbolicLink(fixture.SourceFile, fixture.RetainedFile);
        await Assert.ThrowsAsync<InvalidDataException>(() => IntermediateArchivePruning.RunAsync(fixture.Request, fixture.Receipt));
        Assert.IsTrue(File.Exists(fixture.RetainedFile));
        string link = Path.Combine(fixture.Root, "linked"); Directory.CreateSymbolicLink(link, fixture.Source);
        await Assert.ThrowsAsync<ArgumentException>(() => IntermediateArchivePruning.RunAsync(fixture.Request with
            { CompletedImports = [new(link, fixture.Request.CompletedImports[0].CatalogueSha256)] }, fixture.Receipt));
        File.Delete(fixture.SourceFile); Directory.Delete(link);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("kicad-archive-prune-test-").FullName;
        public string Source { get; }
        public string Retained { get; }
        public string SourceFile => Path.Combine(Source, Name);
        public string RetainedFile => Path.Combine(Retained, Name);
        public string SecondSourceFile => Path.Combine(Source, SecondName);
        public string SecondRetainedFile => Path.Combine(Retained, SecondName);
        public string Receipt => Path.Combine(Root, "receipt.json");
        public byte[] Content { get; } = Encoding.UTF8.GetBytes("synthetic archive-copy fixture");
        public IntermediateArchivePruneRequest Request { get; }
        private const string Name = "kicad-codex-fixture-source.tar.gz";
        private const string SecondName = "kicad-codex-fixture-second.zip";
        public Fixture()
        {
            Source = Directory.CreateDirectory(Path.Combine(Root, "completed-import")).FullName;
            Retained = Directory.CreateDirectory(Path.Combine(Root, "retained-public")).FullName;
            File.WriteAllBytes(SourceFile, Content); File.WriteAllBytes(RetainedFile, Content);
            File.WriteAllText(Path.Combine(Source, "metadata.txt"), "keep");
            Directory.CreateDirectory(Path.Combine(Source, "updates"));
            File.WriteAllText(Path.Combine(Source, "updates", "preview.json"), "preserved feed bytes");
            byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new DownloadManifest(1,
                [new(Name, "source", "fixture", new('a', 40), new('b', 64), Content.Length, Hash(Content))]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            File.WriteAllBytes(Path.Combine(Source, "downloads.json"), metadata);
            File.WriteAllBytes(Path.Combine(Retained, "downloads.json"), metadata);
            Request = new(1, new(Retained, Hash(metadata)), [new(Source, Hash(metadata))]);
        }
        public IntermediateArchivePruneRequest AddSecondArchive()
        {
            byte[] content = Encoding.UTF8.GetBytes("second synthetic archive");
            File.WriteAllBytes(SecondSourceFile, content); File.WriteAllBytes(SecondRetainedFile, content);
            byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new DownloadManifest(1,
                [new(Name, "source", "fixture", new('a', 40), new('b', 64), Content.Length, Hash(Content)),
                 new(SecondName, "win-x64", "fixture", new('a', 40), new('b', 64), content.Length, Hash(content))]),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            File.WriteAllBytes(Path.Combine(Source, "downloads.json"), metadata);
            File.WriteAllBytes(Path.Combine(Retained, "downloads.json"), metadata);
            return new(1, new(Retained, Hash(metadata)), [new(Source, Hash(metadata))]);
        }
        private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

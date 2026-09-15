using System.Security.Cryptography;
using System.Text;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DesignFilePublisherTests
{
    [TestMethod]
    public async Task PublishesAtomicallyAndReturnsReplacementDigest()
    {
        string root = Directory.CreateTempSubdirectory("design-publisher-").FullName;
        try
        {
            string path = Path.Combine(root, "design.xml");
            byte[] before = Encoding.UTF8.GetBytes("<design version=\"1\" />\n");
            byte[] after = Encoding.UTF8.GetBytes("<design version=\"2\" />\n");
            await File.WriteAllBytesAsync(path, before);
            string digest = await DesignFilePublisher.WriteIfUnchangedAsync(path, before, after);
            CollectionAssert.AreEqual(after, await File.ReadAllBytesAsync(path));
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(after)), digest);
            Assert.IsFalse(Directory.GetFiles(root).Any(file => file.Contains(".sync-", StringComparison.Ordinal)));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ChangedSourceIsRejectedWithoutReplacingIt()
    {
        string root = Directory.CreateTempSubdirectory("design-publisher-conflict-").FullName;
        try
        {
            string path = Path.Combine(root, "design.xml");
            byte[] expected = Encoding.UTF8.GetBytes("<design />");
            byte[] changed = Encoding.UTF8.GetBytes("<design changed=\"true\" />");
            byte[] replacement = Encoding.UTF8.GetBytes("<design generated=\"true\" />");
            await File.WriteAllBytesAsync(path, changed);
            var error = await Assert.ThrowsExactlyAsync<AutomationException>(() =>
                DesignFilePublisher.WriteIfUnchangedAsync(path, expected, replacement));
            Assert.AreEqual("design_file_changed", error.Code);
            CollectionAssert.AreEqual(changed, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CancellationBeforeCommitLeavesTheOriginalFile()
    {
        string root = Directory.CreateTempSubdirectory("design-publisher-cancel-").FullName;
        try
        {
            string path = Path.Combine(root, "design.xml");
            byte[] before = Encoding.UTF8.GetBytes("<design />");
            byte[] after = Encoding.UTF8.GetBytes("<design generated=\"true\" />");
            await File.WriteAllBytesAsync(path, before);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
                DesignFilePublisher.WriteIfUnchangedAsync(path, before, after, cancellation.Token));
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }
}

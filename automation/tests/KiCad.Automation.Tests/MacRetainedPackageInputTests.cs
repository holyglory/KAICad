using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class MacRetainedPackageInputTests
{
    [TestMethod]
    public void ParsesExactArm64RetainedPackageIdentity()
    {
        const string commit = "e40d567a4350591dc991cd670f07adeea55444c5";
        const string hash = "24f570075d40b52dff3813a0b2e3fcc1982461083441ad9d5bec4fc3271ef295";
        var input = MacRetainedPackageInput.Parse($"38639617642/1/{commit}/{hash}");
        Assert.AreEqual("38639617642", input.RunId);
        Assert.AreEqual("1", input.Attempt);
        Assert.AreEqual(commit, input.Commit);
        Assert.AreEqual(hash, input.Sha256);
        Assert.AreEqual($"unqualified-macos-{commit}.tar.gz", input.ArchiveName);
    }

    [TestMethod]
    public void RejectsMalformedRetainedPackageIdentity()
    {
        foreach (string value in new[] { "0/1/" + new string('a', 40) + "/" + new string('b', 64),
            "1/0/" + new string('a', 40) + "/" + new string('b', 64),
            "1/1/" + new string('A', 40) + "/" + new string('b', 64),
            "1/1/" + new string('a', 40) + "/" + new string('b', 63) })
            Assert.ThrowsExactly<ArgumentException>(() => MacRetainedPackageInput.Parse(value));
    }
}

using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class HostedValidatorIsolationTests
{
    [TestMethod]
    public void RunningFromSourceOrBuildOutputsIsRejectedButSiblingPublishedRuntimeIsAllowed()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "validator-source"));
        foreach (string runtime in new[] { root, Path.Combine(root, "automation/tools/validator/bin/Release"), Path.Combine(root, "a/../b") })
            Assert.ThrowsExactly<InvalidOperationException>(() => HostedValidatorIsolation.RequireOutside(root, runtime));
        HostedValidatorIsolation.RequireOutside(root, root + "-published");
        Assert.ThrowsExactly<ArgumentException>(() => HostedValidatorIsolation.RequireOutside("relative", root));
        Assert.ThrowsExactly<ArgumentException>(() => HostedValidatorIsolation.RequireOutside(root, "relative"));
    }
}

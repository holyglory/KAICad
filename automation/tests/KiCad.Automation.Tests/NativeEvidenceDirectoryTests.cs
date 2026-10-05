using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class NativeEvidenceDirectoryTests
{
    [TestMethod]
    public void NewRunPreservesPreviousEvidenceWithoutRetainingItAgain()
    {
        string root = Directory.CreateTempSubdirectory("kicad-evidence-test-").FullName;
        try
        {
            string first = NativeEvidenceDirectory.Begin(root);
            Directory.CreateDirectory(Path.Combine(first, "nested"));
            File.WriteAllText(Path.Combine(first, "nested", "screenshot-receipt"), "original bytes");
            string second = NativeEvidenceDirectory.Begin(root);
            Assert.AreEqual(first, second);
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(second).ToArray());
            string archived = Directory.GetDirectories(Path.Combine(root, "native-session-history")).Single();
            Assert.AreEqual("original bytes", File.ReadAllText(Path.Combine(archived, "nested", "screenshot-receipt")));
            File.WriteAllText(Path.Combine(second, "new-receipt"), "new bytes");
            NativeEvidenceDirectory.Begin(root);
            Assert.AreEqual(2, Directory.GetDirectories(Path.Combine(root, "native-session-history")).Length);
            Assert.AreEqual("original bytes", File.ReadAllText(Path.Combine(archived, "nested", "screenshot-receipt")));
        }
        finally { Directory.Delete(root, true); } // Only this test's disposable directory.
    }

    [TestMethod]
    public void LaterPcbCasesKeepTheFailedCaseAndItsGeometryInTheRetainedTree()
    {
        string root = Directory.CreateTempSubdirectory("kicad-case-evidence-").FullName;
        try
        {
            string previous = NativeEvidenceDirectory.Begin(root);
            File.WriteAllText(Path.Combine(previous, "previous-run.json"), "older evidence");
            string failed = NativeEvidenceDirectory.BeginCase(root, "PcbItems", "first-run");
            File.WriteAllText(Path.Combine(failed, "candidate.json"), "untouched candidate geometry");
            string connectivity = NativeEvidenceDirectory.BeginCase(root, "PcbConnectivity", "first-run");
            File.WriteAllText(Path.Combine(connectivity, "connectivity.json"), "native connectivity");
            string crossProbe = NativeEvidenceDirectory.BeginCase(root, "CrossProbe", "first-run");
            File.WriteAllText(Path.Combine(crossProbe, "selection.json"), "native selection");
            string current = Path.Combine(root, "native-session-current");
            CollectionAssert.AreEquivalent(new[] { failed, connectivity, crossProbe }, Directory.GetDirectories(current));
            Assert.AreEqual("untouched candidate geometry", File.ReadAllText(Path.Combine(failed, "candidate.json")));
            Assert.AreEqual("native connectivity", File.ReadAllText(Path.Combine(connectivity, "connectivity.json")));
            Assert.AreEqual("native selection", File.ReadAllText(Path.Combine(crossProbe, "selection.json")));
            Assert.IsTrue(Path.GetFileName(failed).StartsWith("PcbItems-", StringComparison.Ordinal));
            string archived = Directory.GetDirectories(Path.Combine(root, "native-session-history")).Single();
            Assert.AreEqual("older evidence", File.ReadAllText(Path.Combine(archived, "previous-run.json")));
            string next = NativeEvidenceDirectory.BeginCase(root, "PcbItems", "next-run");
            Assert.HasCount(1, Directory.GetDirectories(current));
            Assert.AreEqual(next, Directory.GetDirectories(current).Single());
            string priorRun = Directory.GetDirectories(Path.Combine(root, "native-session-history"))
                .Single(path => Directory.EnumerateFiles(path, "candidate.json", SearchOption.AllDirectories).Any());
            Assert.AreEqual("untouched candidate geometry", File.ReadAllText(Path.Combine(priorRun, Path.GetFileName(failed), "candidate.json")));
        }
        finally { Directory.Delete(root, true); } // Only this test's disposable directory.
    }

    [TestMethod]
    public void ConcurrentCasesWithTheSameTestIdentityHaveSeparateImmutablePaths()
    {
        string root = Directory.CreateTempSubdirectory("kicad-case-evidence-").FullName;
        try
        {
            var cases = new System.Collections.Concurrent.ConcurrentBag<string>();
            Parallel.For(0, 8, index =>
            {
                string evidence = NativeEvidenceDirectory.BeginCase(root, "PcbItems", "parallel-run");
                File.WriteAllText(Path.Combine(evidence, "receipt.json"), index.ToString());
                cases.Add(evidence);
            });
            Assert.HasCount(8, cases.Distinct(StringComparer.Ordinal).ToArray());
            CollectionAssert.AreEquivalent(cases.ToArray(), Directory.GetDirectories(Path.Combine(root, "native-session-current")));
            CollectionAssert.AreEquivalent(Enumerable.Range(0, 8).Select(index => index.ToString()).ToArray(),
                cases.Select(path => File.ReadAllText(Path.Combine(path, "receipt.json"))).ToArray());
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "native-session-history")));
        }
        finally { Directory.Delete(root, true); } // Only this test's disposable directory.
    }
}

namespace KiCad.Automation.Tests;

internal static class NativeEvidenceDirectory
{
    private static readonly object InitializationGate = new();
    private static readonly string LocalRunIdentity = "process-" + Guid.NewGuid().ToString("N");

    // Coordinator serializes runs in this worktree. Preserve the previous
    // output, but retain only this run's tree rather than all past journeys.
    internal static string Begin(string artifacts)
    {
        lock (InitializationGate)
        {
            string current = Path.Combine(artifacts, "native-session-current");
            if (Directory.Exists(current))
            {
                string history = Path.Combine(artifacts, "native-session-history");
                Directory.CreateDirectory(history);
                Directory.Move(current, Path.Combine(history, Guid.NewGuid().ToString("N")));
            }
            Directory.CreateDirectory(current);
            return current;
        }
    }

    // A PCB evidence root belongs to one graph leaf/test process. This lock
    // protects parallel case initialization there, not concurrent processes.
    // The Coordinator run marker also keeps later same-run processes from
    // rotating an already initialized root; concurrent initializers still
    // require the graph's single owning leaf.
    internal static string BeginCase(string artifacts, string testIdentity, string? runIdentity = null)
    {
        string run = runIdentity ?? Environment.GetEnvironmentVariable("DEVCOORDINATOR_RUN_ID")
            ?? LocalRunIdentity;
        lock (InitializationGate)
        {
            string current = Path.Combine(artifacts, "native-session-current");
            string marker = Path.Combine(current, ".native-run-id");
            if (!File.Exists(marker) || File.ReadAllText(marker) != run)
            {
                current = Begin(artifacts);
                File.WriteAllText(marker, run);
            }
            string evidence = Path.Combine(current, testIdentity + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(evidence);
            return evidence;
        }
    }
}

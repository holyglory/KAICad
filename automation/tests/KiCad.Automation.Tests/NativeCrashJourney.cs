using System.Diagnostics;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    // Shared PSU/CPU acceptance journey (psu-cpu-fixture-and-ownership.md §1.9).
    // Lane 2D replaces this body when it delivers the journey.
    private static Task VerifyPsuCpuNativeCrash(NativeClient client, PsuCpuNativeContext context, Process native, int processId,
        string display, string evidence, string instanceId, CancellationToken token)
        => throw new AssertInconclusiveException("Phase 2 lane 2D has not delivered this journey");
}

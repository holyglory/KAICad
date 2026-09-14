using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyPcbDrcJobs(NativeClient client, DocumentSpecifier board,
        string evidence, CancellationToken token)
    {
        // This is a withdrawal assertion, not a DRC job success journey.
        // p23deb822a36256a6 must be qualified before restoring these commands.
        var before = await ObserveLifecycleState(client, board, token);
        var error = await Assert.ThrowsAsync<NativeApiException>(() =>
            client.InvokeAsync<StartPcbDrcJob, PcbDrcJobState>(new()
            {
                Document = board, ProcessEpoch = client.Epoch,
                ExpectedRevision = before.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D")
            }, token));
        Assert.AreEqual(5, error.Status, "The incomplete DRC worker must not be registered.");
        Assert.AreEqual(before, await ObserveLifecycleState(client, board, token));
    }
}

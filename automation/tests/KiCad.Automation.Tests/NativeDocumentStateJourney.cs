using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task<DocumentLifecycleState> ObserveLifecycleState(NativeClient client,
        DocumentSpecifier document, CancellationToken token)
    {
        var query = new ReadDocumentLifecycleState { Document = document.Clone() };
        var state = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(query, token);
        Assert.AreEqual(document, state.Document);
        Assert.IsTrue(Guid.TryParse(state.NativeIdentity, out _));
        Assert.IsTrue(Guid.TryParse(state.Revision.Epoch, out _));
        Assert.HasCount(64, state.StateSha256);
        Assert.IsTrue(state.StateSha256.All(character => char.IsAsciiHexDigitLower(character)));
        Assert.IsTrue(state.ProjectSettingsIncluded);
        Assert.IsFalse(state.CompleteChangeTracking);
        Assert.IsFalse(state.DiskBaselineChecked, "A content observation must not pretend to know loaded-file baselines.");
        Assert.IsNotEmpty(state.NativeFiles);
        Assert.IsTrue(state.NativeFiles.All(Path.IsPathFullyQualified));
        Assert.AreEqual(state, await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(query, token),
            "Reading the same native state must be deterministic and non-mutating.");
        return state;
    }
}

using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DiagramRequirementHistoryFileTests
{
    private static RequirementRevisionOrigin Origin() => new(RequirementRevisionActor.User, "Fixture user",
        new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero), "Fixture edit", [], []);

    private static DiagramRequirementHistory Initial() => new(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
        [new(Guid.NewGuid(), null, new("Power CPU", "Group rails", "Keep sensing quiet"), Origin(), [])]);

    [TestMethod]
    public async Task SaveReopenAndRestorePreserveEveryRevisionAndRejectStaleBytes()
    {
        string root = Directory.CreateTempSubdirectory("kicad-requirement-history-").FullName;
        try
        {
            string path = Path.Combine(root, "requirements.xml"); var initial = Initial();
            var created = await DiagramRequirementHistoryFiles.CreateAsync(root, path, initial);
            string original = await File.ReadAllTextAsync(path);
            Assert.AreEqual(created.ContentSha256, (await DiagramRequirementHistoryFiles.CreateAsync(root, path, initial)).ContentSha256);
            var draft = initial.StartDraft().Edit(DiagramRequirementField.Routing, "Top edge");
            var saved = await DiagramRequirementHistoryFiles.SaveAsync(root, path, created.ContentSha256,
                initial.Current.Id, draft, Guid.NewGuid(), Origin());
            var reopened = await DiagramRequirementHistoryFiles.ReadAsync(root, path, initial.Scope);
            Assert.AreEqual(saved.ContentSha256, reopened.ContentSha256); Assert.HasCount(2, reopened.History.Revisions);
            Assert.AreEqual("Top edge", reopened.History.Current.Requirements.Routing);
            Assert.AreEqual(initial.Current.Requirements, reopened.History.Inspect(initial.Current.Id).Requirements);
            Assert.AreNotEqual(original, await File.ReadAllTextAsync(path));
            await Assert.ThrowsExactlyAsync<AutomationException>(() => DiagramRequirementHistoryFiles.SaveAsync(root, path,
                created.ContentSha256, initial.Current.Id, draft, Guid.NewGuid(), Origin()));
            Assert.AreEqual(saved.ContentSha256, (await DiagramRequirementHistoryFiles.ReadAsync(root, path, initial.Scope)).ContentSha256);
            var restoredDraft = reopened.History.RestoreField(reopened.History.StartDraft(), initial.Current.Id, DiagramRequirementField.Routing);
            var restored = await DiagramRequirementHistoryFiles.SaveAsync(root, path, reopened.ContentSha256,
                reopened.History.Current.Id, restoredDraft, Guid.NewGuid(), Origin());
            Assert.HasCount(3, restored.History.Revisions);
            Assert.AreEqual(initial.Current.Requirements, restored.History.Current.Requirements);
            string beforeNoOp = await File.ReadAllTextAsync(path);
            var noOp = await DiagramRequirementHistoryFiles.SaveAsync(root, path, restored.ContentSha256,
                restored.History.Current.Id, restored.History.StartDraft(), Guid.NewGuid(), Origin());
            Assert.AreEqual(restored.ContentSha256, noOp.ContentSha256);
            Assert.AreEqual(beforeNoOp, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task FailedOrCancelledSaveLeavesTheHistoryAndDraftAvailable()
    {
        string root = Directory.CreateTempSubdirectory("kicad-requirement-history-").FullName;
        try
        {
            string path = Path.Combine(root, "requirements.xml"); var initial = Initial();
            var created = await DiagramRequirementHistoryFiles.CreateAsync(root, path, initial);
            var mine = initial.StartDraft().Edit(DiagramRequirementField.Routing, "Top edge");
            var remote = await DiagramRequirementHistoryFiles.SaveAsync(root, path, created.ContentSha256,
                initial.Current.Id, initial.StartDraft().Edit(DiagramRequirementField.Routing, "Bottom edge"), Guid.NewGuid(), Origin());
            string before = await File.ReadAllTextAsync(path);
            await Assert.ThrowsExactlyAsync<AutomationException>(() => DiagramRequirementHistoryFiles.SaveAsync(root, path,
                remote.ContentSha256, remote.History.Current.Id, mine, Guid.NewGuid(), Origin()));
            Assert.AreEqual(before, await File.ReadAllTextAsync(path)); Assert.AreEqual("Top edge", mine.Requirements.Routing);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => DiagramRequirementHistoryFiles.SaveAsync(root, path,
                remote.ContentSha256, remote.History.Current.Id, mine, Guid.NewGuid(), Origin(), token: cancel.Token));
            Assert.AreEqual(before, await File.ReadAllTextAsync(path));
            var choice = remote.History.PrepareMerge(mine).Choose(DiagramRequirementField.Routing, "Top edge");
            var recovered = await DiagramRequirementHistoryFiles.SaveAsync(root, path, remote.ContentSha256,
                remote.History.Current.Id, mine, Guid.NewGuid(), Origin(), [choice]);
            Assert.AreEqual("Top edge", recovered.History.Current.Requirements.Routing);
            Assert.AreEqual("Bottom edge", recovered.History.Inspect(remote.History.Current.Id).Requirements.Routing);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CreateNeverOverwritesAndWrongOwnersOrPathsCannotBeAdopted()
    {
        string root = Directory.CreateTempSubdirectory("kicad-requirement-history-").FullName;
        try
        {
            string path = Path.Combine(root, "requirements.xml"); var initial = Initial();
            await DiagramRequirementHistoryFiles.CreateAsync(root, path, initial); string before = await File.ReadAllTextAsync(path);
            await Assert.ThrowsExactlyAsync<AutomationException>(() => DiagramRequirementHistoryFiles.CreateAsync(root, path, Initial()));
            await Assert.ThrowsExactlyAsync<AutomationException>(() => DiagramRequirementHistoryFiles.ReadAsync(root, path,
                initial.Scope with { OwnerId = Guid.NewGuid() }));
            await Assert.ThrowsExactlyAsync<AutomationException>(() => DiagramRequirementHistoryFiles.CreateAsync(root, root, initial));
            await Assert.ThrowsExactlyAsync<AutomationException>(() => DiagramRequirementHistoryFiles.CreateAsync(root,
                Path.Combine(Path.GetDirectoryName(root)!, "not-in-selected-root.xml"), initial));
            Assert.AreEqual(before, await File.ReadAllTextAsync(path));
            // A history that continues another implementation's history can only live in the diagram holding both;
            // a separate file can neither create nor adopt one (it would show the later texts without the earlier ones).
            var derived = new DiagramRequirementHistory(initial.Scope, [initial.Current with { ParentId = Guid.NewGuid() }]);
            string derivedPath = Path.Combine(root, "derived.xml");
            await Assert.ThrowsExactlyAsync<AutomationException>(() => DiagramRequirementHistoryFiles.CreateAsync(root, derivedPath, derived));
            Assert.IsFalse(File.Exists(derivedPath));
            await File.WriteAllTextAsync(derivedPath, DiagramRequirementHistoryXml.Write(derived));
            Assert.AreEqual("invalid_requirement_history", (await Assert.ThrowsExactlyAsync<AutomationException>(() =>
                DiagramRequirementHistoryFiles.ReadAsync(root, derivedPath, initial.Scope))).Code);
            if (!OperatingSystem.IsWindows())
            {
                string linked = Path.Combine(root, "linked.xml"); File.CreateSymbolicLink(linked, path);
                await Assert.ThrowsExactlyAsync<AutomationException>(() => DiagramRequirementHistoryFiles.ReadAsync(root, linked, initial.Scope));
            }
        }
        finally { Directory.Delete(root, true); }
    }
}

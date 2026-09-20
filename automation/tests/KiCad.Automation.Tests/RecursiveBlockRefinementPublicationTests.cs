using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveBlockRefinementPublicationTests
{
    private static RefinementPublicationIntent Intent()
    {
        var graph = LinkedDiagramFixture.Create().Graph;
        // Standalone model fixture: no publication or file is asserted to exist.
        var input = new DiagramRefinementInput(Guid.NewGuid(), graph.DocumentId, new string('a', 64), [graph.SelectedRoot], [],
            "Keep the original requirement.", RecursiveBlockFixture.Origin(), []);
        return new(1, Path.Combine(Path.GetTempPath(), "fixture-diagram.xml"), input, input.SourceSha256, new string('b', 64),
            RefinementPublicationStage.Prepared, null, null, null);
    }
    private static PublicationFileObservation Present(string path, string hash) => new(path, PublicationFileStatus.Present, hash);
    private static PublicationFileObservation Missing(string path) => new(path, PublicationFileStatus.Missing, null);

    [TestMethod]
    public void UnattemptedIntentDoesNotCertifyAnAlreadyPresentCandidate()
    {
        var intent = Intent();
        Assert.ThrowsExactly<AutomationException>(() => (intent with { Input = null! }).Validate());
        Assert.ThrowsExactly<AutomationException>(() => (intent with { Version = 2 }).Validate());
        Assert.ThrowsExactly<AutomationException>(() => (intent with { AfterSha256 = intent.BeforeSha256 }).Validate());
        Assert.AreEqual(RefinementRecoveryDisposition.ResumePrepared, RefinementPublicationProof.Inspect(intent, Present(intent.DesignPath, intent.BeforeSha256)));
        Assert.AreEqual(RefinementRecoveryDisposition.NeedsReview, RefinementPublicationProof.Inspect(intent, Present(intent.DesignPath, intent.AfterSha256)));
        Assert.AreEqual(RefinementRecoveryDisposition.NeedsReview, RefinementPublicationProof.Inspect(intent, Missing(intent.DesignPath)));
        Assert.AreEqual(RefinementRecoveryDisposition.NeedsReview, RefinementPublicationProof.Inspect(intent,
            new(intent.DesignPath, PublicationFileStatus.Unreadable, null)));
    }

    [TestMethod]
    public void InterruptedUnixExchangeRequiresBothExactSides()
    {
        var original = Intent(); string stage = original.DesignPath + ".sync-fixture";
        var intent = original with { Stage = RefinementPublicationStage.Replacing, StagedPath = stage, RetainedPath = stage };
        var candidate = Present(stage, intent.AfterSha256); var previous = Present(stage, intent.BeforeSha256);
        Assert.AreEqual(RefinementRecoveryDisposition.ResumePrepared,
            RefinementPublicationProof.Inspect(intent, Present(intent.DesignPath, intent.BeforeSha256), candidate, candidate));
        Assert.AreEqual(RefinementRecoveryDisposition.ConfirmPublication,
            RefinementPublicationProof.Inspect(intent, Present(intent.DesignPath, intent.AfterSha256), previous, previous));
        Assert.AreEqual(RefinementRecoveryDisposition.NeedsReview,
            RefinementPublicationProof.Inspect(intent, Present(intent.DesignPath, intent.AfterSha256), candidate, candidate));
        Assert.AreEqual(RefinementRecoveryDisposition.NeedsReview,
            RefinementPublicationProof.Inspect(intent, Present(intent.DesignPath, intent.BeforeSha256), previous, previous));
        Assert.AreEqual(RefinementRecoveryDisposition.NeedsReview,
            RefinementPublicationProof.Inspect(intent, Present(intent.DesignPath, new string('c', 64)), previous, previous));
    }

    [TestMethod]
    public void WindowsBackupIsNotTheStagingFileAndUnreadableDoesNotMeanMissing()
    {
        var original = Intent(); string stage = original.DesignPath + ".sync-fixture";
        var intent = original with { Stage = RefinementPublicationStage.Replacing, StagedPath = stage, RetainedPath = stage + ".previous" };
        Assert.AreEqual(RefinementRecoveryDisposition.ResumePrepared, RefinementPublicationProof.Inspect(intent,
            Present(intent.DesignPath, intent.BeforeSha256), Present(stage, intent.AfterSha256), Missing(intent.RetainedPath)));
        Assert.AreEqual(RefinementRecoveryDisposition.ConfirmPublication, RefinementPublicationProof.Inspect(intent,
            Present(intent.DesignPath, intent.AfterSha256), Missing(stage), Present(intent.RetainedPath, intent.BeforeSha256)));
        Assert.AreEqual(RefinementRecoveryDisposition.NeedsReview, RefinementPublicationProof.Inspect(intent,
            Present(intent.DesignPath, intent.BeforeSha256), Present(stage, intent.AfterSha256), new(intent.RetainedPath, PublicationFileStatus.Unreadable, null)));
        Assert.ThrowsExactly<AutomationException>(() => RefinementPublicationProof.Inspect(intent,
            Present(intent.DesignPath, intent.AfterSha256), Missing(stage), Present(stage, intent.BeforeSha256)));
        var acknowledged = intent with { Stage = RefinementPublicationStage.Published, ConfirmedAt = DateTimeOffset.UtcNow };
        Assert.AreEqual(RefinementRecoveryDisposition.CompletedPreviously, RefinementPublicationProof.Inspect(acknowledged,
            Present(intent.DesignPath, new string('c', 64))));
        Assert.ThrowsExactly<AutomationException>(() => (acknowledged with { ConfirmedAt = null }).Validate());
    }
}

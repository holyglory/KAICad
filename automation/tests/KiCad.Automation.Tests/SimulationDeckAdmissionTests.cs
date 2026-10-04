using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using Kiapi.Common.Types;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SimulationDeckAdmissionTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("Automation divider\nV1 in 0 5\nR1 in out 1k\nR2 out 0 1k\n.op\n.end\n")]
    [DataRow("Long transient\r\nV1 in 0 PULSE(0 5 0 1n 1n 1m 2m)\r\nC1 out 0 1u\r\n.tran 1u 1000m\r\n.end\r\n")]
    [DataRow("Inline library\n.lib typical\n.model dmod d(is=1e-14)\n.endl\n.end\n")]
    [DataRow("Comments\n* .control is only mentioned here\nR1 a 0 1k ; .include is a comment\n.op\n.end\n")]
    [DataRow("Parameters\n.param rval=1k\nR1 a 0 {rval}\n.options savecurrents\n.op\n.end\n")]
    public void PlainCircuitsAndGeneratedDecksAreAdmitted(string netlist) =>
        SimulationDeckAdmission.Validate(netlist);

    [TestMethod]
    [DataRow("Divider\nR1 in 0 1k\n.control\nshell touch pwned\n.endc\n.end\n")]
    [DataRow("Divider\nR1 in 0 1k\n  .CONTROL\nrun\n.ENDC\n.end\n")]
    [DataRow("Divider\nR1 in 0 1k\n\t.controls\n.end\n")]
    [DataRow("Divider\n.exec\nshell id\n.endc\nR1 in 0 1k\n.end\n")]
    [DataRow("*ng_script\nshell id\n")]
    [DataRow("*NG_SCRIPT_WITH_EXTRA\nsource x\n")]
    [DataRow("Divider\n.include /etc/passwd\n.end\n")]
    [DataRow("Divider\n.INC models.cir\n.end\n")]
    [DataRow("Divider\n.incfoo models.cir\n.end\n")]
    [DataRow("Divider\n.lib /opt/models/tech.lib tt\n.end\n")]
    [DataRow("Divider\n.lib\n+ /opt/models/tech.lib tt\n.end\n")]
    [DataRow("Divider\nR1 in 0 1k\n.op\n\u0007.end\n")]
    public void CommandBlocksScriptsAndFileReadsAreRejected(string netlist)
    {
        var failure = Assert.ThrowsExactly<AutomationException>(() => SimulationDeckAdmission.Validate(netlist));
        Assert.AreEqual("simulation_deck_rejected", failure.Code);
    }

    [TestMethod]
    public void OversizedNetlistsAreRejected()
    {
        var failure = Assert.ThrowsExactly<AutomationException>(() =>
            SimulationDeckAdmission.Validate("Big\n" + new string('*', (1 << 20) + 1)));
        Assert.AreEqual("simulation_deck_rejected", failure.Code);
    }

    [TestMethod]
    public void CompletedSimulationVectorsRequireFiniteValuesAndValidComplexPairs()
    {
        var fixture = SimulationFixture();
        fixture.Result.Vectors.Add(new SimulationVector { Name = "v(out)", Values = { 1.0, 2.0 }, Complex = true });
        SimulationJobContract.ValidateStart(fixture.Request, fixture.Result, fixture.ProcessEpoch);

        var nonFinite = fixture.Result.Clone();
        nonFinite.Vectors[0].Values[1] = double.NaN;
        RejectResult(fixture.Request, nonFinite, fixture.ProcessEpoch);

        var oddComplex = fixture.Result.Clone();
        oddComplex.Vectors[0].Values.Add(3.0);
        RejectResult(fixture.Request, oddComplex, fixture.ProcessEpoch);

        var infiniteProgress = fixture.Result.Clone();
        infiniteProgress.Progress = double.PositiveInfinity;
        RejectResult(fixture.Request, infiniteProgress, fixture.ProcessEpoch);

        var outOfRangeProgress = fixture.Result.Clone();
        outOfRangeProgress.Progress = 101;
        RejectResult(fixture.Request, outOfRangeProgress, fixture.ProcessEpoch);
    }

    [TestMethod]
    public void SimulationDependencyEvidenceBindsIdentityAndDeckProvenance()
    {
        var fixture = SimulationFixture();
        const string deckSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var evidence = new NativeCapabilitySimulationDependencies
        {
            Document = fixture.Document.Clone(), JobId = fixture.JobId, OperationId = fixture.OperationId,
            ProcessEpoch = fixture.ProcessEpoch, GeneratedDeckSha256 = deckSha256,
            DependencyCaptureComplete = false
        };
        evidence.DependencyPaths.Add("/tmp/model.lib");
        evidence.Warnings.Add("Dependency paths were not enumerated");

        SimulationJobContract.ValidateDependencies(evidence, fixture.Document, fixture.ProcessEpoch,
            fixture.JobId, fixture.OperationId, deckSha256);

        var staleOperation = evidence.Clone();
        staleOperation.OperationId = "33333333-3333-3333-3333-333333333333";
        RejectDependencies(staleOperation, fixture, deckSha256);

        var staleHash = evidence.Clone();
        staleHash.GeneratedDeckSha256 = new string('b', 64);
        RejectDependencies(staleHash, fixture, deckSha256);

        var duplicatePath = evidence.Clone();
        duplicatePath.DependencyPaths.Add("/tmp/model.lib");
        RejectDependencies(duplicatePath, fixture, deckSha256);

        var missingWarning = evidence.Clone();
        missingWarning.Warnings.Clear();
        RejectDependencies(missingWarning, fixture, deckSha256);

        var completeWithWarning = evidence.Clone();
        completeWithWarning.DependencyCaptureComplete = true;
        RejectDependencies(completeWithWarning, fixture, deckSha256);
    }

    private static void RejectResult(StartSimulationJob request, SimulationJobState result, string processEpoch)
    {
        var failure = Assert.ThrowsExactly<AutomationException>(() =>
            SimulationJobContract.ValidateStart(request, result, processEpoch));
        Assert.AreEqual("invalid_simulation_result", failure.Code);
    }

    private static void RejectDependencies(NativeCapabilitySimulationDependencies result,
        (DocumentSpecifier Document, string ProcessEpoch, string JobId, string OperationId,
            StartSimulationJob Request, SimulationJobState Result) fixture, string expectedDeckSha256)
    {
        var failure = Assert.ThrowsExactly<AutomationException>(() =>
            SimulationJobContract.ValidateDependencies(result, fixture.Document, fixture.ProcessEpoch,
                fixture.JobId, fixture.OperationId, expectedDeckSha256));
        Assert.AreEqual("invalid_simulation_result", failure.Code);
    }

    private static (DocumentSpecifier Document, string ProcessEpoch, string JobId, string OperationId,
        StartSimulationJob Request, SimulationJobState Result) SimulationFixture()
    {
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypeSchematic,
            Project = new() { Name = "simulation-contract", Path = "/tmp/kicad-simulation-contract" },
            SheetPath = new()
        };
        document.SheetPath.Path.Add(new KIID { Value = "11111111-1111-1111-1111-111111111111" });
        const string processEpoch = "22222222-2222-2222-2222-222222222222";
        const string jobId = "33333333-3333-3333-3333-333333333333";
        const string operationId = "44444444-4444-4444-4444-444444444444";
        var request = new StartSimulationJob
        {
            Document = document.Clone(), OperationId = operationId, ProcessEpoch = processEpoch
        };
        var result = new SimulationJobState
        {
            Document = document.Clone(), JobId = jobId, OperationId = operationId,
            ProcessEpoch = processEpoch, Status = SimulationJobStatus.SimjsCompleted,
            Progress = 100, WorkerFinished = true
        };
        return (document, processEpoch, jobId, operationId, request, result);
    }
}

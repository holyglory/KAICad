using KiCad.Automation.Mcp;
using KiCad.Automation.Model;

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
}

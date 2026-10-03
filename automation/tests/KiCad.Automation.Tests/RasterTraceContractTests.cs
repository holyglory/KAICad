using System.Text.Json;
using KiCad.Automation.Mcp;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

// Managed contract coverage for the native raster proposal envelope. The native
// adapter harness separately exercises Potrace and the retained hole topology.
[TestClass]
public sealed class RasterTraceContractTests
{
    [TestMethod]
    public void TraceJsonRetainsAcceptanceDecisionAndRejectionCode()
    {
        JsonElement accepted = RasterTraceTools.FormatTraceResult(new RasterTraceResult { Accepted = true });
        Assert.IsTrue(accepted.GetProperty("accepted").GetBoolean());

        JsonElement rejected = RasterTraceTools.FormatTraceResult(new RasterTraceResult
        {
            RejectionCode = "trace_complexity"
        });
        Assert.IsFalse(rejected.GetProperty("accepted").GetBoolean());
        Assert.AreEqual("trace_complexity", rejected.GetProperty("rejectionCode").GetString());
    }
}

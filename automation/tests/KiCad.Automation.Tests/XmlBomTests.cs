using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class XmlBomTests
{
    [TestMethod]
    public void TypedCircuitAndEngineeringCodecsAcceptUtf8BomWithoutChangingCanonicalOutput()
    {
        var circuit = CircuitXmlTests.Fixture(); string circuitXml = CircuitXml.Write(circuit);
        Assert.AreEqual(circuitXml, CircuitXml.Write(CircuitXml.Read("\uFEFF" + circuitXml)));
        var (engineering, library) = EngineeringDesignXmlTests.Fixture(); string engineeringXml = EngineeringDesignXml.Write(engineering, [library]);
        Assert.AreEqual(engineeringXml, EngineeringDesignXml.Write(EngineeringDesignXml.Read("\uFEFF" + engineeringXml, [library]), [library]));
        var (design, designLibrary) = SchematicDesignTests.Fixture(); string designXml = SchematicDesignXml.Write(design, [designLibrary]);
        Assert.AreEqual(designXml, SchematicDesignXml.Write(SchematicDesignXml.Read("\uFEFF" + designXml, [designLibrary]), [designLibrary]));
    }

    [TestMethod]
    public void BomDoesNotWeakenStrictUnknownFieldOrDtdRejection()
    {
        string circuit = "\uFEFF" + CircuitXml.Write(CircuitXmlTests.Fixture());
        Assert.AreEqual("invalid_circuit", Assert.ThrowsExactly<AutomationException>(() => CircuitXml.Read(circuit.Replace("</parts>", "<future /></parts>", StringComparison.Ordinal))).Code);
        Assert.AreEqual("invalid_circuit", Assert.ThrowsExactly<AutomationException>(() => CircuitXml.Read("\uFEFF<!DOCTYPE circuit SYSTEM 'file:///not-read'>" + circuit[1..])).Code);
    }
}

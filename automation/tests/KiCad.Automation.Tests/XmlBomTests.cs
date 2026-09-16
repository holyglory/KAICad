using KiCad.Automation.Model;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class XmlBomTests
{
    [TestMethod]
    public void CircuitAndEngineeringCodecsAcceptBomAndStillRejectDtd()
    {
        var circuit = CircuitXmlTests.Fixture(); string xml = CircuitXml.Write(circuit);
        Assert.AreEqual(xml, CircuitXml.Write(CircuitXml.Read("\uFEFF" + xml)));
        var (design, library) = EngineeringDesignXmlTests.Fixture(); string engineering = EngineeringDesignXml.Write(design, [library]);
        Assert.AreEqual(engineering, EngineeringDesignXml.Write(EngineeringDesignXml.Read("\uFEFF" + engineering, [library]), [library]));
        Assert.AreEqual("invalid_circuit", Assert.ThrowsExactly<AutomationException>(() => CircuitXml.Read("\uFEFF<!DOCTYPE circuit SYSTEM 'file:///not-read'>" + xml)).Code);
    }
}

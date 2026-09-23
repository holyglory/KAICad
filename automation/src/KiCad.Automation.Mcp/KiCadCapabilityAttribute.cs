namespace KiCad.Automation.Mcp;

// Declares the service capability row of one MCP tool method; the row's name is
// the tool's McpServerTool name. Scope groups the capability (for example
// "schematic-design"), source names what implements it (for example
// "compiled-mcp plus native-api") and revisionContract lists the target and
// revision identities a caller must supply. Every new tool carries it.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class KiCadCapabilityAttribute(string scope, string source, string revisionContract) : Attribute
{
    public string Scope { get; } = scope;
    public string Source { get; } = source;
    public string RevisionContract { get; } = revisionContract;
}

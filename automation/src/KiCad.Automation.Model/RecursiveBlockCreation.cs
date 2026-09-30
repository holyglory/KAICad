namespace KiCad.Automation.Model;

public sealed partial class RecursiveBlockGraph
{
    /// <summary>A new diagram with one empty root block (contract rbg-v2 section 4.11): one implementation,
    /// one first revision without children or diagram content, one requirement revision and no receipt.
    /// Every identity is derived from the operation, so a retried create recognises its own document.</summary>
    public static RecursiveBlockGraph CreateEmpty(Guid operationId, string rootName, string implementationName,
        DiagramRequirements fields, RequirementRevisionOrigin origin)
    {
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(rootName) || string.IsNullOrWhiteSpace(implementationName)
            || fields is null || origin is null)
            throw new AutomationException("invalid_diagram_create_request",
                "Creating a diagram needs an operation identity, a root name, an implementation name, requirement fields and an origin.");
        fields.Validate(); origin.Validate();
        if (!origin.InputIds.Contains(operationId))
            throw new AutomationException("invalid_diagram_create_request", "The creation origin must name its operation among its inputs.");
        Guid document = DiagramIdentity.Derive(operationId, "document"), block = DiagramIdentity.Derive(operationId, "root-block");
        Guid state = DiagramIdentity.Derive(operationId, "state:root"), revision = DiagramIdentity.Derive(operationId, "revision:root");
        Guid requirements = DiagramIdentity.Derive(operationId, "requirements:root");
        var selection = new BlockSelection(block, state, revision);
        return new(document, selection, [new BlockDesignState(state, block, implementationName, revision)],
            [new RecursiveBlockRevision(selection, null, rootName, requirements, [], origin)],
            [new DiagramRequirementHistory(new(document, block, state), [new(requirements, null, fields, origin, [])])]);
    }
}

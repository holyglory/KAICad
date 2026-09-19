namespace KiCad.Automation.Model;

// Repository composition, not a second electrical or native document model.
public sealed record HardwareDesign(Guid Id, string Name, string ProjectPath, string ModelPath,
    IReadOnlyList<HardwarePort> Ports);
public sealed record HardwarePort(Guid Id, string Name, string Description);
public sealed record HardwareDocument(Guid Id, string Path, string Revision, string Purpose);
public sealed record HardwareLibrary(Guid Id, string Path, string Revision);
public sealed record HardwareEndpoint(Guid DesignId, Guid PortId);
public sealed record HardwareInterface(Guid Id, string Name, string Description,
    IReadOnlyList<HardwareEndpoint> Endpoints);
public sealed record HardwareRepository(Guid Id, string Name, IReadOnlyList<HardwareDesign> Designs,
    IReadOnlyList<HardwareDocument> Documents, IReadOnlyList<HardwareLibrary> Libraries,
    IReadOnlyList<HardwareInterface> Interfaces)
{
    public void Validate()
    {
        var identities = new HashSet<Guid>();
        void Identity(Guid id)
        {
            if (id == Guid.Empty || !identities.Add(id)) throw Invalid("Repository entities require distinct non-empty identities.");
        }
        Identity(Id); Text(Name, "Repository name");
        var writablePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var design in Designs)
        {
            Identity(design.Id); Text(design.Name, "Design name");
            foreach (string path in new[] { design.ProjectPath, design.ModelPath })
            {
                ValidatePath(path);
                if (!writablePaths.Add(path)) throw Invalid("Child designs cannot share a writable project or model file.");
            }
            var portNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var port in design.Ports)
            {
                Identity(port.Id); Text(port.Name, "Port name");
                if (!portNames.Add(port.Name)) throw Invalid("Port names must be unique within a child design.");
            }
        }
        foreach (var document in Documents)
        {
            Identity(document.Id); ValidatePath(document.Path); Text(document.Revision, "Source revision");
            Text(document.Purpose, "Source purpose");
        }
        foreach (var library in Libraries)
        {
            Identity(library.Id); ValidatePath(library.Path); Text(library.Revision, "Library revision");
        }
        var designs = Designs.ToDictionary(d => d.Id);
        var connected = new HashSet<HardwareEndpoint>();
        foreach (var connection in Interfaces)
        {
            Identity(connection.Id); Text(connection.Name, "Interface name");
            if (connection.Endpoints.Count < 2) throw Invalid("An inter-board interface requires at least two endpoints.");
            foreach (var endpoint in connection.Endpoints)
            {
                if (!designs.TryGetValue(endpoint.DesignId, out var design) || !design.Ports.Any(p => p.Id == endpoint.PortId))
                    throw Invalid("An interface endpoint must identify a port owned by its child design.");
                if (!connected.Add(endpoint)) throw Invalid("A port cannot occur twice or belong to competing interfaces.");
            }
            if (connection.Endpoints.Select(e => e.DesignId).Distinct().Count() < 2)
                throw Invalid("An inter-board interface must connect distinct child designs.");
        }
    }

    private static void Text(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) throw Invalid(name + " is required.");
    }
    public static void ValidatePath(string path)
    {
        // A portable Git-relative spelling, independent of the current host OS.
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':')
            || path.Any(char.IsControl) || path.Split('/').Any(p => p is "" or "." or ".."))
            throw Invalid("Paths must use unambiguous repository-relative slash-separated names.");
    }
    internal static AutomationException Invalid(string message) => new("invalid_hardware", message);
}

namespace KiCad.Automation.Model;

public enum StructuralPortSide { Left, Right, Top, Bottom }
public sealed record StructuralPoint(long XNm, long YNm);
public sealed record StructuralBlockPlacement(Guid BlockId, long XNm, long YNm,
    long WidthNm, long HeightNm, bool Locked = false, uint? FillRgb = null);
public sealed record StructuralPortPlacement(Guid PortId, StructuralPortSide Side, long OffsetNm);
public sealed record StructuralConnectionPlacement(Guid ConnectionId, IReadOnlyList<StructuralPoint> Waypoints,
    StructuralPoint? Label = null, bool Locked = false);

/// <summary>Optional structural-canvas geometry, not physical PCB dimensions or
/// electrical meaning. Coordinates use the same exact 100 nm quantum as native
/// schematic geometry, but belong to a distinct view.</summary>
public sealed record StructuralPresentation(IReadOnlyList<StructuralBlockPlacement> Blocks,
    IReadOnlyList<StructuralPortPlacement> Ports, IReadOnlyList<StructuralConnectionPlacement> Connections)
{
    public const long QuantumNm = 100;
    public const long MinimumNm = (long)int.MinValue * QuantumNm;
    public const long MaximumNm = (long)int.MaxValue * QuantumNm;

    public void Validate(StructuralDiagram diagram)
    {
        var blocks = diagram.Blocks.Select(b => b.Id).ToHashSet();
        var ports = diagram.Ports.ToDictionary(p => p.Id);
        var connections = diagram.Connections.Select(c => c.Id).ToHashSet();
        var placed = new Dictionary<Guid, StructuralBlockPlacement>();
        foreach (var block in Blocks)
        {
            if (!blocks.Contains(block.BlockId) || !placed.TryAdd(block.BlockId, block))
                throw Invalid("Block presentation requires one exact existing owner.");
            Coordinate(block.XNm); Coordinate(block.YNm);
            if (block.WidthNm <= 0 || block.HeightNm <= 0 || block.FillRgb is > 0xffffff)
                throw Invalid("Block dimensions must be positive and fill colors must fit RGB.");
            Coordinate(block.WidthNm); Coordinate(block.HeightNm);
            Coordinate(block.XNm + block.WidthNm); Coordinate(block.YNm + block.HeightNm);
        }
        var portIds = new HashSet<Guid>();
        foreach (var port in Ports)
        {
            if (!ports.TryGetValue(port.PortId, out var owner) || !portIds.Add(port.PortId)
                || !placed.TryGetValue(owner.BlockId, out var block) || !Enum.IsDefined(port.Side))
                throw Invalid("Port presentation requires its exact owner and an explicit block placement.");
            Coordinate(port.OffsetNm);
            long sideLength = port.Side is StructuralPortSide.Left or StructuralPortSide.Right ? block.HeightNm : block.WidthNm;
            if (port.OffsetNm < 0 || port.OffsetNm > sideLength)
                throw Invalid("Port offset lies outside its block edge.");
        }
        var connectionIds = new HashSet<Guid>();
        foreach (var connection in Connections)
        {
            if (!connections.Contains(connection.ConnectionId) || !connectionIds.Add(connection.ConnectionId))
                throw Invalid("Connection presentation requires one exact existing owner.");
            foreach (var point in connection.Waypoints) { Coordinate(point.XNm); Coordinate(point.YNm); }
            if (connection.Label is { } label) { Coordinate(label.XNm); Coordinate(label.YNm); }
        }
    }

    private static void Coordinate(long value)
    {
        if (value < MinimumNm || value > MaximumNm || value % QuantumNm != 0)
            throw Invalid("Structural coordinates must fit the native range and 100 nm quantum.");
    }
    private static AutomationException Invalid(string message) => new("invalid_structural_presentation", message);
}

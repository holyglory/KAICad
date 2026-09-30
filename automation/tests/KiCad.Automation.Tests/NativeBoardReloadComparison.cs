using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;

namespace KiCad.Automation.Tests;

// Test-only comparison for nonempty top-level routed-board fixtures. A native
// reload regenerates BOARD's UUID; persisted track/via UUIDs are not replaced.
internal static class NativeBoardReloadComparison
{
    public static GetItemsResponse RebindRuntimeContainer(GetItemsResponse expected, GetItemsResponse actual)
    {
        _ = Root(expected);
        string root = Root(actual);
        var rebound = expected.Clone();
        for (int index = 0; index < rebound.Items.Count; ++index)
        {
            var item = rebound.Items[index];
            if (item.Is(Track.Descriptor))
            {
                var track = item.Unpack<Track>();
                track.Parent = new() { Value = root };
                rebound.Items[index] = Any.Pack(track);
            }
            else
            {
                var via = item.Unpack<Via>();
                via.Parent = new() { Value = root };
                rebound.Items[index] = Any.Pack(via);
            }
        }
        return rebound;
    }

    private static string Root(GetItemsResponse snapshot)
    {
        var roots = snapshot.Items.Select(item =>
        {
            var parent = item.Is(Track.Descriptor) ? item.Unpack<Track>().Parent
                : item.Is(Via.Descriptor) ? item.Unpack<Via>().Parent
                : throw new InvalidDataException("Only top-level tracks and vias belong to this fixture comparison.");
            return parent?.Value ?? throw new InvalidDataException("The native item has no runtime container.");
        }).Distinct(StringComparer.Ordinal).ToArray();
        if (roots.Length != 1 || !Guid.TryParse(roots[0], out _))
            throw new InvalidDataException("The fixture must have one valid runtime board container.");
        return roots[0];
    }
}


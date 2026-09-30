using System.Buffers.Binary;
using System.IO.Compression;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

/// <summary>Where KiCad actually put ink in an offscreen render, in sheet nanometres, so a measured glyph box can be
/// compared with the pixels the schematic painter produced (standing correction kicad-outline-text-native-pixels).
/// Reads only the 8-bit, non-interlaced RGB or RGBA PNG that KiCad's offscreen renderer writes and refuses anything
/// else rather than guessing.</summary>
internal static class NativePresentationRaster
{
    internal sealed record Raster(int Width, int Height, byte[] Rgba)
    {
        public (byte R, byte G, byte B) At(int x, int y)
        {
            int offset = (y * Width + x) * 4;
            return (Rgba[offset], Rgba[offset + 1], Rgba[offset + 2]);
        }
    }

    /// <summary>The ink box of <paramref name="png"/> in sheet coordinates. The background is the colour that fills the
    /// image border; a pixel is ink when it differs from it by at least half of the strongest difference in the image,
    /// which puts every edge where the painted stroke covers about half a pixel. The box spans whole pixels: a
    /// pixel's left edge maps through the viewport's origin and its right edge one pixel step further.</summary>
    internal static (PresentationBounds Ink, double PixelNm) InkBounds(byte[] png, SchematicViewport viewport)
    {
        if (Math.Abs(viewport.PixelXDyNm) > 1e-6 || Math.Abs(viewport.PixelYDxNm) > 1e-6
            || viewport.PixelXDxNm <= 0 || viewport.PixelYDyNm <= 0)
            throw new InvalidDataException("An axis-aligned offscreen view is required to map pixels to the sheet.");
        var ink = Ink(Decode(png));
        long X(int pixel) => (long)Math.Round(viewport.OriginXNm + pixel * viewport.PixelXDxNm);
        long Y(int pixel) => (long)Math.Round(viewport.OriginYNm + pixel * viewport.PixelYDyNm);
        return (new PresentationBounds(X(ink.Left), Y(ink.Top), X(ink.Right + 1), Y(ink.Bottom + 1)),
            Math.Max(viewport.PixelXDxNm, viewport.PixelYDyNm));
    }

    /// <summary>How well two paintings of one text at one scale agree in shape, as the smaller of the shares of each painting's
    /// ink pixels that lie within one pixel of the other's ink (1 when every ink pixel of each has one of the other beside it),
    /// with both paintings aligned on their ink boxes. Turned compares <paramref name="turned"/> with <paramref name="upright"/>
    /// turned half round; Unturned compares it with <paramref name="upright"/> as it is. Text painted upside down agrees with
    /// its upright painting turned half round, and much less with it unturned unless the text reads the same both ways, although
    /// both fill a box of the same size. Where each painting lies does not matter: KiCad turns a field about its anchor, which
    /// is not the middle of its text unless the text is centred.</summary>
    internal static (double Turned, double Unturned) HalfTurnAgreement(byte[] upright, byte[] turned)
    {
        var a = Ink(Decode(upright));
        var b = Ink(Decode(turned));
        // Ink pixels relative to the top-left corner of their ink box.
        HashSet<(int X, int Y)> Points(InkMask ink, bool halfTurn)
        {
            var points = new HashSet<(int X, int Y)>();
            for (int y = ink.Top; y <= ink.Bottom; ++y)
            for (int x = ink.Left; x <= ink.Right; ++x)
                if (ink.Mask[y * ink.Width + x])
                    points.Add(halfTurn ? (ink.Right - x, ink.Bottom - y) : (x - ink.Left, y - ink.Top));
            return points;
        }
        static double Share(HashSet<(int X, int Y)> from, HashSet<(int X, int Y)> to)
        {
            if (from.Count == 0) return 0;
            int near = 0;
            foreach (var (x, y) in from)
            {
                bool found = false;
                for (int dy = -1; dy <= 1 && !found; ++dy)
                for (int dx = -1; dx <= 1 && !found; ++dx)
                    found = to.Contains((x + dx, y + dy));
                if (found) ++near;
            }
            return (double)near / from.Count;
        }
        static double Agreement(HashSet<(int X, int Y)> first, HashSet<(int X, int Y)> second) => Math.Min(Share(first, second), Share(second, first));
        var other = Points(b, halfTurn: false);
        return (Agreement(Points(a, halfTurn: true), other), Agreement(Points(a, halfTurn: false), other));
    }

    // The ink pixels of a painting and their box, found as InkBounds describes; ink reaching the image edge is refused.
    private sealed record InkMask(int Width, int Height, bool[] Mask, int Left, int Top, int Right, int Bottom);

    private static InkMask Ink(Raster raster)
    {
        var border = new Dictionary<(byte, byte, byte), int>();
        void Sample(int x, int y) => border[raster.At(x, y)] = border.GetValueOrDefault(raster.At(x, y)) + 1;
        for (int x = 0; x < raster.Width; ++x) { Sample(x, 0); Sample(x, raster.Height - 1); }
        for (int y = 0; y < raster.Height; ++y) { Sample(0, y); Sample(raster.Width - 1, y); }
        var background = border.MaxBy(p => p.Value).Key;
        int Difference((byte R, byte G, byte B) c) =>
            Math.Abs(c.R - background.Item1) + Math.Abs(c.G - background.Item2) + Math.Abs(c.B - background.Item3);
        int strongest = 0;
        for (int y = 0; y < raster.Height; ++y)
        for (int x = 0; x < raster.Width; ++x)
            strongest = Math.Max(strongest, Difference(raster.At(x, y)));
        if (strongest < 64) throw new InvalidDataException("The offscreen view contains no painted ink.");
        var mask = new bool[raster.Width * raster.Height];
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (int y = 0; y < raster.Height; ++y)
        for (int x = 0; x < raster.Width; ++x)
        {
            if (2 * Difference(raster.At(x, y)) < strongest) continue;
            mask[y * raster.Width + x] = true;
            left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        if (left == 0 || top == 0 || right == raster.Width - 1 || bottom == raster.Height - 1)
            throw new InvalidDataException("Ink reaches the edge of the offscreen view; the region does not contain the whole text.");
        return new(raster.Width, raster.Height, mask, left, top, right, bottom);
    }

    internal static Raster Decode(byte[] png)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(signature)) throw new InvalidDataException("Not a PNG image.");
        int width = 0, height = 0, channels = 0;
        using var compressed = new MemoryStream();
        for (int offset = 8; offset + 12 <= png.Length;)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
            string type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            if (type == "IHDR")
            {
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[..4]));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)));
                byte depth = data[8], colour = data[9], interlace = data[12];
                channels = colour switch { 2 => 3, 6 => 4, _ => 0 };
                if (depth != 8 || channels == 0 || interlace != 0)
                    throw new InvalidDataException($"Unsupported PNG form: depth {depth}, colour type {colour}, interlace {interlace}.");
            }
            else if (type == "IDAT") compressed.Write(data);
            else if (type == "IEND") break;
            offset += 12 + length;
        }
        if (width <= 0 || height <= 0) throw new InvalidDataException("The PNG has no image header.");
        compressed.Position = 0;
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        int stride = width * channels;
        var previous = new byte[stride];
        var current = new byte[stride];
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; ++y)
        {
            int filter = inflater.ReadByte();
            inflater.ReadExactly(current);
            for (int i = 0; i < stride; ++i)
            {
                int a = i >= channels ? current[i - channels] : 0, b = previous[i], c = i >= channels ? previous[i - channels] : 0;
                current[i] = (byte)(current[i] + filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException("Unknown PNG row filter " + filter + ".")
                });
            }
            for (int x = 0; x < width; ++x)
            {
                int target = (y * width + x) * 4, source = x * channels;
                rgba[target] = current[source]; rgba[target + 1] = current[source + 1]; rgba[target + 2] = current[source + 2];
                rgba[target + 3] = channels == 4 ? current[source + 3] : (byte)255;
            }
            (previous, current) = (current, previous);
        }
        return new(width, height, rgba);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}

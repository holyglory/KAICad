using System.Globalization;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

internal static class RasterTraceProjectionAssertions
{
    internal const long SerializerToleranceNm = 100;

    internal readonly record struct Point(long XNm, long YNm);

    internal readonly record struct Polygon(string NativeId, string PieceId,
        IReadOnlyList<Point> Points);

    internal static void AssertAgainstProposal(JsonElement trace,
        IReadOnlyCollection<string>? expectedNativeIds, IEnumerable<Polygon> polygons,
        long originXNm, long originYNm, string context)
    {
        JsonElement[] expectedPieces = trace.GetProperty("filledPieces").EnumerateArray().ToArray();
        Polygon[] actualPolygons = polygons.ToArray();
        Assert.AreEqual(expectedPieces.Length, actualPolygons.Length, context + ": piece count");

        string[] actualIds = actualPolygons.Select(polygon => polygon.NativeId).ToArray();
        Assert.AreEqual(actualIds.Length, actualIds.Distinct(StringComparer.Ordinal).Count(),
            context + ": projected native IDs must be unique");
        if (expectedNativeIds is not null)
        {
            string[] expectedIds = expectedNativeIds.ToArray();
            Assert.AreEqual(expectedIds.Length, expectedIds.Distinct(StringComparer.Ordinal).Count(),
                context + ": expected native IDs must be unique");
            CollectionAssert.AreEquivalent(expectedIds, actualIds, context + ": native IDs");
        }

        string[] expectedPieceIds = expectedPieces
            .Select(piece => piece.GetProperty("id").GetString() ?? string.Empty).ToArray();
        string[] actualPieceIds = actualPolygons.Select(polygon => polygon.PieceId).ToArray();
        Assert.IsTrue(expectedPieceIds.All(pieceId => pieceId.Length > 0),
            context + ": trace piece IDs must be present");
        Assert.AreEqual(expectedPieceIds.Length, expectedPieceIds.Distinct(StringComparer.Ordinal).Count(),
            context + ": trace piece IDs must be unique");
        Assert.AreEqual(actualPieceIds.Length, actualPieceIds.Distinct(StringComparer.Ordinal).Count(),
            context + ": projected piece IDs must be unique");
        CollectionAssert.AreEquivalent(expectedPieceIds, actualPieceIds, context + ": piece IDs");

        Dictionary<string, Polygon> actualByPiece = actualPolygons.ToDictionary(
            polygon => polygon.PieceId, StringComparer.Ordinal);
        foreach (JsonElement expectedPiece in expectedPieces)
        {
            string pieceId = expectedPiece.GetProperty("id").GetString()!;
            Assert.IsTrue(actualByPiece.ContainsKey(pieceId), context + ": missing piece " + pieceId);
            Polygon actual = actualByPiece[pieceId];

            Point[] expectedPoints = expectedPiece.GetProperty("points").EnumerateArray()
                .Select(point => new Point(
                    ReadLong(point.GetProperty("xNm")),
                    ReadLong(point.GetProperty("yNm"))))
                .ToArray();
            Point[] actualPoints = Canonicalize(actual.Points, originXNm, originYNm);
            Assert.AreEqual(expectedPoints.Length, actualPoints.Length,
                context + ": point count for " + pieceId);
            for (int index = 0; index < expectedPoints.Length; index++)
            {
                long deltaX = Math.Abs(expectedPoints[index].XNm - actualPoints[index].XNm);
                long deltaY = Math.Abs(expectedPoints[index].YNm - actualPoints[index].YNm);
                Assert.IsTrue(deltaX <= SerializerToleranceNm && deltaY <= SerializerToleranceNm,
                    $"{context}: {pieceId} point {index} expected "
                    + $"{expectedPoints[index].XNm}:{expectedPoints[index].YNm}, "
                    + $"actual {actualPoints[index].XNm}:{actualPoints[index].YNm}, "
                    + $"tolerance {SerializerToleranceNm}nm");
            }
        }
    }

    private static Point[] Canonicalize(IReadOnlyList<Point> points, long originXNm, long originYNm)
    {
        Point[] local = points.Select(point => new Point(point.XNm - originXNm, point.YNm - originYNm)).ToArray();
        if (local.Length > 1 && local[0] == local[^1])
            Array.Resize(ref local, local.Length - 1);
        return local;
    }

    private static long ReadLong(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? long.Parse(value.GetString()!, CultureInfo.InvariantCulture)
        : value.GetInt64();
}

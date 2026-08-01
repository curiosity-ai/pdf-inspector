using System.Collections.Concurrent;
using Xunit;

namespace PdfInspector.Tests;

/// <summary>
/// Proves the public API can be driven from several threads at once.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in the pipeline is parallel internally — the reference crate enables
/// lopdf's <c>rayon</c> feature but never calls a parallel iterator itself, and
/// this port follows it. What callers need instead is the ability to process
/// several documents concurrently, and that turns on the shared state the port
/// added for its own sake: the interned operator table, the small-integer
/// cache, the standard encoding tables, and above all the bundled-CMap caches
/// in <see cref="ToUnicode.BuiltinCMaps"/>, which hand a parsed CMap to every
/// document that asks for that character collection.
/// </para>
/// <para>
/// Reading the code is not proof, so these tests establish a sequential
/// baseline and then assert that concurrent runs reproduce it exactly. A cache
/// handing out a shared mutable instance, or a document leaking state into a
/// neighbour, shows up as a mismatch rather than a crash — which is why the
/// comparison is against full output and not merely "did not throw".
/// </para>
/// </remarks>
public sealed class ConcurrencyTests
{
    private static List<string> Fixtures() =>
        [.. Directory.EnumerateFiles(TestPaths.Fixtures, "*.pdf")
            // The encrypted fixture needs a password no test supplies.
            .Where(path => !Path.GetFileName(path).StartsWith("encrypted-", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)];

    private static string Fingerprint(PdfProcessResult result) =>
        $"{result.PdfType}|{result.Markdown?.Length ?? -1}|{result.Markdown ?? "<null>"}";

    /// <summary>
    /// Every fixture, processed concurrently, produces what it produces alone.
    /// </summary>
    [Fact]
    public void ConcurrentProcessingMatchesSequentialOutput()
    {
        var fixtures = Fixtures();
        Assert.NotEmpty(fixtures);

        var baseline = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in fixtures)
        {
            baseline[path] = Fingerprint(PdfProcessor.ProcessPdf(path));
        }

        // Several passes over the whole set, so a document meets neighbours that
        // were mid-flight rather than only ones that had finished.
        var work = Enumerable.Range(0, 3).SelectMany(_ => fixtures).ToList();
        var mismatches = new ConcurrentBag<string>();

        Parallel.ForEach(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(Environment.ProcessorCount, 4) },
            path =>
            {
                var actual = Fingerprint(PdfProcessor.ProcessPdf(path));
                if (!string.Equals(actual, baseline[path], StringComparison.Ordinal))
                {
                    mismatches.Add(Path.GetFileName(path));
                }
            });

        Assert.Empty(mismatches);
    }

    /// <summary>
    /// The bundled-CMap caches under contention.
    /// </summary>
    /// <remarks>
    /// <c>shinagawa_identity_h</c> is the fixture that reaches
    /// <c>BuiltinCMaps.ForOrdering</c>, so running many copies of it at once
    /// puts every thread on the same cache entry. If the cache handed out its
    /// stored instance rather than a clone, one document's adjustment to
    /// <c>CodeByteLength</c> or a merge into <c>CharMap</c> would be visible to
    /// the others and the output would diverge.
    /// </remarks>
    [Fact]
    public void SharedCMapCacheSurvivesContention()
    {
        var path = Path.Combine(TestPaths.Fixtures, "shinagawa_identity_h.pdf");
        Assert.True(File.Exists(path));

        var expected = Fingerprint(PdfProcessor.ProcessPdf(path));
        var results = new ConcurrentBag<string>();

        Parallel.For(
            0,
            32,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(Environment.ProcessorCount, 4) },
            _ => results.Add(Fingerprint(PdfProcessor.ProcessPdf(path))));

        Assert.All(results, actual => Assert.Equal(expected, actual));
    }

    /// <summary>
    /// Detection runs a different, shorter path than extraction — page sampling
    /// and the tiled-scan check rather than the full pipeline — so it is worth
    /// its own pass, and mixing the two modes exercises them against each other.
    /// </summary>
    [Fact]
    public void ConcurrentDetectionMatchesSequentialOutput()
    {
        var fixtures = Fixtures();
        var baseline = fixtures.ToDictionary(
            path => path,
            path => PdfProcessor.DetectPdf(path).PdfType,
            StringComparer.Ordinal);

        var mismatches = new ConcurrentBag<string>();

        Parallel.ForEach(
            Enumerable.Range(0, 3).SelectMany(_ => fixtures),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(Environment.ProcessorCount, 4) },
            path =>
            {
                if (PdfProcessor.DetectPdf(path).PdfType != baseline[path])
                {
                    mismatches.Add(Path.GetFileName(path));
                }
            });

        Assert.Empty(mismatches);
    }
}

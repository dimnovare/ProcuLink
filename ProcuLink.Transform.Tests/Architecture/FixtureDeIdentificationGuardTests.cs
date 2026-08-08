using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace ProcuLink.Transform.Tests.Architecture;

/// <summary>
/// Keeps the de-identification claim in <c>ProcuLink.Transform.Tests.csproj</c> honest.
///
/// That csproj called the real-vendor fixtures "trimmed + de-identified" and nothing checked it,
/// so two fixtures shipped with third-party identifiers still in them: a contributor's local
/// filesystem path (naming a customer organisation and its environment) and a set of
/// personal email addresses on a live corporate domain. Every other real fixture in the tree
/// had been scrubbed to <c>example.*</c>-class placeholders by hand — which is exactly the
/// failure mode a hand-applied convention has: it works until someone forgets once.
///
/// This guard sweeps every *tracked* fixture (git, not the filesystem — real customer documents
/// are deliberately kept untracked for this reason, and a filesystem walk would flag them) for
/// three classes that only ever appear by accident:
///
///   1. an email address on a domain that is not reserved-for-documentation,
///   2. an absolute local filesystem path (drive-letter, POSIX home, or UNC),
///   3. a <c>file://</c> URI.
///
/// Failures name the file, the line and the class — never the value. This repository is public
/// and CI logs are public with it; a guard that prints the leak to prove the leak has not
/// removed it. Open the named file locally to see what tripped.
/// </summary>
public class FixtureDeIdentificationGuardTests
{
    // ── Detectors ────────────────────────────────────────────────────────────────────────

    /// <summary>Local-part @ dotted-domain. Deliberately loose: cXML <c>payloadID</c> attributes
    /// use the same syntax and leak the buyer's hosted-instance hostname just as effectively.</summary>
    private static readonly Regex EmailLike = new(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+",
        RegexOptions.Compiled);

    private static readonly Regex FileUri = new(
        @"file://",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Drive-letter path (<c>C:\</c> / <c>C:/</c>), POSIX home path, or UNC share.
    /// The lookbehind keeps <c>https://</c> and friends out — a scheme's last letter is preceded
    /// by another letter, a drive letter is not.</summary>
    private static readonly Regex AbsoluteLocalPath = new(
        @"(?<![A-Za-z0-9])[A-Za-z]:[\\/]"
        + @"|(?<![A-Za-z0-9])/(?:Users|home)/[A-Za-z0-9._\-]+/"
        + @"|\\\\[A-Za-z0-9._\-]+\\",
        RegexOptions.Compiled);

    /// <summary>RFC 2606 + RFC 6761 reserved names. Anything else is somebody's real domain.</summary>
    private static bool IsPlaceholderDomain(string domain)
    {
        var d = domain.ToLowerInvariant().TrimEnd('.');

        if (d is "example" or "localhost" or "invalid" or "test")
        {
            return true;
        }

        foreach (var tld in new[] { ".example", ".invalid", ".test", ".localhost" })
        {
            if (d.EndsWith(tld, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var sld in new[] { "example.com", "example.net", "example.org" })
        {
            if (d == sld || d.EndsWith("." + sld, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static IReadOnlyList<string> ViolationClasses(string line)
    {
        var found = new List<string>();

        foreach (Match m in EmailLike.Matches(line))
        {
            var domain = m.Value[(m.Value.IndexOf('@') + 1)..];
            if (!IsPlaceholderDomain(domain))
            {
                found.Add("email address on a non-placeholder domain");
                break;
            }
        }

        if (FileUri.IsMatch(line))
        {
            found.Add("file:// URI");
        }

        if (AbsoluteLocalPath.IsMatch(line))
        {
            found.Add("absolute local filesystem path");
        }

        return found;
    }

    // ── The guard ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TrackedFixtures_CarryNoRealWorldIdentifiers()
    {
        var scan = ScanTrackedFixtures();

        scan.Violations.Should().BeEmpty(
            "a tracked fixture must not carry real-world identifiers — replace them with "
            + "example.*-class placeholders, preserving the document's shape. Offending "
            + "file:line and class (values withheld: this repository and its CI logs are public):\n"
            + string.Join("\n", scan.Violations));
    }

    /// <summary>
    /// An empty sweep must fail, not pass. Both floors are needed: the file count catches a
    /// broken corpus query, and the byte count catches a corpus that is enumerated but never
    /// actually read.
    /// </summary>
    [Fact]
    public void FixtureSweep_ActuallyReadsTheFixtureCorpus()
    {
        var scan = ScanTrackedFixtures();

        scan.ScannedFiles.Should().BeGreaterThanOrEqualTo(
            20,
            $"only {scan.ScannedFiles} tracked fixture files were scanned under {scan.RepoRoot} "
            + "— the corpus query is broken, so the guard above proves nothing");

        scan.ScannedBytes.Should().BeGreaterThan(
            20_000,
            $"only {scan.ScannedBytes} bytes of fixture content were read — the files were "
            + "listed but not opened, so the guard above proves nothing");
    }

    /// <summary>
    /// Positive control. The sweep going green is only meaningful if the detectors can still
    /// go red — a regex broken into matching nothing would pass <see cref="TrackedFixtures_CarryNoRealWorldIdentifiers"/>
    /// silently and forever. Inputs here are synthetic and belong to nobody.
    /// </summary>
    [Theory]
    [InlineData("<redacted@example.invalid>", "email address on a non-placeholder domain")]
    [InlineData("<cXML payloadID=\"0000000000.0@example.invalid\">", "email address on a non-placeholder domain")]
    [InlineData("xsi:schemaLocation=\"urn:x file:///C:/Users/someone/Docs/schema.xsd\"", "file:// URI")]
    [InlineData("xsi:schemaLocation=\"urn:x file:///C:/Users/someone/Docs/schema.xsd\"", "absolute local filesystem path")]
    [InlineData("<Path>D:\\build\\artifacts\\out.xml</Path>", "absolute local filesystem path")]
    [InlineData("<Path>/home/someone/build/out.xml</Path>", "absolute local filesystem path")]
    [InlineData("<Path>redacted-fixture</Path>", "absolute local filesystem path")]
    [InlineData("<Path>\\\\fileserver\\share\\out.xml</Path>", "absolute local filesystem path")]
    public void Detectors_FireOnKnownBadInput(string line, string expectedClass)
        => ViolationClasses(line).Should().Contain(expectedClass);

    /// <summary>
    /// Negative control. A detector that flags everything is as useless as one that flags
    /// nothing: it would be silenced within a week.
    /// </summary>
    [Theory]
    [InlineData("<Email name=\"default\">test@example.com</Email>")]
    [InlineData("<Email>test.user@buyer.example.com</Email>")]
    [InlineData("<Email>ops@supplier.example</Email>")]
    [InlineData("<cXML payloadID=\"matrix-1@proculink.test\" timestamp=\"2026-06-08T10:00:00+00:00\">")]
    [InlineData("<!DOCTYPE cXML SYSTEM \"http://xml.cxml.org/schemas/cXML/1.2.014/cXML.dtd\"[]>")]
    [InlineData("<URL>https://catalog.example.com/de/de/some-product/v2p34762852</URL>")]
    [InlineData("<Envelope xmlns=\"http://schemas.xmlsoap.org/soap/envelope/\" xml:lang=\"en-US\">")]
    [InlineData("<OrderRequestHeader orderDate=\"2026-06-14T01:42:54-04:00\" type=\"new\">")]
    public void Detectors_StaySilentOnCleanInput(string line)
        => ViolationClasses(line).Should().BeEmpty();

    // ── Corpus ───────────────────────────────────────────────────────────────────────────

    private sealed record FixtureScan(
        string RepoRoot,
        int ScannedFiles,
        long ScannedBytes,
        IReadOnlyList<string> Violations);

    private static FixtureScan ScanTrackedFixtures()
    {
        var repoRoot = FindRepoRoot();
        var violations = new List<string>();
        var scannedFiles = 0;
        long scannedBytes = 0;

        foreach (var relative in TrackedFixtureFiles(repoRoot))
        {
            var absolute = Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolute))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(absolute);
            if (Array.IndexOf(bytes, (byte)0) >= 0)
            {
                continue; // binary fixture — not text-scannable
            }

            scannedFiles++;
            scannedBytes += bytes.LongLength;

            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var violationClass in ViolationClasses(lines[i]))
                {
                    violations.Add($"  {relative}:{i + 1}: {violationClass}");
                }
            }
        }

        return new FixtureScan(repoRoot, scannedFiles, scannedBytes, violations);
    }

    /// <summary>
    /// Tracked files whose *directory* path contains a segment ending in "Fixtures". Derived
    /// from git rather than typed out here — a hand-maintained list is how the next fixture
    /// directory gets added without the guard noticing.
    /// </summary>
    private static IReadOnlyList<string> TrackedFixtureFiles(string repoRoot)
    {
        var psi = new ProcessStartInfo("git", "ls-files -z")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start git to enumerate tracked fixtures");

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"`git ls-files` failed in {repoRoot}: {stderr}");
        }

        return stdout
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(IsUnderFixturesDirectory)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsUnderFixturesDirectory(string relativePath)
    {
        var segments = relativePath.Split('/');
        for (var i = 0; i < segments.Length - 1; i++) // directory segments only
        {
            if (segments[i].EndsWith("Fixtures", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProcuLink.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"could not find ProcuLink.slnx walking up from {AppContext.BaseDirectory}");
    }
}

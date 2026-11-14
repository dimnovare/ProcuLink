using Xunit;

namespace ProcuLink.Api.Tests.Integration;

// 2026-11-13, plan 13b: moved verbatim out of EndToEndPipelineTests.cs, which was deleted with the
// order pipeline it tested. Every Postgres-container class in this project skips through these.


/// <summary>
/// One-time Docker reachability probe. xUnit v2 has no dynamic <c>Assert.Skip</c>,
/// so a custom <see cref="DockerRequiredFactAttribute"/> reads this and statically
/// marks the test skipped when Docker is unreachable — keeping the suite green on
/// machines/CI without Docker instead of erroring on container start.
/// Availability requires an actual engine response (non-empty server version), not
/// just a zero exit code — a wedged Docker Desktop engine can exit 0 while every
/// container start would fail.
/// </summary>
internal static class DockerProbe
{
    public static readonly string? UnavailableReason = Detect();

    private static string? Detect()
    {
        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "info --format \"{{.ServerVersion}}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null)
                return "Docker CLI could not be started.";

            // Drain both pipes concurrently so WaitForExit cannot deadlock on a full buffer.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(15_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return "Docker probe timed out.";
            }

            if (proc.ExitCode != 0)
                return $"`docker info` exited {proc.ExitCode} — Docker daemon not reachable.";

            // A wedged Docker Desktop engine can exit 0 with no server version, so an
            // exit-code check alone reports "available" and every container start fails.
            var serverVersion = stdoutTask.GetAwaiter().GetResult().Trim();
            if (serverVersion.Length == 0)
            {
                var stderr = stderrTask.GetAwaiter().GetResult().Trim();
                return stderr.Length > 0
                    ? $"`docker info` returned no server version — engine not responding ({stderr})."
                    : "`docker info` returned no server version — engine not responding.";
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"Docker CLI unavailable: {ex.Message}";
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that statically skips when Docker is unreachable.
/// </summary>
public sealed class DockerRequiredFactAttribute : FactAttribute
{
    public DockerRequiredFactAttribute()
    {
        if (DockerProbe.UnavailableReason is { } reason)
            Skip = $"Requires Docker/Testcontainers — {reason}";
    }
}

/// <summary>
/// <see cref="DockerRequiredFactAttribute"/>'s Theory twin — statically skips every case when
/// Docker is unreachable.
/// </summary>
public sealed class DockerRequiredTheoryAttribute : TheoryAttribute
{
    public DockerRequiredTheoryAttribute()
    {
        if (DockerProbe.UnavailableReason is { } reason)
            Skip = $"Requires Docker/Testcontainers — {reason}";
    }
}


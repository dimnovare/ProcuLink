namespace ProcuLink.Transform.Conformance;

/// <summary>
/// Accumulates <see cref="ConformanceCheck"/> rows in order and seals them into a
/// <see cref="ConformanceReport"/>. The overall pass is true iff no
/// <see cref="ConformanceSeverity.Error"/> check failed — warnings / info are
/// advisory only. Used by every profile checker so the pass semantics are
/// identical across formats.
/// </summary>
internal sealed class ConformanceCheckBuilder
{
    private readonly StandardsProfile _profile;
    private readonly string _profileName;
    private readonly string _profileVersion;
    private readonly List<ConformanceCheck> _checks = new();

    public ConformanceCheckBuilder(StandardsProfile profile, string profileName, string profileVersion)
    {
        _profile = profile;
        _profileName = profileName;
        _profileVersion = profileVersion;
    }

    /// <summary>Adds a named check.</summary>
    public ConformanceCheckBuilder Add(
        string code, bool passed, string profileRef, string message,
        ConformanceSeverity severity = ConformanceSeverity.Error)
    {
        _checks.Add(new ConformanceCheck(code, severity, passed, message, profileRef));
        return this;
    }

    /// <summary>
    /// Convenience for a required-value check: <paramref name="present"/> true ⇒ pass with
    /// <paramref name="okMessage"/>, false ⇒ fail with <paramref name="failMessage"/>.
    /// </summary>
    public ConformanceCheckBuilder Require(
        string code, bool present, string profileRef, string okMessage, string failMessage,
        ConformanceSeverity severity = ConformanceSeverity.Error) =>
        Add(code, present, profileRef, present ? okMessage : failMessage, severity);

    public ConformanceReport Build()
    {
        var overallPass = _checks.All(c => c.Passed || c.Severity != ConformanceSeverity.Error);
        return new ConformanceReport(_profile, _profileName, _profileVersion, overallPass, _checks);
    }
}

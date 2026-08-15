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

    /// <summary>
    /// Adds a named check. <paramref name="evidence"/> defaults to
    /// <see cref="ConformanceEvidence.SelfCheck"/> — the weaker claim — so a check must name the
    /// third-party artifact it was validated against in order to be reported as one.
    /// </summary>
    public ConformanceCheckBuilder Add(
        string code, bool passed, string profileRef, string message,
        ConformanceSeverity severity = ConformanceSeverity.Error,
        ConformanceEvidence evidence = ConformanceEvidence.SelfCheck)
    {
        _checks.Add(new ConformanceCheck(code, severity, passed, message, profileRef, evidence));
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

    /// <summary>
    /// Seals the accumulated checks into a report.
    ///
    /// <para><b>The <c>Count > 0</c> term is a floor, not a formality.</b> <c>All()</c> is true on an
    /// empty sequence, so without it a builder that added nothing produced
    /// <c>OverallPass == true</c> and rendered "**Result:** PASS" into a document customers are
    /// invited to forward as evidence — a pass over an examination that never happened. No live
    /// checker reaches it today, because all five add a first check unconditionally, which is
    /// exactly why it belongs here rather than being left to that coincidence: an early return, a
    /// guard clause, or a sixth checker is all it would take. Pinned by
    /// <c>ConformanceCheckBuilderFloorTests</c>.</para>
    /// </summary>
    public ConformanceReport Build()
    {
        var overallPass = _checks.Count > 0
                          && _checks.All(c => c.Passed || c.Severity != ConformanceSeverity.Error);
        return new ConformanceReport(_profile, _profileName, _profileVersion, overallPass, _checks);
    }
}

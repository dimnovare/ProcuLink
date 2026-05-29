namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Thrown by <see cref="X12OrderParser"/> when an ANSI X12 850 Purchase Order
/// interchange is structurally malformed (missing ISA envelope, no ST*850
/// transaction set, missing BEG header, unparseable delimiters, etc.). The
/// controller should map this to HTTP 422 Unprocessable Entity.
///
/// Mirrors <see cref="EdifactParseException"/>. Note that <em>optional</em>
/// segments (REF / DTM / N1 / PID and unknown segments) never raise this —
/// the parser skips what it cannot understand and parses defensively.
/// </summary>
public sealed class X12ParseException : Exception
{
    public X12ParseException(string message) : base(message) { }
    public X12ParseException(string message, Exception inner) : base(message, inner) { }
}

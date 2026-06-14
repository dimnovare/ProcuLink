namespace ProcuLink.Transform.Parsing;

/// <summary>
/// A named party on a parsed order (ship-to, bill-to, remit-to, buyer, supplier).
/// Additive Phase-1 lossless capture: addresses + tax/EDI ids that the fixed canonical
/// header never had a slot for. Role is a lowercase tag: "shipTo" | "billTo" | "remitTo"
/// | "buyer" | "supplier". Every field is nullable — a document may carry only some.
/// </summary>
public record ParsedParty(
    string Role,
    string? Name = null,
    string? Street = null,
    string? City = null,
    string? PostalCode = null,
    string? Country = null,
    string? Vat = null,
    string? RegNr = null,
    string? EdiCode = null,
    string? Reference = null,
    string? ContactName = null,
    string? Email = null,
    string? Phone = null);

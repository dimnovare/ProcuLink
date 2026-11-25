namespace ProcuLink.Core.Documents;

/// <summary>The key values of a KSeF FA(3) file, read from the file itself, and the hash a person confirms it by.</summary>
public sealed record KsefSummary(string SellerName, string SellerNip, string? BuyerName, string BuyerId, string Number, string IssueDate,
    string? SaleDate, string Currency, IReadOnlyList<KsefRateTotal> Rates, string Gross, int Lines, string Confirm);

/// <summary>One VAT rate's net and VAT, as written in the file.</summary>
public sealed record KsefRateTotal(string Rate, string Net, string Vat);

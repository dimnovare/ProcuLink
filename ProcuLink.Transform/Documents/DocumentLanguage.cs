using System.Globalization;
using ProcuLink.Core.Documents;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Documents;

/// <summary>
/// The language a page ProcuLink draws (the Factur-X / ZUGFeRD invoice page, a layout PDF's footer) and a document's default
/// email are written in: en, de, fr, pl or et. A person's choice wins; else the sender's country (the issuer writes the
/// document in its own language; the read carries no language of its own); else English. Only the words ProcuLink adds are
/// translated, and the numbers and dates on its own page: the read's labels and values, identifiers and the XML never are.
/// </summary>
public static class DocumentLanguage
{
    public const string English = "en";
    private static readonly string[] Others = ["de", "fr", "pl", "et"];

    /// <summary>A supported code as chosen (any case), else null: "same as the document", blank or unknown.</summary>
    public static string? Known(string? code) =>
        code?.Trim().ToLowerInvariant() is { } c && (c == English || Others.Contains(c)) ? c : null;

    /// <summary>The language of the sender's country; English for a country with no language here or no country.</summary>
    public static string FromCountry(string? country) => StandardCodes.Country(country) switch
    {
        "DE" or "AT" or "CH" or "LI" => "de",
        "FR" or "LU" or "MC" => "fr",
        "PL" => "pl",
        "EE" => "et",
        _ => English,
    };

    /// <summary>The read's language: its sender's country, by meaning (the flow's meanings once it lists fields).</summary>
    public static string Of(ReadResult read, FlowFieldList? fields = null) =>
        FromCountry(read.Fields.FirstOrDefault(f => FieldMeanings.Of(f, fields ?? new()) == FieldMeanings.SenderCountry)?.Value);

    /// <summary>The English text in <paramref name="language"/>; the English itself when there is no translation.</summary>
    public static string Text(string language, string english) =>
        Words.TryGetValue(english, out var t) && Array.IndexOf(Others, language) is var i and >= 0 ? t[i] : english;

    /// <summary>An amount with two decimals: 1234.56 in English (as before), 1.234,56 in German, 1 234,56 in French, Polish and Estonian.</summary>
    public static string Money(string language, decimal? value) =>
        value?.ToString(language == English ? "0.00" : "#,##0.00", Numbers(language)) ?? "";

    /// <summary>A quantity, price or rate in <paramref name="format"/> (null: as stored), with the language's decimal mark.</summary>
    public static string Number(string language, decimal value, string? format) => value.ToString(format, Numbers(language));

    /// <summary>2026-11-06 in English (as before), 06.11.2026 in German, Polish and Estonian, 06/11/2026 in French.</summary>
    public static string? Date(string language, DateOnly? date) =>
        date?.ToString(language switch { English => "yyyy-MM-dd", "fr" => "dd'/'MM'/'yyyy", _ => "dd.MM.yyyy" }, CultureInfo.InvariantCulture);

    // Built here, not from CultureInfo: the container's ICU is not the laptop's. A no-break space groups, never U+202F,
    // which the page's font may not draw.
    private static readonly NumberFormatInfo Comma = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = " " };
    private static readonly NumberFormatInfo German = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = "." };

    private static NumberFormatInfo Numbers(string language) => language switch
    {
        English => NumberFormatInfo.InvariantInfo,
        "de" => German,
        _ => Comma,
    };

    // English → de, fr, pl, et. Standard invoicing terms of each language.
    private static readonly Dictionary<string, string[]> Words = new()
    {
        ["Invoice"] = ["Rechnung", "Facture", "Faktura", "Arve"],
        ["Credit note"] = ["Gutschrift", "Avoir", "Faktura korygująca", "Kreeditarve"],
        ["Invoice number"] = ["Rechnungsnummer", "Numéro de facture", "Numer faktury", "Arve number"],
        ["Issue date"] = ["Rechnungsdatum", "Date de facture", "Data wystawienia", "Arve kuupäev"],
        ["Due date"] = ["Fälligkeitsdatum", "Date d'échéance", "Termin płatności", "Maksetähtaeg"],
        ["Delivery date"] = ["Lieferdatum", "Date de livraison", "Data dostawy", "Tarnekuupäev"],
        ["Seller"] = ["Verkäufer", "Vendeur", "Sprzedawca", "Müüja"],
        ["Seller address"] = ["Anschrift des Verkäufers", "Adresse du vendeur", "Adres sprzedawcy", "Müüja aadress"],
        ["Seller VAT ID"] = ["USt-IdNr. des Verkäufers", "N° TVA du vendeur", "NIP sprzedawcy", "Müüja KMKR nr"],
        ["Buyer"] = ["Käufer", "Acheteur", "Nabywca", "Ostja"],
        ["Buyer address"] = ["Anschrift des Käufers", "Adresse de l'acheteur", "Adres nabywcy", "Ostja aadress"],
        ["Buyer VAT ID"] = ["USt-IdNr. des Käufers", "N° TVA de l'acheteur", "NIP nabywcy", "Ostja KMKR nr"],
        ["Buyer reference"] = ["Käuferreferenz", "Référence acheteur", "Referencja nabywcy", "Ostja viide"],
        ["Order reference"] = ["Bestellnummer", "Référence de commande", "Numer zamówienia", "Tellimuse number"],
        ["Currency"] = ["Währung", "Devise", "Waluta", "Valuuta"],
        ["No."] = ["Pos.", "N°", "Lp.", "Nr"],
        ["Item code"] = ["Artikelnummer", "Référence", "Kod towaru", "Tootekood"],
        ["Description"] = ["Beschreibung", "Désignation", "Nazwa", "Kirjeldus"],
        ["Qty"] = ["Menge", "Qté", "Ilość", "Kogus"],
        ["Unit"] = ["Einheit", "Unité", "J.m.", "Ühik"],
        ["Unit price"] = ["Einzelpreis", "Prix unitaire", "Cena jedn. netto", "Ühiku hind"],
        ["VAT %"] = ["USt. %", "TVA %", "Stawka VAT", "KM %"],
        ["Net"] = ["Netto", "Montant HT", "Wartość netto", "Summa"],
        ["VAT"] = ["USt.", "TVA", "VAT", "Käibemaks"],
        ["Total"] = ["Gesamtbetrag", "Total TTC", "Razem do zapłaty", "Kokku"],
        ["Payment reference"] = ["Verwendungszweck", "Référence de paiement", "Tytuł płatności", "Viitenumber"],
        ["Page"] = ["Seite", "Page", "Strona", "Lehekülg"],
        ["of"] = ["von", "sur", "z", "/"],
        ["Attached: {fileName}. Sent by ProcuLink."] =
        [
            "Im Anhang: {fileName}. Gesendet über ProcuLink.", "Pièce jointe : {fileName}. Envoyé par ProcuLink.",
            "W załączniku: {fileName}. Wysłano przez ProcuLink.", "Manuses: {fileName}. Saadetud ProcuLinki kaudu.",
        ],
    };
}

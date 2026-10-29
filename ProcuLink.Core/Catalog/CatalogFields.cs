namespace ProcuLink.Core.Catalog;

/// <summary>
/// The catalog fields an import file actually SUPPLIED — the set of columns it carried, not the
/// values in them. Passed from the catalog parser to
/// <see cref="ProcuLink.Core.Services.ISupplierCatalogService.UpsertManyAsync"/> so a re-import
/// only overwrites what the file spoke about.
///
/// The distinction it carries: a column ABSENT from the file says nothing, so an existing product
/// keeps its stored value; a column PRESENT with a blank cell is how a supplier removes a value,
/// so it clears. Without it the two were indistinguishable — every draft field is null in both
/// cases — and a price-update file of just <c>code,price</c> erased every name, unit and barcode
/// in the catalog.
///
/// <c>Code</c> is not a member: it is the upsert key and is never overwritten.
/// </summary>
[Flags]
public enum CatalogFields
{
    None                   = 0,
    Name                   = 1 << 0,
    Unit                   = 1 << 1,
    Price                  = 1 << 2,
    Currency               = 1 << 3,
    Barcode                = 1 << 4,
    ExternalId             = 1 << 5,
    /// <summary>The raw manufacturer part number AND its normalised key — always written together.</summary>
    ManufacturerPartNumber = 1 << 6,
    ManufacturerName       = 1 << 7,

    /// <summary>
    /// Every field present. The default wherever presence is not known, which reproduces the
    /// behaviour from before presence was tracked: every field is written from the draft.
    /// </summary>
    All = Name | Unit | Price | Currency | Barcode | ExternalId | ManufacturerPartNumber | ManufacturerName,
}

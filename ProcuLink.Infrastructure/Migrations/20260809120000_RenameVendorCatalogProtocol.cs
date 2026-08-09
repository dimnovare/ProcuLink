using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <summary>
    /// Data-only migration. <c>supplier_catalog_sources.protocol</c> is a PERSISTED DISCRIMINATOR:
    /// <c>CatalogPullService</c> resolves an <c>ICatalogVendorFetcher</c> by looking the column
    /// value up in a dictionary keyed by <c>ICatalogVendorFetcher.Protocol</c>, so a row already in
    /// the database decides at runtime which code runs. The vendor-fetcher token was named after
    /// the distributor whose catalog it fetches; this repository is public, so the token published
    /// a trading relationship and has been renamed to the auth MECHANISM it actually describes.
    ///
    /// <para><b>Renaming the literal in code alone would have orphaned every existing row.</b> The
    /// dictionary lookup would miss, the source would fall through to the file/http branch — whose
    /// host/username/remote-path columns are deliberately CLEARED when a vendor source is saved —
    /// and a working catalog sync would start failing with a sanitised error. That is why this
    /// migration exists rather than a bare find-and-replace.</para>
    ///
    /// <para><b>Why the WHERE clause is an allow-list and not the old value.</b> Matching
    /// <c>WHERE protocol = '&lt;old token&gt;'</c> is the obvious form, and it would write the real
    /// company name straight back into the tree — in a file that survives the very history rewrite
    /// this scrub exists to unblock. Instead the clause names the five transport protocols, which
    /// are the complete set of everything that is NOT the vendor fetcher: the API has only ever
    /// accepted those five plus the single vendor token (<c>SuppliersController</c> validates
    /// against exactly that closed list), so "not one of the five" selects precisely the vendor
    /// rows. No real value is stored in order to migrate the real value away.</para>
    ///
    /// <para>Raw SQL is the sanctioned exception inside a migration. Idempotent: re-running it is a
    /// no-op because the new token is itself in neither set — it is only ever written by this
    /// statement, and after the first run no row matches the predicate any more.</para>
    ///
    /// <para><b>Deploy ordering.</b> Run this with (or before) the code that registers the fetcher
    /// under the new token. The API's accepted-protocol list changes in the same commit, so a
    /// client still sending the old token receives a 400 until it is updated — see the PR body for
    /// the coordinated frontend change. No data is lost either way: existing rows keep syncing on
    /// the new token as soon as this has run.</para>
    /// </summary>
    public partial class RenameVendorCatalogProtocol : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
UPDATE supplier_catalog_sources
   SET protocol = 'aes2fa'
 WHERE protocol NOT IN ('sftp', 'ftp', 'ftps', 'http', 'https');
""");
        }

        /// <inheritdoc />
        /// <remarks>
        /// DELIBERATELY ONE-WAY. The previous token is a real company name, and writing it back
        /// here would re-publish it on a public repository — which is the whole defect this
        /// migration is part of removing. Rolling back therefore requires running the inverse
        /// UPDATE by hand with the old token supplied out of band. Down is a no-op rather than a
        /// throw so that an unrelated rollback past this point is not blocked; the operator must
        /// restore the token themselves if they genuinely intend to run the old code.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}

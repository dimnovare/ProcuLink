using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <summary>
    /// Creates the table that decides which organisation an inbound email belongs to.
    ///
    /// <para>Before this, tenant selection ran on <c>organisations.slug</c> — a kebab-cased company
    /// name plus four hex characters — and the mail relay accepts mail from anyone, so guessing a
    /// slug was enough to file purchase orders into another tenant's inbox. Rows here hold 128 bits
    /// of entropy, hashed for lookup and encrypted for display.</para>
    ///
    /// <para><b>Deploy order matters.</b> This migration only creates the table; it inserts nothing,
    /// because both columns need application-side secrets (the HMAC pepper and the AES-GCM
    /// deployment key) that a migration cannot reach. The rows are written by
    /// <c>IInboundAddressService.BackfillMissingAsync</c>, which <c>MigrationBootstrap</c> runs
    /// immediately after migrating and on every boot thereafter — it is idempotent. Until that
    /// backfill has run once, NO address resolves and inbound mail is deferred (not dropped): the
    /// router answers with a transient rejection, which keeps the provider's ~10.5-hour retry
    /// window open.</para>
    ///
    /// <para>Down drops the table, which is safe in the sense that it restores the schema — but any
    /// address an operator has already handed to a buyer is gone with it, so a rollback needs the
    /// old slug-based resolution restored in code as well.</para>
    /// </summary>
    public partial class AddOrgInboundAddresses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "org_inbound_addresses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organisation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    encrypted_token = table.Column<string>(type: "text", nullable: false),
                    token_prefix = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    last_used_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_org_inbound_addresses", x => x.id);
                    table.ForeignKey(
                        name: "FK_org_inbound_addresses_organisations_organisation_id",
                        column: x => x.organisation_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_org_inbound_addresses_organisation_id",
                table: "org_inbound_addresses",
                column: "organisation_id");

            migrationBuilder.CreateIndex(
                name: "IX_org_inbound_addresses_token_hash",
                table: "org_inbound_addresses",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "org_inbound_addresses");
        }
    }
}

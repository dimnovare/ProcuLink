using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantApiKeysAndOrgSlug : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "slug",
                table: "organisations",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Backfill slugs for any organisations that existed before this migration.
            // Generates:  kebab(name) + '-' + first-4-chars-of-UUID
            // e.g. "Acme Corp" → "acme-corp-a1b2"
            migrationBuilder.Sql("""
                UPDATE organisations
                SET slug =
                    TRIM(BOTH '-' FROM LOWER(REGEXP_REPLACE(name, '[^a-zA-Z0-9]+', '-', 'g')))
                    || '-' || LEFT(REPLACE(CAST(id AS text), '-', ''), 4)
                WHERE slug = '';
                """);

            migrationBuilder.CreateTable(
                name: "advance_shipping_notices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organisation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shipment_id = table.Column<string>(type: "text", nullable: false),
                    despatch_date = table.Column<DateOnly>(type: "date", nullable: false),
                    estimated_delivery_date = table.Column<DateOnly>(type: "date", nullable: true),
                    buyer_order_ref = table.Column<string>(type: "text", nullable: true),
                    supplier_ref = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "received"),
                    source_file_name = table.Column<string>(type: "text", nullable: true),
                    source_file_key = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_advance_shipping_notices", x => x.id);
                    table.ForeignKey(
                        name: "FK_advance_shipping_notices_organisations_organisation_id",
                        column: x => x.organisation_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "integration_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organisation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<string>(type: "text", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    target_url = table.Column<string>(type: "text", nullable: false),
                    encrypted_secret = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    failure_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "FK_integration_subscriptions_organisations_organisation_id",
                        column: x => x.organisation_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organisation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    buyer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    invoice_number = table.Column<string>(type: "text", nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    currency = table.Column<string>(type: "text", nullable: false, defaultValue: "EUR"),
                    payment_terms = table.Column<string>(type: "text", nullable: true),
                    buyer_ref = table.Column<string>(type: "text", nullable: true),
                    supplier_ref = table.Column<string>(type: "text", nullable: true),
                    sub_total = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    tax_total = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    grand_total = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "pending_review"),
                    source_file_name = table.Column<string>(type: "text", nullable: true),
                    source_file_key = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoices", x => x.id);
                    table.ForeignKey(
                        name: "FK_invoices_organisations_organisation_id",
                        column: x => x.organisation_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tenant_api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organisation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    key_hash = table.Column<string>(type: "text", nullable: false),
                    key_prefix = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    expires_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_api_keys", x => x.id);
                    table.ForeignKey(
                        name: "FK_tenant_api_keys_organisations_organisation_id",
                        column: x => x.organisation_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asn_packages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    advance_shipping_notice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organisation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_id = table.Column<string>(type: "text", nullable: false),
                    sscc = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asn_packages", x => x.id);
                    table.ForeignKey(
                        name: "FK_asn_packages_advance_shipping_notices_advance_shipping_noti~",
                        column: x => x.advance_shipping_notice_id,
                        principalTable: "advance_shipping_notices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invoice_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organisation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    unit_code = table.Column<string>(type: "text", nullable: false, defaultValue: "EA"),
                    unit_price = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(7,4)", nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    buyer_item_code = table.Column<string>(type: "text", nullable: true),
                    supplier_item_code = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_invoice_lines_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asn_package_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organisation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    buyer_item_code = table.Column<string>(type: "text", nullable: true),
                    supplier_item_code = table.Column<string>(type: "text", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    unit_code = table.Column<string>(type: "text", nullable: false, defaultValue: "EA")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asn_package_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_asn_package_lines_asn_packages_package_id",
                        column: x => x.package_id,
                        principalTable: "asn_packages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_organisations_slug",
                table: "organisations",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_advance_shipping_notices_organisation_id",
                table: "advance_shipping_notices",
                column: "organisation_id");

            migrationBuilder.CreateIndex(
                name: "IX_asn_package_lines_package_id",
                table: "asn_package_lines",
                column: "package_id");

            migrationBuilder.CreateIndex(
                name: "IX_asn_packages_advance_shipping_notice_id",
                table: "asn_packages",
                column: "advance_shipping_notice_id");

            migrationBuilder.CreateIndex(
                name: "IX_integration_subscriptions_organisation_id_event_type_is_act~",
                table: "integration_subscriptions",
                columns: new[] { "organisation_id", "event_type", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_invoice_lines_invoice_id",
                table: "invoice_lines",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_organisation_id",
                table: "invoices",
                column: "organisation_id");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_api_keys_key_hash",
                table: "tenant_api_keys",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_api_keys_organisation_id",
                table: "tenant_api_keys",
                column: "organisation_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "asn_package_lines");

            migrationBuilder.DropTable(
                name: "integration_subscriptions");

            migrationBuilder.DropTable(
                name: "invoice_lines");

            migrationBuilder.DropTable(
                name: "tenant_api_keys");

            migrationBuilder.DropTable(
                name: "asn_packages");

            migrationBuilder.DropTable(
                name: "invoices");

            migrationBuilder.DropTable(
                name: "advance_shipping_notices");

            migrationBuilder.DropIndex(
                name: "IX_organisations_slug",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "slug",
                table: "organisations");
        }
    }
}

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
            // The organisations.slug column, its unique index, and the 7
            // invoice/ASN/integration/api-key tables are now created solely by
            // 20260528120215_AddInvoicesAndLines (which runs first). Those
            // duplicate operations were removed here because re-creating them
            // on a fresh database crashed with Postgres 42P07
            // "relation already exists".
            //
            // The class and its migration id are kept — existing prod/dev
            // databases already record this id in __EFMigrationsHistory.
            //
            // The ONLY unique work this migration owns is the slug backfill
            // below. The column exists by now (created with defaultValue '' by
            // 120215), so this UPDATE replaces those empty defaults for any
            // organisations that pre-dated the slug column. On a fresh database
            // there are no rows yet, so it is a harmless no-op.
            migrationBuilder.Sql("""
                UPDATE organisations
                SET slug =
                    TRIM(BOTH '-' FROM LOWER(REGEXP_REPLACE(name, '[^a-zA-Z0-9]+', '-', 'g')))
                    || '-' || LEFT(REPLACE(CAST(id AS text), '-', ''), 4)
                WHERE slug = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty. The slug column, its index, and the 7 tables
            // are dropped by 20260528120215_AddInvoicesAndLines.Down(). The slug
            // backfill in Up() is a data update with no reversible inverse.
        }
    }
}

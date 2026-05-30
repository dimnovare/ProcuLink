using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAdvanceShippingNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty.
            //
            // This migration originally duplicated the full schema created by
            // 20260528120215_AddInvoicesAndLines (the organisations.slug column,
            // the 7 invoice/ASN/integration/api-key tables, and their indexes).
            // Applying it after 120215 on a fresh database crashed with Postgres
            // 42P07 "relation already exists". 120215 is now the single source of
            // truth for those objects; this migration's operations were removed so
            // a from-zero `database update` succeeds.
            //
            // The class and its migration id are kept because existing prod/dev
            // databases already record this id in __EFMigrationsHistory and must
            // not see it disappear. The final schema is unchanged.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — see Up(). The duplicate drop operations were
            // removed so this migration owns no schema objects.
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentOutputs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "automation",
                table: "flows",
                type: "text",
                nullable: false,
                defaultValue: "review");

            migrationBuilder.CreateTable(
                name: "flow_destinations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    protocol = table.Column<string>(type: "text", nullable: false),
                    config_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    encrypted_credentials = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flow_destinations", x => x.id);
                    table.ForeignKey(
                        name: "FK_flow_destinations_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "flow_outputs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    flow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    format = table.Column<string>(type: "text", nullable: false),
                    template_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    table_key = table.Column<string>(type: "text", nullable: true),
                    destination_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flow_outputs", x => x.id);
                    table.ForeignKey(
                        name: "FK_flow_outputs_flow_destinations_destination_id",
                        column: x => x.destination_id,
                        principalTable: "flow_destinations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_flow_outputs_flows_flow_id",
                        column: x => x.flow_id,
                        principalTable: "flows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_flow_outputs_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "flow_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    output_id = table.Column<Guid>(type: "uuid", nullable: true),
                    output_name = table.Column<string>(type: "text", nullable: false),
                    destination_id = table.Column<Guid>(type: "uuid", nullable: true),
                    protocol = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    artifact_key = table.Column<string>(type: "text", nullable: true),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    response_code = table.Column<int>(type: "integer", nullable: true),
                    claimed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    next_attempt_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    delivered_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flow_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "FK_flow_deliveries_flow_destinations_destination_id",
                        column: x => x.destination_id,
                        principalTable: "flow_destinations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_flow_deliveries_flow_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "flow_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_flow_deliveries_flow_outputs_output_id",
                        column: x => x.output_id,
                        principalTable: "flow_outputs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_flow_deliveries_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_flow_deliveries_destination_id",
                table: "flow_deliveries",
                column: "destination_id");

            migrationBuilder.CreateIndex(
                name: "IX_flow_deliveries_document_id_output_id",
                table: "flow_deliveries",
                columns: new[] { "document_id", "output_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_flow_deliveries_org_id",
                table: "flow_deliveries",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "IX_flow_deliveries_output_id",
                table: "flow_deliveries",
                column: "output_id");

            migrationBuilder.CreateIndex(
                name: "IX_flow_destinations_org_id",
                table: "flow_destinations",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "IX_flow_outputs_destination_id",
                table: "flow_outputs",
                column: "destination_id");

            migrationBuilder.CreateIndex(
                name: "IX_flow_outputs_flow_id",
                table: "flow_outputs",
                column: "flow_id");

            migrationBuilder.CreateIndex(
                name: "IX_flow_outputs_org_id",
                table: "flow_outputs",
                column: "org_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "flow_deliveries");

            migrationBuilder.DropTable(
                name: "flow_outputs");

            migrationBuilder.DropTable(
                name: "flow_destinations");

            migrationBuilder.DropColumn(
                name: "automation",
                table: "flows");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddExternalApps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "external_app_instances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    application_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    manifest_version = table.Column<int>(type: "INTEGER", nullable: false),
                    manifest_snapshot_json = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    desired_state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    runtime_override = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    runtime_provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    variables_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    published_ports_json = table.Column<string>(type: "TEXT", nullable: false),
                    storage_path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    failure_category = table.Column<string>(type: "TEXT", maxLength: 48, nullable: true),
                    failure_summary = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    needs_recreate = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    installed_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    stopped_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    last_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_app_instances", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "external_app_instance_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    instance_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    detail_json = table.Column<string>(type: "TEXT", nullable: true),
                    occurred_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_app_instance_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_external_app_instance_events_external_app_instances_instance_id",
                        column: x => x.instance_id,
                        principalTable: "external_app_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_external_app_instance_events_instance_sequence",
                table: "external_app_instance_events",
                columns: new[] { "instance_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_external_app_instances_application",
                table: "external_app_instances",
                column: "application_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_app_instances_status",
                table: "external_app_instances",
                columns: new[] { "status", "installed_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_app_instance_events");

            migrationBuilder.DropTable(
                name: "external_app_instances");
        }
    }
}

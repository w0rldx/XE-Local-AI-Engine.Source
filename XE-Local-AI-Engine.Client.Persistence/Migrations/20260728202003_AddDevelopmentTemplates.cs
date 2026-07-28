using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddDevelopmentTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "development_template_materializations",
                columns: table => new
                {
                    selected_folder_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    template_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    template_alias = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    template_path = table.Column<byte[]>(type: "BLOB", nullable: false),
                    template_commit = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_template_materializations", x => x.selected_folder_id);
                    table.ForeignKey(
                        name: "FK_development_template_materializations_selected_folders_selected_folder_id",
                        column: x => x.selected_folder_id,
                        principalTable: "selected_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "development_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    alias = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    host_path = table.Column<byte[]>(type: "BLOB", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_templates", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_development_templates_alias",
                table: "development_templates",
                column: "alias",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "development_template_materializations");

            migrationBuilder.DropTable(
                name: "development_templates");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddDevelopmentCommandProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // command_profile_json is deliberately PLAINTEXT TEXT, unlike the encrypted BLOB columns on
            // development_tasks / development_artifacts that go through NodeEncryptionSaveChangesInterceptor. An
            // encrypted column cannot be indexed, filtered, or digest-compared, and the command profile is non-secret
            // operator-confirmed configuration (executable names, argument vectors, timeouts, glob patterns) — not
            // credentials. Do not "fix" this to an encrypted BLOB in a later migration.
            migrationBuilder.AddColumn<string>(
                name: "command_profile_json",
                table: "development_projects",
                type: "TEXT",
                nullable: true);

            // A SEPARATE dimension from the existing command_profile_version column, which carries the artifact
            // PROTOCOL version ("development-workspace-v1" / "development-validation-v1" / "development-review-v1").
            // This column carries the 64-hex digest of the command profile that produced the artifact; a digest and a
            // protocol version cannot share one 64-character column, hence a new column rather than a reuse.
            migrationBuilder.AddColumn<string>(
                name: "command_profile_digest",
                table: "development_artifacts",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "command_profile_json",
                table: "development_projects");

            migrationBuilder.DropColumn(
                name: "command_profile_digest",
                table: "development_artifacts");
        }
    }
}

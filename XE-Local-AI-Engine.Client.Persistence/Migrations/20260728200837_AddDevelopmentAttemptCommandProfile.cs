using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddDevelopmentAttemptCommandProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The immutable per-attempt snapshot of the command profile the attempt actually ran under. The twin of
            // development_projects.command_profile_json, and PLAINTEXT TEXT for the same reason: an encrypted column
            // cannot be indexed, filtered, or digest-compared, and the profile is non-secret operator-confirmed
            // configuration, not credentials. Do not "fix" this to an encrypted BLOB in a later migration.
            //
            // Nullable, and deliberately not backfilled. A null means "attempt predates this column", which every
            // reader resolves by falling back to the project's profile — exactly the behaviour before this migration.
            // Backfilling the project's CURRENT profile onto historical attempts would be a lie: it would assert those
            // attempts ran under a profile that may have been edited since, which is the very confusion this column
            // exists to remove.
            migrationBuilder.AddColumn<string>(
                name: "command_profile_json",
                table: "development_attempts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "command_profile_json",
                table: "development_attempts");
        }
    }
}

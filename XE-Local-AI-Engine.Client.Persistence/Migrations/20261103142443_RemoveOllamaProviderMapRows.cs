using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveOllamaProviderMapRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Those models then route to llamacpp and fail like any model that is not installed.
            migrationBuilder.Sql("DELETE FROM model_provider_map WHERE provider_name = 'ollama' COLLATE NOCASE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The deleted rows cannot be restored, and nothing reads them any more.
        }
    }
}

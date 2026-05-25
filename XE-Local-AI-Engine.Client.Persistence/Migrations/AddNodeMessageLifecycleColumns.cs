#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations;

using Microsoft.EntityFrameworkCore.Migrations;

/// <inheritdoc />
public partial class AddNodeMessageLifecycleColumns : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("error",
            "messages",
            "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>("request_id",
            "messages",
            "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>("status",
            "messages",
            "TEXT",
            nullable: false,
            defaultValue: "completed");

        migrationBuilder.AddColumn<long>("updated_at_utc",
            "messages",
            "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.Sql("UPDATE messages SET updated_at_utc = created_at_utc WHERE updated_at_utc = 0;");

        migrationBuilder.CreateIndex("IX_messages_request_id",
            "messages",
            "request_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_messages_request_id",
            "messages");

        migrationBuilder.DropColumn("error",
            "messages");

        migrationBuilder.DropColumn("request_id",
            "messages");

        migrationBuilder.DropColumn("status",
            "messages");

        migrationBuilder.DropColumn("updated_at_utc",
            "messages");
    }
}

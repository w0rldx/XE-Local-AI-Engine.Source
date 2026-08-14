namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb;

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

[DbContext(typeof(NodeChatDbContext))]
[Migration("20260814090000_AddModelProviderMapRevision")]
public sealed class AddModelProviderMapRevision : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "revision",
            table: "model_provider_map",
            type: "TEXT",
            nullable: false,
            defaultValue: "legacy");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "revision",
            table: "model_provider_map");
    }
}

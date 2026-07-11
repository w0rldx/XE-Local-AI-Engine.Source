using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddBenchmarkProfileRevisionBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "flash_attn",
                table: "model_fit_benchmarks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kv_type_v",
                table: "model_fit_benchmarks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "profile_id",
                table: "model_fit_benchmarks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_fit_benchmarks_profile_id",
                table: "model_fit_benchmarks",
                column: "profile_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_model_fit_benchmarks_profile_id",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "flash_attn",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "kv_type_v",
                table: "model_fit_benchmarks");

            migrationBuilder.DropColumn(
                name: "profile_id",
                table: "model_fit_benchmarks");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddTrainingArtifactQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "source_artifact_id",
                table: "training_evaluation_runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_kind",
                table: "training_evaluation_runs",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "InstalledModel");

            migrationBuilder.AddColumn<Guid>(
                name: "quality_comparison_id",
                table: "training_artifacts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "quality_decision_json",
                table: "training_artifacts",
                type: "BLOB",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_training_evaluation_runs_source_artifact",
                table: "training_evaluation_runs",
                column: "source_artifact_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_artifacts_quality_comparison",
                table: "training_artifacts",
                column: "quality_comparison_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_training_evaluation_runs_source_artifact",
                table: "training_evaluation_runs");

            migrationBuilder.DropIndex(
                name: "ix_training_artifacts_quality_comparison",
                table: "training_artifacts");

            migrationBuilder.DropColumn(
                name: "source_artifact_id",
                table: "training_evaluation_runs");

            migrationBuilder.DropColumn(
                name: "target_kind",
                table: "training_evaluation_runs");

            migrationBuilder.DropColumn(
                name: "quality_comparison_id",
                table: "training_artifacts");

            migrationBuilder.DropColumn(
                name: "quality_decision_json",
                table: "training_artifacts");
        }
    }
}

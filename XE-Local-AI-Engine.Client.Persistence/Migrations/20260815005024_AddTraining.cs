using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddTraining : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tool_mock_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tool_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE"),
                    mock_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    verification_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    verification_state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_mock_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "training_base_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    repo_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false, collation: "NOCASE"),
                    revision = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    files_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    total_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    license_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    error_message = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_training_base_artifacts", x => x.id);
                    table.CheckConstraint("CK_training_base_artifacts_total_bytes", "total_bytes >= 0");
                });

            migrationBuilder.CreateTable(
                name: "training_dataset_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    definition_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    definition_version = table.Column<long>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_training_dataset_definitions", x => x.id);
                    table.CheckConstraint("CK_training_dataset_definitions_version", "definition_version > 0");
                });

            migrationBuilder.CreateTable(
                name: "training_datasets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    definition_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    definition_version = table.Column<long>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    content_fingerprint = table.Column<string>(type: "TEXT", maxLength: 67, nullable: true),
                    total_sample_count = table.Column<int>(type: "INTEGER", nullable: false),
                    good_sample_count = table.Column<int>(type: "INTEGER", nullable: false),
                    bad_sample_count = table.Column<int>(type: "INTEGER", nullable: false),
                    rejected_sample_count = table.Column<int>(type: "INTEGER", nullable: false),
                    duplicate_sample_count = table.Column<int>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_training_datasets", x => x.id);
                    table.CheckConstraint("CK_training_datasets_counts", "total_sample_count >= 0 AND good_sample_count >= 0 AND bad_sample_count >= 0 AND rejected_sample_count >= 0 AND duplicate_sample_count >= 0");
                    table.ForeignKey(
                        name: "FK_training_datasets_training_dataset_definitions_definition_id",
                        column: x => x.definition_id,
                        principalTable: "training_dataset_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dataset_generation_work_items",
                columns: table => new
                {
                    queue_sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    dataset_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    enqueued_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    finished_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    error_message = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dataset_generation_work_items", x => x.queue_sequence);
                    table.CheckConstraint("CK_dataset_generation_work_items_attempt", "attempt = 1");
                    table.ForeignKey(
                        name: "FK_dataset_generation_work_items_training_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "training_datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "training_dataset_samples",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    dataset_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    label = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    review_state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    content_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    validation_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    provenance = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    source_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_training_dataset_samples", x => x.id);
                    table.CheckConstraint("CK_training_dataset_samples_sequence", "sequence >= 0");
                    table.ForeignKey(
                        name: "FK_training_dataset_samples_training_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "training_datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dataset_generation_work_items_status_sequence",
                table: "dataset_generation_work_items",
                columns: new[] { "status", "queue_sequence" });

            migrationBuilder.CreateIndex(
                name: "ux_dataset_generation_work_items_dataset",
                table: "dataset_generation_work_items",
                column: "dataset_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tool_mock_definitions_tool_name",
                table: "tool_mock_definitions",
                column: "tool_name");

            migrationBuilder.CreateIndex(
                name: "ux_training_base_artifacts_repo_revision",
                table: "training_base_artifacts",
                columns: new[] { "repo_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_training_dataset_samples_dataset_source_hash",
                table: "training_dataset_samples",
                columns: new[] { "dataset_id", "source_hash" });

            migrationBuilder.CreateIndex(
                name: "ux_training_dataset_samples_dataset_sequence",
                table: "training_dataset_samples",
                columns: new[] { "dataset_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_training_datasets_definition_created_at",
                table: "training_datasets",
                columns: new[] { "definition_id", "created_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dataset_generation_work_items");

            migrationBuilder.DropTable(
                name: "tool_mock_definitions");

            migrationBuilder.DropTable(
                name: "training_base_artifacts");

            migrationBuilder.DropTable(
                name: "training_dataset_samples");

            migrationBuilder.DropTable(
                name: "training_datasets");

            migrationBuilder.DropTable(
                name: "training_dataset_definitions");
        }
    }
}

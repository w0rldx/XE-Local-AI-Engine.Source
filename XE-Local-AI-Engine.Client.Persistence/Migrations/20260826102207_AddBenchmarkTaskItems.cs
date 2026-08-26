using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations
{
    /// <summary>
    ///     The ONE schema change the task-suite data layer makes: the <c>benchmark_task_items</c> table, the project's
    ///     item-set hash, and the four identity stamps a run is frozen with.
    ///     <para>
    ///         Three of those stamps are NOT NULL, which is the point of them. A nullable <c>cell_key</c> would put
    ///         every ungrouped run of a project into one anonymous bucket and average their scores together with
    ///         nothing to notice it, so existing rows are backfilled to their own singleton cell — a plaintext derived
    ///         value, which is why this backfill is legal at all. The two hash columns take a legacy constant that
    ///         they are also COMPARED against, so a run frozen before task items existed is never read as stale.
    ///     </para>
    ///     <para>
    ///         No ENCRYPTED backfill is attempted, and none can be: a migration runs without the node encryption key,
    ///         and <c>prompt_json</c> is a required encrypted blob AAD-bound to its own item's id. Item 0 of a project
    ///         created before this migration is therefore materialized by the store on first touch, inside the normal
    ///         EF write path where both interceptors run.
    ///     </para>
    /// </summary>
    public partial class AddBenchmarkTaskItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cell_key",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "task_input_hash",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 67,
                nullable: false,
                defaultValue: "v1:legacy");

            migrationBuilder.AddColumn<Guid>(
                name: "task_item_id",
                table: "benchmark_runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "task_item_index",
                table: "benchmark_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "task_item_set_hash",
                table: "benchmark_runs",
                type: "TEXT",
                maxLength: 67,
                nullable: false,
                defaultValue: "v1:legacy");

            migrationBuilder.AddColumn<string>(
                name: "task_item_set_hash",
                table: "benchmark_projects",
                type: "TEXT",
                maxLength: 67,
                nullable: true);

            // Every pre-existing run becomes its own singleton cell, so it ranks exactly as it did and can never
            // collide with another freeze's. Derived and plaintext — the one kind of backfill a migration may do.
            migrationBuilder.Sql("UPDATE benchmark_runs SET cell_key = 'run:' || id;");

            migrationBuilder.CreateTable(
                name: "benchmark_task_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    parent_item_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    index = table.Column<int>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    input_hash = table.Column<string>(type: "TEXT", maxLength: 67, nullable: false),
                    counts_toward_score = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    prompt_json = table.Column<byte[]>(type: "BLOB", nullable: false),
                    reference_answer_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    verifier_config_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    generator_config_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_benchmark_task_items", x => x.id);
                    table.CheckConstraint("CK_benchmark_task_items_index", "\"index\" >= 0");
                    table.CheckConstraint("CK_benchmark_task_items_kind", "kind IN ('prompt', 'niah', 'niahCase')");
                    table.CheckConstraint("CK_benchmark_task_items_revision", "revision >= 1");
                    table.ForeignKey(
                        name: "FK_benchmark_task_items_benchmark_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "benchmark_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_runs_project_cell_key",
                table: "benchmark_runs",
                columns: new[] { "project_id", "cell_key" });

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_runs_project_task_item_id",
                table: "benchmark_runs",
                columns: new[] { "project_id", "task_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_task_items_parent",
                table: "benchmark_task_items",
                column: "parent_item_id");

            migrationBuilder.CreateIndex(
                name: "ux_benchmark_task_items_project_index",
                table: "benchmark_task_items",
                columns: new[] { "project_id", "index" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "benchmark_task_items");

            migrationBuilder.DropIndex(
                name: "ix_benchmark_runs_project_cell_key",
                table: "benchmark_runs");

            migrationBuilder.DropIndex(
                name: "ix_benchmark_runs_project_task_item_id",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "cell_key",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "task_input_hash",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "task_item_id",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "task_item_index",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "task_item_set_hash",
                table: "benchmark_runs");

            migrationBuilder.DropColumn(
                name: "task_item_set_hash",
                table: "benchmark_projects");
        }
    }
}

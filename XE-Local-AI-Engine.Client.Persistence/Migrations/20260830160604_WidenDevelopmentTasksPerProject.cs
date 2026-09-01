using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations
{
    /// <summary>
    ///     A development project may carry MORE THAN ONE task: workflow decomposition materializes one implementation
    ///     task per child node, and every one of them belongs in the project whose trust acknowledgement, model ids,
    ///     egress policy and command profile the parent run was already authorised against.
    ///     <para>
    ///         Index only — no column moves, so SQLite alters the index in place rather than rebuilding the table. The
    ///         index itself stays because every project-scoped read (the task list, the project detail) uses it; it
    ///         loses its uniqueness and, with it, the <c>ux_</c> name this codebase reserves for indexes that enforce
    ///         one row.
    ///     </para>
    ///     <para>
    ///         <c>Down</c> restores the unique index and therefore FAILS on a database that has since grown a second
    ///         task for one project — correctly: the rows are the operator's work, and a rollback must not decide which
    ///         of them to destroy.
    ///     </para>
    /// </summary>
    public partial class WidenDevelopmentTasksPerProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_development_tasks_project_id",
                table: "development_tasks");

            migrationBuilder.CreateIndex(
                name: "ix_development_tasks_project_id",
                table: "development_tasks",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_development_tasks_project_id",
                table: "development_tasks");

            migrationBuilder.CreateIndex(
                name: "ux_development_tasks_project_id",
                table: "development_tasks",
                column: "project_id",
                unique: true);
        }
    }
}

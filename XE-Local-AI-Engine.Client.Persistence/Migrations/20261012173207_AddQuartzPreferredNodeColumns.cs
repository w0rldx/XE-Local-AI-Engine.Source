using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuartzPreferredNodeColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Quartz.NET 3.19 node-affinity columns, verbatim from upstream add_preferred_node_sqlite.sql.
            // Quartz probes for both at startup; required by Quartz 4.x.
            migrationBuilder.Sql("ALTER TABLE QRTZ_TRIGGERS ADD COLUMN PREFERRED_NODE NVARCHAR(200) NULL;");
            migrationBuilder.Sql("ALTER TABLE QRTZ_TRIGGERS ADD COLUMN PREFERRED_NODE_AUTO BIT NOT NULL DEFAULT 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE QRTZ_TRIGGERS DROP COLUMN PREFERRED_NODE_AUTO;");
            migrationBuilder.Sql("ALTER TABLE QRTZ_TRIGGERS DROP COLUMN PREFERRED_NODE;");
        }
    }
}

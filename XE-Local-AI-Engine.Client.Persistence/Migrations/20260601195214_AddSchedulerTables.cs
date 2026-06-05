using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddSchedulerTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scheduled_job_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    template_id = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    schedule_kind = table.Column<int>(type: "INTEGER", nullable: false),
                    cron_expression = table.Column<string>(type: "TEXT", nullable: true),
                    interval_seconds = table.Column<long>(type: "INTEGER", nullable: true),
                    repeat_count = table.Column<int>(type: "INTEGER", nullable: true),
                    start_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    end_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    time_zone_id = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "UTC"),
                    misfire_policy = table.Column<int>(type: "INTEGER", nullable: false),
                    prevent_overlap = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    max_runtime_seconds = table.Column<int>(type: "INTEGER", nullable: true),
                    parameter_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    created_by = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    disabled_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    deleted_at_utc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduled_job_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scheduled_job_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    scheduled_job_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    template_id = table.Column<string>(type: "TEXT", nullable: false),
                    quartz_fire_instance_id = table.Column<string>(type: "TEXT", nullable: true),
                    triggered_by = table.Column<int>(type: "INTEGER", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    scheduled_fire_time_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    actual_fire_time_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    duration_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    summary = table.Column<string>(type: "TEXT", nullable: true),
                    details_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    error_message = table.Column<string>(type: "TEXT", nullable: true),
                    error_details = table.Column<string>(type: "TEXT", nullable: true),
                    cancellation_requested_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduled_job_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scheduled_job_run_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    level = table.Column<int>(type: "INTEGER", nullable: false),
                    message = table.Column<string>(type: "TEXT", nullable: true),
                    data_json = table.Column<byte[]>(type: "BLOB", nullable: true),
                    occurred_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduled_job_run_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_scheduled_job_run_events_scheduled_job_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "scheduled_job_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_job_definitions_template_id_enabled",
                table: "scheduled_job_definitions",
                columns: new[] { "template_id", "enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_job_run_events_run_id_sequence",
                table: "scheduled_job_run_events",
                columns: new[] { "run_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_job_runs_quartz_fire_instance_id",
                table: "scheduled_job_runs",
                column: "quartz_fire_instance_id",
                unique: true,
                filter: "quartz_fire_instance_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_job_runs_scheduled_job_id_actual_fire_time_utc",
                table: "scheduled_job_runs",
                columns: new[] { "scheduled_job_id", "actual_fire_time_utc" });

            // Quartz.NET 3.18.1 ADO job-store schema (QRTZ_* tables), verbatim from the official
            // database/tables/tables_sqlite.sql at git tag v3.18.1. The persistent store reads these
            // at scheduler startup with PerformSchemaValidation=true and table prefix QRTZ_.
            // The upstream leading DROP statements are intentionally omitted (this is a tracked,
            // run-once migration on a fresh DB); the DELETE_* triggers emulate ON DELETE CASCADE for
            // the trigger sub-tables on SQLite. Microsoft.Data.Sqlite executes the batched statements.
            migrationBuilder.Sql(@"
CREATE TABLE QRTZ_JOB_DETAILS
  (
    SCHED_NAME NVARCHAR(120) NOT NULL,
	JOB_NAME NVARCHAR(150) NOT NULL,
    JOB_GROUP NVARCHAR(150) NOT NULL,
    DESCRIPTION NVARCHAR(250) NULL,
    JOB_CLASS_NAME   NVARCHAR(250) NOT NULL,
    IS_DURABLE BIT NOT NULL,
    IS_NONCONCURRENT BIT NOT NULL,
    IS_UPDATE_DATA BIT  NOT NULL,
	REQUESTS_RECOVERY BIT NOT NULL,
    JOB_DATA BLOB NULL,
    PRIMARY KEY (SCHED_NAME,JOB_NAME,JOB_GROUP)
);

CREATE TABLE QRTZ_TRIGGERS
  (
    SCHED_NAME NVARCHAR(120) NOT NULL,
	TRIGGER_NAME NVARCHAR(150) NOT NULL,
    TRIGGER_GROUP NVARCHAR(150) NOT NULL,
    JOB_NAME NVARCHAR(150) NOT NULL,
    JOB_GROUP NVARCHAR(150) NOT NULL,
    DESCRIPTION NVARCHAR(250) NULL,
    NEXT_FIRE_TIME BIGINT NULL,
    PREV_FIRE_TIME BIGINT NULL,
    PRIORITY INTEGER NULL,
    TRIGGER_STATE NVARCHAR(16) NOT NULL,
    TRIGGER_TYPE NVARCHAR(8) NOT NULL,
    START_TIME BIGINT NOT NULL,
    END_TIME BIGINT NULL,
    CALENDAR_NAME NVARCHAR(200) NULL,
    MISFIRE_INSTR INTEGER NULL,
    MISFIRE_ORIG_FIRE_TIME INTEGER NULL,
    EXECUTION_GROUP NVARCHAR(200) NULL,
    JOB_DATA BLOB NULL,
    PRIMARY KEY (SCHED_NAME,TRIGGER_NAME,TRIGGER_GROUP),
    FOREIGN KEY (SCHED_NAME,JOB_NAME,JOB_GROUP)
        REFERENCES QRTZ_JOB_DETAILS(SCHED_NAME,JOB_NAME,JOB_GROUP)
);

CREATE TABLE QRTZ_SIMPLE_TRIGGERS
  (
    SCHED_NAME NVARCHAR(120) NOT NULL,
	TRIGGER_NAME NVARCHAR(150) NOT NULL,
    TRIGGER_GROUP NVARCHAR(150) NOT NULL,
    REPEAT_COUNT BIGINT NOT NULL,
    REPEAT_INTERVAL BIGINT NOT NULL,
    TIMES_TRIGGERED BIGINT NOT NULL,
    PRIMARY KEY (SCHED_NAME,TRIGGER_NAME,TRIGGER_GROUP),
    FOREIGN KEY (SCHED_NAME,TRIGGER_NAME,TRIGGER_GROUP)
        REFERENCES QRTZ_TRIGGERS(SCHED_NAME,TRIGGER_NAME,TRIGGER_GROUP) ON DELETE CASCADE
);

CREATE TRIGGER DELETE_SIMPLE_TRIGGER DELETE ON QRTZ_TRIGGERS
BEGIN
	DELETE FROM QRTZ_SIMPLE_TRIGGERS WHERE SCHED_NAME=OLD.SCHED_NAME AND TRIGGER_NAME=OLD.TRIGGER_NAME AND TRIGGER_GROUP=OLD.TRIGGER_GROUP;
END
;

CREATE TABLE QRTZ_SIMPROP_TRIGGERS
  (
    SCHED_NAME NVARCHAR (120) NOT NULL ,
    TRIGGER_NAME NVARCHAR (150) NOT NULL ,
    TRIGGER_GROUP NVARCHAR (150) NOT NULL ,
    STR_PROP_1 NVARCHAR (512) NULL,
    STR_PROP_2 NVARCHAR (512) NULL,
    STR_PROP_3 NVARCHAR (512) NULL,
    INT_PROP_1 INT NULL,
    INT_PROP_2 INT NULL,
    LONG_PROP_1 BIGINT NULL,
    LONG_PROP_2 BIGINT NULL,
    DEC_PROP_1 NUMERIC NULL,
    DEC_PROP_2 NUMERIC NULL,
    BOOL_PROP_1 BIT NULL,
    BOOL_PROP_2 BIT NULL,
    TIME_ZONE_ID NVARCHAR(80) NULL,
	PRIMARY KEY (SCHED_NAME,TRIGGER_NAME,TRIGGER_GROUP),
	FOREIGN KEY (SCHED_NAME,TRIGGER_NAME,TRIGGER_GROUP)
        REFERENCES QRTZ_TRIGGERS(SCHED_NAME,TRIGGER_NAME,TRIGGER_GROUP) ON DELETE CASCADE
);

CREATE TRIGGER DELETE_SIMPROP_TRIGGER DELETE ON QRTZ_TRIGGERS
BEGIN
	DELETE FROM QRTZ_SIMPROP_TRIGGERS WHERE SCHED_NAME=OLD.SCHED_NAME AND TRIGGER_NAME=OLD.TRIGGER_NAME AND TRIGGER_GROUP=OLD.TRIGGER_GROUP;
END
;

CREATE TABLE QRTZ_CRON_TRIGGERS
  (
    SCHED_NAME NVARCHAR(120) NOT NULL,
	TRIGGER_NAME NVARCHAR(150) NOT NULL,
    TRIGGER_GROUP NVARCHAR(150) NOT NULL,
    CRON_EXPRESSION NVARCHAR(250) NOT NULL,
    TIME_ZONE_ID NVARCHAR(80),
    PRIMARY KEY (SCHED_NAME,TRIGGER_NAME,TRIGGER_GROUP),
    FOREIGN KEY (SCHED_NAME,TRIGGER_NAME,TRIGGER_GROUP)
        REFERENCES QRTZ_TRIGGERS(SCHED_NAME,TRIGGER_NAME,TRIGGER_GROUP) ON DELETE CASCADE
);

CREATE TRIGGER DELETE_CRON_TRIGGER DELETE ON QRTZ_TRIGGERS
BEGIN
	DELETE FROM QRTZ_CRON_TRIGGERS WHERE SCHED_NAME=OLD.SCHED_NAME AND TRIGGER_NAME=OLD.TRIGGER_NAME AND TRIGGER_GROUP=OLD.TRIGGER_GROUP;
END
;

CREATE TABLE QRTZ_BLOB_TRIGGERS
  (
    SCHED_NAME NVARCHAR(120) NOT NULL,
	TRIGGER_NAME NVARCHAR(150) NOT NULL,
    TRIGGER_GROUP NVARCHAR(150) NOT NULL,
    BLOB_DATA BLOB NULL,
    PRIMARY KEY (SCHED_NAME,TRIGGER_NAME,TRIGGER_GROUP),
    FOREIGN KEY (SCHED_NAME,TRIGGER_NAME,TRIGGER_GROUP)
        REFERENCES QRTZ_TRIGGERS(SCHED_NAME,TRIGGER_NAME,TRIGGER_GROUP) ON DELETE CASCADE
);

CREATE TRIGGER DELETE_BLOB_TRIGGER DELETE ON QRTZ_TRIGGERS
BEGIN
	DELETE FROM QRTZ_BLOB_TRIGGERS WHERE SCHED_NAME=OLD.SCHED_NAME AND TRIGGER_NAME=OLD.TRIGGER_NAME AND TRIGGER_GROUP=OLD.TRIGGER_GROUP;
END
;

CREATE TABLE QRTZ_CALENDARS
  (
    SCHED_NAME NVARCHAR(120) NOT NULL,
	CALENDAR_NAME  NVARCHAR(200) NOT NULL,
    CALENDAR BLOB NOT NULL,
    PRIMARY KEY (SCHED_NAME,CALENDAR_NAME)
);

CREATE TABLE QRTZ_PAUSED_TRIGGER_GRPS
  (
    SCHED_NAME NVARCHAR(120) NOT NULL,
	TRIGGER_GROUP NVARCHAR(150) NOT NULL,
    PRIMARY KEY (SCHED_NAME,TRIGGER_GROUP)
);

CREATE TABLE QRTZ_FIRED_TRIGGERS
  (
    SCHED_NAME NVARCHAR(120) NOT NULL,
	ENTRY_ID NVARCHAR(140) NOT NULL,
    TRIGGER_NAME NVARCHAR(150) NOT NULL,
    TRIGGER_GROUP NVARCHAR(150) NOT NULL,
    INSTANCE_NAME NVARCHAR(200) NOT NULL,
    FIRED_TIME BIGINT NOT NULL,
    SCHED_TIME BIGINT NOT NULL,
	PRIORITY INTEGER NOT NULL,
    STATE NVARCHAR(16) NOT NULL,
    JOB_NAME NVARCHAR(150) NULL,
    JOB_GROUP NVARCHAR(150) NULL,
    IS_NONCONCURRENT BIT NULL,
    REQUESTS_RECOVERY BIT NULL,
    EXECUTION_GROUP NVARCHAR(200) NULL,
    PRIMARY KEY (SCHED_NAME,ENTRY_ID)
);

CREATE TABLE QRTZ_SCHEDULER_STATE
  (
    SCHED_NAME NVARCHAR(120) NOT NULL,
	INSTANCE_NAME NVARCHAR(200) NOT NULL,
    LAST_CHECKIN_TIME BIGINT NOT NULL,
    CHECKIN_INTERVAL BIGINT NOT NULL,
    PRIMARY KEY (SCHED_NAME,INSTANCE_NAME)
);

CREATE TABLE QRTZ_LOCKS
  (
    SCHED_NAME NVARCHAR(120) NOT NULL,
	LOCK_NAME  NVARCHAR(40) NOT NULL,
    PRIMARY KEY (SCHED_NAME,LOCK_NAME)
);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scheduled_job_definitions");

            migrationBuilder.DropTable(
                name: "scheduled_job_run_events");

            migrationBuilder.DropTable(
                name: "scheduled_job_runs");

            // Drop the Quartz.NET ADO job-store tables (QRTZ_*) created in Up(). Order mirrors the
            // upstream tables_sqlite.sql DROP block (children before parents); each table's DELETE_*
            // triggers are removed automatically by SQLite when the owning table is dropped.
            migrationBuilder.Sql(@"
DROP TABLE IF EXISTS QRTZ_FIRED_TRIGGERS;
DROP TABLE IF EXISTS QRTZ_PAUSED_TRIGGER_GRPS;
DROP TABLE IF EXISTS QRTZ_SCHEDULER_STATE;
DROP TABLE IF EXISTS QRTZ_LOCKS;
DROP TABLE IF EXISTS QRTZ_SIMPROP_TRIGGERS;
DROP TABLE IF EXISTS QRTZ_SIMPLE_TRIGGERS;
DROP TABLE IF EXISTS QRTZ_CRON_TRIGGERS;
DROP TABLE IF EXISTS QRTZ_BLOB_TRIGGERS;
DROP TABLE IF EXISTS QRTZ_TRIGGERS;
DROP TABLE IF EXISTS QRTZ_JOB_DETAILS;
DROP TABLE IF EXISTS QRTZ_CALENDARS;
");
        }
    }
}

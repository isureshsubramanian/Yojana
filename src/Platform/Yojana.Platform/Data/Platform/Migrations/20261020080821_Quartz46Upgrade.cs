using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Scheduling;

#nullable disable

namespace Yojana.Platform.Data.Platform.Migrations
{
    /// <summary>
    /// Quartz.NET 4.6 job-store columns (scheduler version, interrupt request, per-trigger misfire threshold). A 4.6
    /// node refuses to start without them. Each vendored script checks before it alters, so the step is safe on a
    /// database whose tables already have them, and on one where 4.5 nodes are still running.
    /// </summary>
    public partial class Quartz46Upgrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var batch in QuartzSchema.Upgrade46Batches())
            {
                migrationBuilder.Sql(batch);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only for rolling the package back to 4.5, which never names these columns.
            migrationBuilder.Sql("IF COL_LENGTH('quartz.QRTZ_TRIGGERS','MISFIRE_THRESHOLD') IS NOT NULL ALTER TABLE [quartz].[QRTZ_TRIGGERS] DROP COLUMN [MISFIRE_THRESHOLD];");
            migrationBuilder.Sql("IF COL_LENGTH('quartz.QRTZ_FIRED_TRIGGERS','INTERRUPT_REQUESTED_TIME') IS NOT NULL ALTER TABLE [quartz].[QRTZ_FIRED_TRIGGERS] DROP COLUMN [INTERRUPT_REQUESTED_TIME];");
            migrationBuilder.Sql("IF COL_LENGTH('quartz.QRTZ_SCHEDULER_STATE','QUARTZ_VERSION') IS NOT NULL ALTER TABLE [quartz].[QRTZ_SCHEDULER_STATE] DROP COLUMN [QUARTZ_VERSION];");
        }
    }
}

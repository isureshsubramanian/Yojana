using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations;

public partial class P3T16bWorkbenchChoices : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_workbench_request_status",
            schema: "serialisation",
            table: "workbench_request");

        migrationBuilder.AddColumn<DateTime>(name: "classified_at", schema: "serialisation", table: "workbench_request", type: "datetime2(3)", nullable: true);
        migrationBuilder.AddColumn<string>(name: "classified_by", schema: "serialisation", table: "workbench_request", type: "nvarchar(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<DateTime>(name: "escalated_at", schema: "serialisation", table: "workbench_request", type: "datetime2(3)", nullable: true);
        migrationBuilder.AddColumn<string>(name: "escalated_by", schema: "serialisation", table: "workbench_request", type: "nvarchar(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<string>(name: "unclassified_description", schema: "serialisation", table: "workbench_request", type: "nvarchar(2000)", maxLength: 2000, nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_workbench_request_status",
            schema: "serialisation",
            table: "workbench_request",
            sql: "status IN ('draft', 'awaiting_approval', 'approved', 'rejected', 'committed', 'superseded', 'unclassified', 'no_command_fits')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_workbench_request_status",
            schema: "serialisation",
            table: "workbench_request");

        migrationBuilder.DropColumn(name: "classified_at", schema: "serialisation", table: "workbench_request");
        migrationBuilder.DropColumn(name: "classified_by", schema: "serialisation", table: "workbench_request");
        migrationBuilder.DropColumn(name: "escalated_at", schema: "serialisation", table: "workbench_request");
        migrationBuilder.DropColumn(name: "escalated_by", schema: "serialisation", table: "workbench_request");
        migrationBuilder.DropColumn(name: "unclassified_description", schema: "serialisation", table: "workbench_request");

        migrationBuilder.AddCheckConstraint(
            name: "ck_workbench_request_status",
            schema: "serialisation",
            table: "workbench_request",
            sql: "status IN ('draft', 'awaiting_approval', 'approved', 'rejected', 'committed', 'superseded')");
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P7B01AssertionCustody : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "custody_episode_id",
                schema: "serialisation",
                table: "customer_assertion",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "unit_at_customer_site",
                schema: "serialisation",
                table: "customer_assertion",
                type: "bit",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "custody_episode_id",
                schema: "serialisation",
                table: "customer_assertion");

            migrationBuilder.DropColumn(
                name: "unit_at_customer_site",
                schema: "serialisation",
                table: "customer_assertion");
        }
    }
}

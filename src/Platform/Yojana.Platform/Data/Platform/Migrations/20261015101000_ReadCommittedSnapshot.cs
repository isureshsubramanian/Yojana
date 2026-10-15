using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Platform.Data.Platform.Migrations
{
    /// <summary>
    /// Database-level setting that cannot run inside a transaction, so it is the only operation of this
    /// migration (EF Core would otherwise warn about a partially applied migration on failure). Idempotent.
    /// </summary>
    [DbContext(typeof(PlatformDbContext))]
    [Migration("20261015101000_ReadCommittedSnapshot")]
    public sealed class ReadCommittedSnapshot : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnableReadCommittedSnapshot();
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Left on: turning snapshot isolation off is an operator decision, not a rollback.
        }
    }
}

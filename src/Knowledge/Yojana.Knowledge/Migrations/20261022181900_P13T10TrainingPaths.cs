using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Knowledge.Migrations
{
    /// <inheritdoc />
    public partial class P13T10TrainingPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "training_path",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    path_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    role = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    introduction = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    current_version = table.Column<int>(type: "int", nullable: false),
                    created_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    last_changed_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    last_changed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_training_path", x => new { x.tenant_id, x.path_id });
                    table.CheckConstraint("ck_training_path_version", "current_version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "training_path_version",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    path_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    version = table.Column<int>(type: "int", nullable: false),
                    role = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    introduction = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    changed_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    changed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_training_path_version", x => new { x.tenant_id, x.path_id, x.version });
                    table.CheckConstraint("ck_training_path_version_number", "version >= 1");
                    table.ForeignKey(
                        name: "fk_training_path_version_tenant_id_path_id",
                        columns: x => new { x.tenant_id, x.path_id },
                        principalSchema: "knowledge",
                        principalTable: "training_path",
                        principalColumns: new[] { "tenant_id", "path_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "training_progress",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    path_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entry_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    read_entry_version = table.Column<int>(type: "int", nullable: false),
                    read_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_training_progress", x => new { x.tenant_id, x.path_id, x.entry_id, x.user_id });
                    table.CheckConstraint("ck_training_progress_entry_version", "read_entry_version >= 1");
                    table.ForeignKey(
                        name: "fk_training_progress_tenant_id_entry_id",
                        columns: x => new { x.tenant_id, x.entry_id },
                        principalSchema: "knowledge",
                        principalTable: "knowledge_entry",
                        principalColumns: new[] { "tenant_id", "entry_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_training_progress_tenant_id_path_id",
                        columns: x => new { x.tenant_id, x.path_id },
                        principalSchema: "knowledge",
                        principalTable: "training_path",
                        principalColumns: new[] { "tenant_id", "path_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "training_welcome_dismissal",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    path_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    dismissed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_training_welcome_dismissal", x => new { x.tenant_id, x.path_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_training_welcome_dismissal_tenant_id_path_id",
                        columns: x => new { x.tenant_id, x.path_id },
                        principalSchema: "knowledge",
                        principalTable: "training_path",
                        principalColumns: new[] { "tenant_id", "path_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "training_path_entry",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    path_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    version = table.Column<int>(type: "int", nullable: false),
                    position = table.Column<int>(type: "int", nullable: false),
                    entry_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_training_path_entry", x => new { x.tenant_id, x.path_id, x.version, x.position });
                    table.CheckConstraint("ck_training_path_entry_position", "position >= 1");
                    table.ForeignKey(
                        name: "fk_training_path_entry_tenant_id_entry_id",
                        columns: x => new { x.tenant_id, x.entry_id },
                        principalSchema: "knowledge",
                        principalTable: "knowledge_entry",
                        principalColumns: new[] { "tenant_id", "entry_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_training_path_entry_tenant_id_path_id_version",
                        columns: x => new { x.tenant_id, x.path_id, x.version },
                        principalSchema: "knowledge",
                        principalTable: "training_path_version",
                        principalColumns: new[] { "tenant_id", "path_id", "version" },
                        onDelete: ReferentialAction.Restrict);
                });

            foreach (var table in new[] { "training_path", "training_path_version", "training_path_entry",
                         "training_progress", "training_welcome_dismissal" })
                migrationBuilder.AddTenantPolicy("knowledge", table);

            migrationBuilder.CreateIndex(
                name: "ix_training_path_tenant_id_last_changed_at_path_id",
                schema: "knowledge",
                table: "training_path",
                columns: new[] { "tenant_id", "last_changed_at", "path_id" });

            migrationBuilder.CreateIndex(
                name: "ux_training_path_tenant_id_role",
                schema: "knowledge",
                table: "training_path",
                columns: new[] { "tenant_id", "role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_training_path_entry_tenant_id_entry_id_path_id_version",
                schema: "knowledge",
                table: "training_path_entry",
                columns: new[] { "tenant_id", "entry_id", "path_id", "version" });

            migrationBuilder.CreateIndex(
                name: "ux_training_path_entry_tenant_id_path_id_version_entry_id",
                schema: "knowledge",
                table: "training_path_entry",
                columns: new[] { "tenant_id", "path_id", "version", "entry_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_training_path_version_tenant_id_path_id_changed_at",
                schema: "knowledge",
                table: "training_path_version",
                columns: new[] { "tenant_id", "path_id", "changed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_training_progress_tenant_id_entry_id",
                schema: "knowledge",
                table: "training_progress",
                columns: new[] { "tenant_id", "entry_id" });

            migrationBuilder.CreateIndex(
                name: "ix_training_progress_tenant_id_user_id_read_at_path_id_entry_id",
                schema: "knowledge",
                table: "training_progress",
                columns: new[] { "tenant_id", "user_id", "read_at", "path_id", "entry_id" });

            migrationBuilder.CreateIndex(
                name: "ix_training_welcome_dismissal_tenant_id_user_id_dismissed_at_path_id",
                schema: "knowledge",
                table: "training_welcome_dismissal",
                columns: new[] { "tenant_id", "user_id", "dismissed_at", "path_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "training_path_entry", "training_progress", "training_welcome_dismissal",
                         "training_path_version", "training_path" })
                migrationBuilder.RemoveTenantPolicy("knowledge", table);

            migrationBuilder.DropTable(
                name: "training_path_entry",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "training_progress",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "training_welcome_dismissal",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "training_path_version",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "training_path",
                schema: "knowledge");
        }
    }
}

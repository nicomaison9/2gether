using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwoGether.Server.Migrations
{
    /// <inheritdoc />
    public partial class ConfigurerConservationMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MessageRetentionSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RetentionDays = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageRetentionSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DirectMessages_SentAt",
                table: "DirectMessages",
                column: "SentAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageRetentionSettings");

            migrationBuilder.DropIndex(
                name: "IX_DirectMessages_SentAt",
                table: "DirectMessages");
        }
    }
}

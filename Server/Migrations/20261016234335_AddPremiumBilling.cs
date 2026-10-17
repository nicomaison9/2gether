using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TwoGether.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPremiumBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PremiumCheckoutReservations",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    StripeSessionId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PremiumCheckoutReservations", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_PremiumCheckoutReservations_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PremiumSubscriptions",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    StripeCustomerId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    StripeSubscriptionId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CurrentPeriodEnd = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastStripeEventCreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CancelAtPeriodEnd = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PremiumSubscriptions", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_PremiumSubscriptions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StripeWebhookReceipts",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StripeWebhookReceipts", x => x.EventId);
                    table.ForeignKey(
                        name: "FK_StripeWebhookReceipts_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PremiumCheckoutReservations_StripeSessionId",
                table: "PremiumCheckoutReservations",
                column: "StripeSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PremiumSubscriptions_StripeSubscriptionId",
                table: "PremiumSubscriptions",
                column: "StripeSubscriptionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StripeWebhookReceipts_UserId",
                table: "StripeWebhookReceipts",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PremiumCheckoutReservations");

            migrationBuilder.DropTable(
                name: "PremiumSubscriptions");

            migrationBuilder.DropTable(
                name: "StripeWebhookReceipts");
        }
    }
}

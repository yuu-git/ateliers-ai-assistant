using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ateliers.Ai.Assistant.Infrastructure.Conversations.Migrations
{
    /// <inheritdoc />
    public partial class WorkerQueueAndSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Processing",
                table: "WebArticles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "RetryAfterTicks",
                table: "WebArticles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkerSourceStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerSourceStates", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkerSourceStates");

            migrationBuilder.DropColumn(
                name: "Processing",
                table: "WebArticles");

            migrationBuilder.DropColumn(
                name: "RetryAfterTicks",
                table: "WebArticles");
        }
    }
}

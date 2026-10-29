using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ateliers.Ai.Assistant.Infrastructure.Conversations.Migrations
{
    /// <inheritdoc />
    public partial class WebMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebArticles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    FetchedTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    MarkdownPath = table.Column<string>(type: "TEXT", nullable: true),
                    Notified = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebArticles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebCollectionRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebCollectionRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebNotifications",
                columns: table => new
                {
                    ArticleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebNotifications", x => x.ArticleId);
                    table.ForeignKey(
                        name: "FK_WebNotifications_WebArticles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "WebArticles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebArticles_Key",
                table: "WebArticles",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebArticles_Source_Notified",
                table: "WebArticles",
                columns: new[] { "Source", "Notified" });

            migrationBuilder.CreateIndex(
                name: "IX_WebCollectionRuns_StartedTicks",
                table: "WebCollectionRuns",
                column: "StartedTicks");

            migrationBuilder.CreateIndex(
                name: "IX_WebNotifications_CreatedTicks",
                table: "WebNotifications",
                column: "CreatedTicks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebCollectionRuns");

            migrationBuilder.DropTable(
                name: "WebNotifications");

            migrationBuilder.DropTable(
                name: "WebArticles");
        }
    }
}

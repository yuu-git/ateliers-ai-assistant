using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ateliers.Ai.Assistant.Infrastructure.Conversations.Migrations
{
    /// <inheritdoc />
    public partial class NotificationReadingAndTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsBookmarked",
                table: "WebArticles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "ReadAtTicks",
                table: "WebArticles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WebTags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebTags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebArticleTags",
                columns: table => new
                {
                    ArticleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TagId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebArticleTags", x => new { x.ArticleId, x.TagId });
                    table.ForeignKey(
                        name: "FK_WebArticleTags_WebArticles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "WebArticles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WebArticleTags_WebTags_TagId",
                        column: x => x.TagId,
                        principalTable: "WebTags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebArticles_FetchedTicks",
                table: "WebArticles",
                column: "FetchedTicks");

            migrationBuilder.CreateIndex(
                name: "IX_WebArticles_IsBookmarked_FetchedTicks",
                table: "WebArticles",
                columns: new[] { "IsBookmarked", "FetchedTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_WebArticles_ReadAtTicks_FetchedTicks",
                table: "WebArticles",
                columns: new[] { "ReadAtTicks", "FetchedTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_WebArticles_Source_FetchedTicks",
                table: "WebArticles",
                columns: new[] { "Source", "FetchedTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_WebArticleTags_TagId_ArticleId",
                table: "WebArticleTags",
                columns: new[] { "TagId", "ArticleId" });

            migrationBuilder.CreateIndex(
                name: "IX_WebTags_NormalizedName",
                table: "WebTags",
                column: "NormalizedName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebArticleTags");

            migrationBuilder.DropTable(
                name: "WebTags");

            migrationBuilder.DropIndex(
                name: "IX_WebArticles_FetchedTicks",
                table: "WebArticles");

            migrationBuilder.DropIndex(
                name: "IX_WebArticles_IsBookmarked_FetchedTicks",
                table: "WebArticles");

            migrationBuilder.DropIndex(
                name: "IX_WebArticles_ReadAtTicks_FetchedTicks",
                table: "WebArticles");

            migrationBuilder.DropIndex(
                name: "IX_WebArticles_Source_FetchedTicks",
                table: "WebArticles");

            migrationBuilder.DropColumn(
                name: "IsBookmarked",
                table: "WebArticles");

            migrationBuilder.DropColumn(
                name: "ReadAtTicks",
                table: "WebArticles");
        }
    }
}

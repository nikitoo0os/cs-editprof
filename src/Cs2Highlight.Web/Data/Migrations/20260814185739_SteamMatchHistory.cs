using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cs2Highlight.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class SteamMatchHistory : Migration
    {
        private static readonly string[] MatchIndexColumns =
            ["SteamHistoryConnectionId", "ShareCode"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SteamHistoryConnections",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    SteamId64 = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ProtectedAuthenticationCode = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    CursorShareCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSyncedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SteamHistoryConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SteamHistoryConnections_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SteamHistoryMatches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SteamHistoryConnectionId = table.Column<long>(type: "INTEGER", nullable: false),
                    ShareCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MatchId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ReservationId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TvPort = table.Column<int>(type: "INTEGER", nullable: false),
                    PlayedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Score = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Availability = table.Column<string>(type: "TEXT", nullable: false),
                    AvailabilityErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastCheckedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SteamHistoryMatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SteamHistoryMatches_SteamHistoryConnections_SteamHistoryConnectionId",
                        column: x => x.SteamHistoryConnectionId,
                        principalTable: "SteamHistoryConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SteamHistoryConnections_UserId",
                table: "SteamHistoryConnections",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SteamHistoryMatches_SteamHistoryConnectionId_ShareCode",
                table: "SteamHistoryMatches",
                columns: MatchIndexColumns,
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SteamHistoryMatches");

            migrationBuilder.DropTable(
                name: "SteamHistoryConnections");
        }
    }
}

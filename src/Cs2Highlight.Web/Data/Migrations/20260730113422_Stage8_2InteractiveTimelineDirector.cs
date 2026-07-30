using System;
#pragma warning disable CA1707, CA1861
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cs2Highlight.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Stage8_2InteractiveTimelineDirector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GenerationTimelinePlans",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GenerationId = table.Column<long>(type: "INTEGER", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    ExcerptStartMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    ExcerptEndMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    RevisionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    RevisionCursor = table.Column<int>(type: "INTEGER", nullable: false),
                    DiagnosticsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ConcurrencyToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LockedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationTimelinePlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationTimelinePlans_Generations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GenerationTimelineAnchors",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimelinePlanId = table.Column<long>(type: "INTEGER", nullable: false),
                    AnchorId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MarkerType = table.Column<string>(type: "TEXT", nullable: false),
                    HighlightId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    TargetMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    IsLocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    FeasibilityStatus = table.Column<string>(type: "TEXT", nullable: false),
                    RequiredBaseSpeed = table.Column<double>(type: "REAL", nullable: false),
                    RequiredLocalSpeed = table.Column<double>(type: "REAL", nullable: false),
                    EstimatedPreRollSeconds = table.Column<double>(type: "REAL", nullable: false),
                    EstimatedPostRollSeconds = table.Column<double>(type: "REAL", nullable: false),
                    WarningsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationTimelineAnchors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationTimelineAnchors_GenerationTimelinePlans_TimelinePlanId",
                        column: x => x.TimelinePlanId,
                        principalTable: "GenerationTimelinePlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GenerationTimelineGaps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimelinePlanId = table.Column<long>(type: "INTEGER", nullable: false),
                    GapId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PreviousAnchorId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    NextAnchorId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    StartMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    EndMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    PlanJson = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationTimelineGaps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationTimelineGaps_GenerationTimelinePlans_TimelinePlanId",
                        column: x => x.TimelinePlanId,
                        principalTable: "GenerationTimelinePlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GenerationTimelineRevisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimelinePlanId = table.Column<long>(type: "INTEGER", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationTimelineRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationTimelineRevisions_GenerationTimelinePlans_TimelinePlanId",
                        column: x => x.TimelinePlanId,
                        principalTable: "GenerationTimelinePlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GenerationTimelineAnchors_TimelinePlanId_AnchorId",
                table: "GenerationTimelineAnchors",
                columns: new[] { "TimelinePlanId", "AnchorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenerationTimelineGaps_TimelinePlanId_GapId",
                table: "GenerationTimelineGaps",
                columns: new[] { "TimelinePlanId", "GapId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenerationTimelinePlans_GenerationId",
                table: "GenerationTimelinePlans",
                column: "GenerationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenerationTimelineRevisions_TimelinePlanId_Number",
                table: "GenerationTimelineRevisions",
                columns: new[] { "TimelinePlanId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GenerationTimelineAnchors");

            migrationBuilder.DropTable(
                name: "GenerationTimelineGaps");

            migrationBuilder.DropTable(
                name: "GenerationTimelineRevisions");

            migrationBuilder.DropTable(
                name: "GenerationTimelinePlans");
        }
    }
}

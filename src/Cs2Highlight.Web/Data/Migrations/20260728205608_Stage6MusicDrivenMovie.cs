using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cs2Highlight.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Stage6MusicDrivenMovie : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PrimaryKillTick",
                table: "GenerationHighlights",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RoundStartTick",
                table: "GenerationHighlights",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SafeEndTick",
                table: "GenerationHighlights",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "TickRate",
                table: "GenerationHighlights",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "GenerationEditSegments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GenerationId = table.Column<long>(type: "INTEGER", nullable: false),
                    GenerationHighlightId = table.Column<long>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    MusicalAnchorId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    OutputStartMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    PrimaryKillOutputMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    BaseSpeedFactor = table.Column<double>(type: "REAL", nullable: false),
                    TimeWarpPlanJson = table.Column<string>(type: "TEXT", nullable: false),
                    TransitionIn = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TransitionOut = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MatchScore = table.Column<double>(type: "REAL", nullable: false),
                    ScoreBreakdownJson = table.Column<string>(type: "TEXT", nullable: false),
                    WarningsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationEditSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationEditSegments_GenerationHighlights_GenerationHighlightId",
                        column: x => x.GenerationHighlightId,
                        principalTable: "GenerationHighlights",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GenerationEditSegments_Generations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GenerationMovieSettings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GenerationId = table.Column<long>(type: "INTEGER", nullable: false),
                    MovieStyle = table.Column<string>(type: "TEXT", nullable: false),
                    SyncIntensity = table.Column<string>(type: "TEXT", nullable: false),
                    ColorGradePreset = table.Column<string>(type: "TEXT", nullable: false),
                    MusicGainDb = table.Column<double>(type: "REAL", nullable: false),
                    GameplayGainDb = table.Column<double>(type: "REAL", nullable: false),
                    TransitionPreference = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MusicDurationPolicy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LockedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationMovieSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationMovieSettings_Generations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GenerationMusic",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GenerationId = table.Column<long>(type: "INTEGER", nullable: false),
                    OriginalFileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    StoredPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    SampleRate = table.Column<int>(type: "INTEGER", nullable: false),
                    Channels = table.Column<int>(type: "INTEGER", nullable: false),
                    TempoBpm = table.Column<double>(type: "REAL", nullable: true),
                    TempoConfidence = table.Column<double>(type: "REAL", nullable: true),
                    AnalyzerName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AnalyzerVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    AnalysisSchemaVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    AnalysisArtifactId = table.Column<long>(type: "INTEGER", nullable: true),
                    RightsConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    RightsConfirmedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationMusic", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationMusic_Generations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GenerationMusicAnchors",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GenerationId = table.Column<long>(type: "INTEGER", nullable: false),
                    AnchorId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    TimeMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    Strength = table.Column<double>(type: "REAL", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationMusicAnchors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationMusicAnchors_Generations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GenerationEditSegments_GenerationHighlightId",
                table: "GenerationEditSegments",
                column: "GenerationHighlightId");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationEditSegments_GenerationId_Sequence",
                table: "GenerationEditSegments",
                columns: ["GenerationId", "Sequence"],
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenerationMovieSettings_GenerationId",
                table: "GenerationMovieSettings",
                column: "GenerationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenerationMusic_GenerationId",
                table: "GenerationMusic",
                column: "GenerationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenerationMusicAnchors_GenerationId_AnchorId",
                table: "GenerationMusicAnchors",
                columns: ["GenerationId", "AnchorId"],
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GenerationEditSegments");

            migrationBuilder.DropTable(
                name: "GenerationMovieSettings");

            migrationBuilder.DropTable(
                name: "GenerationMusic");

            migrationBuilder.DropTable(
                name: "GenerationMusicAnchors");

            migrationBuilder.DropColumn(
                name: "PrimaryKillTick",
                table: "GenerationHighlights");

            migrationBuilder.DropColumn(
                name: "RoundStartTick",
                table: "GenerationHighlights");

            migrationBuilder.DropColumn(
                name: "SafeEndTick",
                table: "GenerationHighlights");

            migrationBuilder.DropColumn(
                name: "TickRate",
                table: "GenerationHighlights");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861 // EF Core generates constant column-name arrays for indexes.
#nullable disable

namespace Cs2Highlight.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Stage8CinematicDirector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutomaticCinematicCameras",
                table: "GenerationMovieSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CinematicDuration",
                table: "GenerationMovieSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CinematicEditIntensity",
                table: "GenerationMovieSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "GenerationBrollCandidates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GenerationId = table.Column<long>(type: "INTEGER", nullable: false),
                    GenerationDemoId = table.Column<long>(type: "INTEGER", nullable: false),
                    CandidateId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    RoundNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    StartTick = table.Column<long>(type: "INTEGER", nullable: false),
                    EndTick = table.Column<long>(type: "INTEGER", nullable: false),
                    MovementScore = table.Column<double>(type: "REAL", nullable: false),
                    CinematicScore = table.Column<double>(type: "REAL", nullable: false),
                    ActionDensity = table.Column<double>(type: "REAL", nullable: false),
                    TrajectoryJson = table.Column<string>(type: "TEXT", nullable: false),
                    Selected = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationBrollCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationBrollCandidates_GenerationDemos_GenerationDemoId",
                        column: x => x.GenerationDemoId,
                        principalTable: "GenerationDemos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GenerationBrollCandidates_Generations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GenerationCinematicPlans",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GenerationId = table.Column<long>(type: "INTEGER", nullable: false),
                    PlannerVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MusicExcerptJson = table.Column<string>(type: "TEXT", nullable: false),
                    PlanJson = table.Column<string>(type: "TEXT", nullable: false),
                    LockedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationCinematicPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationCinematicPlans_Generations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GenerationMusicSections",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GenerationId = table.Column<long>(type: "INTEGER", nullable: false),
                    SectionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    StartMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    EndMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    Energy = table.Column<double>(type: "REAL", nullable: false),
                    RhythmicDensity = table.Column<double>(type: "REAL", nullable: false),
                    BassEnergy = table.Column<double>(type: "REAL", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationMusicSections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationMusicSections_Generations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GenerationCameraShots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GenerationId = table.Column<long>(type: "INTEGER", nullable: false),
                    GenerationBrollCandidateId = table.Column<long>(type: "INTEGER", nullable: true),
                    ShotId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    StartTick = table.Column<long>(type: "INTEGER", nullable: false),
                    EndTick = table.Column<long>(type: "INTEGER", nullable: false),
                    KeyframesJson = table.Column<string>(type: "TEXT", nullable: false),
                    FovStart = table.Column<double>(type: "REAL", nullable: false),
                    FovEnd = table.Column<double>(type: "REAL", nullable: false),
                    PreviewStatus = table.Column<string>(type: "TEXT", nullable: false),
                    FallbackType = table.Column<string>(type: "TEXT", nullable: false),
                    PreviewAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    PreviewPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationCameraShots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationCameraShots_GenerationBrollCandidates_GenerationBrollCandidateId",
                        column: x => x.GenerationBrollCandidateId,
                        principalTable: "GenerationBrollCandidates",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_GenerationCameraShots_Generations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GenerationBrollCandidates_GenerationDemoId",
                table: "GenerationBrollCandidates",
                column: "GenerationDemoId");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationBrollCandidates_GenerationId_CandidateId",
                table: "GenerationBrollCandidates",
                columns: new[] { "GenerationId", "CandidateId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenerationCameraShots_GenerationBrollCandidateId",
                table: "GenerationCameraShots",
                column: "GenerationBrollCandidateId");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationCameraShots_GenerationId_ShotId",
                table: "GenerationCameraShots",
                columns: new[] { "GenerationId", "ShotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenerationCinematicPlans_GenerationId",
                table: "GenerationCinematicPlans",
                column: "GenerationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenerationMusicSections_GenerationId_SectionId",
                table: "GenerationMusicSections",
                columns: new[] { "GenerationId", "SectionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GenerationCameraShots");

            migrationBuilder.DropTable(
                name: "GenerationCinematicPlans");

            migrationBuilder.DropTable(
                name: "GenerationMusicSections");

            migrationBuilder.DropTable(
                name: "GenerationBrollCandidates");

            migrationBuilder.DropColumn(
                name: "AutomaticCinematicCameras",
                table: "GenerationMovieSettings");

            migrationBuilder.DropColumn(
                name: "CinematicDuration",
                table: "GenerationMovieSettings");

            migrationBuilder.DropColumn(
                name: "CinematicEditIntensity",
                table: "GenerationMovieSettings");
        }
    }
}

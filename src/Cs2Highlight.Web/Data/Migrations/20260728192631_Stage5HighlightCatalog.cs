using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cs2Highlight.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Stage5HighlightCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EffectPreset",
                table: "Generations",
                type: "TEXT",
                nullable: false,
                defaultValue: "Clean");

            migrationBuilder.AddColumn<long>(
                name: "EstimatedDurationMilliseconds",
                table: "Generations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<double>(
                name: "BeautyScore",
                table: "GenerationHighlights",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "CombatScore",
                table: "GenerationHighlights",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "GenerationHighlights",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<long>(
                name: "EstimatedDurationMilliseconds",
                table: "GenerationHighlights",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "KillsJson",
                table: "GenerationHighlights",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "MapName",
                table: "GenerationHighlights",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "Recommended",
                table: "GenerationHighlights",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ScoreBreakdownJson",
                table: "GenerationHighlights",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<bool>(
                name: "SelectedByUser",
                table: "GenerationHighlights",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SelectionOrder",
                table: "GenerationHighlights",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TagsJson",
                table: "GenerationHighlights",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<double>(
                name: "TotalScore",
                table: "GenerationHighlights",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "WeaponSequenceJson",
                table: "GenerationHighlights",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "GenerationEffectPlans",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GenerationId = table.Column<long>(type: "INTEGER", nullable: false),
                    GenerationHighlightId = table.Column<long>(type: "INTEGER", nullable: false),
                    Preset = table.Column<string>(type: "TEXT", nullable: false),
                    TimelineJson = table.Column<string>(type: "TEXT", nullable: false),
                    EffectPlanJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationEffectPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationEffectPlans_GenerationHighlights_GenerationHighlightId",
                        column: x => x.GenerationHighlightId,
                        principalTable: "GenerationHighlights",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GenerationEffectPlans_Generations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GenerationEffectPlans_GenerationHighlightId",
                table: "GenerationEffectPlans",
                column: "GenerationHighlightId");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationEffectPlans_GenerationId_GenerationHighlightId",
                table: "GenerationEffectPlans",
#pragma warning disable CA1861
                columns: new[] { "GenerationId", "GenerationHighlightId" },
#pragma warning restore CA1861
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GenerationEffectPlans");

            migrationBuilder.DropColumn(
                name: "EffectPreset",
                table: "Generations");

            migrationBuilder.DropColumn(
                name: "EstimatedDurationMilliseconds",
                table: "Generations");

            migrationBuilder.DropColumn(
                name: "BeautyScore",
                table: "GenerationHighlights");

            migrationBuilder.DropColumn(
                name: "CombatScore",
                table: "GenerationHighlights");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "GenerationHighlights");

            migrationBuilder.DropColumn(
                name: "EstimatedDurationMilliseconds",
                table: "GenerationHighlights");

            migrationBuilder.DropColumn(
                name: "KillsJson",
                table: "GenerationHighlights");

            migrationBuilder.DropColumn(
                name: "MapName",
                table: "GenerationHighlights");

            migrationBuilder.DropColumn(
                name: "Recommended",
                table: "GenerationHighlights");

            migrationBuilder.DropColumn(
                name: "ScoreBreakdownJson",
                table: "GenerationHighlights");

            migrationBuilder.DropColumn(
                name: "SelectedByUser",
                table: "GenerationHighlights");

            migrationBuilder.DropColumn(
                name: "SelectionOrder",
                table: "GenerationHighlights");

            migrationBuilder.DropColumn(
                name: "TagsJson",
                table: "GenerationHighlights");

            migrationBuilder.DropColumn(
                name: "TotalScore",
                table: "GenerationHighlights");

            migrationBuilder.DropColumn(
                name: "WeaponSequenceJson",
                table: "GenerationHighlights");
        }
    }
}

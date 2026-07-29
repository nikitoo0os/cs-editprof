using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cs2Highlight.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Stage7DynamicEffects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EffectIntensity",
                table: "GenerationMovieSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "Balanced");

            migrationBuilder.AddColumn<string>(
                name: "EffectPlannerVersion",
                table: "GenerationMovieSettings",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "7.0");

            migrationBuilder.AddColumn<int>(
                name: "EffectSeed",
                table: "GenerationMovieSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EnabledEffectGroupsJson",
                table: "GenerationMovieSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "DeterministicSeed",
                table: "GenerationEffectPlans",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DynamicEffectPlanJson",
                table: "GenerationEffectPlans",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockedAt",
                table: "GenerationEffectPlans",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlannerVersion",
                table: "GenerationEffectPlans",
                type: "TEXT",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EffectIntensity",
                table: "GenerationMovieSettings");

            migrationBuilder.DropColumn(
                name: "EffectPlannerVersion",
                table: "GenerationMovieSettings");

            migrationBuilder.DropColumn(
                name: "EffectSeed",
                table: "GenerationMovieSettings");

            migrationBuilder.DropColumn(
                name: "EnabledEffectGroupsJson",
                table: "GenerationMovieSettings");

            migrationBuilder.DropColumn(
                name: "DeterministicSeed",
                table: "GenerationEffectPlans");

            migrationBuilder.DropColumn(
                name: "DynamicEffectPlanJson",
                table: "GenerationEffectPlans");

            migrationBuilder.DropColumn(
                name: "LockedAt",
                table: "GenerationEffectPlans");

            migrationBuilder.DropColumn(
                name: "PlannerVersion",
                table: "GenerationEffectPlans");
        }
    }
}

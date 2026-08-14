using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cs2Highlight.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PerDemoPlayerAndHighlightSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HighlightSelectionCompleted",
                table: "GenerationDemos",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SelectedPlayerName",
                table: "GenerationDemos",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SelectedSteamId",
                table: "GenerationDemos",
                type: "TEXT",
                maxLength: 17,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE GenerationDemos
                SET SelectedSteamId = (
                        SELECT SelectedSteamId
                        FROM Generations
                        WHERE Generations.Id = GenerationDemos.GenerationId),
                    SelectedPlayerName = (
                        SELECT SelectedPlayerName
                        FROM Generations
                        WHERE Generations.Id = GenerationDemos.GenerationId),
                    HighlightSelectionCompleted = CASE
                        WHEN EXISTS (
                            SELECT 1
                            FROM GenerationHighlights
                            WHERE GenerationHighlights.GenerationDemoId = GenerationDemos.Id
                              AND GenerationHighlights.SelectedByUser = 1)
                        THEN 1 ELSE 0 END
                WHERE EXISTS (
                    SELECT 1
                    FROM Generations
                    WHERE Generations.Id = GenerationDemos.GenerationId
                      AND Generations.SelectedSteamId IS NOT NULL);

                UPDATE Generations
                SET Status = 'AwaitingPlayerSelection',
                    CurrentStage = 'AwaitingPlayerSelection'
                WHERE Status IN ('AwaitingMusicUpload', 'AwaitingMovieConfiguration')
                  AND EXISTS (
                      SELECT 1
                      FROM GenerationDemos
                      WHERE GenerationDemos.GenerationId = Generations.Id
                        AND GenerationDemos.AnalysisStatus = 'Succeeded'
                        AND GenerationDemos.HighlightSelectionCompleted = 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HighlightSelectionCompleted",
                table: "GenerationDemos");

            migrationBuilder.DropColumn(
                name: "SelectedPlayerName",
                table: "GenerationDemos");

            migrationBuilder.DropColumn(
                name: "SelectedSteamId",
                table: "GenerationDemos");
        }
    }
}

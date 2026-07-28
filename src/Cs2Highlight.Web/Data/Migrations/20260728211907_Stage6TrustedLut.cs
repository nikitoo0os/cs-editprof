using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cs2Highlight.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Stage6TrustedLut : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LutAssetKey",
                table: "GenerationMovieSettings",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LutAssetKey",
                table: "GenerationMovieSettings");
        }
    }
}

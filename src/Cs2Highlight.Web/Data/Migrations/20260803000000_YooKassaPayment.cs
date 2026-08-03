using Cs2Highlight.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cs2Highlight.Web.Data.Migrations;

[DbContext(typeof(GenerationDbContext))]
[Migration("20260803000000_YooKassaPayment")]
public sealed class YooKassaPayment : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ConfirmationUrl",
            table: "Payments",
            type: "TEXT",
            maxLength: 2048,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ConfirmationUrl",
            table: "Payments");
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861

#nullable disable

namespace Cs2Highlight.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialWeb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Generations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    SelectedSteamId = table.Column<string>(type: "TEXT", maxLength: 17, nullable: true),
                    SelectedPlayerName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    MaximumHighlights = table.Column<int>(type: "INTEGER", nullable: false),
                    MinimumScore = table.Column<double>(type: "REAL", nullable: false),
                    OutputOrder = table.Column<string>(type: "TEXT", nullable: false),
                    AspectRatio = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    Fps = table.Column<int>(type: "INTEGER", nullable: false),
                    TransitionType = table.Column<string>(type: "TEXT", nullable: false),
                    TransitionDurationMilliseconds = table.Column<int>(type: "INTEGER", nullable: false),
                    PriceAmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    PriceCurrency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    PaymentStatus = table.Column<string>(type: "TEXT", nullable: false),
                    PaymentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PaymentIdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ProgressPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentStage = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    GenerationStartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    GenerationCompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FinalVideoArtifactId = table.Column<long>(type: "INTEGER", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Generations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GenerationArtifacts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GenerationId = table.Column<long>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    StoredPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationArtifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationArtifacts_Generations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GenerationDemos",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GenerationId = table.Column<long>(type: "INTEGER", nullable: false),
                    OriginalFileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    StoredPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UploadOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    AnalysisStatus = table.Column<string>(type: "TEXT", nullable: false),
                    MapName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    TickRate = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationDemos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationDemos_Generations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GenerationEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GenerationId = table.Column<long>(type: "INTEGER", nullable: false),
                    Level = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ProgressPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationEvents_Generations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GenerationHighlights",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GenerationId = table.Column<long>(type: "INTEGER", nullable: false),
                    GenerationDemoId = table.Column<long>(type: "INTEGER", nullable: false),
                    HighlightId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SteamId = table.Column<string>(type: "TEXT", maxLength: 17, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Score = table.Column<double>(type: "REAL", nullable: false),
                    RoundNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    StartTick = table.Column<long>(type: "INTEGER", nullable: false),
                    EndTick = table.Column<long>(type: "INTEGER", nullable: false),
                    FirstKillTick = table.Column<long>(type: "INTEGER", nullable: false),
                    LastKillTick = table.Column<long>(type: "INTEGER", nullable: false),
                    KillCount = table.Column<int>(type: "INTEGER", nullable: false),
                    HeadshotCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SelectedForCompilation = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompilationOrder = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationHighlights", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationHighlights_Generations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GenerationPlayers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GenerationId = table.Column<long>(type: "INTEGER", nullable: false),
                    SteamId = table.Column<string>(type: "TEXT", maxLength: 17, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DemoCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalKills = table.Column<int>(type: "INTEGER", nullable: false),
                    CandidateCount = table.Column<int>(type: "INTEGER", nullable: false),
                    IsSelected = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationPlayers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationPlayers_Generations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GenerationId = table.Column<long>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProviderPaymentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SucceededAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payments_Generations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GenerationArtifacts_GenerationId",
                table: "GenerationArtifacts",
                column: "GenerationId");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationDemos_GenerationId_Sha256",
                table: "GenerationDemos",
                columns: new[] { "GenerationId", "Sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenerationEvents_GenerationId",
                table: "GenerationEvents",
                column: "GenerationId");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationHighlights_GenerationId_GenerationDemoId_HighlightId",
                table: "GenerationHighlights",
                columns: new[] { "GenerationId", "GenerationDemoId", "HighlightId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenerationPlayers_GenerationId_SteamId",
                table: "GenerationPlayers",
                columns: new[] { "GenerationId", "SteamId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Generations_PublicId",
                table: "Generations",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_GenerationId",
                table: "Payments",
                column: "GenerationId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_IdempotencyKey",
                table: "Payments",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ProviderPaymentId",
                table: "Payments",
                column: "ProviderPaymentId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GenerationArtifacts");

            migrationBuilder.DropTable(
                name: "GenerationDemos");

            migrationBuilder.DropTable(
                name: "GenerationEvents");

            migrationBuilder.DropTable(
                name: "GenerationHighlights");

            migrationBuilder.DropTable(
                name: "GenerationPlayers");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "Generations");
        }
    }
}

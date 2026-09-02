using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Alarms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Alarms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    RaisedAt = table.Column<string>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AssetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CabinetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AcknowledgedAt = table.Column<string>(type: "TEXT", nullable: true),
                    AcknowledgedBy = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alarms", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alarms_AssetId",
                table: "Alarms",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_Alarms_CabinetId",
                table: "Alarms",
                column: "CabinetId");

            migrationBuilder.CreateIndex(
                name: "IX_Alarms_Scope",
                table: "Alarms",
                column: "Scope",
                unique: true,
                filter: "[Status] = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_Alarms_Status_RaisedAt",
                table: "Alarms",
                columns: new[] { "Status", "RaisedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alarms");
        }
    }
}

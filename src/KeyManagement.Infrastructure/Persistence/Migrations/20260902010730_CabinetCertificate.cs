using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CabinetCertificate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CertificateThumbprint",
                table: "Cabinets",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cabinets_CertificateThumbprint",
                table: "Cabinets",
                column: "CertificateThumbprint",
                unique: true,
                filter: "[CertificateThumbprint] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Cabinets_CertificateThumbprint",
                table: "Cabinets");

            migrationBuilder.DropColumn(
                name: "CertificateThumbprint",
                table: "Cabinets");
        }
    }
}

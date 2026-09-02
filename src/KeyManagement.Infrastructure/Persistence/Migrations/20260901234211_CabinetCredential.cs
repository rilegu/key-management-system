using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CabinetCredential : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CredentialHash",
                table: "Cabinets",
                type: "TEXT",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CredentialHash",
                table: "Cabinets");
        }
    }
}

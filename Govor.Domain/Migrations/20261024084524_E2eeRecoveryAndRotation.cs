using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Govor.Domain.Migrations
{
    /// <inheritdoc />
    public partial class E2eeRecoveryAndRotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccountIdentityKey",
                table: "UserCryptoSessions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccountSignature",
                table: "UserCryptoSessions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Generation",
                table: "UserCryptoSessions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "PreviousFingerprint",
                table: "UserCryptoSessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "E2eeRecoveryVaults",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    IdentityKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Ciphertext = table.Column<string>(type: "character varying(4000000)", maxLength: 4000000, nullable: false),
                    Signature = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_E2eeRecoveryVaults", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_E2eeRecoveryVaults_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "E2eeRecoveryVaults");

            migrationBuilder.DropColumn(
                name: "AccountIdentityKey",
                table: "UserCryptoSessions");

            migrationBuilder.DropColumn(
                name: "AccountSignature",
                table: "UserCryptoSessions");

            migrationBuilder.DropColumn(
                name: "Generation",
                table: "UserCryptoSessions");

            migrationBuilder.DropColumn(
                name: "PreviousFingerprint",
                table: "UserCryptoSessions");
        }
    }
}

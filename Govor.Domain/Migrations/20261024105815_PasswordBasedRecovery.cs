using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Govor.Domain.Migrations
{
    /// <inheritdoc />
    public partial class PasswordBasedRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CredentialVersion",
                table: "Users",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "SrpIdentity",
                table: "Users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SrpSalt",
                table: "Users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SrpVerifier",
                table: "Users",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Epoch",
                table: "E2eeRecoveryVaults",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "PasswordCiphertext",
                table: "E2eeRecoveryVaults",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PasswordCredentialVersion",
                table: "E2eeRecoveryVaults",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "PasswordSignature",
                table: "E2eeRecoveryVaults",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "E2eeIdentityChanges",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Epoch = table.Column<long>(type: "bigint", nullable: false),
                    PreviousIdentityKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IdentityKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ChangedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_E2eeIdentityChanges", x => new { x.UserId, x.Epoch });
                    table.ForeignKey(
                        name: "FK_E2eeIdentityChanges_Users_UserId",
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
                name: "E2eeIdentityChanges");

            migrationBuilder.DropColumn(
                name: "CredentialVersion",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SrpIdentity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SrpSalt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SrpVerifier",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Epoch",
                table: "E2eeRecoveryVaults");

            migrationBuilder.DropColumn(
                name: "PasswordCiphertext",
                table: "E2eeRecoveryVaults");

            migrationBuilder.DropColumn(
                name: "PasswordCredentialVersion",
                table: "E2eeRecoveryVaults");

            migrationBuilder.DropColumn(
                name: "PasswordSignature",
                table: "E2eeRecoveryVaults");
        }
    }
}

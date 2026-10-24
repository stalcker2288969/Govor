using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Govor.Domain.Migrations
{
    /// <inheritdoc />
    public partial class PreserveResetSigners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreviousDeviceKeyJson",
                table: "E2eeIdentityChanges",
                type: "character varying(10000)",
                maxLength: 10000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PreviousSessionId",
                table: "E2eeIdentityChanges",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviousDeviceKeyJson",
                table: "E2eeIdentityChanges");

            migrationBuilder.DropColumn(
                name: "PreviousSessionId",
                table: "E2eeIdentityChanges");
        }
    }
}

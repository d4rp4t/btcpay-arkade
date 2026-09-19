using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropSwaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Swaps",
                schema: "BTCPayServer.Plugins.Ark");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Swaps",
                schema: "BTCPayServer.Plugins.Ark",
                columns: table => new
                {
                    SwapId = table.Column<string>(type: "text", nullable: false),
                    WalletId = table.Column<string>(type: "text", nullable: false),
                    ContractScript = table.Column<string>(type: "text", nullable: false),
                    Address = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpectedAmount = table.Column<long>(type: "bigint", nullable: false),
                    FailReason = table.Column<string>(type: "text", nullable: true),
                    Hash = table.Column<string>(type: "text", nullable: false),
                    Invoice = table.Column<string>(type: "text", nullable: false),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SwapType = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Swaps", x => new { x.SwapId, x.WalletId });
                    table.ForeignKey(
                        name: "FK_Swaps_WalletContracts_ContractScript_WalletId",
                        columns: x => new { x.ContractScript, x.WalletId },
                        principalSchema: "BTCPayServer.Plugins.Ark",
                        principalTable: "WalletContracts",
                        principalColumns: new[] { "Script", "WalletId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Swaps_Wallets_WalletId",
                        column: x => x.WalletId,
                        principalSchema: "BTCPayServer.Plugins.Ark",
                        principalTable: "Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Swaps_ContractScript_WalletId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Swaps",
                columns: new[] { "ContractScript", "WalletId" });

            migrationBuilder.CreateIndex(
                name: "IX_Swaps_WalletId",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Swaps",
                column: "WalletId");
        }
    }
}

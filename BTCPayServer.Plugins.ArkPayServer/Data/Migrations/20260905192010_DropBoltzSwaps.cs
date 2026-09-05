using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropBoltzSwaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing intent rows are deleted rather than migrated, deliberately.
            //
            // The column changes below are not additive: `Status` and `Type` move from integer to
            // the enum's NAME, and Postgres converts an existing integer through I/O rather than
            // through the enum, so a stored 1 becomes the string "1" and not "Pending". `OfferHex`
            // is renamed to `Metadata`, whose new content is a JSON bag — the old raw hex is not
            // valid in it. Left in place, every pre-migration row would parse-fail on read: not a
            // clean failure at upgrade time, but an exception the first time someone opened the
            // page.
            //
            // These rows are in-flight swap bookkeeping, not funds. The money sits in covenants the
            // sweeper finds by contract, which this does not touch — so dropping the rows costs
            // history, not sats.
            migrationBuilder.Sql(
                @"DELETE FROM ""BTCPayServer.Plugins.Ark"".""ArkadeSwapIntents"";");

            migrationBuilder.DropTable(
                name: "Swaps",
                schema: "BTCPayServer.Plugins.Ark");

            migrationBuilder.DropColumn(
                name: "Invoice",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents");

            migrationBuilder.DropColumn(
                name: "MakerDescriptor",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents");

            migrationBuilder.DropColumn(
                name: "Preimage",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents");

            migrationBuilder.RenameColumn(
                name: "OfferHex",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents",
                newName: "Metadata");

            migrationBuilder.AlterColumn<string>(
                name: "Type",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Metadata",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents",
                newName: "OfferHex");

            migrationBuilder.AlterColumn<int>(
                name: "Type",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents",
                type: "integer",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<int>(
                name: "Status",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents",
                type: "integer",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AddColumn<string>(
                name: "Invoice",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MakerDescriptor",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Preimage",
                schema: "BTCPayServer.Plugins.Ark",
                table: "ArkadeSwapIntents",
                type: "text",
                nullable: true);

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

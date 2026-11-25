using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Data.Migrations
{
    /// <inheritdoc />
    public partial class PaymentsMoveToFinancials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The payment tables (checkout_sessions, subscriptions, payment_receipts, processed_payment_events,
            // payment_settings) are Financials.API's now and no longer in this model. They are left in place, not
            // dropped: Financials copies their rows into its own database the first time it starts (LegacyImport),
            // and a deployment drops them by hand, or in a later migration, once it has.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}

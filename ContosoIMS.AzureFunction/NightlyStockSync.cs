using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Azure.Functions.Worker.Extensions.Timer;

namespace ContosoIMS.AzureFunction
{
    public class NightlySyncFunction
    {
        private readonly ILogger<NightlySyncFunction> _logger;

        public NightlySyncFunction(ILogger<NightlySyncFunction> logger)
        {
            _logger = logger;
        }

        [Function("NightlyStockSync")]
        public async Task Run([TimerTrigger("0 0 2 * * *")] TimerInfo timerInfo)
        {
            _logger.LogInformation("NightlyStockSync started at {Time}", DateTime.UtcNow);

            string connectionString =
                $"AuthType=ClientSecret;" +
                $"Url={Environment.GetEnvironmentVariable("DataverseUrl")};" +
                $"ClientId={Environment.GetEnvironmentVariable("ClientId")};" +
                $"ClientSecret={Environment.GetEnvironmentVariable("ClientSecret")};" +
                $"TenantId={Environment.GetEnvironmentVariable("TenantId")};";

            using var svc = new ServiceClient(connectionString);

            if (!svc.IsReady)
            {
                _logger.LogError("Dataverse connection failed: {Error}", svc.LastError);
                return;
            }

            // ─── Step 1: Fetch all active products ───────────────────────────
            var productQuery = new QueryExpression("product")
            {
                ColumnSet = new ColumnSet(
                    "productid",
                    "name",
                    "productnumber",
                    "cim_currentstock",
                    "cim_reorderthreeshold"
                )
            };
            productQuery.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);

            var products = (await svc.RetrieveMultipleAsync(productQuery)).Entities;
            _logger.LogInformation("Found {Count} active products.", products.Count);

            var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);

            foreach (var product in products)
            {
                Guid productId = product.Id;
                int stockNow = product.GetAttributeValue<int>("cim_currentstock");
                int reorderPoint = product.GetAttributeValue<int>("cim_reorderthreeshold");
                string sku = product.GetAttributeValue<string>("productnumber");

                _logger.LogInformation(
                    "Processing SKU={Sku} | Stock={Stock} | ReorderPoint={Reorder}",
                    sku, stockNow, reorderPoint
                );

                // ─── Step 2: Fetch all transactions for this product ──────────
                var txnQuery = new QueryExpression("cim_stocktransaction")
                {
                    ColumnSet = new ColumnSet(
                        "cim_transactiontype",
                        "cim_quantity",
                        "cim_transactiondate",
                        "cim_stockbefore",
                        "cim_stockafter"
                    )
                };
                txnQuery.Criteria.AddCondition(
                    "cim_product", ConditionOperator.Equal, productId
                );
                txnQuery.AddOrder("cim_transactiondate", OrderType.Ascending);

                var transactions = (await svc.RetrieveMultipleAsync(txnQuery)).Entities;

                // ─── Step 3: Calculate inbound / outbound totals ──────────────
                int totalInbound = transactions
                    .Where(t => t.GetAttributeValue<OptionSetValue>("cim_transactiontype")
                                 ?.Value == 767270000)  // Inbound
                    .Sum(t => t.GetAttributeValue<int>("cim_quantity"));

                int totalOutbound = transactions
                    .Where(t => t.GetAttributeValue<OptionSetValue>("cim_transactiontype")
                                 ?.Value == 767270001)  // Outbound
                    .Sum(t => t.GetAttributeValue<int>("cim_quantity"));

                // ─── Detect discrepancy: compare last transaction's Stock After with current stock
                bool discrepancy = false;
                int discrepancyAmt = 0;

                if (transactions.Count > 0)
                {
                    var lastTransaction = transactions.Last();
                    int lastStockAfter = lastTransaction.GetAttributeValue<int>("cim_stockafter");

                    // Discrepancy only if the latest Stock After doesn't match current stock
                    discrepancy = lastStockAfter != stockNow;
                    discrepancyAmt = discrepancy ? Math.Abs(lastStockAfter - stockNow) : 0;
                }

                // ─── Step 4: Movement ranking (last 30 days outbound) ─────────
                int recentOutbound = transactions.Count(t =>
                    t.GetAttributeValue<OptionSetValue>("cim_transactiontype")
                     ?.Value == 767270001 &&
                    t.GetAttributeValue<DateTime>("cim_transactiondate") >= thirtyDaysAgo
                );

                // ✅ Movement Tag choice values
                int movementTagValue = recentOutbound >= 10 ? 767270000   // Fast Moving
                                     : recentOutbound >= 3 ? 767270001   // Normal
                                     : 767270002;  // Slow Moving

                // ✅ Consistency Flag choice values
                int consistencyFlag = discrepancy
                    ? 767270001   // Discrepancy Found
                    : 767270000;  // Ok

                _logger.LogInformation(
                    "SKU={Sku} | Inbound={In} | Outbound={Out} | " +
                    "Current={Now} | " +
                    "Discrepancy={Disc} | Movement={Move}",
                    sku, totalInbound, totalOutbound,
                    stockNow,
                    discrepancy, movementTagValue
                );

                // ─── Step 5: Write Inventory Snapshot ────────────────────────
                var snapshot = new Entity("cim_inventorysnapshot");
                snapshot["cim_product"] = new EntityReference("product", productId);
                snapshot["cim_snapshotdate"] = DateTime.UtcNow.Date;          // Date only
                snapshot["cim_stocklevel"] = stockNow;                      // Whole number
                snapshot["cim_reorderthreesholdsnapshot"] = reorderPoint;          // Whole number
                snapshot["cim_inboundcount"] = totalInbound;                  // Whole number
                snapshot["cim_outboundcount"] = totalOutbound;                 // Whole number
                snapshot["cim_totalunitsmoved"] = totalInbound + totalOutbound;  // Whole number
                snapshot["cim_discrepancyamount"] = discrepancyAmt;                // Whole number
                snapshot["cim_consistencyflag"] = new OptionSetValue(consistencyFlag);  // ✅ Choice
                snapshot["cim_movementtag"] = new OptionSetValue(movementTagValue); // ✅ Choice

                await svc.CreateAsync(snapshot);
                _logger.LogInformation("Snapshot created for SKU={Sku}", sku);

                // ─── Step 6: Log discrepancy if found ───────────────────────────
                if (discrepancy)
                {
                    _logger.LogWarning(
                        "Discrepancy on {Sku}: Current={Now}, Diff={Diff}",
                        sku, stockNow, discrepancyAmt
                    );
                    // Note: Product table has no cim_discrepancyflagged / cim_lastdiscrepancydate columns.
                    // Discrepancy is already captured on the snapshot (cim_consistencyflag, cim_discrepancyamount).
                }
            }

            _logger.LogInformation(
                "NightlyStockSync completed. {Count} snapshots written.",
                products.Count
            );
        }
    }
}
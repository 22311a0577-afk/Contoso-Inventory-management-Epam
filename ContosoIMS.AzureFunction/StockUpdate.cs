using System.Net;
using System.Text.Json;
using ContosoIMS.AzureFunction.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace ContosoIMS.AzureFunction
{
    public class StockUpdateFunction
    {
        private readonly ILogger<StockUpdateFunction> _logger;

        public StockUpdateFunction(ILogger<StockUpdateFunction> logger)
        {
            _logger = logger;
        }

        [Function("StockUpdate")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post",
                Route = "stock/update")] HttpRequestData req)
        {
            _logger.LogInformation("StockUpdate triggered.");

            var (input, validationError) = await ParseAndValidateRequest(req);
            if (validationError != null) return validationError;

            var serviceClient = BuildServiceClient();
            if (serviceClient == null || !serviceClient.IsReady)
                return await BuildResponse(req, HttpStatusCode.InternalServerError,
                    new StockUpdateResponse { Success = false, Message = "Dataverse connection failed." });

            try
            {
                return await ProcessStockUpdate(req, serviceClient, input!);
            }
            catch (Exception ex)
            {
                _logger.LogError("Unhandled error: {Message}", ex.Message);
                return await BuildResponse(req, HttpStatusCode.InternalServerError,
                    new StockUpdateResponse { Success = false, Message = "Internal error: " + ex.Message });
            }
        }

        // ─── Parse & Validate ─────────────────────────────────────────────
        private async Task<(StockUpdateRequest?, HttpResponseData?)> ParseAndValidateRequest(
            HttpRequestData req)
        {
            string body = await new StreamReader(req.Body).ReadToEndAsync();
            _logger.LogInformation("Request body: {Body}", body);

            StockUpdateRequest? input;
            try
            {
                input = JsonSerializer.Deserialize<StockUpdateRequest>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                _logger.LogError("Deserialize failed: {Message}", ex.Message);
                return (null, await BuildResponse(req, HttpStatusCode.BadRequest,
                    new StockUpdateResponse { Success = false, Message = "Invalid request body." }));
            }

            if (input == null || string.IsNullOrWhiteSpace(input.Sku) || input.Quantity <= 0)
                return (null, await BuildResponse(req, HttpStatusCode.BadRequest,
                    new StockUpdateResponse { Success = false, Message = "SKU and Quantity are required." }));

            if (input.TransactionType != "Inbound" && input.TransactionType != "Outbound")
                return (null, await BuildResponse(req, HttpStatusCode.BadRequest,
                    new StockUpdateResponse
                    {
                        Success = false,
                        Message = $"TransactionType must be Inbound or Outbound. Got: '{input.TransactionType}'"
                    }));

            return (input, null);
        }

        // ─── Build ServiceClient ──────────────────────────────────────────
        private ServiceClient? BuildServiceClient()
        {
            string connectionString =
                $"AuthType=ClientSecret;" +
                $"Url={Environment.GetEnvironmentVariable("DataverseUrl")};" +
                $"ClientId={Environment.GetEnvironmentVariable("ClientId")};" +
                $"ClientSecret={Environment.GetEnvironmentVariable("ClientSecret")};" +
                $"TenantId={Environment.GetEnvironmentVariable("TenantId")};";
            try
            {
                return new ServiceClient(connectionString);
            }
            catch (Exception ex)
            {
                _logger.LogError("Dataverse init failed: {Message}", ex.Message);
                return null;
            }
        }

        // ─── Main Processing ──────────────────────────────────────────────
        private async Task<HttpResponseData> ProcessStockUpdate(
            HttpRequestData req,
            ServiceClient svc,
            StockUpdateRequest input)
        {
            var product = await GetProduct(svc, input.Sku);
            if (product == null)
                return await BuildResponse(req, HttpStatusCode.NotFound,
                    new StockUpdateResponse
                    {
                        Success = false,
                        Message = $"Product '{input.Sku}' not found."
                    });

            int currentStock    = product.GetAttributeValue<int>("cim_currentstock");
            int reorderThreshold = product.GetAttributeValue<int>("cim_reorderthreeshold");

            // ✅ FIXED — allow taking out ALL stock (>= instead of >)
            if (input.TransactionType == "Outbound" && input.Quantity > currentStock)
                return await BuildResponse(req, HttpStatusCode.BadRequest,
                    new StockUpdateResponse
                    {
                        Success = false,
                        Message = $"Insufficient stock. Current: {currentStock}, Requested: {input.Quantity}"
                    });

            int newStock        = CalculateNewStock(currentStock, input);
            int stockStatus     = DetermineStockStatus(newStock, reorderThreshold);
            bool alertTriggered = newStock < reorderThreshold; // ✅ strict less than

            await UpdateProduct(svc, product.Id, newStock, stockStatus, alertTriggered, input.TransactionType);

            Guid txnId = await CreateTransaction(svc, product.Id, input, currentStock, newStock, alertTriggered);

            _logger.LogInformation("Stock updated. New level: {Stock}", newStock);

            return await BuildResponse(req, HttpStatusCode.OK,
                new StockUpdateResponse
                {
                    Success        = true,
                    NewStockLevel  = newStock,
                    AlertTriggered = alertTriggered,
                    TransactionId  = txnId.ToString(),
                    Message        = $"Stock {input.TransactionType.ToLower()} processed. New level: {newStock}"
                });
        }

        // ─── Get Product ──────────────────────────────────────────────────
        private async Task<Entity?> GetProduct(ServiceClient svc, string sku)
        {
            var query = new QueryExpression("product")
            {
                ColumnSet = new ColumnSet(
                    "productid", "name", "productnumber",
                    "cim_currentstock", "cim_reorderthreeshold", "cim_stockstatus"
                )
            };
            query.Criteria.AddCondition("productnumber", ConditionOperator.Equal, sku);

            var results = await svc.RetrieveMultipleAsync(query);
            return results.Entities.Count > 0 ? results.Entities[0] : null;
        }

        // ─── Calculate New Stock ──────────────────────────────────────────
        private static int CalculateNewStock(int currentStock, StockUpdateRequest input)
        {
            return input.TransactionType == "Inbound"
                ? currentStock + input.Quantity
                : currentStock - input.Quantity;  // ✅ allows full depletion
        }

        // ─── Determine Stock Status ───────────────────────────────────────
        private static int DetermineStockStatus(int newStock, int reorderThreshold)
        {
            if (newStock == 0)          return 767270002; // Out of Stock
            if (newStock <= reorderThreshold) return 767270001; // Critical
            return 767270000;                             // Active
        }

        // ─── Update Product ───────────────────────────────────────────────
        private async Task UpdateProduct(
            ServiceClient svc, Guid productId,
            int newStock, int stockStatus,
            bool alertTriggered, string transactionType)
        {
            var update = new Entity("product", productId);
            update["cim_currentstock"] = newStock;
            update["cim_stockstatus"]  = new OptionSetValue(stockStatus);
            update["cim_lowstock"]     = alertTriggered;

            if (transactionType == "Inbound")
                update["cim_lastrestockeddate"] = DateTime.UtcNow;

            await svc.UpdateAsync(update);
        }

        // ─── Create Transaction ───────────────────────────────────────────
        private async Task<Guid> CreateTransaction(
            ServiceClient svc, Guid productId,
            StockUpdateRequest input,
            int stockBefore, int stockAfter,
            bool alertTriggered)
        {
            var txn = new Entity("cim_stocktransaction");
            txn["cim_transactionid"]   = $"TXN-{DateTime.UtcNow:yyyyMMdd-HHmmssff}";
            txn["cim_product"]         = new EntityReference("product", productId);
            txn["cim_transactiontype"] = new OptionSetValue(
                input.TransactionType == "Inbound" ? 767270000 : 767270001
            );
            txn["cim_quantity"]        = input.Quantity;
            txn["cim_stockbefore"]     = stockBefore;
            txn["cim_stockafter"]      = stockAfter;
            txn["cim_source"]          = new OptionSetValue(GetSourceOption(input.Source));
            txn["cim_notes"]           = input.Notes ?? "";
            txn["cim_transactiondate"] = DateTime.UtcNow;
            txn["cim_alerttriggered"]  = alertTriggered;

            return await svc.CreateAsync(txn);
        }

        // ─── Build Response ───────────────────────────────────────────────
        private static async Task<HttpResponseData> BuildResponse(
            HttpRequestData req,
            HttpStatusCode status,
            StockUpdateResponse payload)
        {
            var response = req.CreateResponse(status);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(payload,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return response;
        }

        // ─── Get Source Option ────────────────────────────────────────────
        private static int GetSourceOption(string source)
        {
            return (source ?? "").Trim() switch
            {
                "Vendor Delivery"   => 767270000,
                "Sales Order"       => 767270001,
                "Customer Return"   => 767270002,
                "Internal Transfer" => 767270003,
                "Write-off"         => 767270004,
                "Manual Adjustment" => 767270005,
                _                   => 767270005
            };
        }
    }
}
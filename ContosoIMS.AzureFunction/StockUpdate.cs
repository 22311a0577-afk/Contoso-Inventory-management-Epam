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
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "stock/update")] HttpRequestData req)
        {
            _logger.LogInformation("StockUpdate function triggered.");

            // --- Read and validate request body ---
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            StockUpdateRequest? input;

            try
            {
                input = JsonSerializer.Deserialize<StockUpdateRequest>(requestBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to deserialize request: {Message}", ex.Message);
                return await BuildResponse(req, HttpStatusCode.BadRequest,
                    new StockUpdateResponse { Success = false, Message = "Invalid request body." });
            }

            if (input == null || string.IsNullOrWhiteSpace(input.Sku) || input.Quantity <= 0)
            {
                return await BuildResponse(req, HttpStatusCode.BadRequest,
                    new StockUpdateResponse { Success = false, Message = "SKU and Quantity are required." });
            }

            if (input.TransactionType != "Inbound" && input.TransactionType != "Outbound")
            {
                return await BuildResponse(req, HttpStatusCode.BadRequest,
                    new StockUpdateResponse { Success = false, Message = "TransactionType must be 'Inbound' or 'Outbound'." });
            }

            // --- Build Dataverse ServiceClient ---
            string dataverseUrl = Environment.GetEnvironmentVariable("DataverseUrl") ?? "";
            string clientId     = Environment.GetEnvironmentVariable("ClientId") ?? "";
            string clientSecret = Environment.GetEnvironmentVariable("ClientSecret") ?? "";
            string tenantId     = Environment.GetEnvironmentVariable("TenantId") ?? "";

            string connectionString =
                $"AuthType=ClientSecret;" +
                $"Url={dataverseUrl};" +
                $"ClientId={clientId};" +
                $"ClientSecret={clientSecret};" +
                $"TenantId={tenantId};";

           // using var serviceClient = new ServiceClient(connectionString);
             ServiceClient? serviceClient = null;

try
{
    serviceClient = new ServiceClient(connectionString);
}
catch (Exception ex)
{
    _logger.LogError($"Dataverse init failed: {ex.Message}");
}

if (serviceClient == null || !serviceClient.IsReady)
{
    return await BuildResponse(req, HttpStatusCode.InternalServerError,
        new StockUpdateResponse
        {
            Success = false,
            Message = "Dataverse connection failed"
        });
}
            if (!serviceClient.IsReady)
            {
                _logger.LogError("Dataverse connection failed: {Error}", serviceClient.LastError);
                return await BuildResponse(req, HttpStatusCode.InternalServerError,
                    new StockUpdateResponse { Success = false, Message = "Dataverse connection failed." });
            }

            try
            {
                // --- Lookup Product by SKU ---
var productQuery = new QueryExpression("product")
{
    ColumnSet = new ColumnSet(
        "productid",
        "name",
        "productnumber",
        "cim_currentstock",
        "cim_reorderthreeshold",   
        "cim_stockstatus"
    )
};

productQuery.Criteria.AddCondition("productnumber", ConditionOperator.Equal, input.Sku);

var productResults = await serviceClient.RetrieveMultipleAsync(productQuery);

if (productResults.Entities.Count == 0)
{
    return await BuildResponse(req, HttpStatusCode.NotFound,
        new StockUpdateResponse
        {
            Success = false,
            Message = $"Product with SKU '{input.Sku}' not found."
        });
}

Entity product = productResults.Entities[0];
Guid productId = product.Id;

// --- Get current stock and reorder threshold ---
int currentStock = product.Contains("cim_currentstock")
    ? product.GetAttributeValue<int>("cim_currentstock")
    : 0;

int reorderThreshold = product.Contains("cim_reorderthreeshold")
    ? product.GetAttributeValue<int>("cim_reorderthreeshold")
    : 0;

// --- Calculate new stock ---
int newStock;

if (input.TransactionType == "Inbound")
{
    newStock = currentStock + input.Quantity;
}
else
{
    if (input.Quantity > currentStock)
    {
        return await BuildResponse(req, HttpStatusCode.BadRequest,
            new StockUpdateResponse
            {
                Success = false,
                Message = $"Insufficient stock. Current: {currentStock}, Requested: {input.Quantity}"
            });
    }

    newStock = currentStock - input.Quantity;
}

// --- Stock Status Logic ---
int stockStatus;

if (newStock == 0)
    stockStatus = 767270002; // Out of Stock
else if (newStock <= reorderThreshold)
    stockStatus = 767270001; // Critical
else
    stockStatus = 767270000; // Active

bool alertTriggered = newStock <= reorderThreshold;

// --- Update Product ---
var productUpdate = new Entity("product", productId);
productUpdate["cim_currentstock"] = newStock;
productUpdate["cim_stockstatus"] = new OptionSetValue(stockStatus);

if (input.TransactionType == "Inbound")
{
    productUpdate["cim_lastrestockeddate"] = DateTime.UtcNow;
}

await serviceClient.UpdateAsync(productUpdate);

// --- Create Transaction ---
var transaction = new Entity("cim_stocktransaction");
transaction["cim_transactionid"] = $"TXN-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
transaction["cim_product"] = new EntityReference("product", productId);
transaction["cim_transactiontype"] = new OptionSetValue(
    input.TransactionType == "Inbound" ? 767270000 : 767270001
);
transaction["cim_quantity"] = input.Quantity;
transaction["cim_stockbefore"] = currentStock;
transaction["cim_stockafter"] = newStock;
transaction["cim_source"] = new OptionSetValue(GetSourceOption(input.Source));
transaction["cim_notes"] = input.Notes;
//transaction["cim_requestedby"] = input.RequestedBy;
transaction["cim_transactiondate"] = DateTime.UtcNow;
transaction["cim_alerttriggered"] = alertTriggered;

Guid transactionId = await serviceClient.CreateAsync(transaction);

// --- Response ---
return await BuildResponse(req, HttpStatusCode.OK,
    new StockUpdateResponse
    {
        Success = true,
        NewStockLevel = newStock,
        AlertTriggered = alertTriggered,
        TransactionId = transactionId.ToString(),
        Message = $"Stock {input.TransactionType.ToLower()} processed. New level: {newStock}"
    });
            }
            catch (Exception ex)
            {
                _logger.LogError("Unhandled error: {Message}", ex.Message);
                return await BuildResponse(req, HttpStatusCode.InternalServerError,
                    new StockUpdateResponse { Success = false, Message = "An internal error occurred." });
            }
        }

        private static async Task<HttpResponseData> BuildResponse(
            HttpRequestData req, HttpStatusCode status, StockUpdateResponse payload)
        {
            var response = req.CreateResponse(status);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(payload,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return response;
        }
        private int GetSourceOption(string source)
{
     return source switch
    {
        "Vendor Delivery" => 767270000,
        "Sales Order" => 767270001,
        "Customer Return" => 767270002,
        "Internal Transfer" => 767270003,
        "Write-off" => 767270004,
        "Manual Adjustment" => 767270005,
        _ => throw new Exception($"Invalid source value: {source}")
    };
}
    }
}
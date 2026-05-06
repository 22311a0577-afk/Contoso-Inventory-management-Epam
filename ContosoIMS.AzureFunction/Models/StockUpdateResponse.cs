
namespace ContosoIMS.AzureFunction.Models
{
public class StockUpdateResponse
{
    public bool Success { get; set; }
    public int NewStockLevel { get; set; }
    public bool AlertTriggered { get; set; }
    public string TransactionId { get; set; }
    public string Message { get; set; }
}
}
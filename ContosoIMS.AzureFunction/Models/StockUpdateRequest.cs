


namespace ContosoIMS.AzureFunction.Models
{
    public class StockUpdateRequest
{
    public string Sku { get; set; }
    public string TransactionType { get; set; }
    public int Quantity { get; set; }
    public string Source { get; set; }
    public string Notes { get; set; }
    public string RequestedBy { get; set; }
}
}

namespace Erner.Models;

public class TradeResult
{
    public int OrderId { get; set; }
    public string Status { get; set; } = "";
    public double Strike { get; set; }
    public string Right { get; set; } = ""; // "P" or "C"
    public int Quantity { get; set; }
    public double AvgFillPrice { get; set; }
    public string Expiry { get; set; } = "";
}

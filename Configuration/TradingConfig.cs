namespace Erner.Configuration;

public class TradingConfig
{
    public string TradeTimeLocal { get; set; } = "12:45:00";
    public string TradeTimeZone { get; set; } = "America/Los_Angeles";
    public double PutDeltaTarget { get; set; } = -0.15;
    public double CallDeltaTarget { get; set; } = 0.10;
    public int PutContracts { get; set; } = 1;
    public int CallContracts { get; set; } = 1;
    public string OrderType { get; set; } = "LMT"; // "LMT" or "MKT"
    public string Symbol { get; set; } = "SPX";
    public string TradingClass { get; set; } = "SPXW";
    public string Exchange { get; set; } = "SMART";
    public string Currency { get; set; } = "USD";
    public string Multiplier { get; set; } = "100";
    public double StrikeRangePercent { get; set; } = 0.10;
    public bool ExitAfterTrade { get; set; } = true;
}

namespace Erner.Models;

public class StrikeDelta
{
    public double Strike { get; set; }
    public double Delta { get; set; }
    public string Right { get; set; } = ""; // "P" or "C"
    public double ImpliedVol { get; set; }
    public double OptionPrice { get; set; }
}

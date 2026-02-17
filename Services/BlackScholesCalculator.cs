namespace Erner.Services;

/// <summary>
/// Fallback Black-Scholes delta calculator for when IB model greeks are unavailable.
/// Uses European option pricing (appropriate for SPX options).
/// </summary>
public static class BlackScholesCalculator
{
    // Reasonable defaults for SPX; these are approximations for the fallback case
    private const double DefaultRiskFreeRate = 0.05;
    private const double DefaultDividendYield = 0.013;

    /// <summary>
    /// Compute call delta: e^(-qT) * N(d1)
    /// </summary>
    public static double CallDelta(double S, double K, double T, double sigma,
        double r = DefaultRiskFreeRate, double q = DefaultDividendYield)
    {
        if (T <= 0 || sigma <= 0) return 0;
        double d1 = D1(S, K, r, q, T, sigma);
        return Math.Exp(-q * T) * NormalCdf(d1);
    }

    /// <summary>
    /// Compute put delta: e^(-qT) * (N(d1) - 1)
    /// </summary>
    public static double PutDelta(double S, double K, double T, double sigma,
        double r = DefaultRiskFreeRate, double q = DefaultDividendYield)
    {
        if (T <= 0 || sigma <= 0) return 0;
        double d1 = D1(S, K, r, q, T, sigma);
        return Math.Exp(-q * T) * (NormalCdf(d1) - 1.0);
    }

    /// <summary>
    /// Find the strike from a list of candidates whose delta is closest to the target.
    /// </summary>
    public static double FindStrikeForDelta(double targetDelta, string right, double S, double T,
        double sigma, IEnumerable<double> candidateStrikes)
    {
        double bestStrike = 0;
        double bestDiff = double.MaxValue;

        foreach (double strike in candidateStrikes)
        {
            double delta = right == "P"
                ? PutDelta(S, strike, T, sigma)
                : CallDelta(S, strike, T, sigma);

            double diff = Math.Abs(delta - targetDelta);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestStrike = strike;
            }
        }

        return bestStrike;
    }

    private static double D1(double S, double K, double r, double q, double T, double sigma)
    {
        return (Math.Log(S / K) + (r - q + 0.5 * sigma * sigma) * T) / (sigma * Math.Sqrt(T));
    }

    /// <summary>
    /// Standard normal CDF using the Abramowitz and Stegun rational approximation.
    /// Maximum error ~1.5e-7.
    /// </summary>
    public static double NormalCdf(double x)
    {
        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;

        int sign = x < 0 ? -1 : 1;
        x = Math.Abs(x) / Math.Sqrt(2.0);

        double t = 1.0 / (1.0 + p * x);
        double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);

        return 0.5 * (1.0 + sign * y);
    }
}

using Erner.Configuration;
using Erner.Models;
using IBApi;
using Microsoft.Extensions.Logging;

namespace Erner.Services;

/// <summary>
/// Finds the target-delta strikes for a given expiry by querying IB for option chains
/// and market data (greeks), with a Black-Scholes fallback.
/// </summary>
public class OptionChainService
{
    // Stable IB conId for the SPX index
    private const int SpxConId = 416904;

    private readonly IbConnection _connection;
    private readonly TradingConfig _config;
    private readonly ILogger<OptionChainService> _logger;

    public OptionChainService(IbConnection connection, TradingConfig config, ILogger<OptionChainService> logger)
    {
        _connection = connection;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Find the put and call strikes closest to the configured delta targets for the given expiry.
    /// Returns the full StrikeDelta info for each side plus the mid-price.
    /// </summary>
    public async Task<(StrikeDelta put, StrikeDelta call, double putMid, double callMid)> FindTargetStrikesAsync(
        string expiry, double underlyingPrice)
    {
        _logger.LogInformation("Finding target strikes for expiry {Expiry} with underlying at {Price:F2}",
            expiry, underlyingPrice);

        // 1. Get available strikes from the option chain
        var chain = await _connection.RequestOptionChainAsync("SPX", "IND", SpxConId);

        // Find the entry matching our trading class (SPXW) and containing our expiry
        var matchingEntry = chain.Entries
            .Where(e => e.TradingClass.Equals(_config.TradingClass, StringComparison.OrdinalIgnoreCase))
            .Where(e => e.Expirations.Contains(expiry))
            .FirstOrDefault();

        if (matchingEntry == null)
        {
            // Try any entry with our expiry
            matchingEntry = chain.Entries
                .Where(e => e.Expirations.Contains(expiry))
                .FirstOrDefault();

            if (matchingEntry == null)
                throw new InvalidOperationException(
                    $"No option chain found for expiry {expiry} with trading class {_config.TradingClass}. " +
                    $"Available trading classes: {string.Join(", ", chain.Entries.Select(e => e.TradingClass))}. " +
                    $"Available expirations (first entry): {string.Join(", ", chain.Entries.FirstOrDefault()?.Expirations.OrderBy(x => x).Take(10) ?? Enumerable.Empty<string>())}...");
        }

        _logger.LogInformation("Using option chain: TradingClass={TradingClass}, Exchange={Exchange}, {StrikeCount} strikes",
            matchingEntry.TradingClass, matchingEntry.Exchange, matchingEntry.Strikes.Count);

        // 2. Filter strikes to within range of current price
        double lowerBound = underlyingPrice * (1 - _config.StrikeRangePercent);
        double upperBound = underlyingPrice * (1 + _config.StrikeRangePercent);

        var candidateStrikes = matchingEntry.Strikes
            .Where(s => s >= lowerBound && s <= upperBound)
            .OrderBy(s => s)
            .ToList();

        _logger.LogInformation("Filtered to {Count} candidate strikes in range [{Lower:F0}, {Upper:F0}]",
            candidateStrikes.Count, lowerBound, upperBound);

        if (candidateStrikes.Count == 0)
            throw new InvalidOperationException(
                $"No strikes found within {_config.StrikeRangePercent:P0} of {underlyingPrice:F2}");

        // 3. Separate into put candidates (below price) and call candidates (above price)
        var putStrikes = candidateStrikes.Where(s => s < underlyingPrice).ToList();
        var callStrikes = candidateStrikes.Where(s => s > underlyingPrice).ToList();

        _logger.LogInformation("Put candidates: {PutCount} strikes, Call candidates: {CallCount} strikes",
            putStrikes.Count, callStrikes.Count);

        // 4. Request market data for all candidates
        var putResult = await FindStrikeByDelta(putStrikes, expiry, "P", _config.PutDeltaTarget,
            underlyingPrice, matchingEntry.TradingClass);
        var callResult = await FindStrikeByDelta(callStrikes, expiry, "C", _config.CallDeltaTarget,
            underlyingPrice, matchingEntry.TradingClass);

        _logger.LogInformation("Selected PUT  strike: {Strike:F0} (delta={Delta:F4}, mid={Mid:F2})",
            putResult.strike.Strike, putResult.strike.Delta, putResult.mid);
        _logger.LogInformation("Selected CALL strike: {Strike:F0} (delta={Delta:F4}, mid={Mid:F2})",
            callResult.strike.Strike, callResult.strike.Delta, callResult.mid);

        return (putResult.strike, callResult.strike, putResult.mid, callResult.mid);
    }

    private async Task<(StrikeDelta strike, double mid)> FindStrikeByDelta(
        List<double> strikes, string expiry, string right, double targetDelta,
        double underlyingPrice, string tradingClass)
    {
        // Build contracts for all candidate strikes
        var contracts = strikes.Select((s, i) => (
            id: i,
            contract: BuildOptionContract(s, expiry, right, tradingClass)
        )).ToList();

        // Request market data in batch
        var mktData = await _connection.RequestMarketDataBatchAsync(contracts);

        // Find strikes with valid greeks from IB
        var strikesWithGreeks = new List<(double strike, double delta, double iv, double price, double mid)>();
        var strikesWithoutGreeks = new List<double>();

        foreach (var (id, data) in mktData)
        {
            double strike = strikes[id];
            if (data.Delta != 0) // Has greeks
            {
                strikesWithGreeks.Add((strike, data.Delta, data.ImpliedVol, data.OptionPrice, data.MidPrice));
            }
            else
            {
                strikesWithoutGreeks.Add(strike);
            }
        }

        _logger.LogDebug("{Right} strikes with IB greeks: {WithGreeks}, without: {Without}",
            right, strikesWithGreeks.Count, strikesWithoutGreeks.Count);

        StrikeDelta? bestStrike = null;
        double bestMid = 0;
        double bestDiff = double.MaxValue;

        // First try IB greeks
        foreach (var (strike, delta, iv, price, mid) in strikesWithGreeks)
        {
            double diff = Math.Abs(delta - targetDelta);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestStrike = new StrikeDelta
                {
                    Strike = strike,
                    Delta = delta,
                    Right = right,
                    ImpliedVol = iv,
                    OptionPrice = price
                };
                bestMid = mid;
            }
        }

        // If we found a good match from IB greeks, use it
        if (bestStrike != null && bestDiff < 0.05)
        {
            return (bestStrike, bestMid);
        }

        // Fallback: use Black-Scholes for strikes without greeks
        _logger.LogWarning("Using Black-Scholes fallback for {Right} delta calculation", right);

        // Estimate IV from whatever IB data we have, or use a default
        double estimatedIv = strikesWithGreeks.Any()
            ? strikesWithGreeks.Average(s => s.iv > 0 ? s.iv : 0.20)
            : 0.20;

        // Time to expiry in years (assume 1 trading day ~ 1/252)
        double T = 1.0 / 252.0;

        foreach (double strike in strikesWithoutGreeks)
        {
            double delta = right == "P"
                ? BlackScholesCalculator.PutDelta(underlyingPrice, strike, T, estimatedIv)
                : BlackScholesCalculator.CallDelta(underlyingPrice, strike, T, estimatedIv);

            double diff = Math.Abs(delta - targetDelta);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                // Get mid price from market data if available
                double mid = 0;
                int idx = strikes.IndexOf(strike);
                if (mktData.ContainsKey(idx))
                    mid = mktData[idx].MidPrice;

                bestStrike = new StrikeDelta
                {
                    Strike = strike,
                    Delta = delta,
                    Right = right,
                    ImpliedVol = estimatedIv,
                    OptionPrice = mid > 0 ? mid : 0
                };
                bestMid = mid;
            }
        }

        if (bestStrike == null)
            throw new InvalidOperationException($"Could not find a {right} strike near {targetDelta} delta");

        return (bestStrike, bestMid);
    }

    /// <summary>
    /// Build an IB Contract object for an SPX option.
    /// </summary>
    public Contract BuildOptionContract(double strike, string expiry, string right, string? tradingClass = null)
    {
        return new Contract
        {
            Symbol = _config.Symbol,
            SecType = "OPT",
            Exchange = _config.Exchange,
            Currency = _config.Currency,
            LastTradeDateOrContractMonth = expiry,
            Strike = strike,
            Right = right,
            Multiplier = _config.Multiplier,
            TradingClass = tradingClass ?? _config.TradingClass
        };
    }

    /// <summary>
    /// Build an IB Contract object for the SPX index (for getting the underlying price).
    /// </summary>
    public static Contract BuildIndexContract()
    {
        return new Contract
        {
            Symbol = "SPX",
            SecType = "IND",
            Exchange = "CBOE",
            Currency = "USD"
        };
    }
}

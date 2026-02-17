using Erner.Configuration;
using Microsoft.Extensions.Logging;

namespace Erner.Services;

/// <summary>
/// Top-level workflow coordinator. Handles timing, connects to IB,
/// finds target delta strikes, and places sell orders.
/// </summary>
public class TradeOrchestrator
{
    private readonly IbConnection _connection;
    private readonly OptionChainService _optionChainService;
    private readonly OrderService _orderService;
    private readonly ConnectionConfig _connectionConfig;
    private readonly TradingConfig _tradingConfig;
    private readonly ILogger<TradeOrchestrator> _logger;

    public TradeOrchestrator(
        IbConnection connection,
        OptionChainService optionChainService,
        OrderService orderService,
        ConnectionConfig connectionConfig,
        TradingConfig tradingConfig,
        ILogger<TradeOrchestrator> logger)
    {
        _connection = connection;
        _optionChainService = optionChainService;
        _orderService = orderService;
        _connectionConfig = connectionConfig;
        _tradingConfig = tradingConfig;
        _logger = logger;
    }

    public async Task ExecuteAsync(bool immediate, CancellationToken ct)
    {
        try
        {
            // 1. Wait for trade time (unless --now)
            if (!immediate)
            {
                await WaitForTradeTimeAsync(ct);
            }
            else
            {
                _logger.LogInformation("Immediate mode: skipping wait for trade time");
            }

            // 2. Connect to IB
            await _connection.ConnectAsync(
                _connectionConfig.Host,
                _connectionConfig.Port,
                _connectionConfig.ClientId);

            // 3. Determine next trading day
            var easternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var nowEastern = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, easternZone);
            var nextTradingDay = TradingDayCalculator.GetNextTradingDay(nowEastern.Date);
            string expiry = nextTradingDay.ToString("yyyyMMdd");

            _logger.LogInformation("Target expiry: {Expiry} (next trading day after {Today})",
                expiry, nowEastern.Date.ToString("yyyy-MM-dd"));

            // 4. Get underlying price
            _logger.LogInformation("Requesting SPX underlying price...");
            var indexContract = OptionChainService.BuildIndexContract();
            double underlyingPrice = await _connection.RequestUnderlyingPriceAsync(indexContract);
            _logger.LogInformation("SPX underlying price: {Price:F2}", underlyingPrice);

            // 5. Find target delta strikes
            var (putStrike, callStrike, putMid, callMid) =
                await _optionChainService.FindTargetStrikesAsync(expiry, underlyingPrice);

            // 6. Build the final option contracts
            var putContract = _optionChainService.BuildOptionContract(
                putStrike.Strike, expiry, "P");
            var callContract = _optionChainService.BuildOptionContract(
                callStrike.Strike, expiry, "C");

            // If mid prices weren't available during delta scan, request them now
            if (putMid <= 0 || callMid <= 0)
            {
                _logger.LogInformation("Requesting fresh bid/ask for selected strikes...");
                if (putMid <= 0)
                {
                    var putData = await _connection.RequestMarketDataSnapshotAsync(putContract);
                    putMid = putData.MidPrice;
                }
                if (callMid <= 0)
                {
                    var callData = await _connection.RequestMarketDataSnapshotAsync(callContract);
                    callMid = callData.MidPrice;
                }
            }

            // 7. Place sell orders concurrently
            _logger.LogInformation("Placing sell orders...");
            _logger.LogInformation("  PUT:  {Strike:F0} x{Qty} @ mid ${Mid:F2} ({OrderType})",
                putStrike.Strike, _tradingConfig.PutContracts, putMid, _tradingConfig.OrderType);
            _logger.LogInformation("  CALL: {Strike:F0} x{Qty} @ mid ${Mid:F2} ({OrderType})",
                callStrike.Strike, _tradingConfig.CallContracts, callMid, _tradingConfig.OrderType);

            var putTask = _orderService.SellOptionAsync(
                putContract, _tradingConfig.PutContracts, putMid, expiry);
            var callTask = _orderService.SellOptionAsync(
                callContract, _tradingConfig.CallContracts, callMid, expiry);

            var results = await Task.WhenAll(putTask, callTask);

            // 8. Report results
            _logger.LogInformation("=== Trade Summary ===");
            double totalPremium = 0;
            foreach (var result in results)
            {
                string sideLabel = result.Right == "P" ? "PUT " : "CALL";
                if (result.Status == "Filled")
                {
                    double premium = result.AvgFillPrice * int.Parse(_tradingConfig.Multiplier) * result.Quantity;
                    totalPremium += premium;
                    _logger.LogInformation("  {Side} {Strike:F0} x{Qty}: FILLED @ ${Price:F2} (premium: ${Premium:F2})",
                        sideLabel, result.Strike, result.Quantity, result.AvgFillPrice, premium);
                }
                else
                {
                    _logger.LogWarning("  {Side} {Strike:F0} x{Qty}: {Status}",
                        sideLabel, result.Strike, result.Quantity, result.Status);
                }
            }
            _logger.LogInformation("Total premium collected: ${Premium:F2}", totalPremium);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trade execution failed");
            throw;
        }
        finally
        {
            _connection.Disconnect();
        }
    }

    private async Task WaitForTradeTimeAsync(CancellationToken ct)
    {
        if (!TimeSpan.TryParse(_tradingConfig.TradeTimeLocal, out var tradeTime))
        {
            throw new InvalidOperationException(
                $"Invalid trade time format: '{_tradingConfig.TradeTimeLocal}'. Expected HH:mm:ss");
        }

        var tradeZone = TimeZoneInfo.FindSystemTimeZoneById(_tradingConfig.TradeTimeZone);
        var nowInZone = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tradeZone);
        var targetToday = nowInZone.Date + tradeTime;

        // If the target time has already passed today, warn and exit
        if (nowInZone > targetToday)
        {
            _logger.LogWarning(
                "Trade time {Time} ({Zone}) has already passed today ({Now}). Running immediately.",
                _tradingConfig.TradeTimeLocal, _tradingConfig.TradeTimeZone, nowInZone.ToString("HH:mm:ss"));
            return;
        }

        var waitDuration = targetToday - nowInZone;
        _logger.LogInformation(
            "Waiting until {Time} {Zone} ({Duration} from now)...",
            _tradingConfig.TradeTimeLocal, _tradingConfig.TradeTimeZone,
            waitDuration.ToString(@"hh\:mm\:ss"));

        // Log countdown periodically
        while (waitDuration.TotalSeconds > 60)
        {
            var sleepTime = waitDuration.TotalMinutes > 10
                ? TimeSpan.FromMinutes(5)
                : TimeSpan.FromMinutes(1);

            await Task.Delay(sleepTime, ct);

            nowInZone = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tradeZone);
            waitDuration = targetToday - nowInZone;

            if (waitDuration.TotalSeconds > 0)
            {
                _logger.LogInformation("  T-minus {Duration}...", waitDuration.ToString(@"hh\:mm\:ss"));
            }
        }

        // Final precise wait
        if (waitDuration.TotalSeconds > 0)
        {
            await Task.Delay(waitDuration, ct);
        }

        _logger.LogInformation("Trade time reached. Executing...");
    }
}

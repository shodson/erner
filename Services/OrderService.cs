using Erner.Configuration;
using Erner.Models;
using IBApi;
using Microsoft.Extensions.Logging;

namespace Erner.Services;

/// <summary>
/// Handles building and placing option sell orders through the IB API.
/// </summary>
public class OrderService
{
    private readonly IbConnection _connection;
    private readonly TradingConfig _config;
    private readonly ILogger<OrderService> _logger;

    public OrderService(IbConnection connection, TradingConfig config, ILogger<OrderService> logger)
    {
        _connection = connection;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Sell an option contract. Returns a TradeResult with fill information.
    /// </summary>
    public async Task<TradeResult> SellOptionAsync(Contract contract, int quantity, double midPrice, string expiry)
    {
        var order = new Order
        {
            Action = "SELL",
            TotalQuantity = quantity,
            OrderType = _config.OrderType,
            Tif = "DAY",
            Transmit = true
        };

        if (_config.OrderType == "LMT")
        {
            if (midPrice <= 0)
            {
                _logger.LogWarning("Mid price is {MidPrice} for {Right} {Strike}; switching to MKT order",
                    midPrice, contract.Right, contract.Strike);
                order.OrderType = "MKT";
            }
            else
            {
                order.LmtPrice = midPrice;
            }
        }

        var result = await _connection.PlaceOrderAsync(contract, order);

        var tradeResult = new TradeResult
        {
            OrderId = result.OrderId,
            Status = result.Status,
            Strike = contract.Strike,
            Right = contract.Right,
            Quantity = quantity,
            AvgFillPrice = result.AvgFillPrice,
            Expiry = expiry
        };

        if (result.Status == "Filled")
        {
            _logger.LogInformation(
                "FILLED: Sold {Qty}x {Symbol} {Expiry} {Right}{Strike} @ ${Price:F2}",
                quantity, contract.Symbol, expiry, contract.Right, contract.Strike, result.AvgFillPrice);
        }
        else
        {
            _logger.LogWarning(
                "Order {OrderId} ended with status {Status}: {Message}",
                result.OrderId, result.Status, result.Message);
        }

        return tradeResult;
    }
}

using System.Collections.Concurrent;
using IBApi;
using Microsoft.Extensions.Logging;

namespace Erner.Services;

/// <summary>
/// Wraps the IB TWS API's callback-driven EWrapper into an async/await pattern
/// using TaskCompletionSource for each request type.
/// </summary>
public class IbConnection : EWrapper, IDisposable
{
    private readonly ILogger<IbConnection> _logger;
    private EClientSocket _clientSocket = null!;
    private EReaderMonitorSignal _signal = null!;
    private EReader? _reader;
    private Thread? _readerThread;
    private int _nextReqId = 1000;
    private int _nextOrderId;
    private bool _disposed;

    // Connection
    private TaskCompletionSource<int>? _connectionTcs;

    // Option chain requests
    private readonly ConcurrentDictionary<int, OptionChainCollector> _chainRequests = new();

    // Market data snapshot requests
    private readonly ConcurrentDictionary<int, MarketDataCollector> _mktDataRequests = new();

    // Order tracking
    private readonly ConcurrentDictionary<int, OrderCollector> _orderRequests = new();

    // Contract details requests
    private readonly ConcurrentDictionary<int, ContractDetailsCollector> _contractDetailsRequests = new();

    public IbConnection(ILogger<IbConnection> logger)
    {
        _logger = logger;
    }

    public bool IsConnected => _clientSocket?.IsConnected() ?? false;

    public int GetNextReqId() => Interlocked.Increment(ref _nextReqId);

    public int GetNextOrderId() => Interlocked.Increment(ref _nextOrderId);

    #region Connection

    public async Task ConnectAsync(string host, int port, int clientId, int timeoutSeconds = 10)
    {
        _signal = new EReaderMonitorSignal();
        _clientSocket = new EClientSocket(this, _signal);
        _connectionTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        _logger.LogInformation("Connecting to IB at {Host}:{Port} with clientId {ClientId}...", host, port, clientId);
        _clientSocket.eConnect(host, port, clientId);

        // Start the reader after connecting
        _reader = new EReader(_clientSocket, _signal);
        _reader.Start();

        _readerThread = new Thread(() =>
        {
            while (_clientSocket.IsConnected())
            {
                _signal.waitForSignal();
                _reader.processMsgs();
            }
        })
        { IsBackground = true, Name = "IB-EReader" };
        _readerThread.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        cts.Token.Register(() => _connectionTcs.TrySetException(
            new TimeoutException($"Connection to IB at {host}:{port} timed out after {timeoutSeconds}s. " +
                "Verify TWS/Gateway is running and API connections are enabled.")));

        _nextOrderId = await _connectionTcs.Task;
        _logger.LogInformation("Connected to IB. Next valid order ID: {OrderId}", _nextOrderId);
    }

    public void Disconnect()
    {
        if (_clientSocket?.IsConnected() == true)
        {
            _logger.LogInformation("Disconnecting from IB...");
            _clientSocket.eDisconnect(true);
        }
    }

    #endregion

    #region Request Methods

    /// <summary>
    /// Request the option chain parameters (available expirations and strikes) for a symbol.
    /// </summary>
    public async Task<OptionChainResult> RequestOptionChainAsync(string symbol, string secType, int underlyingConId, int timeoutSeconds = 30)
    {
        int reqId = GetNextReqId();
        var collector = new OptionChainCollector();
        _chainRequests[reqId] = collector;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        cts.Token.Register(() => collector.Tcs.TrySetException(
            new TimeoutException($"Option chain request timed out after {timeoutSeconds}s")));

        _logger.LogDebug("Requesting option chain for {Symbol} (conId={ConId})", symbol, underlyingConId);
        _clientSocket.reqSecDefOptParams(reqId, symbol, "", secType, underlyingConId);

        var result = await collector.Tcs.Task;
        _chainRequests.TryRemove(reqId, out _);
        return result;
    }

    /// <summary>
    /// Request a market data snapshot for a single contract. Returns tick data including greeks.
    /// </summary>
    public async Task<MarketDataResult> RequestMarketDataSnapshotAsync(Contract contract, int timeoutSeconds = 15)
    {
        int reqId = GetNextReqId();
        var collector = new MarketDataCollector();
        _mktDataRequests[reqId] = collector;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        cts.Token.Register(() =>
        {
            // On timeout, return whatever we have so far rather than throwing
            collector.Tcs.TrySetResult(collector.Result);
        });

        // Request snapshot market data; genericTickList "106" requests option implied vol
        _clientSocket.reqMktData(reqId, contract, "106", true, false, new List<TagValue>());

        var result = await collector.Tcs.Task;
        _mktDataRequests.TryRemove(reqId, out _);
        return result;
    }

    /// <summary>
    /// Request market data snapshots for multiple contracts in batch.
    /// </summary>
    public async Task<Dictionary<int, MarketDataResult>> RequestMarketDataBatchAsync(
        List<(int id, Contract contract)> contracts, int timeoutSeconds = 30)
    {
        var tasks = new List<(int id, Task<MarketDataResult> task)>();

        foreach (var (id, contract) in contracts)
        {
            int reqId = GetNextReqId();
            var collector = new MarketDataCollector { ExternalId = id };
            _mktDataRequests[reqId] = collector;

            _clientSocket.reqMktData(reqId, contract, "106", true, false, new List<TagValue>());
            tasks.Add((id, collector.Tcs.Task));
        }

        // Wait for all with timeout
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        cts.Token.Register(() =>
        {
            foreach (var kvp in _mktDataRequests)
                kvp.Value.Tcs.TrySetResult(kvp.Value.Result);
        });

        var results = new Dictionary<int, MarketDataResult>();
        foreach (var (id, task) in tasks)
        {
            try
            {
                results[id] = await task;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Market data request for id {Id} failed: {Error}", id, ex.Message);
            }
        }

        // Cancel any remaining subscriptions
        foreach (var reqId in _mktDataRequests.Keys.ToList())
        {
            _clientSocket.cancelMktData(reqId);
            _mktDataRequests.TryRemove(reqId, out _);
        }

        return results;
    }

    /// <summary>
    /// Place an order and wait for it to be filled or reach a terminal state.
    /// </summary>
    public async Task<OrderResult> PlaceOrderAsync(Contract contract, Order order, int timeoutSeconds = 120)
    {
        int orderId = GetNextOrderId();
        order.OrderId = orderId;
        var collector = new OrderCollector(orderId, contract, order);
        _orderRequests[orderId] = collector;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        cts.Token.Register(() =>
        {
            collector.Tcs.TrySetResult(new OrderResult
            {
                OrderId = orderId,
                Status = "Timeout",
                Message = $"Order not filled within {timeoutSeconds}s"
            });
        });

        _logger.LogInformation("Placing order {OrderId}: {Action} {Qty} {Symbol} {Right} {Strike} @ {Type} {Price}",
            orderId, order.Action, order.TotalQuantity,
            contract.Symbol, contract.Right, contract.Strike,
            order.OrderType, order.OrderType == "LMT" ? order.LmtPrice : 0);

        _clientSocket.placeOrder(orderId, contract, order);

        var result = await collector.Tcs.Task;
        _orderRequests.TryRemove(orderId, out _);
        return result;
    }

    /// <summary>
    /// Cancel an existing order.
    /// </summary>
    public void CancelOrder(int orderId)
    {
        _logger.LogInformation("Cancelling order {OrderId}", orderId);
        _clientSocket.cancelOrder(orderId);
    }

    /// <summary>
    /// Request the underlying price for an index contract.
    /// </summary>
    public async Task<double> RequestUnderlyingPriceAsync(Contract contract, int timeoutSeconds = 15)
    {
        var result = await RequestMarketDataSnapshotAsync(contract, timeoutSeconds);
        if (result.LastPrice > 0) return result.LastPrice;
        if (result.ClosePrice > 0) return result.ClosePrice;
        throw new InvalidOperationException($"Could not get price for {contract.Symbol}. " +
            "Ensure market data subscriptions are active.");
    }

    #endregion

    #region EWrapper Callbacks - Connection

    public void connectAck()
    {
        _logger.LogDebug("connectAck received");
        if (_clientSocket.AsyncEConnect)
            _clientSocket.startApi();
    }

    public void connectionClosed()
    {
        _logger.LogWarning("IB connection closed");
    }

    public void nextValidId(int orderId)
    {
        _logger.LogDebug("nextValidId: {OrderId}", orderId);
        _nextOrderId = orderId;
        _connectionTcs?.TrySetResult(orderId);
    }

    public void managedAccounts(string accountsList)
    {
        _logger.LogInformation("Managed accounts: {Accounts}", accountsList);
    }

    #endregion

    #region EWrapper Callbacks - Market Data

    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
    {
        if (_mktDataRequests.TryGetValue(tickerId, out var collector))
        {
            switch (field)
            {
                case 1: collector.Result.BidPrice = price; break;   // BID
                case 2: collector.Result.AskPrice = price; break;   // ASK
                case 4: collector.Result.LastPrice = price; break;   // LAST
                case 9: collector.Result.ClosePrice = price; break;  // CLOSE
            }
        }
    }

    public void tickSize(int tickerId, int field, int size)
    {
        // Not needed for our use case
    }

    public void tickString(int tickerId, int field, string value)
    {
        // Not needed
    }

    public void tickGeneric(int tickerId, int field, double value)
    {
        // Not needed
    }

    public void tickOptionComputation(int tickerId, int field, double impliedVolatility,
        double delta, double optPrice, double pvDividend, double gamma, double vega,
        double theta, double undPrice)
    {
        if (_mktDataRequests.TryGetValue(tickerId, out var collector))
        {
            // Field 13 = model-based computation (preferred)
            // Field 10 = bid computation, Field 11 = ask computation
            // Field 12 = last computation
            if (field == 13 || (field == 10 && !collector.Result.HasModelGreeks))
            {
                if (delta > -2 && delta < 2) // Valid delta range
                {
                    collector.Result.Delta = delta;
                    collector.Result.ImpliedVol = impliedVolatility;
                    collector.Result.OptionPrice = optPrice;
                    collector.Result.Gamma = gamma;
                    collector.Result.Vega = vega;
                    collector.Result.Theta = theta;
                    collector.Result.UnderlyingPrice = undPrice;
                    collector.Result.HasModelGreeks = field == 13;
                }
            }
        }
    }

    public void tickSnapshotEnd(int tickerId)
    {
        if (_mktDataRequests.TryGetValue(tickerId, out var collector))
        {
            collector.Tcs.TrySetResult(collector.Result);
        }
    }

    public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints,
        double totalDividends, int holdDays, string futureLastTradeDate, double dividendImpact,
        double dividendsToLastTradeDate) { }

    public void tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions) { }

    public void marketDataType(int reqId, int marketDataType) { }

    #endregion

    #region EWrapper Callbacks - Option Chain

    public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId,
        string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes)
    {
        if (_chainRequests.TryGetValue(reqId, out var collector))
        {
            collector.Entries.Add(new OptionChainEntry
            {
                Exchange = exchange,
                UnderlyingConId = underlyingConId,
                TradingClass = tradingClass,
                Multiplier = multiplier,
                Expirations = expirations,
                Strikes = strikes
            });
        }
    }

    public void securityDefinitionOptionParameterEnd(int reqId)
    {
        if (_chainRequests.TryGetValue(reqId, out var collector))
        {
            var result = new OptionChainResult { Entries = collector.Entries };
            collector.Tcs.TrySetResult(result);
        }
    }

    #endregion

    #region EWrapper Callbacks - Orders

    public void orderStatus(int orderId, string status, double filled, double remaining,
        double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId,
        string whyHeld, double mktCapPrice)
    {
        _logger.LogInformation("Order {OrderId} status: {Status}, filled={Filled}, remaining={Remaining}, avgPrice={AvgPrice}",
            orderId, status, filled, remaining, avgFillPrice);

        if (_orderRequests.TryGetValue(orderId, out var collector))
        {
            collector.LastStatus = status;
            collector.Filled = filled;
            collector.AvgFillPrice = avgFillPrice;

            if (status == "Filled")
            {
                collector.Tcs.TrySetResult(new OrderResult
                {
                    OrderId = orderId,
                    Status = "Filled",
                    Filled = filled,
                    AvgFillPrice = avgFillPrice
                });
            }
            else if (status == "Cancelled" || status == "Inactive")
            {
                collector.Tcs.TrySetResult(new OrderResult
                {
                    OrderId = orderId,
                    Status = status,
                    Message = whyHeld
                });
            }
        }
    }

    public void openOrder(int orderId, Contract contract, Order order, OrderState orderState)
    {
        _logger.LogDebug("Open order {OrderId}: {Status}", orderId, orderState.Status);
    }

    public void openOrderEnd() { }

    public void orderBound(long orderId, int apiClientId, int apiOrderId) { }

    public void completedOrder(Contract contract, Order order, OrderState orderState) { }

    public void completedOrdersEnd() { }

    #endregion

    #region EWrapper Callbacks - Errors

    public void error(Exception e)
    {
        _logger.LogError(e, "IB API exception");
    }

    public void error(string str)
    {
        _logger.LogError("IB API error: {Message}", str);
    }

    public void error(int id, int errorCode, string errorMsg)
    {
        // Error codes 2100-2110 are informational/warning messages
        if (errorCode >= 2100 && errorCode <= 2110)
        {
            _logger.LogWarning("IB warning [{Code}] (reqId={Id}): {Message}", errorCode, id, errorMsg);
            return;
        }

        // Market data farm connection messages
        if (errorCode == 2104 || errorCode == 2106 || errorCode == 2158)
        {
            _logger.LogInformation("IB info [{Code}]: {Message}", errorCode, errorMsg);
            return;
        }

        _logger.LogError("IB error [{Code}] (reqId={Id}): {Message}", errorCode, id, errorMsg);

        // Fail relevant TCS if this is a fatal error for a request
        if (id >= 0)
        {
            if (_mktDataRequests.TryGetValue(id, out var mktCollector))
            {
                // 354 = not subscribed, return what we have
                if (errorCode == 354)
                    mktCollector.Tcs.TrySetResult(mktCollector.Result);
                else
                    mktCollector.Tcs.TrySetException(new IbApiException(errorCode, errorMsg));
            }

            if (_chainRequests.TryGetValue(id, out var chainCollector))
                chainCollector.Tcs.TrySetException(new IbApiException(errorCode, errorMsg));

            if (_orderRequests.TryGetValue(id, out var orderCollector))
            {
                if (errorCode == 201) // Order rejected
                    orderCollector.Tcs.TrySetResult(new OrderResult
                    {
                        OrderId = id,
                        Status = "Rejected",
                        Message = errorMsg
                    });
            }
        }
    }

    #endregion

    #region EWrapper Callbacks - Contract Details

    public void contractDetails(int reqId, ContractDetails contractDetails)
    {
        if (_contractDetailsRequests.TryGetValue(reqId, out var collector))
        {
            collector.Details.Add(contractDetails);
        }
    }

    public void contractDetailsEnd(int reqId)
    {
        if (_contractDetailsRequests.TryGetValue(reqId, out var collector))
        {
            collector.Tcs.TrySetResult(collector.Details);
        }
    }

    public void bondContractDetails(int reqId, ContractDetails contract) { }

    #endregion

    #region EWrapper Callbacks - Unused (no-op implementations)

    public void currentTime(long time) { }
    public void execDetails(int reqId, Contract contract, Execution execution) { }
    public void execDetailsEnd(int reqId) { }
    public void commissionReport(CommissionReport commissionReport) { }
    public void updateAccountValue(string key, string value, string currency, string accountName) { }
    public void updatePortfolio(Contract contract, double position, double marketPrice, double marketValue,
        double averageCost, double unrealizedPNL, double realizedPNL, string accountName) { }
    public void updateAccountTime(string timestamp) { }
    public void accountDownloadEnd(string account) { }
    public void accountSummary(int reqId, string account, string tag, string value, string currency) { }
    public void accountSummaryEnd(int reqId) { }
    public void position(string account, Contract contract, double pos, double avgCost) { }
    public void positionEnd() { }
    public void pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL) { }
    public void pnlSingle(int reqId, int pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value) { }
    public void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance,
        string benchmark, string projection, string legsStr) { }
    public void scannerDataEnd(int reqId) { }
    public void scannerParameters(string xml) { }
    public void updateMktDepth(int tickerId, int position, int operation, int side, double price, int size) { }
    public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side,
        double price, int size, bool isSmartDepth) { }
    public void fundamentalData(int reqId, string data) { }
    public void historicalData(int reqId, Bar bar) { }
    public void historicalDataEnd(int reqId, string start, string end) { }
    public void historicalDataUpdate(int reqId, Bar bar) { }
    public void historicalTicks(int reqId, HistoricalTick[] ticks, bool done) { }
    public void historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done) { }
    public void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done) { }
    public void headTimestamp(int reqId, string headTimestamp) { }
    public void histogramData(int reqId, HistogramEntry[] data) { }
    public void realtimeBar(int reqId, long date, double open, double high, double low, double close,
        long volume, double wap, int count) { }
    public void tickNews(int tickerId, long timeStamp, string providerCode, string articleId,
        string headline, string extraData) { }
    public void newsProviders(NewsProvider[] newsProviders) { }
    public void newsArticle(int requestId, int articleType, string articleText) { }
    public void historicalNews(int requestId, string time, string providerCode, string articleId, string headline) { }
    public void historicalNewsEnd(int requestId, bool hasMore) { }
    public void receiveFA(int faDataType, string faXmlData) { }
    public void verifyMessageAPI(string apiData) { }
    public void verifyCompleted(bool isSuccessful, string errorText) { }
    public void verifyAndAuthMessageAPI(string apiData, string xyzChallenge) { }
    public void verifyAndAuthCompleted(bool isSuccessful, string errorText) { }
    public void displayGroupList(int reqId, string groups) { }
    public void displayGroupUpdated(int reqId, string contractInfo) { }
    public void softDollarTiers(int reqId, SoftDollarTier[] tiers) { }
    public void familyCodes(FamilyCode[] familyCodes) { }
    public void symbolSamples(int reqId, ContractDescription[] contractDescriptions) { }
    public void mktDepthExchanges(DepthMktDataDescription[] depthMktDataDescriptions) { }
    public void smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap) { }
    public void tickByTickAllLast(int reqId, int tickType, long time, double price, int size,
        TickAttribLast tickAttriblast, string exchange, string specialConditions) { }
    public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, int bidSize,
        int askSize, TickAttribBidAsk tickAttribBidAsk) { }
    public void tickByTickMidPoint(int reqId, long time, double midPoint) { }
    public void rerouteMktDataReq(int reqId, int conId, string exchange) { }
    public void rerouteMktDepthReq(int reqId, int conId, string exchange) { }
    public void marketRule(int marketRuleId, PriceIncrement[] priceIncrements) { }
    public void replaceFAEnd(int reqId, string text) { }
    public void deltaNeutralValidation(int reqId, DeltaNeutralContract deltaNeutralContract) { }
    public void updateNewsBulletin(int msgId, int msgType, string message, string origExchange) { }
    public void positionMulti(int requestId, string account, string modelCode, Contract contract, double pos, double avgCost) { }
    public void positionMultiEnd(int requestId) { }
    public void accountUpdateMulti(int requestId, string account, string modelCode, string key, string value, string currency) { }
    public void accountUpdateMultiEnd(int requestId) { }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (!_disposed)
        {
            Disconnect();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    #endregion
}

#region Data Classes

public class OptionChainCollector
{
    public TaskCompletionSource<OptionChainResult> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<OptionChainEntry> Entries { get; } = new();
}

public class OptionChainResult
{
    public List<OptionChainEntry> Entries { get; set; } = new();
}

public class OptionChainEntry
{
    public string Exchange { get; set; } = "";
    public int UnderlyingConId { get; set; }
    public string TradingClass { get; set; } = "";
    public string Multiplier { get; set; } = "";
    public HashSet<string> Expirations { get; set; } = new();
    public HashSet<double> Strikes { get; set; } = new();
}

public class MarketDataCollector
{
    public TaskCompletionSource<MarketDataResult> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public MarketDataResult Result { get; } = new();
    public int ExternalId { get; set; }
}

public class MarketDataResult
{
    public double BidPrice { get; set; }
    public double AskPrice { get; set; }
    public double LastPrice { get; set; }
    public double ClosePrice { get; set; }
    public double Delta { get; set; }
    public double ImpliedVol { get; set; }
    public double OptionPrice { get; set; }
    public double Gamma { get; set; }
    public double Vega { get; set; }
    public double Theta { get; set; }
    public double UnderlyingPrice { get; set; }
    public bool HasModelGreeks { get; set; }

    public double MidPrice => (BidPrice > 0 && AskPrice > 0)
        ? Math.Round((BidPrice + AskPrice) / 2.0, 2)
        : 0;
}

public class OrderCollector
{
    public OrderCollector(int orderId, Contract contract, Order order)
    {
        OrderId = orderId;
        Contract = contract;
        Order = order;
    }

    public int OrderId { get; }
    public Contract Contract { get; }
    public Order Order { get; }
    public TaskCompletionSource<OrderResult> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string LastStatus { get; set; } = "";
    public double Filled { get; set; }
    public double AvgFillPrice { get; set; }
}

public class OrderResult
{
    public int OrderId { get; set; }
    public string Status { get; set; } = "";
    public double Filled { get; set; }
    public double AvgFillPrice { get; set; }
    public string Message { get; set; } = "";
}

public class ContractDetailsCollector
{
    public TaskCompletionSource<List<ContractDetails>> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<ContractDetails> Details { get; } = new();
}

public class IbApiException : Exception
{
    public int ErrorCode { get; }

    public IbApiException(int errorCode, string message)
        : base($"IB error [{errorCode}]: {message}")
    {
        ErrorCode = errorCode;
    }
}

#endregion

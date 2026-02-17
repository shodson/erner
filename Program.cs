using Erner.Configuration;
using Erner.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Erner;

class Program
{
    static async Task<int> Main(string[] args)
    {
        // Parse CLI arguments
        var cliArgs = ParseArguments(args);

        if (cliArgs.ContainsKey("help"))
        {
            PrintHelp();
            return 0;
        }

        // Load appsettings.json as base config
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        var configuration = configBuilder.Build();

        // Build configs from appsettings.json defaults
        var connectionConfig = new ConnectionConfig();
        configuration.GetSection("Connection").Bind(connectionConfig);

        var tradingConfig = new TradingConfig();
        configuration.GetSection("Trading").Bind(tradingConfig);

        // Apply CLI overrides
        ApplyCliOverrides(connectionConfig, tradingConfig, cliArgs);

        // Determine if immediate execution
        bool immediate = cliArgs.ContainsKey("now");

        // Set up logging
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            var logLevel = configuration.GetValue("Logging:LogLevel:Default", LogLevel.Information);
            if (cliArgs.ContainsKey("verbose"))
                logLevel = LogLevel.Debug;

            builder
                .SetMinimumLevel(logLevel)
                .AddSimpleConsole(options =>
                {
                    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                    options.SingleLine = true;
                });
        });

        var logger = loggerFactory.CreateLogger<Program>();

        // Log configuration
        logger.LogInformation("=== Erner SPX Options Seller ===");
        logger.LogInformation("Connection: {Host}:{Port} (clientId={ClientId})",
            connectionConfig.Host, connectionConfig.Port, connectionConfig.ClientId);
        logger.LogInformation("Mode: {Mode}",
            connectionConfig.Port switch
            {
                7497 => "TWS Paper Trading",
                7496 => "TWS Live Trading",
                4002 => "Gateway Paper Trading",
                4001 => "Gateway Live Trading",
                _ => $"Custom (port {connectionConfig.Port})"
            });
        logger.LogInformation("Strategy: Sell {PutQty}x {PutDelta:F2}-delta PUT + {CallQty}x {CallDelta:F2}-delta CALL",
            tradingConfig.PutContracts, tradingConfig.PutDeltaTarget,
            tradingConfig.CallContracts, tradingConfig.CallDeltaTarget);
        logger.LogInformation("Order type: {OrderType}", tradingConfig.OrderType);

        if (!immediate)
        {
            logger.LogInformation("Scheduled trade time: {Time} ({Zone})",
                tradingConfig.TradeTimeLocal, tradingConfig.TradeTimeZone);
        }

        // Build services
        var connection = new IbConnection(loggerFactory.CreateLogger<IbConnection>());
        var optionChainService = new OptionChainService(connection, tradingConfig,
            loggerFactory.CreateLogger<OptionChainService>());
        var orderService = new OrderService(connection, tradingConfig,
            loggerFactory.CreateLogger<OrderService>());
        var orchestrator = new TradeOrchestrator(connection, optionChainService, orderService,
            connectionConfig, tradingConfig,
            loggerFactory.CreateLogger<TradeOrchestrator>());

        // Execute
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            logger.LogWarning("Ctrl+C received, shutting down...");
            cts.Cancel();
        };

        try
        {
            await orchestrator.ExecuteAsync(immediate, cts.Token);
            logger.LogInformation("Done.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Operation cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fatal error");
            return 2;
        }
    }

    /// <summary>
    /// Simple CLI argument parser. Supports --key value, --key=value, and --flag.
    /// </summary>
    static Dictionary<string, string> ParseArguments(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (!arg.StartsWith("--"))
                continue;

            string key = arg[2..]; // strip --

            // Handle --key=value
            int eqIdx = key.IndexOf('=');
            if (eqIdx >= 0)
            {
                result[key[..eqIdx]] = key[(eqIdx + 1)..];
                continue;
            }

            // Handle flags (--now, --paper, --live, --gateway, --help, --verbose)
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
            {
                result[key] = "true";
                continue;
            }

            // Handle --key value
            result[key] = args[++i];
        }

        return result;
    }

    static void ApplyCliOverrides(ConnectionConfig conn, TradingConfig trade,
        Dictionary<string, string> args)
    {
        // Connection overrides
        if (args.TryGetValue("host", out var host))
            conn.Host = host;
        if (args.TryGetValue("client-id", out var clientId))
            conn.ClientId = int.Parse(clientId);

        // Port resolution: explicit --port wins, then --paper/--live/--gateway flags
        if (args.TryGetValue("port", out var port))
        {
            conn.Port = int.Parse(port);
        }
        else
        {
            bool gateway = args.ContainsKey("gateway");
            bool paper = args.ContainsKey("paper");
            bool live = args.ContainsKey("live");

            if (paper && gateway) conn.Port = 4002;
            else if (live && gateway) conn.Port = 4001;
            else if (paper) conn.Port = 7497;
            else if (live) conn.Port = 7496;
            // else: keep appsettings.json default
        }

        // Trading overrides
        if (args.TryGetValue("time", out var time))
            trade.TradeTimeLocal = time;
        if (args.TryGetValue("timezone", out var tz))
            trade.TradeTimeZone = tz;
        if (args.TryGetValue("put-contracts", out var putQty))
            trade.PutContracts = int.Parse(putQty);
        if (args.TryGetValue("call-contracts", out var callQty))
            trade.CallContracts = int.Parse(callQty);
        if (args.TryGetValue("put-delta", out var putDelta))
            trade.PutDeltaTarget = double.Parse(putDelta);
        if (args.TryGetValue("call-delta", out var callDelta))
            trade.CallDeltaTarget = double.Parse(callDelta);
        if (args.TryGetValue("order-type", out var orderType))
            trade.OrderType = orderType.ToUpperInvariant();
        if (args.TryGetValue("symbol", out var symbol))
            trade.Symbol = symbol;
        if (args.TryGetValue("trading-class", out var tradingClass))
            trade.TradingClass = tradingClass;
        if (args.TryGetValue("exchange", out var exchange))
            trade.Exchange = exchange;
    }

    static void PrintHelp()
    {
        Console.WriteLine(@"
Erner - SPX Options Seller
Sells next-trading-day SPX options at target delta values.

USAGE:
    erner [OPTIONS]

CONNECTION OPTIONS:
    --host <addr>           IB host address (default: 127.0.0.1)
    --port <port>           IB API port (default: 7497)
    --client-id <id>        API client ID (default: 1)
    --paper                 Use paper trading port (TWS: 7497, Gateway: 4002)
    --live                  Use live trading port (TWS: 7496, Gateway: 4001)
    --gateway               Use IB Gateway ports instead of TWS ports

TRADING OPTIONS:
    --now                   Execute immediately instead of waiting for scheduled time
    --time <HH:mm:ss>       Trade execution time (default: 12:45:00)
    --timezone <tz>         Timezone for trade time (default: America/Los_Angeles)
    --put-contracts <n>     Number of put contracts to sell (default: 1)
    --call-contracts <n>    Number of call contracts to sell (default: 1)
    --put-delta <d>         Target put delta, negative (default: -0.15)
    --call-delta <d>        Target call delta, positive (default: 0.10)
    --order-type <type>     Order type: LMT or MKT (default: LMT)

OTHER:
    --verbose               Enable debug logging
    --help                  Show this help message

EXAMPLES:
    # Paper trading, execute immediately
    erner --now --paper

    # Live trading via Gateway, 2 puts and 1 call at 1:00 PM Pacific
    erner --live --gateway --put-contracts 2 --time 13:00:00

    # Custom deltas
    erner --now --put-delta -0.10 --call-delta 0.05
");
    }
}

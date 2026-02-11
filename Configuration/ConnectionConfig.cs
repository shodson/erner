namespace Erner.Configuration;

public class ConnectionConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7497; // 7497=paper, 7496=live
    public int ClientId { get; set; } = 1;
}

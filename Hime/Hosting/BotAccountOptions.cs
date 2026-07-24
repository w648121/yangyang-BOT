namespace Hime.Hosting;

/// <summary>
/// Configures the QQ/LLBot accounts that feed the single Hime message pipeline.
/// Feature services remain shared singletons; these entries only describe transport connections.
/// </summary>
public sealed class BotAccountsOptions
{
    public List<BotAccountConnectionOptions> Connections { get; set; } = [];

    public IReadOnlyList<BotAccountConnectionOptions> GetEnabledConnections()
    {
        var enabled = Connections
            .Where(connection => connection.Enabled)
            .Select(connection => connection.Normalized())
            .ToArray();

        return enabled.Length > 0
            ? enabled
            : [BotAccountConnectionOptions.CreateDefault()];
    }
}

public sealed class BotAccountConnectionOptions
{
    public string Id { get; set; } = "primary";

    public string DisplayName { get; set; } = "主机器人";

    public bool Enabled { get; set; } = true;

    public bool IsPrimary { get; set; } = true;

    public long SelfId { get; set; }

    public string Host { get; set; } = "127.0.0.1";

    public ushort Port { get; set; } = 3010;

    public string AccessToken { get; set; } = string.Empty;

    public string OneBotApiBaseUrl { get; set; } = string.Empty;

    internal BotAccountConnectionOptions Normalized()
    {
        var id = string.IsNullOrWhiteSpace(Id) ? $"account-{Port}" : Id.Trim();
        var displayName = string.IsNullOrWhiteSpace(DisplayName) ? id : DisplayName.Trim();
        var host = string.IsNullOrWhiteSpace(Host) ? "127.0.0.1" : Host.Trim();
        var port = Port == 0 ? (ushort)3010 : Port;
        return new BotAccountConnectionOptions
        {
            Id = id,
            DisplayName = displayName,
            Enabled = Enabled,
            IsPrimary = IsPrimary,
            SelfId = SelfId,
            Host = host,
            Port = port,
            AccessToken = AccessToken?.Trim() ?? string.Empty,
            OneBotApiBaseUrl = OneBotApiBaseUrl?.Trim() ?? string.Empty
        };
    }

    internal static BotAccountConnectionOptions CreateDefault() => new();
}

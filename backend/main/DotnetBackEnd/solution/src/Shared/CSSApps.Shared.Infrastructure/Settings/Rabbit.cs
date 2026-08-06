namespace CSSApps.Shared.Infrastructure.Settings;

public class RabbitMQOptions
{
    public string? Host { get; set; }

    public string? Username { get; set; } = null!;

    public string? Password { get; set; } = null!;

    public string? VirtualHost { get; set; }

    public int Port { get; set; }

    public IntegrationEventSettings IntegrationEvents { get; set; } = null!;
}

public class IntegrationEventSettings
{
    public RetrySettings RetrySettings { get; set; } = null!;
}

public class RetrySettings
{
    public int RepeatCount { get; set; }
}
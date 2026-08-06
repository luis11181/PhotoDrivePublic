using System.Text.Json;
using CSSApps.Shared.Contracts.Interfaces;

namespace CSSApps.Shared.Infrastructure.Data.DesignTime;

public static class DesignTimeDbContextFactorySupport
{
    public static IExecutionContextAccessor ExecutionContextAccessor { get; } = new DesignTimeExecutionContextAccessor();

    public static string GetRequiredConnectionString(string connectionStringName)
    {
        var environmentVariableValue = Environment.GetEnvironmentVariable($"ConnectionStrings__{connectionStringName}");
        if (!string.IsNullOrWhiteSpace(environmentVariableValue))
        {
            return environmentVariableValue;
        }

        var hostSettingsDirectory = ResolveHostSettingsDirectory();
        var settingsFilePath = Path.Combine(hostSettingsDirectory, "appsettings.json");
        if (File.Exists(settingsFilePath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsFilePath));
            if (document.RootElement.TryGetProperty("ConnectionStrings", out var connectionStringsElement) &&
                connectionStringsElement.TryGetProperty(connectionStringName, out var connectionStringElement) &&
                !string.IsNullOrWhiteSpace(connectionStringElement.GetString()))
            {
                return connectionStringElement.GetString()!;
            }
        }

        throw new InvalidOperationException(
            $"Connection string '{connectionStringName}' was not found. Set ConnectionStrings__{connectionStringName} or define it in appsettings.json.");
    }

    private static string ResolveHostSettingsDirectory()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            foreach (var candidate in new[]
                     {
                         directory.FullName,
                         Path.Combine(directory.FullName, "src", "Hosts", "CSSApps.Api"),
                         Path.Combine(directory.FullName, "DotnetBackEnd", "solution", "src", "Hosts", "CSSApps.Api")
                     })
            {
                if (File.Exists(Path.Combine(candidate, "appsettings.json"))) return candidate;
            }
        }

        throw new InvalidOperationException("Could not locate the CSSApps.Api host settings directory for design-time EF operations.");
    }

    private sealed class DesignTimeExecutionContextAccessor : IExecutionContextAccessor
    {
        public Guid? UserId => null;

        public Guid? CorrelationId => null;

        public bool IsAvailable => false;
    }
}

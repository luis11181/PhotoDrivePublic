using CSSApps.Shared.Contracts.Interfaces;

namespace CSSApps.Shared.Infrastructure.Data.DesignTime;

public static class DesignTimeDbContextFactorySupport
{
    public static IExecutionContextAccessor ExecutionContextAccessor { get; } = new DesignTimeExecutionContextAccessor();

    /// <summary>
    /// EF CLI operations (adding migrations, scripts) never touch the shared cloud database. They use a throwaway
    /// local Turso file unless <c>PHOTODRIVE_DESIGN_TIME_DATABASE</c> points at another local file.
    /// </summary>
    public static string LocalConnectionString()
    {
        var path = Environment.GetEnvironmentVariable("PHOTODRIVE_DESIGN_TIME_DATABASE");
        if (string.IsNullOrWhiteSpace(path)) path = Path.Combine(Path.GetTempPath(), "photodrive-design-time.db");
        return $"Data Source={path}";
    }

    private sealed class DesignTimeExecutionContextAccessor : IExecutionContextAccessor
    {
        public Guid? UserId => null;

        public Guid? CorrelationId => null;

        public bool IsAvailable => false;
    }
}

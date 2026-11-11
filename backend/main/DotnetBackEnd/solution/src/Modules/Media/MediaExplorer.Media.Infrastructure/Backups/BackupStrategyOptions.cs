using Microsoft.Extensions.Configuration;

namespace MediaExplorer.Media.Infrastructure;

/// <summary>Optional redundant recovery layers configured under <c>MediaStorage:BackupStrategies</c>.</summary>
internal sealed record BackupStrategyOptions(bool RecoveryManifestsEnabled)
{
    public static BackupStrategyOptions FromConfiguration(IConfiguration configuration) => new(
        configuration.GetValue("MediaStorage:BackupStrategies:RecoveryManifestsEnabled", true));
}

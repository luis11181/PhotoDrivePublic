using Microsoft.Extensions.Configuration;

namespace MediaExplorer.Media.Infrastructure;

internal sealed record BackupStrategyOptions(
    bool RecoveryManifestsEnabled,
    bool PortableJsonExportsEnabled)
{
    public static BackupStrategyOptions FromConfiguration(IConfiguration configuration) => new(
        configuration.GetValue("MediaStorage:BackupStrategies:RecoveryManifestsEnabled", true),
        configuration.GetValue("MediaStorage:BackupStrategies:PortableJsonExportsEnabled", false));
}

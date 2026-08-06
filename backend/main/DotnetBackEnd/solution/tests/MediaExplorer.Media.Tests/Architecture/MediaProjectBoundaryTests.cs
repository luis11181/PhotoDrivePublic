using FluentAssertions;
using MediaExplorer.Media.Api.Controllers.v1;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Domain;

namespace MediaExplorer.Media.Tests.Architecture;

public sealed class MediaProjectBoundaryTests
{
    [Fact]
    public void Domain_has_no_dependency_on_outer_media_layers()
    {
        ReferencedMediaAssemblies(typeof(CatalogFile).Assembly)
            .Should().BeEmpty();
    }

    [Fact]
    public void Application_does_not_depend_on_api_or_infrastructure()
    {
        ReferencedMediaAssemblies(typeof(ICatalogQueryService).Assembly)
            .Should().NotContain(["MediaExplorer.Media.Api", "MediaExplorer.Media.Infrastructure"]);
    }

    [Fact]
    public void Api_depends_on_application_contracts_not_infrastructure()
    {
        ReferencedMediaAssemblies(typeof(ItemsController).Assembly)
            .Should().NotContain("MediaExplorer.Media.Infrastructure");
    }

    private static string[] ReferencedMediaAssemblies(System.Reflection.Assembly assembly) => assembly
        .GetReferencedAssemblies()
        .Select(reference => reference.Name!)
        .Where(name => name.StartsWith("MediaExplorer.Media.", StringComparison.Ordinal))
        .ToArray();
}

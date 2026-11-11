using System.Text.Json;
using FluentAssertions;
using MediaExplorer.Media.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class ImportSourceDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "import-source-test-" + Guid.NewGuid().ToString("N"));
    private ImportSourceDirectory Source => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["MediaStorage:Imports:SourceDirectory"] = Path.Combine(_root, "inbox")
    }).Build());

    public ImportSourceDirectoryTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "inbox", "Album 1 Elian"));
        Directory.CreateDirectory(Path.Combine(_root, "inbox", "Takeout", "Google Photos"));
        Directory.CreateDirectory(Path.Combine(_root, "private"));
    }

    [Theory]
    [InlineData("../private")]
    [InlineData("Takeout/../../private")]
    [InlineData("/etc")]
    [InlineData("C:\\Users\\someone")]
    [InlineData("Takeout\\Google Photos")]
    public void Source_cannot_escape_the_configured_inbox(string requested)
        => Source.Resolve(requested).IsFailure.Should().BeTrue();

    [Fact]
    public void Folder_pages_are_relative_sorted_and_do_not_expose_server_paths()
    {
        var first = Source.List("", null, 1, default).SuccessValue;
        first.Folders.Should().ContainSingle().Which.Name.Should().Be("Album 1 Elian");
        first.ParentPath.Should().BeNull();
        first.NextCursor.Should().NotBeNull();
        var second = Source.List("", first.NextCursor, 1, default).SuccessValue;
        second.Folders.Single().Path.Should().Be("Takeout");
        second.NextCursor.Should().BeNull();
        var nested = Source.List("Takeout", null, 50, default).SuccessValue;
        nested.ParentPath.Should().Be("");
        nested.Folders.Single().Path.Should().Be("Takeout/Google Photos");
        JsonSerializer.Serialize(nested).Should().NotContain(_root);
        Source.Resolve("Takeout/Google Photos").SuccessValue.Should().Be(Path.Combine(_root, "inbox", "Takeout", "Google Photos"));
    }

    [Fact]
    public void Symbolic_link_folders_are_hidden_and_cannot_be_selected()
    {
        try { Directory.CreateSymbolicLink(Path.Combine(_root, "inbox", "outside"), Path.Combine(_root, "private")); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException) { return; }
        Source.List("", null, 50, default).SuccessValue.Folders.Should().NotContain(folder => folder.Name == "outside");
        Source.Resolve("outside").IsFailure.Should().BeTrue();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

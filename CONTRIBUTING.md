# Contributing

Keep changes focused, testable, and safe for existing originals and catalogs.
Never submit credentials, private media, databases, installation hostnames, or
machine-specific paths.

Before opening a pull request, run:

```bash
cd frontend && npm ci && npm run lint && npm run build
cd ../backend/main/DotnetBackEnd/solution
dotnet test tests/MediaExplorer.Media.Tests/MediaExplorer.Media.Tests.csproj
dotnet build src/Hosts/CSSApps.Api/CSSApps.Api.csproj --no-restore
```

Do not hand-edit EF Core migrations. Generate them with the EF CLI, review the
output, and verify both databases can migrate from empty state.

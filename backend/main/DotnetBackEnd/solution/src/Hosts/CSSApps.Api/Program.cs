using System.Text.Json.Serialization;
using Asp.Versioning;
using CSSApps.Api.Authentication;
using CSSApps.Api.DependencyInjection;
using CSSApps.Api.DependencyInjection.ExecutionContext;
using CSSApps.Shared.Contracts.Constants;
using CSSApps.Shared.Contracts.Interfaces;
using CSSApps.Shared.Utils.ApiHelpers.DependencyInjection;
using CSSApps.Shared.Utils.ApiHelpers.Middlewares;
using CSSApps.Users.Api;
using CSSApps.Users.Infrastructure.Data;
using MediaExplorer.Media.Infrastructure;
using Microsoft.EntityFrameworkCore;
using CSSApps.Users.Contracts.Interfaces.Auth;
using MediaExplorer.Media.Application;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using CSSApps.Api.AccessControl;
using CSSApps.Api.Operations;
using Microsoft.FeatureManagement;

namespace CSSApps.Api;

/// <summary>
/// ASP.NET Core host for the modules currently enabled in Media Explorer.
/// </summary>
#pragma warning disable CA1052 // Program must be non-static for WebApplicationFactory support.
public class Program
{
  public static async Task Main(string[] args)
  {
    var builder = WebApplication.CreateBuilder(args);
    ConfigureConfiguration(builder);
    if (await TryRunConfigurationCommandAsync(builder, args)) return;
    ValidateAuthenticationConfiguration(builder.Configuration);
    ProductionConfigurationValidator.Validate(builder.Configuration, builder.Environment);
    ConfigureServices(builder);

    var app = builder.Build();
    if (await TryRunOfflineHostCommandAsync(app, args)) return;
    var ownerRecovery = args.Length == 4 && args.Take(3).SequenceEqual(["users", "recover-owner", "--email"], StringComparer.OrdinalIgnoreCase);
    if (args.Length > 0 && !args[0].StartsWith('-') && !ownerRecovery)
      throw new ArgumentException("Unknown PhotoDrive command. Refusing to start the web server for an unrecognized operator command.");
    await ApplyDatabaseInitializationAndSeedAsync(app);
    if (await TryRunHostCommandAsync(app, args)) return;
    ConfigurePipeline(app);
    await app.RunAsync();
  }

  private static async Task<bool> TryRunConfigurationCommandAsync(WebApplicationBuilder builder, string[] args)
  {
    if (args.SequenceEqual(["config", "explain"], StringComparer.OrdinalIgnoreCase))
    {
      var report = SanitizedConfigurationReport.Create(
        builder.Configuration,
        builder.Environment.EnvironmentName,
        builder.Environment.ContentRootPath);
      await Console.Out.WriteLineAsync(report.ToDisplayText());
      return true;
    }

    if (args.SequenceEqual(["config", "validate"], StringComparer.OrdinalIgnoreCase))
    {
      ValidateAuthenticationConfiguration(builder.Configuration);
      ProductionConfigurationValidator.Validate(builder.Configuration, builder.Environment);
      await Console.Out.WriteLineAsync($"{builder.Environment.EnvironmentName} configuration is valid.");
      return true;
    }

    return false;
  }

  private static void ConfigureConfiguration(WebApplicationBuilder builder)
  {
    var environmentName = builder.Environment.EnvironmentName;

    builder.Configuration
      .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
      .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: true)
      .AddJsonFile("authentication.json", optional: false, reloadOnChange: true)
      .AddJsonFile($"authentication.{environmentName}.json", optional: true, reloadOnChange: true)
      .AddJsonFile("logging.json", optional: true, reloadOnChange: true)
      .AddJsonFile($"logging.{environmentName}.json", optional: true, reloadOnChange: true)
      .AddJsonFile("infrastructure.json", optional: true, reloadOnChange: true)
      .AddJsonFile($"infrastructure.{environmentName}.json", optional: true, reloadOnChange: true)
      .AddJsonFile("usersSettings.json", optional: true, reloadOnChange: true)
      .AddJsonFile($"usersSettings.{environmentName}.json", optional: true, reloadOnChange: true)
      .AddJsonFile("deployment.json", optional: false, reloadOnChange: true)
      .AddJsonFile($"deployment.{environmentName}.json", optional: true, reloadOnChange: true)
      // Untracked secrets (database token, etc.); see secrets.example.json. Environment variables still override them.
      .AddJsonFile("secrets.json", optional: true, reloadOnChange: true)
      .AddJsonFile($"secrets.{environmentName}.json", optional: true, reloadOnChange: true)
      .AddEnvironmentVariables();
  }

  private static void ConfigureServices(WebApplicationBuilder builder)
  {
    builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://*:6045");

    builder.Services.AddHealthChecks();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddHttpClient();
    builder.Services.AddMemoryCache();
    builder.Services.AddAntiforgery(options =>
    {
      options.HeaderName = "X-CSRF-Token";
      options.Cookie.Name = "media_explorer_csrf";
      options.Cookie.SameSite = SameSiteMode.Lax;
      options.Cookie.SecurePolicy = RequiresHttps(builder.Configuration, builder.Environment)
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.SameAsRequest;
    });

    // Persisted in every environment so sign-in and CSRF cookies stay valid across restarts.
    {
      var keyDirectory = Path.GetFullPath(builder.Configuration["Security:DataProtectionKeysDirectory"]!);
      Directory.CreateDirectory(keyDirectory);
      builder.Services.AddDataProtection()
        .SetApplicationName("MediaExplorer")
        .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
    }

    if (builder.Configuration.GetValue<bool>("ReverseProxy:Enabled"))
    {
      builder.Services.Configure<ForwardedHeadersOptions>(options =>
        TrustedProxyConfiguration.Apply(builder.Configuration, options));
    }
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton(new ClientIpAccessPolicy(
      builder.Configuration,
      defaultEnabled: !builder.Environment.IsDevelopment()));
    builder.Services.AddFeatureManagement();

    builder.Services.AddSingleton<ExecutionContextAccessor>();
    builder.Services.AddSingleton<IExecutionContextAccessor>(provider =>
      provider.GetRequiredService<ExecutionContextAccessor>());
    builder.Services.AddSingleton<IExecutionContextImpersonationAccessor>(provider =>
      provider.GetRequiredService<ExecutionContextAccessor>());

    var frontendOriginPolicy = new FrontendOriginPolicy(builder.Configuration);
    builder.Services.AddCors(options =>
    {
      options.AddPolicy("Frontend", policy =>
        policy.SetIsOriginAllowed(frontendOriginPolicy.IsAllowed).AllowAnyMethod().AllowAnyHeader().WithExposedHeaders("X-CSRF-Token", "X-Correlation-ID").AllowCredentials().SetPreflightMaxAge(TimeSpan.FromMinutes(10)));
    });

    builder.Services.AddControllers().AddJsonOptions(options =>
      options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    builder.Services.AddApiVersioning(options =>
    {
      options.DefaultApiVersion = new ApiVersion(1, 0);
      options.AssumeDefaultVersionWhenUnspecified = true;
      options.ReportApiVersions = true;
      options.ApiVersionReader = ApiVersionReader.Combine(
        new UrlSegmentApiVersionReader(),
        new QueryStringApiVersionReader(CommonConstants.Headers.ApiVersionKey),
        new HeaderApiVersionReader(CommonConstants.Headers.ApiVersionKey),
        new MediaTypeApiVersionReader(CommonConstants.Headers.ApiVersionKey));
    }).AddApiExplorer(options =>
    {
      options.GroupNameFormat = "'v'VVV";
      options.SubstituteApiVersionInUrl = true;
    });

    builder.Services.AddSwaggerDocumentation();

    builder.Services.AddMyAuthentication(builder.Configuration);
    builder.Services.AddMyRateLimiter(builder.Configuration);
    // Must precede the shared handlers so cloud outages map to an explicit 503 before the generic 500.
    builder.Services.AddExceptionHandler<DatabaseUnavailableExceptionHandler>();
    builder.Services.AddMyErrorHandlers(builder.Environment);
    builder.Services.AddUsersModule(builder.Configuration, builder.Environment);
    builder.Services.AddScoped<IShareRecipientDirectory, FamilyShareRecipientDirectory>();
    builder.Services.AddScoped<IUploaderIdentityDirectory, FamilyUploaderIdentityDirectory>();
    builder.Services.AddMediaInfrastructure(builder.Configuration);
  }

  /// <summary>
  /// Fails startup before the application can expose a deployment with no usable sign-in path
  /// or with a bootstrap password that password authentication cannot consume.
  /// </summary>
  private static void ValidateAuthenticationConfiguration(IConfiguration configuration)
  {
    var passwordEnabled = configuration.GetValue<bool>("FamilyAuthentication:PasswordLoginEnabled");
    var googleClientId = configuration["OAuth:Google:ClientId"];
    var googleEnabled = !string.IsNullOrWhiteSpace(googleClientId) &&
                        !googleClientId.StartsWith("replace-", StringComparison.OrdinalIgnoreCase);
    var initialPassword = configuration["FamilyAuthentication:BootstrapOwner:InitialPassword"];

    if (!passwordEnabled && !googleEnabled)
      throw new InvalidOperationException("At least one of password or Google sign-in must be configured.");
    if (!passwordEnabled && !string.IsNullOrWhiteSpace(initialPassword))
      throw new InvalidOperationException("A bootstrap password cannot be set while password sign-in is disabled.");
  }

  private static void ConfigurePipeline(WebApplication app)
  {
    if (app.Configuration.GetValue<bool>("ReverseProxy:Enabled"))
    {
      app.UseForwardedHeaders();
    }

    app.UseMiddleware<ClientIpAccessMiddleware>();

    app.UseSwaggerDocumentation();

    if (RequiresHttps(app.Configuration, app.Environment))
    {
      app.UseHsts();
      app.UseHttpsRedirection();
    }
    app.UseSecurityHeaders();
    app.Use(async (context, next) =>
    {
      if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
          !Path.HasExtension(context.Request.Path.Value))
      {
        var route = context.Request.Path.Value?.Trim('/') ?? string.Empty;
        var page = string.IsNullOrEmpty(route) ? "index.html" : $"{route}.html";
        if (app.Environment.WebRootFileProvider.GetFileInfo(page).Exists)
        {
          context.Request.Path = $"/{page}";
        }
      }

      await next();
    });
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseMiddleware<OperationLoggingMiddleware>();
    app.UseMyErrorHandlers();
    // Before authentication: session checks already read through the chosen database route.
    app.UseMiddleware<DatabaseRouteMiddleware>();
    app.UseCors("Frontend");
    app.UseMyAuthentication();
    app.UseMyRateLimiter();

    app.MapHealthChecks("/health");
    app.MapControllers();
  }

  private static bool RequiresHttps(IConfiguration configuration, IHostEnvironment environment) =>
    configuration.GetValue<bool?>("Security:RequireHttps") ?? !environment.IsDevelopment();

  /// <summary>
  /// Runs operator commands before normal startup. Each exits without starting the web server.
  /// </summary>
  private static async Task<bool> TryRunOfflineHostCommandAsync(WebApplication app, string[] args)
  {
    if (args.SequenceEqual(["database", "migrate", "--confirm", "MIGRATE"], StringComparer.Ordinal))
    {
      await MigrateAsync(app);
      return true;
    }

    // One-time move of the previous two-file SQLite installation into a new, empty Turso database.
    // REMOVE-WITH-SQLITE-IMPORT: part of the one-time v1 (SQLite) to v2 (Turso) import. Development was imported on 2026-11-09 and Production on 2026-11-10; delete it once the v1 files are no longer kept (search the repo for this marker).
    if (args.Length >= 2 &&
        string.Equals(args[0], "database", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(args[1], "import-sqlite", StringComparison.OrdinalIgnoreCase))
    {
      var catalogPath = ReadOption(args, "--catalog");
      var usersPath = ReadOption(args, "--users");
      if (string.IsNullOrWhiteSpace(catalogPath) || string.IsNullOrWhiteSpace(usersPath))
        throw new ArgumentException("Import requires --catalog <catalog.db> and --users <users.db>.");
      if (!string.Equals(ReadOption(args, "--confirm"), "IMPORT", StringComparison.Ordinal))
        throw new ArgumentException("Importing requires --confirm IMPORT.");

      await MigrateAsync(app);
      await using var scope = app.Services.CreateAsyncScope();
      var import = ActivatorUtilities.CreateInstance<LegacySqliteImport>(scope.ServiceProvider);
      var copied = await import.ImportAsync(catalogPath, usersPath, CancellationToken.None);
      foreach (var (table, rows) in copied.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        Console.WriteLine($"{table}: {rows}");
      Console.WriteLine("Import completed. Search tables are rebuilt when PhotoDrive starts.");
      return true;
    }

    return false;
  }

  private static async Task MigrateAsync(WebApplication app)
  {
    await using var scope = app.Services.CreateAsyncScope();
    var users = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
    var catalog = scope.ServiceProvider.GetRequiredService<MediaCatalogDbContext>();
    Console.WriteLine("Applying Users database migrations.");
    await users.Database.MigrateAsync();
    Console.WriteLine("Applying Media catalog migrations.");
    await catalog.Database.MigrateAsync();
    Console.WriteLine("Database migrations completed.");
  }

  private static string? ReadOption(string[] args, string option)
  {
    var index = Array.FindIndex(args, value => string.Equals(value, option, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
  }

  /// <summary>
  /// Applies schema changes only when explicitly requested in Development, but always reconciles
  /// security seed data so roles, service identities, and the required first owner remain valid.
  /// </summary>
  private static async Task ApplyDatabaseInitializationAndSeedAsync(WebApplication app)
  {
    var initializationMode = app.Configuration["Startup:DevelopmentDatabaseInitializationMode"];
    await using var scope = app.Services.CreateAsyncScope();
    var mediaCatalogInitializer = scope.ServiceProvider.GetRequiredService<MediaCatalogInitializer>();

    if (app.Environment.IsDevelopment() &&
        string.Equals(initializationMode, "Migrate", StringComparison.OrdinalIgnoreCase))
    {
      await mediaCatalogInitializer.ApplyMigrationsAsync();

      var dbContext = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
      app.Logger.LogInformation("Applying users module migrations.");
      await dbContext.Database.MigrateAsync();
    }

    // Derived search tables are not EF-modeled; create or rebuild them after migrations have been applied.
    await mediaCatalogInitializer.EnsureDerivedSchemaAsync();

    var usersDataSeeder = scope.ServiceProvider.GetRequiredService<UsersDataSeeder>();
    await usersDataSeeder.SeedAsync();
  }

  /// <summary>
  /// Handles offline owner recovery for an operator with host access and exits without starting
  /// the web server. Keeping this command local avoids creating an internet-facing master reset.
  /// </summary>
  private static async Task<bool> TryRunHostCommandAsync(WebApplication app, string[] args)
  {
    if (args.Length != 4 ||
        !string.Equals(args[0], "users", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(args[1], "recover-owner", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(args[2], "--email", StringComparison.OrdinalIgnoreCase))
    {
      return false;
    }

    await using var scope = app.Services.CreateAsyncScope();
    var service = scope.ServiceProvider.GetRequiredService<IUserAdministrationService>();
    var result = await service.IssueHostOwnerRecoveryAsync(args[3], CancellationToken.None);
    if (result.IsFailure)
    {
      Console.Error.WriteLine(result.ErrorValue.Message);
      Environment.ExitCode = 1;
      return true;
    }

    Console.WriteLine("One-time owner password-reset code (store it privately; it expires shortly):");
    Console.WriteLine(result.SuccessValue.Code);
    Console.WriteLine($"Expires at: {result.SuccessValue.ExpiresAt:O}");
    return true;
  }
}

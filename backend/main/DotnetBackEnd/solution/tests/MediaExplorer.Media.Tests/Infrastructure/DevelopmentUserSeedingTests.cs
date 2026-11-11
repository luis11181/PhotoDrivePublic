using CSSApps.Shared.Contracts.Interfaces;
using CSSApps.Users.Application.Services.Auth.Password;
using CSSApps.Users.Application.Settings;
using CSSApps.Users.Contracts.Constants;
using CSSApps.Users.Domain.Entities.UserAggregate;
using CSSApps.Shared.Infrastructure.Data.Turso;
using CSSApps.Users.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class DevelopmentUserSeedingTests
{
    // The same local Turso file engine the application uses (plain SQLite is no longer a dependency).
    private static DbContextOptions<UsersDbContext> UsersDbOptions(string databasePath)
    {
        var database = new TursoDatabase(
            Options.Create(new TursoDatabaseOptions { Url = databasePath }),
            NullLogger<TursoDatabase>.Instance);
        var builder = new DbContextOptionsBuilder<UsersDbContext>();
        database.Configure(builder, DatabaseRouteKind.Cloud, TursoDatabase.UsersHistoryTable);
        return builder.Options;
    }

    [Fact]
    public async Task Development_fixture_is_a_ready_to_use_owner_without_bootstrap_configuration()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"media-explorer-dev-user-{Guid.NewGuid():N}.db");
        try
        {
            await using var dbContext = new UsersDbContext(
                UsersDbOptions(databasePath),
                NullExecutionContextAccessor.Instance);
            await dbContext.Database.EnsureCreatedAsync();

            var environment = Substitute.For<IHostEnvironment>();
            environment.EnvironmentName.Returns(Environments.Development);
            var passwordService = new PasswordService(NullLogger<PasswordService>.Instance);
            var seeder = new UsersDataSeeder(
                dbContext,
                passwordService,
                Options.Create(new FamilyAuthenticationOptions { PasswordLoginEnabled = true }),
                Options.Create(new GoogleOAuthConfig()),
                Options.Create(new DevelopmentUserSeedingOptions
                {
                    Enabled = true,
                    Email = "mediaexplorer.dev@example.test",
                    Password = "PhotoDriveDev!2026",
                    FirstName = "Media Explorer",
                    LastName = "Development",
                    Role = Roles.Owner
                }),
                environment,
                NullLogger<UsersDataSeeder>.Instance);

            await seeder.SeedAsync();

            var user = await dbContext.Users.Include(candidate => candidate.Roles).SingleAsync(
                candidate => candidate.Email == "mediaexplorer.dev@example.test");
            user.AccountStatus.Should().Be(HumanAccountStatus.Active);
            user.AllowedLoginMethods.Should().Be(LoginMethod.Password);
            user.IsEmailVerified.Should().BeTrue();
            user.MustChangePassword.Should().BeFalse();
            user.Roles.Select(role => role.Name).Should().ContainSingle().Which.Should().Be(Roles.Owner);
            passwordService.VerifyPassword("PhotoDriveDev!2026", user.PasswordHash!).SuccessValue
                .Should().BeTrue();
        }
        finally
        {
            DeleteTemporaryDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Development_fixture_configuration_is_ignored_outside_Development()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"media-explorer-prod-user-{Guid.NewGuid():N}.db");
        try
        {
            await using var dbContext = new UsersDbContext(
                UsersDbOptions(databasePath),
                NullExecutionContextAccessor.Instance);
            await dbContext.Database.EnsureCreatedAsync();

            var environment = Substitute.For<IHostEnvironment>();
            environment.EnvironmentName.Returns(Environments.Production);
            var seeder = new UsersDataSeeder(
                dbContext,
                new PasswordService(NullLogger<PasswordService>.Instance),
                Options.Create(new FamilyAuthenticationOptions
                {
                    PasswordLoginEnabled = true,
                    BootstrapOwner = new BootstrapOwnerOptions
                    {
                        Email = "real-owner@example.test",
                        InitialPassword = "RealOwnerBootstrap!2026"
                    }
                }),
                Options.Create(new GoogleOAuthConfig()),
                Options.Create(new DevelopmentUserSeedingOptions
                {
                    Enabled = true,
                    Email = "mediaexplorer.dev@example.test",
                    Password = "PhotoDriveDev!2026",
                    FirstName = "Media Explorer",
                    Role = Roles.Owner
                }),
                environment,
                NullLogger<UsersDataSeeder>.Instance);

            await seeder.SeedAsync();

            (await dbContext.Users.AnyAsync(user => user.Email == "mediaexplorer.dev@example.test"))
                .Should().BeFalse();
            (await dbContext.Users.AnyAsync(user => user.Email == "real-owner@example.test"))
                .Should().BeTrue();
        }
        finally
        {
            DeleteTemporaryDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Development_fixture_does_not_block_a_distinct_configured_bootstrap_owner()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"media-explorer-dev-bootstrap-{Guid.NewGuid():N}.db");
        try
        {
            await using var dbContext = new UsersDbContext(
                UsersDbOptions(databasePath),
                NullExecutionContextAccessor.Instance);
            await dbContext.Database.EnsureCreatedAsync();

            var environment = Substitute.For<IHostEnvironment>();
            environment.EnvironmentName.Returns(Environments.Development);
            var seeder = new UsersDataSeeder(
                dbContext,
                new PasswordService(NullLogger<PasswordService>.Instance),
                Options.Create(new FamilyAuthenticationOptions
                {
                    PasswordLoginEnabled = true,
                    BootstrapOwner = new BootstrapOwnerOptions
                    {
                        Email = "real-owner@example.test"
                    }
                }),
                Options.Create(new GoogleOAuthConfig
                {
                    ClientId = "development-google-client.apps.googleusercontent.com"
                }),
                Options.Create(new DevelopmentUserSeedingOptions
                {
                    Enabled = true,
                    Email = "mediaexplorer.dev@example.test",
                    Password = "PhotoDriveDev!2026",
                    FirstName = "Media Explorer",
                    LastName = "Development",
                    Role = Roles.Owner
                }),
                environment,
                NullLogger<UsersDataSeeder>.Instance);

            await seeder.SeedAsync();

            var owners = await dbContext.Users
                .Include(user => user.Roles)
                .Where(user => user.Roles.Any(role => role.Name == Roles.Owner))
                .OrderBy(user => user.Email)
                .ToListAsync();
            owners.Select(user => user.Email).Should().Equal(
                "mediaexplorer.dev@example.test",
                "real-owner@example.test");
            var bootstrapOwner = owners.Single(user => user.Email == "real-owner@example.test");
            bootstrapOwner.AllowedLoginMethods.Should().HaveFlag(LoginMethod.Google);
            bootstrapOwner.AccountStatus.Should().Be(HumanAccountStatus.Invited);
        }
        finally
        {
            DeleteTemporaryDatabase(databasePath);
        }
    }

    [Fact]
    public async Task An_existing_non_fixture_owner_still_blocks_a_new_bootstrap_owner_in_Development()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"media-explorer-dev-owner-guard-{Guid.NewGuid():N}.db");
        try
        {
            await using var dbContext = new UsersDbContext(
                UsersDbOptions(databasePath),
                NullExecutionContextAccessor.Instance);
            await dbContext.Database.EnsureCreatedAsync();

            var environment = Substitute.For<IHostEnvironment>();
            environment.EnvironmentName.Returns(Environments.Development);
            var developmentOptions = Options.Create(new DevelopmentUserSeedingOptions
            {
                Enabled = true,
                Email = "mediaexplorer.dev@example.test",
                Password = "PhotoDriveDev!2026",
                FirstName = "Media Explorer",
                LastName = "Development",
                Role = Roles.Owner
            });
            var googleOptions = Options.Create(new GoogleOAuthConfig
            {
                ClientId = "development-google-client.apps.googleusercontent.com"
            });

            var firstSeeder = new UsersDataSeeder(
                dbContext,
                new PasswordService(NullLogger<PasswordService>.Instance),
                Options.Create(new FamilyAuthenticationOptions
                {
                    BootstrapOwner = new BootstrapOwnerOptions { Email = "first-owner@example.test" }
                }),
                googleOptions,
                developmentOptions,
                environment,
                NullLogger<UsersDataSeeder>.Instance);
            await firstSeeder.SeedAsync();

            var secondSeeder = new UsersDataSeeder(
                dbContext,
                new PasswordService(NullLogger<PasswordService>.Instance),
                Options.Create(new FamilyAuthenticationOptions
                {
                    BootstrapOwner = new BootstrapOwnerOptions { Email = "second-owner@example.test" }
                }),
                googleOptions,
                developmentOptions,
                environment,
                NullLogger<UsersDataSeeder>.Instance);
            await secondSeeder.SeedAsync();

            (await dbContext.Users.AnyAsync(user => user.Email == "first-owner@example.test"))
                .Should().BeTrue();
            (await dbContext.Users.AnyAsync(user => user.Email == "second-owner@example.test"))
                .Should().BeFalse();
        }
        finally
        {
            DeleteTemporaryDatabase(databasePath);
        }
    }

    private static void DeleteTemporaryDatabase(string databasePath)
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class NullExecutionContextAccessor : IExecutionContextAccessor
    {
        internal static readonly NullExecutionContextAccessor Instance = new();
        public Guid? UserId => null;
        public Guid? CorrelationId => null;
        public bool IsAvailable => false;
    }
}

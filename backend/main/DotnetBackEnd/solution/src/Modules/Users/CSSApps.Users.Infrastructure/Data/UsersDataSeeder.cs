using CSSApps.Users.Application.Services.Auth.Password;
using CSSApps.Users.Application.Settings;
using CSSApps.Users.Contracts.Constants;
using CSSApps.Users.Domain.Entities.UserAggregate;
using CSSApps.Users.Domain.Entities.UserAggregate.ValueObjects;
using CSSApps.Shared.Contracts.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CSSApps.Users.Infrastructure.Data;

public sealed class UsersDataSeeder
{
    private readonly UsersDbContext _dbContext;
    private readonly PasswordService _passwordService;
    private readonly IOptions<FamilyAuthenticationOptions> _familyOptions;
    private readonly IOptions<GoogleOAuthConfig> _googleOptions;
    private readonly IOptions<DevelopmentUserSeedingOptions> _developmentOptions;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<UsersDataSeeder> _logger;

    public UsersDataSeeder(
        UsersDbContext dbContext,
        PasswordService passwordService,
        IOptions<FamilyAuthenticationOptions> familyOptions,
        IOptions<GoogleOAuthConfig> googleOptions,
        IOptions<DevelopmentUserSeedingOptions> developmentOptions,
        IHostEnvironment environment,
        ILogger<UsersDataSeeder> logger)
    {
        _dbContext = dbContext;
        _passwordService = passwordService;
        _familyOptions = familyOptions;
        _googleOptions = googleOptions;
        _developmentOptions = developmentOptions;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// Reconciles required roles and non-human service identities, then establishes either
    /// the Development test fixture or the one-time bootstrap owner when needed.
    /// </summary>
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var roles = await _dbContext.Roles
            .ToDictionaryAsync(role => role.Name, cancellationToken);

        EnsureRequiredRolesExist(roles);

        await EnsureServiceAccountsAsync(roles, cancellationToken);

        await EnsureDevelopmentDefaultUserAsync(roles, cancellationToken);
        await EnsureBootstrapOwnerAsync(roles, cancellationToken);

        if (!_dbContext.ChangeTracker.HasChanges())
        {
            _logger.LogDebug("Users seed completed with no pending changes.");
            return;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Users seed completed successfully.");
    }

    /// <summary>
    /// Keeps a deterministic login available for agent and smoke testing. The environment
    /// check is the primary safety boundary: this fixture must never be created outside Development.
    /// </summary>
    private async Task EnsureDevelopmentDefaultUserAsync(
        Dictionary<string, Role> roles,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment() || !_developmentOptions.Value.Enabled)
        {
            return;
        }

        var options = _developmentOptions.Value;
        var email = RequireValue(options.Email, nameof(options.Email)).Trim().ToLowerInvariant();
        var password = RequireValue(options.Password, nameof(options.Password));
        var firstName = RequireValue(options.FirstName, nameof(options.FirstName));
        var roleName = RequireValue(options.Role, nameof(options.Role)).Trim().ToUpperInvariant();

        if (roleName is not (Roles.Owner or Roles.Member) || !roles.TryGetValue(roleName, out var role))
        {
            throw new InvalidOperationException(
                $"Development fixture role must be {Roles.Owner} or {Roles.Member}.");
        }

        var passwordStrength = PasswordService.IsPasswordStrong(password);
        if (passwordStrength.IsFailure)
        {
            throw new InvalidOperationException(passwordStrength.ErrorValue.Message);
        }

        var user = await _dbContext.Users
            .Include(existingUser => existingUser.Roles)
            .FirstOrDefaultAsync(existingUser =>
                existingUser.Email == email && !existingUser.IsServiceAccount,
                cancellationToken);

        if (user is null)
        {
            var createResult = User.CreateInvited(
                email,
                CreatePersonName(firstName, options.LastName));
            if (createResult.IsFailure)
            {
                throw new InvalidOperationException(createResult.ErrorValue.Message);
            }

            user = createResult.SuccessValue;
            await _dbContext.Users.AddAsync(user, cancellationToken);
        }
        else
        {
            var contactResult = user.UpdateContactInfo(
                name: CreatePersonName(firstName, options.LastName));
            if (contactResult.IsFailure)
            {
                throw new InvalidOperationException(contactResult.ErrorValue.Message);
            }
        }

        var accessResult = user.ConfigureAccess(LoginMethod.Password, HumanAccountStatus.Active);
        if (accessResult.IsFailure)
        {
            throw new InvalidOperationException(accessResult.ErrorValue.Message);
        }

        var roleResult = user.SetHumanRole(role);
        if (roleResult.IsFailure)
        {
            throw new InvalidOperationException(roleResult.ErrorValue.Message);
        }

        user.VerifyEmail();
        user.Activate();

        var passwordMatches = user.HasPassword() &&
                              _passwordService.VerifyPassword(password, user.PasswordHash!).Match(
                                  success => success,
                                  _ => false);
        if (!passwordMatches)
        {
            var setPassword = user.SetPassword(HashPassword(password));
            if (setPassword.IsFailure)
            {
                throw new InvalidOperationException(setPassword.ErrorValue.Message);
            }
        }

        _logger.LogInformation("Ensured the Development-only test owner exists.");
    }

    private static void EnsureRequiredRolesExist(Dictionary<string, Role> roles)
    {
        var missingRoles = UsersSeedData.Roles
            .Where(roleDefinition => !roles.ContainsKey(roleDefinition.Name))
            .Select(roleDefinition => roleDefinition.Name)
            .ToArray();

        if (missingRoles.Length != 0)
        {
            throw new InvalidOperationException(
                $"Missing required seeded roles: {string.Join(", ", missingRoles)}. Apply the EF model migrations for UsersDbContext first.");
        }
    }

    /// <summary>
    /// Reconciles internal identities separately from family accounts. Service identities are
    /// deliberately unable to use human login methods or receive human Owner/Member roles.
    /// </summary>
    private async Task EnsureServiceAccountsAsync(
        Dictionary<string, Role> roles,
        CancellationToken cancellationToken)
    {
        foreach (var account in UsersSeedData.ServiceAccounts)
        {
            var user = await _dbContext.Users
                .Include(existingUser => existingUser.Roles)
                .FirstOrDefaultAsync(existingUser => existingUser.Id == account.Id, cancellationToken);

            if (user is null)
            {
                user = CreateServiceAccount(account);
                await _dbContext.Users.AddAsync(user, cancellationToken);
                _logger.LogInformation("Created service account user {Email}.", account.Email);
            }
            else
            {
                UpdateServiceAccount(user, account);
            }

            foreach (var roleName in account.RoleNames)
            {
                if (!roles.TryGetValue(roleName, out var role))
                {
                    throw new InvalidOperationException($"Required role '{roleName}' was not found.");
                }

                user.AssignRole(role);
            }
        }
    }

    /// <summary>
    /// Creates or promotes the configured bootstrap account only while no active human owner exists.
    /// After ownership is established, later configuration changes cannot create or promote another owner.
    /// </summary>
    private async Task EnsureBootstrapOwnerAsync(
        Dictionary<string, Role> roles,
        CancellationToken cancellationToken)
    {
        var options = _familyOptions.Value;
        var email = options.BootstrapOwner.Email?.Trim().ToLowerInvariant();
        var configuredDevelopmentFixtureEmail =
            _developmentOptions.Value.Email?.Trim().ToLowerInvariant();
        var developmentFixtureEmail =
            _environment.IsDevelopment() &&
            _developmentOptions.Value.Enabled &&
            !string.IsNullOrWhiteSpace(email) &&
            !string.Equals(configuredDevelopmentFixtureEmail, email, StringComparison.Ordinal)
                ? configuredDevelopmentFixtureEmail
                : null;

        // A distinct configured bootstrap owner should still be established in Development.
        // The deterministic fixture exists for testing and must not consume that one-time slot.
        var hasTrackedOwner = _dbContext.Users.Local.Any(user =>
            !user.IsServiceAccount &&
            user.AccountStatus != HumanAccountStatus.Disabled &&
            user.Roles.Any(role => role.Name == Roles.Owner) &&
            (developmentFixtureEmail is null || user.Email != developmentFixtureEmail));
        var persistedOwners = _dbContext.Users.Where(user =>
            !user.IsServiceAccount &&
            user.AccountStatus != HumanAccountStatus.Disabled &&
            user.Roles.Any(role => role.Name == Roles.Owner));
        if (developmentFixtureEmail is not null)
        {
            persistedOwners = persistedOwners.Where(user => user.Email != developmentFixtureEmail);
        }

        if (hasTrackedOwner || await persistedOwners.AnyAsync(cancellationToken))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException(
                $"{FamilyAuthenticationOptions.SectionName}:BootstrapOwner:Email is required until the first owner exists.");
        if (!_googleOptions.Value.IsConfigured &&
            string.IsNullOrWhiteSpace(options.BootstrapOwner.InitialPassword))
            throw new InvalidOperationException(
                "A one-time bootstrap owner password is required when Google is not configured and no owner exists.");
        if (!roles.TryGetValue(Roles.Owner, out var role))
        {
            throw new InvalidOperationException("The required Owner role was not found.");
        }

        var user = await _dbContext.Users
            .Include(existingUser => existingUser.Roles)
            .FirstOrDefaultAsync(existingUser => existingUser.Email == email, cancellationToken);

        if (user is null)
        {
            var createResult = User.CreateInvited(
                email,
                new PersonName { FirstName = email.Split('@')[0] });
            if (createResult.IsFailure) throw new InvalidOperationException(createResult.ErrorValue.Message);
            user = createResult.SuccessValue;
            await _dbContext.Users.AddAsync(user, cancellationToken);
        }

        var needsBootstrapPassword =
            !PasswordService.IsSupportedHash(user.PasswordHash) ||
            user.SecurityStamp == Guid.Empty;
        var methods = LoginMethod.None;
        if (_googleOptions.Value.IsConfigured) methods |= LoginMethod.Google;
        if (options.PasswordLoginEnabled) methods |= LoginMethod.Password;
        var accessResult = user.ConfigureAccess(methods, HumanAccountStatus.Invited);
        if (accessResult.IsFailure) throw new InvalidOperationException(accessResult.ErrorValue.Message);
        var roleResult = user.SetHumanRole(role);
        if (roleResult.IsFailure) throw new InvalidOperationException(roleResult.ErrorValue.Message);

        if (!string.IsNullOrWhiteSpace(options.BootstrapOwner.InitialPassword) && needsBootstrapPassword)
        {
            var strength = PasswordService.IsPasswordStrong(options.BootstrapOwner.InitialPassword);
            if (strength.IsFailure) throw new InvalidOperationException(strength.ErrorValue.Message);
            var initialResult = user.SetInitialPassword(HashPassword(options.BootstrapOwner.InitialPassword));
            if (initialResult.IsFailure) throw new InvalidOperationException(initialResult.ErrorValue.Message);
        }

        _logger.LogInformation("Ensured the configured bootstrap owner account exists.");
    }

    private User CreateServiceAccount(UsersSeedData.ServiceAccountSeedDefinition account)
    {
        var createResult = User.CreateWithPassword(
            account.Email,
            HashPassword(Guid.NewGuid().ToString("N")),
            CreatePersonName(account.FirstName, account.LastName),
            account.Id);

        if (createResult.IsFailure)
        {
            throw new InvalidOperationException(createResult.ErrorValue.Message);
        }

        var user = createResult.SuccessValue;
        user.VerifyEmail();
        user.CreatedBy = UsersSeedData.SeededBy;
        user.CreatedDate = UsersSeedData.SeededAtUtc;
        user.UpdatedBy = UsersSeedData.SeededBy;
        user.UpdatedDate = UsersSeedData.SeededAtUtc;
        user.Status = CommonConstants.EntityStatusType.Active;
        user.MarkServiceAccount();
        return user;
    }

    private static void UpdateServiceAccount(User user, UsersSeedData.ServiceAccountSeedDefinition account)
    {
        var updateContactInfoResult = user.UpdateContactInfo(
            email: account.Email,
            name: CreatePersonName(account.FirstName, account.LastName));

        if (updateContactInfoResult.IsFailure)
        {
            throw new InvalidOperationException(updateContactInfoResult.ErrorValue.Message);
        }

        if (!user.IsEmailVerified)
        {
            user.VerifyEmail();
        }

        user.UpdatedBy = UsersSeedData.SeededBy;
        user.UpdatedDate = UsersSeedData.SeededAtUtc;
        user.Status = CommonConstants.EntityStatusType.Active;
        user.MarkServiceAccount();
    }

    private string HashPassword(string password)
    {
        var hashPasswordResult = _passwordService.HashPassword(password);
        if (hashPasswordResult.IsFailure)
        {
            throw new InvalidOperationException(hashPasswordResult.ErrorValue.Message);
        }

        return hashPasswordResult.SuccessValue;
    }

    private static PersonName CreatePersonName(string firstName, string? lastName)
    {
        return new PersonName
        {
            FirstName = firstName.Trim(),
            LastName = NormalizeOptionalValue(lastName)
        };
    }

    private static string? NormalizeOptionalValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string RequireValue(string? value, string propertyName)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidOperationException(
            $"{DevelopmentUserSeedingOptions.SectionName}:{propertyName} must be configured when the Development fixture is enabled.");
    }

}

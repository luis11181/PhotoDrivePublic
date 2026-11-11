using System.ComponentModel.DataAnnotations;
using CSSApps.Shared.Contracts.Constants;
using CSSApps.Shared.Contracts.DomainCommons.Entities;
using CSSApps.Shared.Contracts.DomainCommons.Primitives;
using CSSApps.Shared.Contracts.Primitives;
using CSSApps.Shared.Contracts.ValueObjects;
using CSSApps.Users.Domain.Entities.UserAggregate.ValueObjects;

namespace CSSApps.Users.Domain.Entities.UserAggregate;

public class User : AuditableEntity, IAggregateRoot
{
    private readonly List<Role> _roles = [];

    protected User()
    {
    } // EF Core uses to initialize instance and populate with reflection

    private User(string email, PersonName name, Guid? id = null) : this() // Private constructor uses base
    {
        Id = id ?? Guid.CreateVersion7();
        Email = NormalizeEmail(email);
        Name = name;
        SecurityStamp = Guid.NewGuid();
        AccountStatus = HumanAccountStatus.Invited;
        AllowedLoginMethods = LoginMethod.None;
    }

    public Guid Id { get; private set; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1721:Property names should not match get methods", Justification = "Domain property")]
    public string? OAuthProviderId { get; private set; } // Stores the OAuth provider ID (Google, Facebook, etc.)
    public string? OAuthProvider { get; private set; } // Stores which OAuth provider was used for registration
    public string? PasswordHash { get; private set; } // For local password authentication
    public HumanAccountStatus AccountStatus { get; private set; }
    public LoginMethod AllowedLoginMethods { get; private set; }
    public bool MustChangePassword { get; private set; }
    public bool IsServiceAccount { get; private set; }
    public Guid SecurityStamp { get; private set; }
   
    public string Email { get; private set; } = null!;

    public PersonName Name { get; private set; } = null!;

    //public DocumentInfo? Document { get; private set; }
    public PhoneNumber? Phone { get; private set; }

    //public Address? Address { get; private set; }
    public DateTime? DateOfBirth { get; private set; }
    public string? Nationality { get; private set; }
    public string? PreferredLanguage { get; private set; }
    public string? TimeZone { get; private set; }
    public bool IsEmailVerified { get; private set; }
    public bool IsPhoneVerified { get; private set; }
    public IReadOnlyCollection<Role> Roles => _roles.AsReadOnly();

    public static Result<User> CreateFromOAuth(
          string provider,
          string providerId,
          string email,
          PersonName name,
          Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return new ResultError("OAuth provider cannot be empty");

        if (string.IsNullOrWhiteSpace(providerId))
            return new ResultError("Provider ID cannot be empty");

        if (string.IsNullOrWhiteSpace(email))
            return new ResultError("Email cannot be empty");

        if (name is null)
            return new ResultError("Name is required");

        var user = new User(email, name, id);
        user.OAuthProvider = provider;
        user.OAuthProviderId = providerId;
        user.AllowedLoginMethods = LoginMethod.Google;

        return user;
    }

    public static Result<User> CreateWithPassword(
          string email,
          string passwordHash,
          PersonName name,
          Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(email))
            return new ResultError("Email cannot be empty");

        if (string.IsNullOrWhiteSpace(passwordHash))
            return new ResultError("Password hash cannot be empty");

        if (name is null)
            return new ResultError("Name is required");

        var user = new User(email, name, id);
        user.PasswordHash = passwordHash;
        user.AllowedLoginMethods = LoginMethod.Password;

        return user;
    }

    public static Result<User> CreateInvited(string email, PersonName name, Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(email)) return new ResultError("Email cannot be empty");
        if (name is null) return new ResultError("Name is required");
        return new User(email, name, id);
    }
    public Result<User> UpdateContactInfo(
        string? email = null,
        PersonName? name = null,
        PhoneNumber? phone = null,
        Address? address = null)
    {
        if (email != null)
        {
            if (string.IsNullOrWhiteSpace(email))
                return new ResultError("Email is required");
            Email = NormalizeEmail(email);
        }

        if (name != null)
            Name = name;

        if (phone != null)
            Phone = phone;

        // if (address != null)
        //     Address = address;

        return this;
    }

    public Result<User> UpdatePersonalInfo(
        DateTime? dateOfBirth = null,
        string? nationality = null,
        string? preferredLanguage = null,
        string? timeZone = null)
    {
        if (dateOfBirth != null)
        {
            if (dateOfBirth.Value > DateTime.UtcNow)
                return new ResultError("Date of birth cannot be in the future");
            DateOfBirth = dateOfBirth;
        }

        if (nationality != null)
            Nationality = nationality;

        if (preferredLanguage != null)
            PreferredLanguage = preferredLanguage;

        if (timeZone != null)
            TimeZone = timeZone;

        return this;
    }

    public void AssignRole(Role role)
    {
        if (!_roles.Contains(role))
        {
            _roles.Add(role);
        }
    }

    /// <summary>
    /// Replaces the single human role and rotates the security stamp so role changes invalidate
    /// already-issued access tokens.
    /// </summary>
    public Result SetHumanRole(Role role)
    {
        if (IsServiceAccount) return new ResultError("Service accounts cannot have human roles");
        _roles.Clear();
        _roles.Add(role);
        RotateSecurityStamp();
        return Result.Success();
    }

    public string? GetOAuthProviderId(string provider)
    {
        return provider == OAuthProvider ? OAuthProviderId : null;
    }

    public Result SetPassword(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
            return new ResultError("Password hash cannot be empty");
        
        PasswordHash = passwordHash;
        MustChangePassword = false;
        RotateSecurityStamp();
        return Result.Success();
    }

    /// <summary>
    /// Applies the owner's allowlist choices for this account. Changing either status or login
    /// methods rotates the security stamp so the new policy affects existing sessions.
    /// </summary>
    public Result ConfigureAccess(LoginMethod methods, HumanAccountStatus status)
    {
        if (IsServiceAccount) return new ResultError("Service account access is managed separately");
        if (methods == LoginMethod.None) return new ResultError("At least one sign-in method is required");
        AllowedLoginMethods = methods;
        AccountStatus = status;
        RotateSecurityStamp();
        return Result.Success();
    }

    /// <summary>
    /// Stores a one-time bootstrap password and marks the account as restricted until the user
    /// replaces it with a password of their own.
    /// </summary>
    public Result SetInitialPassword(string passwordHash)
    {
        if (!AllowedLoginMethods.HasFlag(LoginMethod.Password))
            return new ResultError("Password sign-in is not allowed for this account");
        if (string.IsNullOrWhiteSpace(passwordHash)) return new ResultError("Password hash cannot be empty");
        PasswordHash = passwordHash;
        MustChangePassword = true;
        return Result.Success();
    }

    public void RemovePassword()
    {
        PasswordHash = null;
        MustChangePassword = false;
        RotateSecurityStamp();
    }

    /// <summary>
    /// Removes the bootstrap credential immediately after its first successful use. The
    /// MustChangePassword flag remains set until the replacement password is saved.
    /// </summary>
    public Result ConsumeInitialPassword()
    {
        if (!MustChangePassword || string.IsNullOrWhiteSpace(PasswordHash))
            return new ResultError("No initial password is pending");
        PasswordHash = null;
        RotateSecurityStamp();
        return Result.Success();
    }

    public void Activate()
    {
        if (!IsServiceAccount && AccountStatus == HumanAccountStatus.Invited)
            AccountStatus = HumanAccountStatus.Active;
    }

    /// <summary>
    /// Permanently separates an internal identity from human authentication and authorization.
    /// </summary>
    public void MarkServiceAccount()
    {
        IsServiceAccount = true;
        AccountStatus = HumanAccountStatus.Disabled;
        AllowedLoginMethods = LoginMethod.None;
        _roles.Clear();
        RotateSecurityStamp();
    }

    /// <summary>
    /// Rotates the value embedded in access tokens; request-time validation rejects every token
    /// carrying the previous stamp.
    /// </summary>
    public void RevokeSessions() => RotateSecurityStamp();

    public bool CanUse(LoginMethod method) =>
        !IsServiceAccount &&
        AccountStatus != HumanAccountStatus.Disabled &&
        AllowedLoginMethods.HasFlag(method);

    public bool HasPassword()
    {
        return !string.IsNullOrEmpty(PasswordHash);
    }

    public bool CanLoginWithPassword()
    {
        return HasPassword() && IsEmailVerified;
    }

    public bool IsOAuthUser()
    {
        return !string.IsNullOrEmpty(OAuthProvider) && !string.IsNullOrEmpty(OAuthProviderId);
    }

    /// <summary>
    /// Links an OAuth provider to an existing user account.
    /// This enables unified login - same account accessible via email/password OR OAuth.
    /// </summary>
    public Result LinkOAuthProvider(string provider, string providerId)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return new ResultError("OAuth provider cannot be empty");

        if (string.IsNullOrWhiteSpace(providerId))
            return new ResultError("Provider ID cannot be empty");

        // If already linked to the same provider, verify it's the same account
        if (IsOAuthUser())
        {
            if (OAuthProvider == provider && OAuthProviderId == providerId)
                return Result.Success(); // Already linked to this exact account
            
            if (OAuthProvider == provider)
                return new ResultError($"Account is already linked to a different {provider} account");
            
            // Note: If you want to support multiple OAuth providers per user in the future,
            // you'd need a separate OAuthLink entity. For now, one provider per user.
            return new ResultError($"Account is already linked to {OAuthProvider}");
        }

        OAuthProvider = provider;
        OAuthProviderId = providerId;
        RotateSecurityStamp();
        
        return Result.Success();
    }

    /// <summary>
    /// Verifies the user's email address.
    /// Used when a user signs in with a verified OAuth provider like Google.
    /// </summary>
    public void VerifyEmail()
    {
        IsEmailVerified = true;
    }

    private void RotateSecurityStamp() => SecurityStamp = Guid.NewGuid();

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

}

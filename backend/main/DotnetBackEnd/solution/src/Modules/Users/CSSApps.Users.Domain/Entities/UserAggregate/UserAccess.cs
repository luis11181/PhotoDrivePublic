namespace CSSApps.Users.Domain.Entities.UserAggregate;

public enum HumanAccountStatus
{
    Invited,
    Active,
    Disabled
}

[Flags]
public enum LoginMethod
{
    None = 0,
    Google = 1,
    Password = 2
}

public enum SecurityTokenPurpose
{
    PasswordSetup,
    PasswordReset,
    HostOwnerRecovery
}

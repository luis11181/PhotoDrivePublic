namespace CSSApps.Users.Contracts.Constants;

public abstract class Roles
{
    public const string Owner = "OWNER";
    public const string Member = "MEMBER";

    // Legacy constants remain only for the copied integration-test project. Product
    // authorization and fresh databases use the human Owner/Member roles above.
    public const string SuperAdmin = "SUPER_ADMIN";
    public const string Admin = "ADMIN";
    public const string FreeUser = "FREE_USER";
    public const string PaidUser = "PAID_USER";
}

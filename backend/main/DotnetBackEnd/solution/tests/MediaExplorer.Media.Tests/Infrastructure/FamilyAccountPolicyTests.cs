using CSSApps.Users.Contracts.Constants;
using CSSApps.Users.Domain.Entities.UserAggregate;
using CSSApps.Users.Domain.Entities.UserAggregate.ValueObjects;
using FluentAssertions;
using CSSApps.Users.Application.Services.Auth.Password;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class FamilyAccountPolicyTests
{
    [Fact]
    public void Invited_account_allows_only_owner_selected_sign_in_methods()
    {
        var result = User.CreateInvited(" Family@Example.COM ", new PersonName { FirstName = "Family" });
        result.IsSuccess.Should().BeTrue();
        var user = result.SuccessValue;

        user.ConfigureAccess(LoginMethod.Google, HumanAccountStatus.Invited).IsSuccess.Should().BeTrue();

        user.Email.Should().Be("family@example.com");
        user.CanUse(LoginMethod.Google).Should().BeTrue();
        user.CanUse(LoginMethod.Password).Should().BeFalse();
    }

    [Fact]
    public void Service_identity_cannot_receive_a_human_owner_role()
    {
        var user = User.CreateInvited("service@example.test", new PersonName { FirstName = "Service" }).SuccessValue;
        var owner = Role.Create(Roles.Owner).SuccessValue;
        user.MarkServiceAccount();

        var result = user.SetHumanRole(owner);

        result.IsFailure.Should().BeTrue();
        user.Roles.Should().BeEmpty();
        user.CanUse(LoginMethod.Google).Should().BeFalse();
        user.CanUse(LoginMethod.Password).Should().BeFalse();
    }

    [Fact]
    public void Initial_password_is_consumed_and_requires_a_permanent_replacement()
    {
        var user = User.CreateInvited("owner@example.test", new PersonName { FirstName = "Owner" }).SuccessValue;
        user.ConfigureAccess(LoginMethod.Password, HumanAccountStatus.Invited);
        user.SetInitialPassword("stored-hash").IsSuccess.Should().BeTrue();

        user.ConsumeInitialPassword().IsSuccess.Should().BeTrue();

        user.HasPassword().Should().BeFalse();
        user.MustChangePassword.Should().BeTrue();
    }

    [Fact]
    public void Security_token_is_single_use()
    {
        var token = SecurityToken.Create(
            Guid.NewGuid(),
            SecurityTokenPurpose.PasswordReset,
            "hash",
            expiresAt: DateTime.UtcNow.AddMinutes(30)).SuccessValue;

        token.Consume(DateTime.UtcNow).IsSuccess.Should().BeTrue();
        token.Consume(DateTime.UtcNow).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Password_hash_is_versioned_and_verifies_in_constant_time_path()
    {
        var service = new PasswordService(NullLogger<PasswordService>.Instance);

        var hash = service.HashPassword("StrongFamily!Password2026").SuccessValue;

        hash.Should().StartWith("pbkdf2-sha256$600000$");
        PasswordService.IsSupportedHash(hash).Should().BeTrue();
        PasswordService.IsSupportedHash("obsolete-pre-release-hash").Should().BeFalse();
        service.VerifyPassword("StrongFamily!Password2026", hash).SuccessValue.Should().BeTrue();
        service.VerifyPassword("WrongFamily!Password2026", hash).SuccessValue.Should().BeFalse();
    }
}

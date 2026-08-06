using CSSApps.Api.DependencyInjection.RateLimiting;
using CSSApps.Users.Api.Controllers.v1;
using FluentAssertions;
using Microsoft.AspNetCore.RateLimiting;

namespace MediaExplorer.Media.Tests.Api;

public sealed class RateLimitingPolicyTests
{
    [Fact]
    public void General_defaults_allow_large_authenticated_media_bursts_without_queuing()
    {
        var options = new RateLimitingOptions();

        options.Authenticated.PermitLimit.Should().Be(10000);
        options.Authenticated.WindowSeconds.Should().Be(60);
        options.Authenticated.QueueLimit.Should().Be(0);
        options.Global.PermitLimit.Should().Be(100000);
        options.Global.QueueLimit.Should().Be(0);
    }

    [Fact]
    public void Strict_authentication_defaults_remain_small_and_do_not_queue()
    {
        var options = new RateLimitingOptions();

        options.StrictAuthentication.PermitLimit.Should().Be(5);
        options.StrictAuthentication.WindowSeconds.Should().Be(60);
        options.StrictAuthentication.QueueLimit.Should().Be(0);
    }

    [Theory]
    [InlineData(typeof(UsersAuthLoginController), nameof(UsersAuthLoginController.Login))]
    [InlineData(typeof(UsersAuthLoginController), nameof(UsersAuthLoginController.GoogleLogin))]
    [InlineData(typeof(UsersAdministrationController), nameof(UsersAdministrationController.ResetPassword))]
    [InlineData(typeof(UsersAuthController), nameof(UsersAuthController.RefreshToken))]
    public void Anonymous_credential_and_token_operations_use_the_strict_policy(
        Type controller,
        string action)
    {
        var attribute = controller.GetMethod(action)!
            .GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true)
            .Cast<EnableRateLimitingAttribute>()
            .Single();

        attribute.PolicyName.Should().Be("StrictPolicy");
    }
}

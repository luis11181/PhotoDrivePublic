using System.Net;
using System.Text;
using CSSApps.Api.AccessControl;
using CSSApps.Shared.Utils.ApiHelpers;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class ClientIpAccessControlTests
{
    [Theory]
    [InlineData("198.51.100.42", true)]
    [InlineData("10.24.8.9", true)]
    [InlineData("10.255.255.255", true)]
    [InlineData("11.0.0.1", false)]
    [InlineData("2001:db8:abcd::5", true)]
    [InlineData("2001:db9::5", false)]
    public void Multiple_exact_and_cidr_entries_are_applied(string address, bool expected)
    {
        var policy = Policy(new Dictionary<string, string?>
        {
            ["AccessControl:IpAllowlist:Enabled"] = "true",
            ["AccessControl:IpAllowlist:AllowedNetworks"] = "198.51.100.42, 10.0.0.0/8; 2001:db8::/32"
        });

        policy.IsAllowed(IPAddress.Parse(address)).Should().Be(expected);
    }

    [Fact]
    public async Task Caller_supplied_forwarding_header_cannot_bypass_the_canonical_connection_address()
    {
        var policy = Policy(new Dictionary<string, string?>
        {
            ["AccessControl:IpAllowlist:Enabled"] = "true",
            ["AccessControl:IpAllowlist:AllowedNetworks"] = "198.51.100.42"
        });
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.42";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        var called = false;
        var middleware = new ClientIpAccessMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            policy,
            NullLogger<ClientIpAccessMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        called.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Canonical_connection_address_is_used_by_access_control_and_rate_limiting()
    {
        var policy = Policy(new Dictionary<string, string?>
        {
            ["AccessControl:IpAllowlist:Enabled"] = "true",
            ["AccessControl:IpAllowlist:AllowedNetworks"] = "198.51.100.0/24"
        });
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.42");
        var called = false;
        var middleware = new ClientIpAccessMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            policy,
            NullLogger<ClientIpAccessMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("198.51.100.42"));
        RequestInfo.GetClientIpAddress(context).Should().Be("198.51.100.42");
    }

    [Fact]
    public async Task Disabled_allowlist_permits_the_request_without_trusting_a_caller_header()
    {
        var policy = Policy(new Dictionary<string, string?>
        {
            ["AccessControl:IpAllowlist:Enabled"] = "false"
        });
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.42";
        context.Connection.RemoteIpAddress = IPAddress.Parse("172.20.0.4");
        var called = false;
        var middleware = new ClientIpAccessMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            policy,
            NullLogger<ClientIpAccessMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("172.20.0.4"));
    }

    [Fact]
    public async Task Health_bypass_is_limited_to_a_loopback_caller()
    {
        var policy = Policy(new Dictionary<string, string?>
        {
            ["AccessControl:IpAllowlist:Enabled"] = "true",
            ["AccessControl:IpAllowlist:AllowedNetworks"] = "198.51.100.42"
        });
        var external = new DefaultHttpContext();
        external.Response.Body = new MemoryStream();
        external.Request.Path = "/health";
        external.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        var externalCalled = false;
        var middleware = new ClientIpAccessMiddleware(
            _ => { externalCalled = true; return Task.CompletedTask; },
            policy,
            NullLogger<ClientIpAccessMiddleware>.Instance);

        await middleware.InvokeAsync(external);

        externalCalled.Should().BeFalse();
        external.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

        var local = new DefaultHttpContext();
        local.Request.Path = "/health";
        local.Connection.RemoteIpAddress = IPAddress.Loopback;
        var localCalled = false;
        middleware = new ClientIpAccessMiddleware(
            _ => { localCalled = true; return Task.CompletedTask; },
            policy,
            NullLogger<ClientIpAccessMiddleware>.Instance);

        await middleware.InvokeAsync(local);

        localCalled.Should().BeTrue();
    }

    [Fact]
    public void Enabled_reverse_proxy_requires_explicit_trusted_peers()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:Enabled"] = "true"
        }).Build();

        TrustedProxyConfiguration.FindConfigurationErrors(configuration)
            .Should().ContainSingle(error => error.Contains("KnownProxies", StringComparison.Ordinal));
    }

    [Fact]
    public void Loopback_proxies_and_non_global_networks_are_valid()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:Enabled"] = "true",
            ["ReverseProxy:KnownProxies:0"] = "127.0.0.1",
            ["ReverseProxy:KnownProxies:1"] = "::1",
            ["ReverseProxy:KnownNetworks:0"] = "172.30.0.0/24"
        }).Build();

        TrustedProxyConfiguration.FindConfigurationErrors(configuration).Should().BeEmpty();
    }

    [Fact]
    public async Task Trusted_proxy_consumes_only_the_rightmost_forwarded_address()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:Enabled"] = "true",
            ["ReverseProxy:KnownProxies:0"] = "127.0.0.1"
        }).Build();
        var options = new ForwardedHeadersOptions();
        TrustedProxyConfiguration.Apply(configuration, options);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.42, 203.0.113.7";
        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Options.Create(options));

        await middleware.Invoke(context);

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("203.0.113.7"));
    }

    [Fact]
    public async Task Untrusted_peer_cannot_change_its_address_with_a_forwarded_header()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:Enabled"] = "true",
            ["ReverseProxy:KnownProxies:0"] = "127.0.0.1"
        }).Build();
        var options = new ForwardedHeadersOptions();
        TrustedProxyConfiguration.Apply(configuration, options);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.42";
        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Options.Create(options));

        await middleware.Invoke(context);

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("203.0.113.7"));
    }

    [Fact]
    public void Higher_priority_json_array_replaces_a_lower_priority_scalar_list()
    {
        using var deploymentJson = new MemoryStream(Encoding.UTF8.GetBytes("""
            {
              "AccessControl": {
                "IpAllowlist": {
                  "AllowedNetworks": [ "203.0.113.7" ]
                }
              }
            }
            """));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AccessControl:IpAllowlist:Enabled"] = "true",
                ["AccessControl:IpAllowlist:AllowedNetworks"] = "198.51.100.42"
            })
            .AddJsonStream(deploymentJson)
            .Build();
        var policy = new ClientIpAccessPolicy(configuration, defaultEnabled: true);

        policy.IsAllowed(IPAddress.Parse("203.0.113.7")).Should().BeTrue();
        policy.IsAllowed(IPAddress.Parse("198.51.100.42")).Should().BeFalse();
    }

    private static ClientIpAccessPolicy Policy(Dictionary<string, string?> values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), defaultEnabled: true);
}

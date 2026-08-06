using System.Security.Claims;
using CSSApps.Shared.Contracts.APICommons;
using CSSApps.Shared.Contracts.Primitives;
using CSSApps.Shared.Utils.ApiHelpers.Filters;
using FluentAssertions;
using MediaExplorer.Media.Api.Controllers.v1;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using RequireCsrfToken = CSSApps.Shared.Utils.ApiHelpers.Filters.RequireAntiforgeryTokenAttribute;

namespace MediaExplorer.Media.Tests.Api;

public sealed class MediaEndpointPolicyTests
{
    [Fact]
    public void Developer_maintenance_requires_the_human_owner_policy()
    {
        var authorize = typeof(DeveloperMaintenanceController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();

        authorize.Policy.Should().Be("Owner");
    }

    [Fact]
    public void Cache_builds_require_the_human_owner_policy()
    {
        var authorize = typeof(CacheBuildsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();

        authorize.Policy.Should().Be("Owner");
    }

    [Fact]
    public void Imports_require_authentication_without_requiring_the_owner_policy()
    {
        var authorize = typeof(ImportsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();

        authorize.Policy.Should().BeNull();
    }

    [Fact]
    public void Batch_and_tag_mutations_use_the_api_only_antiforgery_filter()
    {
        var mutations = new[]
        {
            typeof(ItemsController).GetMethod(nameof(ItemsController.Batch))!,
            typeof(ItemsController).GetMethod(nameof(ItemsController.SetCaptureDate))!,
            typeof(TagsController).GetMethod(nameof(TagsController.Create))!,
            typeof(TagsController).GetMethod(nameof(TagsController.Update))!,
            typeof(TagsController).GetMethod(nameof(TagsController.Delete))!,
            typeof(TagsController).GetMethod(nameof(TagsController.SetItems))!,
            typeof(TimelineEventsController).GetMethod(nameof(TimelineEventsController.Create))!,
            typeof(TimelineEventsController).GetMethod(nameof(TimelineEventsController.Update))!,
            typeof(TimelineEventsController).GetMethod(nameof(TimelineEventsController.Delete))!
        };

        mutations.Should().OnlyContain(method =>
            method.GetCustomAttributes(typeof(RequireCsrfToken), inherit: true).Length == 1);
    }

    [Fact]
    public async Task Query_returns_unauthorized_when_the_authenticated_identity_has_no_user_id()
    {
        var queries = Substitute.For<ICatalogQueryService>();
        var commands = Substitute.For<ICatalogCommandService>();
        var batches = Substitute.For<ICatalogBatchService>();
        var delivery = Substitute.For<IMediaDeliveryService>();
        var controller = new ItemsController(queries, commands, batches, delivery)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.Query(null, "children", null, false, false, "all", "name_asc", null, 100, null, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
        await queries.DidNotReceive().QueryFilesAsync(Arg.Any<Guid>(), Arg.Any<FileQueryOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Details_maps_a_not_found_result_without_controller_exception_flow()
    {
        var queries = Substitute.For<ICatalogQueryService>();
        var commands = Substitute.For<ICatalogCommandService>();
        var batches = Substitute.For<ICatalogBatchService>();
        var delivery = Substitute.For<IMediaDeliveryService>();
        var itemId = Guid.NewGuid();
        queries.GetItemAsync(Arg.Any<Guid>(), itemId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<MediaItemRes>.Failure(MediaErrors.NotFound("Missing item."))));
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
                "test"))
        };
        var controller = new ItemsController(queries, commands, batches, delivery)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var result = await controller.Details(itemId, CancellationToken.None);

        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        var envelope = notFound.Value.Should().BeOfType<ApiRes>().Subject;
        envelope.Successful.Should().BeFalse();
        envelope.Error!.Status.Should().Be(StatusCodes.Status404NotFound);
        envelope.Error.Detail.Should().Be("Missing item.");
    }

    [Fact]
    public async Task Content_wraps_an_invalid_range_in_the_shared_api_envelope()
    {
        var queries = Substitute.For<ICatalogQueryService>();
        var commands = Substitute.For<ICatalogCommandService>();
        var batches = Substitute.For<ICatalogBatchService>();
        var delivery = Substitute.For<IMediaDeliveryService>();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
                "test"))
        };
        context.Request.Headers.Range = "bytes=invalid";
        var controller = new ItemsController(queries, commands, batches, delivery)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var result = await controller.Content(Guid.NewGuid(), CancellationToken.None);

        var response = result.Should().BeOfType<ObjectResult>().Subject;
        response.StatusCode.Should().Be(StatusCodes.Status416RangeNotSatisfiable);
        var envelope = response.Value.Should().BeOfType<ApiRes>().Subject;
        envelope.Successful.Should().BeFalse();
        envelope.Error!.Status.Should().Be(StatusCodes.Status416RangeNotSatisfiable);
        await delivery.DidNotReceive().OpenContentAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Content_head_uses_catalog_metadata_without_opening_original_bytes()
    {
        var queries = Substitute.For<ICatalogQueryService>();
        var commands = Substitute.For<ICatalogCommandService>();
        var batches = Substitute.For<ICatalogBatchService>();
        var delivery = Substitute.For<IMediaDeliveryService>();
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var modified = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);
        var info = new MediaBinaryInfo("video/mp4", 400_000_000, modified, new string('a', 64));
        delivery.GetContentInfoAsync(userId, itemId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<MediaBinaryInfo>.Success(info)));
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                "test"))
        };
        context.Request.Method = HttpMethods.Head;
        context.Request.Headers.Range = "bytes=100-199";
        var controller = new ItemsController(queries, commands, batches, delivery)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var result = await controller.Content(itemId, default);

        result.Should().BeOfType<EmptyResult>();
        context.Response.StatusCode.Should().Be(StatusCodes.Status206PartialContent);
        context.Response.ContentLength.Should().Be(100);
        context.Response.Headers.ContentRange.ToString().Should().Be("bytes 100-199/400000000");
        context.Response.Headers.AcceptRanges.ToString().Should().Be("bytes");
        await delivery.Received(1).GetContentInfoAsync(userId, itemId, Arg.Any<CancellationToken>());
        await delivery.DidNotReceive().OpenContentAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Download_writes_the_prepared_attachment_without_an_api_envelope()
    {
        var queries = Substitute.For<ICatalogQueryService>();
        var commands = Substitute.For<ICatalogCommandService>();
        var batches = Substitute.For<ICatalogBatchService>();
        var delivery = Substitute.For<IMediaDeliveryService>();
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var bytes = "download bytes"u8.ToArray();
        var download = new MediaDownloadContent(
            "family photo.jpg",
            "image/jpeg",
            bytes.Length,
            fileCount: 1,
            totalBytes: bytes.Length,
            (destination, cancellationToken) => destination.WriteAsync(bytes, cancellationToken).AsTask());
        delivery.PrepareDownloadAsync(userId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Result<MediaDownloadContent>.Success(download));
        var body = new MemoryStream();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                "test"))
        };
        context.Response.Body = body;
        var controller = new ItemsController(queries, commands, batches, delivery)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var result = await controller.Download([itemId], CancellationToken.None);

        result.Should().BeOfType<EmptyResult>();
        body.ToArray().Should().Equal(bytes);
        context.Response.ContentType.Should().Be("image/jpeg");
        context.Response.ContentLength.Should().Be(bytes.Length);
        context.Response.Headers.ContentDisposition.ToString().Should().Contain("family%20photo.jpg");
        await delivery.Received(1).PrepareDownloadAsync(
            userId,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { itemId })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequireAntiforgeryToken_rejects_a_mutation_without_a_token()
    {
        using var provider = CreateAntiforgeryProvider();
        var context = CreateFilterContext(new DefaultHttpContext { RequestServices = provider });

        await new RequireCsrfToken().OnAuthorizationAsync(context);

        context.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task RequireAntiforgeryToken_accepts_a_matching_cookie_and_header()
    {
        using var provider = CreateAntiforgeryProvider();
        var antiforgery = provider.GetRequiredService<IAntiforgery>();
        var issuingContext = new DefaultHttpContext { RequestServices = provider };
        var tokens = antiforgery.GetAndStoreTokens(issuingContext);
        var setCookie = issuingContext.Response.Headers.SetCookie.SingleOrDefault(value => value?.StartsWith("media-explorer-tests-csrf=", StringComparison.Ordinal) == true);
        setCookie.Should().NotBeNull();
        tokens.RequestToken.Should().NotBeNull();
        var cookie = setCookie!.Split(';', 2)[0];
        var requestContext = new DefaultHttpContext { RequestServices = provider };
        requestContext.Request.Headers.Cookie = cookie;
        requestContext.Request.Headers["X-CSRF-Token"] = tokens.RequestToken!;
        var context = CreateFilterContext(requestContext);

        await new RequireCsrfToken().OnAuthorizationAsync(context);

        context.Result.Should().BeNull();
    }

    private static ServiceProvider CreateAntiforgeryProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-Token";
            options.Cookie.Name = "media-explorer-tests-csrf";
        });
        return services.BuildServiceProvider();
    }

    private static AuthorizationFilterContext CreateFilterContext(HttpContext httpContext) => new(
        new ActionContext(httpContext, new RouteData(), new ActionDescriptor()), []);
}

using FluentAssertions;
using GenProxy.Api.Host;
using GenProxy.Api.Host.Middleware;
using GenProxy.Api.Host.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GenProxy.Api.UnitTests;

public class ApiKeyAuthenticationMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WithValidXApiKey_SetsSharedContextItemAndCallsNext()
    {
        var wasCalled = false;
        var middleware = new ApiKeyAuthenticationMiddleware(
            context =>
            {
                wasCalled = true;
                return Task.CompletedTask;
            },
            Options.Create(new ApiKeyOptions { Keys = ["valid-key"] }),
            new TestHostEnvironment(),
            NullLogger<ApiKeyAuthenticationMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Headers[Constants.Auth.ApiKeyHeaderName] = "valid-key";

        await middleware.InvokeAsync(context);

        wasCalled.Should().BeTrue();
        context.Items[Constants.Auth.ApiKeyContextItemKey].Should().Be("valid-key");
    }

    [Fact]
    public async Task InvokeAsync_WithAuthorizationHeaderOnly_ReturnsUnauthorized()
    {
        var middleware = new ApiKeyAuthenticationMiddleware(
            _ => Task.CompletedTask,
            Options.Create(new ApiKeyOptions { Keys = ["valid-key"] }),
            new TestHostEnvironment(),
            NullLogger<ApiKeyAuthenticationMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer valid-key";

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task InvokeAsync_WithNoKeysAndDevelopmentOptOutEnabled_CallsNext()
    {
        var wasCalled = false;
        var middleware = new ApiKeyAuthenticationMiddleware(
            _ =>
            {
                wasCalled = true;
                return Task.CompletedTask;
            },
            Options.Create(new ApiKeyOptions
            {
                AllowUnauthenticatedInDevelopment = true
            }),
            new TestHostEnvironment { EnvironmentName = Environments.Development },
            NullLogger<ApiKeyAuthenticationMiddleware>.Instance);

        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        wasCalled.Should().BeTrue();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = nameof(ApiKeyAuthenticationMiddlewareTests);

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

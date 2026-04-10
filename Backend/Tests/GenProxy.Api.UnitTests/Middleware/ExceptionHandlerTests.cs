using FluentAssertions;
using GenProxy.Api.Host.Middleware;
using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.UnitTests.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace GenProxy.Api.UnitTests;

public class ExceptionHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_WhenPromptBudgetExceeded_LogsWithoutExceptionObject()
    {
        var logger = new TestLogger<ExceptionHandler>();
        var problemDetailsService = new Mock<IProblemDetailsService>();
        ProblemDetailsContext? capturedContext = null;
        problemDetailsService
            .Setup(service => service.TryWriteAsync(It.IsAny<ProblemDetailsContext>()))
            .Callback<ProblemDetailsContext>(context => capturedContext = context)
            .Returns(ValueTask.FromResult(true));
        var handler = new ExceptionHandler(logger, problemDetailsService.Object);
        var httpContext = new DefaultHttpContext();
        var exception = new UpstreamPromptBudgetExceededException("Prompt exceeds input budget: 10 > 9 allowed.");

        var handled = await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        capturedContext.Should().NotBeNull();
        capturedContext!.ProblemDetails.Title.Should().Be("Prompt exceeds token budget.");
        capturedContext.ProblemDetails.Detail.Should().Contain("Prompt exceeds input budget");
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Information);
        logger.Entries[0].Exception.Should().BeNull();
        logger.Entries[0].Message.Should().Contain("Prompt exceeds token budget.");
    }

    [Fact]
    public async Task TryHandleAsync_WhenUpstreamRuntimeUnavailable_LogsWithExceptionObject()
    {
        var logger = new TestLogger<ExceptionHandler>();
        var problemDetailsService = new Mock<IProblemDetailsService>();
        problemDetailsService
            .Setup(service => service.TryWriteAsync(It.IsAny<ProblemDetailsContext>()))
            .Returns(ValueTask.FromResult(true));
        var handler = new ExceptionHandler(logger, problemDetailsService.Object);
        var httpContext = new DefaultHttpContext();
        var exception = new UpstreamRuntimeException("generation runtime offline");

        var handled = await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Warning);
        logger.Entries[0].Exception.Should().BeSameAs(exception);
        logger.Entries[0].Message.Should().Contain("Upstream runtime unavailable.");
    }
}

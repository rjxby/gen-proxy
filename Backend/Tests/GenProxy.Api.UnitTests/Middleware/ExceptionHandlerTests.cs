using FluentAssertions;
using GenProxy.Api.Host.Middleware;
using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.UnitTests.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using System.Diagnostics;
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
        var exception = new LlamaRuntimePromptBudgetExceededException("Prompt exceeds input budget: 10 > 9 allowed.");

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
        var exception = new LlamaRuntimeCallException("generation runtime offline");

        var handled = await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Warning);
        logger.Entries[0].Exception.Should().BeSameAs(exception);
        logger.Entries[0].Message.Should().Contain("Upstream runtime unavailable.");
    }

    [Fact]
    public async Task TryHandleAsync_WhenGenerationOverridesAreUnsupported_ReturnsBadRequestWithoutExceptionObject()
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
        var exception = new LlamaRuntimeUnsupportedGenerationOverridesException("Request-level generation overrides are not supported by this runtime yet.");

        var handled = await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        capturedContext.Should().NotBeNull();
        capturedContext!.ProblemDetails.Title.Should().Be("Unsupported generation overrides.");
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Information);
        logger.Entries[0].Exception.Should().BeNull();
    }

    [Fact]
    public async Task TryHandleAsync_WhenResponseFormatIsNotSupported_ReturnsBadRequest()
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
        var exception = new GenProxy.Api.Services.Contracts.ResponseFormatNotSupportedException("json_schema is unavailable.");

        var handled = await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        capturedContext.Should().NotBeNull();
        capturedContext!.ProblemDetails.Title.Should().Be("Unsupported response format.");
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Exception.Should().BeNull();
    }

    [Fact]
    public async Task TryHandleAsync_WhenRuntimeArgumentIsInvalid_ReturnsBadRequest()
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
        var exception = new LlamaRuntimeInvalidArgumentException("ResponseFormat.JsonSchema must be valid JSON.");

        var handled = await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        capturedContext.Should().NotBeNull();
        capturedContext!.ProblemDetails.Title.Should().Be("Invalid runtime request.");
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Exception.Should().BeNull();
    }

    [Fact]
    public async Task TryHandleAsync_WhenStructuredOutputRequirementIsNotSatisfied_ReturnsBadGateway()
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
        var exception = new GenProxy.Api.Services.Contracts.StructuredOutputNotSatisfiedException("runtime did not satisfy structured output.");

        var handled = await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        capturedContext.Should().NotBeNull();
        capturedContext!.ProblemDetails.Title.Should().Be("Structured output requirement not satisfied.");
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Exception.Should().BeSameAs(exception);
    }

    [Fact]
    public async Task TryHandleAsync_WhenRuntimeStructuredOutputRequirementIsNotSatisfied_ReturnsBadGateway()
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
        var exception = new LlamaRuntimeStructuredOutputNotSatisfiedException("Inference did not return a valid JSON object.");

        var handled = await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        capturedContext.Should().NotBeNull();
        capturedContext!.ProblemDetails.Title.Should().Be("Structured output requirement not satisfied.");
        capturedContext.ProblemDetails.Detail.Should().Be("Inference did not return a valid JSON object.");
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Exception.Should().BeSameAs(exception);
    }

    [Fact]
    public async Task TryHandleAsync_WhenActivityExists_UsesActivityTraceIdInProblemDetails()
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
        httpContext.TraceIdentifier = "http-trace-id";
        var exception = new LlamaRuntimeCallException("generation runtime offline");

        using var activity = new Activity("test-request");
        activity.Start();

        await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        capturedContext.Should().NotBeNull();
        capturedContext!.ProblemDetails.Extensions["trace_id"].Should().Be(activity.TraceId.ToString());
    }
}

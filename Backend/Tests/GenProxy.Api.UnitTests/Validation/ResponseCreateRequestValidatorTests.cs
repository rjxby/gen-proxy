using FluentAssertions;
using GenProxy.Api.Host;
using GenProxy.Api.Host.Endpoints;
using GenProxy.Api.Host.Validation;
using Microsoft.Extensions.Options;
using Xunit;

namespace GenProxy.Api.UnitTests;

public class ResponseCreateRequestValidatorTests
{
    private readonly ResponseCreateRequestValidator _validator = new(Options.Create(new RequestLimitsOptions()));

    [Fact]
    public async Task ValidateAsync_WhenModelMissing_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "",
            Input = "hello"
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(error => error.PropertyName == "model");
    }

    [Fact]
    public async Task ValidateAsync_WhenInputMissing_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "gpt-5.1",
            Input = ""
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(error => error.PropertyName == "input");
    }

    [Fact]
    public async Task ValidateAsync_WhenInputTooLong_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "gpt-5.1",
            Input = new string('a', 65537)
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "input");
    }

    [Fact]
    public async Task ValidateAsync_WhenMetadataHasTooManyEntries_ReturnsError()
    {
        var metadata = Enumerable.Range(0, 17)
            .ToDictionary(index => $"key{index}", index => "value");

        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "gpt-5.1",
            Input = "hello",
            Metadata = metadata
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "metadata");
    }
}

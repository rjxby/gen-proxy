using FluentValidation;
using GenProxy.Api.Host.Endpoints;
using Microsoft.Extensions.Options;

namespace GenProxy.Api.Host.Validation;

public sealed class ResponseCreateRequestValidator : AbstractValidator<ResponseCreateRequest>
{
    public ResponseCreateRequestValidator(IOptions<RequestLimitsOptions> options)
    {
        var limits = options.Value;

        RuleFor(request => request.Model)
            .NotEmpty()
            .WithMessage("Model is required.")
            .MaximumLength(limits.MaxModelCharacters)
            .WithMessage($"Model must be at most {limits.MaxModelCharacters} characters.")
            .OverridePropertyName("model");

        RuleFor(request => request.Input)
            .NotEmpty()
            .WithMessage("Input is required.")
            .MaximumLength(limits.MaxInputCharacters)
            .WithMessage($"Input must be at most {limits.MaxInputCharacters} characters.")
            .OverridePropertyName("input");

        RuleFor(request => request.Metadata)
            .Custom((metadata, context) =>
            {
                if (metadata is null)
                {
                    return;
                }

                if (metadata.Count > limits.MaxMetadataEntries)
                {
                    context.AddFailure("metadata", $"Metadata must contain at most {limits.MaxMetadataEntries} entries.");
                }

                if (metadata.Keys.Any(key => key.Length > limits.MaxMetadataKeyCharacters))
                {
                    context.AddFailure("metadata", $"Metadata keys must be at most {limits.MaxMetadataKeyCharacters} characters.");
                }

                if (metadata.Values.Any(value => value.Length > limits.MaxMetadataValueCharacters))
                {
                    context.AddFailure("metadata", $"Metadata values must be at most {limits.MaxMetadataValueCharacters} characters.");
                }
            });
    }
}

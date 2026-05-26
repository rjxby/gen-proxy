using FluentValidation;
using GenProxy.Api.Host.Endpoints;
using GenProxy.Api.Services.Contracts.Models;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GenProxy.Api.Host.Validation;

public sealed class ResponseCreateRequestValidator : AbstractValidator<ResponseCreateRequest>
{
    private static readonly Regex JsonSchemaNameRegex = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

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
            .Custom((input, context) => ValidateInput(input, limits.MaxInputCharacters, context));

        RuleFor(request => request.ResponseFormat)
            .Custom(ValidateResponseFormat);

        RuleFor(request => request.MaxOutputTokens)
            .GreaterThan(0)
            .When(request => request.MaxOutputTokens.HasValue)
            .WithMessage("Max output tokens must be greater than 0.")
            .OverridePropertyName("max_output_tokens");

        RuleFor(request => request.Temperature)
            .InclusiveBetween(0f, 2f)
            .When(request => request.Temperature.HasValue)
            .WithMessage("Temperature must be between 0 and 2.")
            .OverridePropertyName("temperature");

        RuleFor(request => request.TopP)
            .InclusiveBetween(0f, 1f)
            .When(request => request.TopP.HasValue)
            .WithMessage("Top-p must be between 0 and 1.")
            .OverridePropertyName("top_p");

        RuleFor(request => request.Stream)
            .Must(stream => stream is not true)
            .WithMessage("Streaming is not supported in this stage.")
            .When(request => request.Stream.HasValue)
            .OverridePropertyName("stream");

        RuleFor(request => request.Tools)
            .Custom((tools, context) =>
            {
                if (HasProvidedJsonValue(tools))
                {
                    context.AddFailure("tools", "Tools are not supported in this stage.");
                }
            });

        RuleFor(request => request.ToolChoice)
            .Custom((toolChoice, context) =>
            {
                if (HasProvidedJsonValue(toolChoice))
                {
                    context.AddFailure("tool_choice", "Tool choice is not supported in this stage.");
                }
            });

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

    private static bool HasProvidedJsonValue(JsonElement? value)
    {
        return value is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined };
    }

    private static void ValidateResponseFormat(
        ResponseFormatRequest? responseFormat,
        ValidationContext<ResponseCreateRequest> context)
    {
        if (responseFormat is null)
        {
            return;
        }

        if (!RequestedResponseFormats.IsSupported(responseFormat.Type))
        {
            context.AddFailure(
                "response_format.type",
                $"Response format type must be one of '{RequestedResponseFormats.Text}' or '{RequestedResponseFormats.JsonSchema}'.");
            return;
        }

        if (responseFormat.Type == RequestedResponseFormats.Text)
        {
            if (responseFormat.JsonSchema is not null)
            {
                context.AddFailure(
                    "response_format.json_schema",
                    "JSON schema configuration is only valid when response_format.type is 'json_schema'.");
            }

            return;
        }

        if (responseFormat.JsonSchema is null)
        {
            context.AddFailure(
                "response_format.json_schema",
                "JSON schema configuration is required when response_format.type is 'json_schema'.");
            return;
        }

        var jsonSchema = responseFormat.JsonSchema;
        if (string.IsNullOrWhiteSpace(jsonSchema.Name))
        {
            context.AddFailure("response_format.json_schema.name", "JSON schema name is required.");
        }
        else
        {
            if (jsonSchema.Name.Length > 64)
            {
                context.AddFailure("response_format.json_schema.name", "JSON schema name must be at most 64 characters.");
            }

            if (!JsonSchemaNameRegex.IsMatch(jsonSchema.Name))
            {
                context.AddFailure(
                    "response_format.json_schema.name",
                    "JSON schema name may contain only letters, numbers, underscores, and dashes.");
            }
        }

        if (!HasProvidedJsonValue(jsonSchema.Schema))
        {
            context.AddFailure("response_format.json_schema.schema", "JSON schema payload is required.");
        }
        else if (jsonSchema.Schema is not { ValueKind: JsonValueKind.Object })
        {
            context.AddFailure("response_format.json_schema.schema", "JSON schema payload must be an object.");
        }

        if (jsonSchema.Strict == false)
        {
            context.AddFailure(
                "response_format.json_schema.strict",
                "Only strict JSON schema response formats are supported.");
        }
    }

    private static void ValidateInput(
        IReadOnlyList<ResponseInputMessageRequest>? input,
        int maxInputCharacters,
        ValidationContext<ResponseCreateRequest> context)
    {
        if (input is null || input.Count == 0)
        {
            context.AddFailure("input", "Input must contain exactly one user message.");
            return;
        }

        if (input.Count != 1)
        {
            context.AddFailure("input", "Only a single user message is supported in this stage.");
            return;
        }

        var message = input[0];
        if (message is null)
        {
            context.AddFailure("input[0]", "Only message objects are supported.");
            return;
        }

        var isValid = true;
        if (message.Type != "message")
        {
            context.AddFailure("input[0].type", "Only input items with type 'message' are supported.");
            isValid = false;
        }

        if (message.Role != "user")
        {
            context.AddFailure("input[0].role", "Only user messages are supported in this stage.");
            isValid = false;
        }

        if (message.Content is null || message.Content.Count == 0)
        {
            context.AddFailure("input[0].content", "Message content must contain at least one input_text part.");
            return;
        }

        var contentTexts = new List<string>(message.Content.Count);
        for (var contentIndex = 0; contentIndex < message.Content.Count; contentIndex++)
        {
            var contentPart = message.Content[contentIndex];
            var contentPath = $"input[0].content[{contentIndex}]";

            if (contentPart is null)
            {
                context.AddFailure(contentPath, "Content parts must be objects.");
                isValid = false;
                continue;
            }

            if (contentPart.Type != "input_text")
            {
                context.AddFailure($"{contentPath}.type", "Only input_text content parts are supported.");
                isValid = false;
            }

            if (string.IsNullOrWhiteSpace(contentPart.Text))
            {
                context.AddFailure($"{contentPath}.text", "Text content parts must include non-empty text.");
                isValid = false;
                continue;
            }

            contentTexts.Add(contentPart.Text);
        }

        if (!isValid)
        {
            return;
        }

        var prompt = string.Join('\n', contentTexts);
        if (prompt.Length > maxInputCharacters)
        {
            context.AddFailure("input", $"Input must be at most {maxInputCharacters} characters after normalization.");
        }
    }
}

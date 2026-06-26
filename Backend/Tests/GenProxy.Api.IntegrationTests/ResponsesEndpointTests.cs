using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using GenProxy.Api.Host;
using GenProxy.Api.Host.Endpoints;
using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Contracts.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using GenProxy.Api.Host.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using GenProxy.Api.Services.Contracts.Models;
using Xunit;

namespace GenProxy.Api.IntegrationTests;

public class ResponsesEndpointTests
{
    [Fact]
    public async Task PostResponses_WithValidApiKey_ReturnsResponsesCompatibleResponse()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world"),
            max_output_tokens = 512
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var payload = await response.Content.ReadFromJsonAsync<ResponsesApiResponse>();
        payload.Should().NotBeNull();
        payload!.Object.Should().Be(ResponseObjectType.Response);
        payload.Model.Should().Be("runtime-model");
        payload.Status.Should().Be(ResponseStatus.Completed);
        payload.Output[0].Type.Should().Be(ResponseItemType.Message);
        payload.Output[0].Status.Should().Be(ResponseStatus.Completed);
        payload.Output[0].Role.Should().Be(ResponseRole.Assistant);
        payload.Output[0].Content[0].Type.Should().Be(ResponseContentPartType.OutputText);
        payload.OutputText.Should().Be("generated: hello world");
        payload.Usage.InputTokens.Should().Be(42);
        payload.Usage.OutputTokens.Should().Be(7);
        payload.Usage.TotalTokens.Should().Be(49);
        payload.CreatedAt.Should().BePositive();
    }

    [Fact]
    public async Task SwaggerDocument_UsesWireEnumStrings()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient(),
            environmentName: Environments.Development);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/swagger/v1/swagger.json");
        request.Headers.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        document.Should().NotBeNull();

        var requestSchema = ResolveSchema(
            document!,
            document!["paths"]!["/v1/responses"]!["post"]!["requestBody"]!["content"]!["application/json"]!["schema"]);
        requestSchema.Should().NotBeNull();

        var requiredRequestProperties = requestSchema!["required"]!
            .AsArray()
            .Select(value => value!.GetValue<string>())
            .ToList();
        requiredRequestProperties.Should().Contain("model");
        requiredRequestProperties.Should().Contain("input");

        var responseFormatSchema = ResolveSchema(document!, requestSchema["properties"]!["response_format"]);
        requestSchema["properties"]!["response_format"]!["description"]!
            .GetValue<string>()
            .Should()
            .Be("Optional response format override. When provided, type is required.");

        responseFormatSchema["required"]!
            .AsArray()
            .Select(value => value!.GetValue<string>())
            .Should()
            .ContainSingle("type");

        responseFormatSchema["properties"]!["type"]!["enum"]!
            .AsArray()
            .Select(value => value!.GetValue<string>())
            .Should()
            .Equal("text", "json_schema");

        var jsonSchemaSchema = ResolveSchema(document!, responseFormatSchema["properties"]!["json_schema"]);
        jsonSchemaSchema["properties"]!["schema"].Should().NotBeNull();

        var inputSchema = requestSchema["properties"]!["input"];
        inputSchema.Should().NotBeNull();
        inputSchema!["oneOf"].Should().BeNull();
        inputSchema["type"]!.GetValue<string>().Should().Be("array");
        inputSchema["minItems"]!.GetValue<int>().Should().Be(1);
        inputSchema["maxItems"]!.GetValue<int>().Should().Be(1);

        var inputItemSchema = ResolveSchema(document!, inputSchema["items"]);
        inputItemSchema["properties"]!["type"]!["enum"]![0]!.GetValue<string>().Should().Be("message");
        inputItemSchema["properties"]!["role"]!["enum"]![0]!.GetValue<string>().Should().Be("user");
        inputItemSchema["required"]!
            .AsArray()
            .Select(value => value!.GetValue<string>())
            .Should()
            .BeEquivalentTo(["type", "role", "content"]);

        var contentSchema = inputItemSchema["properties"]!["content"]!;
        contentSchema["minItems"]!.GetValue<int>().Should().Be(1);
        var contentItemSchema = ResolveSchema(document!, contentSchema["items"]);
        contentItemSchema["required"]!
            .AsArray()
            .Select(value => value!.GetValue<string>())
            .Should()
            .BeEquivalentTo(["type", "text"]);
        contentItemSchema["properties"]!["type"]!["enum"]![0]!
            .GetValue<string>()
            .Should()
            .Be("input_text");
        contentItemSchema["properties"]!["text"]!["description"]!
            .GetValue<string>()
            .Should()
            .Be("Must be a non-empty string.");
    }

    [Fact]
    public async Task PostResponses_WithStructuredMessageInput_ReturnsResponsesCompatibleResponse()
    {
        var generationRuntimeClient = new FakeGenerationRuntimeClient();
        await using var factory = CreateFactory(
            generationRuntimeClient,
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = new object[]
            {
                new
                {
                    type = "message",
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_text",
                            text = "hello world"
                        }
                    }
                }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        generationRuntimeClient.LastPrompt.Should().Be("hello world");
        var payload = await response.Content.ReadFromJsonAsync<ResponsesApiResponse>();
        payload.Should().NotBeNull();
        payload!.OutputText.Should().Be("generated: hello world");
    }

    [Fact]
    public async Task PostResponses_WithStructuredMessageInputParts_JoinsThemWithNewLines()
    {
        var generationRuntimeClient = new FakeGenerationRuntimeClient();
        await using var factory = CreateFactory(
            generationRuntimeClient,
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = new object[]
            {
                new
                {
                    type = "message",
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_text",
                            text = "hello"
                        },
                        new
                        {
                            type = "input_text",
                            text = "world"
                        }
                    }
                }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        generationRuntimeClient.LastPrompt.Should().Be("hello\nworld");
    }

    [Fact]
    public async Task PostResponses_WithTopLevelStringInput_ReturnsValidationProblem()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = "hello world"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostResponses_WithJsonSchemaResponseFormatAndSupportedRuntime_ReturnsResponse()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("return valid json"),
            response_format = JsonSchemaResponseFormat(),
            max_output_tokens = 512
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<ResponsesApiResponse>();
        payload.Should().NotBeNull();
        payload!.OutputText.Should().Be("{\"result\":\"return valid json\"}");
    }

    [Fact]
    public async Task PostResponses_WhenPromptReducerRuntimeDisabled_DoesNotRequireReducerClientOrAddress()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            promptReducerRuntimeClient: null,
            promptReducerRuntimeEnabled: false,
            promptReducerRuntimeAddress: string.Empty);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world")
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var payload = await response.Content.ReadFromJsonAsync<ResponsesApiResponse>();
        payload.Should().NotBeNull();
        payload!.OutputText.Should().Be("generated: hello world");
    }

    [Fact]
    public async Task PostResponses_ForwardsTemperatureAndTopPToGenerationRuntime()
    {
        var generationRuntimeClient = new FakeGenerationRuntimeClient();
        await using var factory = CreateFactory(
            generationRuntimeClient,
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world"),
            temperature = 0.25,
            top_p = 0.8,
            max_output_tokens = 512
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        generationRuntimeClient.LastGenerationOptions.Should().NotBeNull();
        generationRuntimeClient.LastGenerationOptions!.Temperature.Should().BeApproximately(0.25f, 0.001f);
        generationRuntimeClient.LastGenerationOptions.TopP.Should().BeApproximately(0.8f, 0.001f);
        generationRuntimeClient.LastGenerationOptions.MaxOutputTokens.Should().Be(512);
    }

    [Fact]
    public async Task PostResponses_WhenMaxOutputTokensDoesNotMatchRuntimeBudget_ReturnsBadRequest()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world"),
            max_output_tokens = 64
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Unsupported generation overrides.");
        body.Should().Contain("max_output_tokens");
    }

    [Fact]
    public async Task PostResponses_WhenResponseFormatTypeIsUnknown_ReturnsValidationProblem()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world"),
            response_format = new
            {
                type = "xml"
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey("response_format.type");
    }

    [Fact]
    public async Task PostResponses_WhenResponseFormatTypeIsLegacyJsonObject_ReturnsValidationProblem()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world"),
            response_format = new
            {
                type = "json_object"
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey("response_format.type");
    }

    [Fact]
    public async Task PostResponses_WhenStructuredInputRoleIsNotUser_ReturnsValidationProblem()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = new object[]
            {
                new
                {
                    type = "message",
                    role = "assistant",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_text",
                            text = "hello world"
                        }
                    }
                }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey("input[0].role");
    }

    [Fact]
    public async Task PostResponses_WhenStructuredInputContainsMultipleMessages_ReturnsValidationProblem()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = new object[]
            {
                new
                {
                    type = "message",
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_text",
                            text = "hello"
                        }
                    }
                },
                new
                {
                    type = "message",
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_text",
                            text = "world"
                        }
                    }
                }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey("input");
    }

    [Fact]
    public async Task PostResponses_WhenStructuredInputContentTypeIsUnsupported_ReturnsValidationProblem()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = new object[]
            {
                new
                {
                    type = "message",
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "output_text",
                            text = "hello world"
                        }
                    }
                }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey("input[0].content[0].type");
    }

    [Fact]
    public async Task PostResponses_WhenToolsProvided_ReturnsValidationProblem()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world"),
            tools = new object[]
            {
                new
                {
                    type = "function",
                    name = "read_file"
                }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey("tools");
    }

    [Fact]
    public async Task PostResponses_WhenToolChoiceProvided_ReturnsValidationProblem()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world"),
            tool_choice = new
            {
                type = "auto"
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey("tool_choice");
    }

    [Fact]
    public async Task PostResponses_WhenStreamEnabled_ReturnsValidationProblem()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world"),
            stream = true
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey("stream");
    }

    [Fact]
    public async Task PostResponses_WhenJsonSchemaResponseFormatIsUnsupported_ReturnsBadRequest()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(supportsJsonOutput: false),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world"),
            response_format = JsonSchemaResponseFormat()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Title.Should().Be("Unsupported response format.");
    }

    [Fact]
    public async Task PostResponses_WhenStructuredOutputIsUnsupported_ReturnsBadRequest()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(supportsStructuredOutput: false),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world"),
            response_format = JsonSchemaResponseFormat()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Title.Should().Be("Unsupported response format.");
    }


    [Fact]
    public async Task PostResponses_WhenStructuredOutputRequirementIsNotSatisfied_ReturnsBadGateway()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(jsonSchemaStructuredOutputSatisfied: false),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world"),
            response_format = JsonSchemaResponseFormat()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Title.Should().Be("Structured output requirement not satisfied.");
    }

    [Fact]
    public async Task PostResponses_WhenJsonSchemaRuntimeTraceIsMissing_ReturnsBadGateway()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(includeJsonSchemaRuntimeTrace: false),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world"),
            response_format = JsonSchemaResponseFormat()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Title.Should().Be("Structured output requirement not satisfied.");
    }

    [Fact]
    public async Task PostResponses_WhenJsonSchemaStructuredOutputWasNotApplied_ReturnsBadGateway()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(jsonSchemaStructuredOutputApplied: false),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world"),
            response_format = JsonSchemaResponseFormat()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Title.Should().Be("Structured output requirement not satisfied.");
    }

    [Fact]
    public async Task PostResponses_WhenJsonSchemaOutputIsNotValidJson_ReturnsBadGateway()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(jsonSchemaContent: "not-json"),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world"),
            response_format = JsonSchemaResponseFormat()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Title.Should().Be("Structured output requirement not satisfied.");
    }

    [Fact]
    public async Task PostResponses_WhenJsonSchemaOutputIsNotAnObject_ReturnsBadGateway()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(jsonSchemaContent: "[1,2,3]"),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world"),
            response_format = JsonSchemaResponseFormat()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Title.Should().Be("Structured output requirement not satisfied.");
    }

    [Fact]
    public async Task PostResponses_WhenRuntimeRejectsJsonSchemaOutput_ReturnsBadGateway()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(
                generateException: new LlamaRuntimeStructuredOutputNotSatisfiedException("Inference did not return a valid JSON object.")),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world"),
            response_format = JsonSchemaResponseFormat()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Title.Should().Be("Structured output requirement not satisfied.");
        problem.Detail.Should().Be("Inference did not return a valid JSON object.");
    }

    [Fact]
    public async Task PostResponses_WithoutApiKey_ReturnsUnauthorized()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world")
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostResponses_WhenPromptStillTooLarge_ReturnsUnprocessableEntity()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(fitsAfterReduction: false, oversizedPrompt: "hello world"),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world")
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Title.Should().Be("Prompt exceeds token budget.");
        problem.Detail.Should().Contain("after prompt reduction");
    }

    [Fact]
    public async Task PostResponses_WhenRequestInvalid_ReturnsValidationProblem()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "",
            input = StructuredInput("hello")
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostResponses_WhenInputTooLarge_ReturnsValidationProblem()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput(new string('a', 65537))
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey("input");
    }

    [Fact]
    public async Task PostResponses_WhenMetadataHasTooManyEntries_ReturnsValidationProblem()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var metadata = Enumerable.Range(0, 17)
            .ToDictionary(index => $"key{index}", index => "value");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world"),
            metadata
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey("metadata");
    }

    [Fact]
    public async Task PostResponses_WhenRequestBodyTooLarge_ReturnsPayloadTooLarge()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent.Create(new
            {
                model = "stories15m",
                input = StructuredInput(new string('a', 140000))
            })
        };
        request.Headers.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task PostResponses_WhenReducerRuntimeDisabled_UsesLeadingTruncationWithoutReducerClient()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(truncatedPrompt: "lloworld", oversizedPrompt: "helloworld"),
            promptReducerRuntimeClient: null,
            promptReducerRuntimeEnabled: false);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("helloworld")
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<ResponsesApiResponse>();
        payload.Should().NotBeNull();
        payload!.OutputText.Should().Be("generated: lloworld");
    }

    [Fact]
    public async Task PostResponses_WhenGenerationRuntimeUnavailable_ReturnsServiceUnavailableProblem()
    {
        await using var factory = CreateFactory(
            new ThrowingGenerationRuntimeClient(new LlamaRuntimeCallException("generation runtime offline")),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world")
        });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Title.Should().Be("Upstream runtime unavailable.");
    }

    [Fact]
    public async Task PostResponses_WhenGenerationRuntimeRejectsGenerationOverrides_ReturnsBadRequestProblem()
    {
        await using var factory = CreateFactory(
            new ThrowingGenerationRuntimeClient(
                new LlamaRuntimeUnsupportedGenerationOverridesException(
                    "Request-level generation overrides are not supported by this runtime yet. Omit Generation to use runtime defaults.")),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world"),
            temperature = 0.25
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Title.Should().Be("Unsupported generation overrides.");
        problem.Detail.Should().Contain("Request-level generation overrides are not supported");
    }

    [Fact]
    public async Task PostResponses_WhenReducerRuntimeRejectsOversizedReductionPrompt_ReturnsUnprocessableEntityWithoutGeneration()
    {
        var generationRuntimeClient = new FakeGenerationRuntimeClient(oversizedPrompt: "hello world");
        await using var factory = CreateFactory(
            generationRuntimeClient,
            new ThrowingPromptReducerRuntimeClient(
                new LlamaRuntimePromptBudgetExceededException("Prompt exceeds input budget: 6250 tokens > 3584 allowed.")));

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world")
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        generationRuntimeClient.LastPrompt.Should().BeNull();
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Title.Should().Be("Prompt exceeds token budget.");
        problem.Detail.Should().Contain("Prompt exceeds input budget");
    }

    [Fact]
    public async Task PostResponses_WithInvalidGenerationRuntimeAddress_FailsStartupValidation()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["GenerationRuntime:Address"] = "not-a-valid-uri"
                    });
                });
            });

        var act = () => factory.CreateClient();

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void PostResponses_WithPlaintextGenerationRuntimeAddress_FailsStartupValidation()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["GenerationRuntime:Address"] = "http://localhost:50051"
                    });
                });
            });

        var act = () => factory.CreateClient();

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void PostResponses_WithInvalidPromptReducerRuntimeAddress_AllowsStartupWhenReducerRuntimeDisabled()
    {
        using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            promptReducerRuntimeClient: null,
            promptReducerRuntimeEnabled: false,
            promptReducerRuntimeAddress: "not-a-valid-uri");

        var act = () => factory.CreateClient();

        act.Should().NotThrow();
    }

    [Fact]
    public void PostResponses_WithBlankPromptReducerRuntimeAddress_AllowsStartupWhenReducerRuntimeDisabled()
    {
        using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            promptReducerRuntimeClient: null,
            promptReducerRuntimeEnabled: false,
            promptReducerRuntimeAddress: string.Empty);

        var act = () => factory.CreateClient();

        act.Should().NotThrow();
    }

    [Fact]
    public void PostResponses_WithInvalidPromptReducerRuntimeAddress_FailsStartupValidationWhenReducerRuntimeEnabled()
    {
        using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient(),
            promptReducerRuntimeAddress: "not-a-valid-uri");

        var act = () => factory.CreateClient();

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void PostResponses_WithPlaintextPromptReducerRuntimeAddress_FailsStartupValidationWhenReducerRuntimeEnabled()
    {
        using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient(),
            promptReducerRuntimeAddress: "http://localhost:50052");

        var act = () => factory.CreateClient();

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void PostResponses_WithoutConfiguredApiKeys_FailsStartupValidation()
    {
        using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient(),
            configureServices: services =>
            {
                services.PostConfigure<ApiKeyOptions>(options => options.Keys.Clear());
            });

        var act = () => factory.CreateClient();

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public async Task PostResponses_WithoutConfiguredApiKeys_AllowsRequestsOnlyWhenDevelopmentOptOutEnabled()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient(),
            environmentName: Environments.Development,
            configureServices: services =>
            {
                services.PostConfigure<ApiKeyOptions>(options =>
                {
                    options.Keys.Clear();
                    options.AllowUnauthenticatedInDevelopment = true;
                });
            });

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world")
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostResponses_EmitsCombinedHttpLogWithoutBodiesByDefault()
    {
        var sink = new LogSink();

        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient(),
            configureLogging: logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new SinkLoggerProvider(sink));
                logging.SetMinimumLevel(LogLevel.Information);
            },
            configureSettings: settings =>
            {
                settings["Logging:LogLevel:Default"] = "Information";
                settings["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning";
                settings["Logging:LogLevel:Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware"] = "Information";
            });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world")
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var httpLogMessage = GetSingleHttpLogMessage(sink);
        httpLogMessage.Should().Contain("POST");
        httpLogMessage.Should().Contain("/v1/responses");
        httpLogMessage.Should().Contain("200");
        httpLogMessage.Should().NotContain("\"text\":\"hello world\"");
        httpLogMessage.Should().NotContain("\"output_text\":\"generated: hello world\"");
        httpLogMessage.Should().NotContain("test-api-key");
    }

    [Fact]
    public async Task PostResponses_WhenExceptionHandled_EmitsCombinedHttpLogWithFinalStatusCode()
    {
        var sink = new LogSink();

        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(
                generateException: new LlamaRuntimeStructuredOutputNotSatisfiedException("Inference did not return a valid JSON object.")),
            new FakePromptReducerRuntimeClient(),
            configureLogging: logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new SinkLoggerProvider(sink));
                logging.SetMinimumLevel(LogLevel.Information);
            },
            configureSettings: settings =>
            {
                settings["Logging:LogLevel:Default"] = "Information";
                settings["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning";
                settings["Logging:LogLevel:Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware"] = "Information";
            });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world"),
            response_format = JsonSchemaResponseFormat()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        var httpLogMessage = GetSingleHttpLogMessage(sink);
        httpLogMessage.Should().Contain("POST");
        httpLogMessage.Should().Contain("/v1/responses");
        httpLogMessage.Should().Contain("502");
        httpLogMessage.Should().NotContain("StatusCode: 200");
    }

    [Fact]
    public async Task PostResponses_WhenBodyLoggingEnabled_EmitsCombinedHttpLogWithBodies()
    {
        var sink = new LogSink();

        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient(),
            configureLogging: logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new SinkLoggerProvider(sink));
                logging.SetMinimumLevel(LogLevel.Information);
            },
            configureSettings: settings =>
            {
                settings["Logging:LogLevel:Default"] = "Information";
                settings["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning";
                settings["Logging:LogLevel:Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware"] = "Information";
                settings["ResponsesLogging:LogBodies"] = "true";
            });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world")
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var httpLogMessage = GetSingleHttpLogMessage(sink);
        httpLogMessage.Should().Contain("\"text\":\"hello world\"");
        httpLogMessage.Should().Contain("\"output_text\":\"generated: hello world\"");
        httpLogMessage.Should().NotContain("test-api-key");
    }

    [Fact]
    public async Task PostResponses_WhenRequestRejected_DoesNotLogBodyByDefault()
    {
        var sink = new LogSink();

        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient(),
            configureLogging: logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new SinkLoggerProvider(sink));
                logging.SetMinimumLevel(LogLevel.Information);
            },
            configureSettings: settings =>
            {
                settings["Logging:LogLevel:Default"] = "Information";
                settings["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning";
                settings["Logging:LogLevel:Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware"] = "Information";
            });

        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "stories15m",
            input = StructuredInput("hello world")
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var httpLogMessage = GetSingleHttpLogMessage(sink);
        httpLogMessage.Should().Contain("POST");
        httpLogMessage.Should().Contain("/v1/responses");
        httpLogMessage.Should().Contain("401");
        httpLogMessage.Should().NotContain("\"text\":\"hello world\"");
    }

    private static object[] StructuredInput(params string[] texts)
    {
        return
        [
            new
            {
                type = "message",
                role = "user",
                content = texts
                    .Select(text => new
                    {
                        type = "input_text",
                        text
                    })
                    .ToArray()
            }
        ];
    }

    private static object JsonSchemaResponseFormat()
    {
        return new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "result",
                schema = new
                {
                    type = "object",
                    properties = new
                    {
                        result = new { type = "string" }
                    },
                    required = new[] { "result" },
                    additionalProperties = false
                },
                strict = true
            }
        };
    }

    private static WebApplicationFactory<Program> CreateFactory(
        IGenerationRuntimeClient generationRuntimeClient,
        IPromptReducerRuntimeClient? promptReducerRuntimeClient,
        bool promptReducerRuntimeEnabled = true,
        string promptReducerRuntimeAddress = "https://localhost:50052",
        string environmentName = "Testing",
        Action<Dictionary<string, string?>>? configureSettings = null,
        Action<IServiceCollection>? configureServices = null,
        Action<ILoggingBuilder>? configureLogging = null)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environmentName);
                builder.UseSetting("PromptReducerRuntime:Enabled", promptReducerRuntimeEnabled.ToString());
                builder.UseSetting("GenerationRuntime:Address", "https://localhost:50051");
                builder.UseSetting("PromptReducerRuntime:Address", promptReducerRuntimeAddress);
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    var settings = new Dictionary<string, string?>
                    {
                        ["PromptReducerRuntime:Enabled"] = promptReducerRuntimeEnabled.ToString(),
                        ["GenerationRuntime:Address"] = "https://localhost:50051",
                        ["PromptReducerRuntime:Address"] = promptReducerRuntimeAddress
                    };
                    configureSettings?.Invoke(settings);
                    configBuilder.AddInMemoryCollection(settings);
                });
                builder.ConfigureLogging(logging => configureLogging?.Invoke(logging));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IGenerationRuntimeClient>();
                    services.AddSingleton(generationRuntimeClient);
                    if (promptReducerRuntimeClient is not null)
                    {
                        services.RemoveAll<IPromptReducerRuntimeClient>();
                        services.AddSingleton(promptReducerRuntimeClient);
                    }

                    configureServices?.Invoke(services);
                });
            });
    }

    private static JsonNode ResolveSchema(JsonNode document, JsonNode? schema)
    {
        schema.Should().NotBeNull();

        if (schema!["$ref"] is not { } reference)
        {
            if (schema["allOf"] is { } allOf)
            {
                allOf.AsArray().Should().ContainSingle();
                return ResolveSchema(document, allOf[0]);
            }

            return schema;
        }

        var referencePath = reference.GetValue<string>();
        referencePath.Should().StartWith("#/");

        var resolved = document;
        foreach (var segment in referencePath[2..].Split('/').Select(DecodeJsonPointerSegment))
        {
            resolved = resolved[segment]!;
        }

        return resolved;
    }

    private static string DecodeJsonPointerSegment(string segment)
    {
        return segment.Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal);
    }

    private sealed class FakeGenerationRuntimeClient(
        bool fitsAfterReduction = true,
        string truncatedPrompt = "__none__",
        string oversizedPrompt = "__oversized__",
        bool supportsStructuredOutput = true,
        bool supportsJsonOutput = true,
        bool includeJsonSchemaRuntimeTrace = true,
        bool jsonSchemaStructuredOutputApplied = true,
        bool jsonSchemaStructuredOutputSatisfied = true,
        string? jsonSchemaContent = null,
        string runtimeModel = "runtime-model",
        Exception? generateException = null) : IGenerationRuntimeClient
    {
        private readonly string _truncatedPrompt = truncatedPrompt;
        private readonly string _oversizedPrompt = oversizedPrompt;
        private readonly bool _supportsStructuredOutput = supportsStructuredOutput;
        private readonly bool _supportsJsonOutput = supportsJsonOutput;
        private readonly bool _includeJsonSchemaRuntimeTrace = includeJsonSchemaRuntimeTrace;
        private readonly bool _jsonSchemaStructuredOutputApplied = jsonSchemaStructuredOutputApplied;
        private readonly bool _jsonSchemaStructuredOutputSatisfied = jsonSchemaStructuredOutputSatisfied;
        private readonly string? _jsonSchemaContent = jsonSchemaContent;
        private readonly string _runtimeModel = runtimeModel;
        private readonly Exception? _generateException = generateException;

        public string? LastPrompt { get; private set; }

        public LlamaGenerationOptions? LastGenerationOptions { get; private set; }

        public Task<TokenEstimation> EstimateTokensAsync(string prompt, CancellationToken cancellationToken)
        {
            if (prompt == _oversizedPrompt)
            {
                return Task.FromResult(new TokenEstimation(9000, 8192, 512, 7680, false));
            }

            if (prompt == _truncatedPrompt)
            {
                return Task.FromResult(new TokenEstimation(6000, 8192, 512, 7680, true));
            }

            return Task.FromResult(new TokenEstimation(6000, 8192, 512, 7680, fitsAfterReduction));
        }

        public Task<LlamaCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new LlamaCapabilities(
                _runtimeModel,
                8192,
                SupportsStructuredOutput: _supportsStructuredOutput,
                SupportsJsonOutput: _supportsJsonOutput,
                SupportsSpeculativeDecoding: false,
                TokenizerFamily: "llama"));
        }

        public Task<LlamaGenerationResult> GenerateAsync(
            string requestId,
            string prompt,
            LlamaGenerationOptions? options,
            CancellationToken cancellationToken)
        {
            if (_generateException is not null)
            {
                throw _generateException;
            }

            LastPrompt = prompt;
            LastGenerationOptions = options;
            var content = options?.ResponseFormat == LlamaResponseFormatType.JsonSchema
                ? _jsonSchemaContent ?? $"{{\"result\":\"{prompt}\"}}"
                : $"generated: {prompt}";

            return Task.FromResult(new LlamaGenerationResult(
                requestId,
                _runtimeModel,
                content,
                new LlamaUsage(42, 7, 49),
                options?.ResponseFormat == LlamaResponseFormatType.JsonSchema
                    ? _includeJsonSchemaRuntimeTrace
                        ? new LlamaRuntimeTrace(
                            _jsonSchemaStructuredOutputApplied,
                            _jsonSchemaStructuredOutputSatisfied,
                            false)
                        : null
                    : null));
        }
    }

    private sealed class FakePromptReducerRuntimeClient : IPromptReducerRuntimeClient
    {
        public Task<TokenEstimation> EstimateTokensAsync(string prompt, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<LlamaCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new LlamaCapabilities("prompt-reducer", 4096, false, false, false, "llama"));
        }

        public Task<LlamaGenerationResult> GenerateAsync(
            string requestId,
            string prompt,
            LlamaGenerationOptions? options,
            CancellationToken cancellationToken)
        {
            var promptMarker = "Original user input:\n";
            var originalPrompt = prompt.Contains(promptMarker, StringComparison.Ordinal)
                ? prompt[(prompt.IndexOf(promptMarker, StringComparison.Ordinal) + promptMarker.Length)..]
                : prompt;

            return Task.FromResult(new LlamaGenerationResult(requestId, "prompt-reducer", originalPrompt, null, null));
        }
    }

    private sealed class ThrowingGenerationRuntimeClient(Exception exception) : IGenerationRuntimeClient
    {
        private readonly Exception _exception = exception;

        public Task<TokenEstimation> EstimateTokensAsync(string prompt, CancellationToken cancellationToken)
        {
            throw _exception;
        }

        public Task<LlamaCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
        {
            throw _exception;
        }

        public Task<LlamaGenerationResult> GenerateAsync(
            string requestId,
            string prompt,
            LlamaGenerationOptions? options,
            CancellationToken cancellationToken)
        {
            throw _exception;
        }
    }

    private sealed class ThrowingPromptReducerRuntimeClient(Exception exception) : IPromptReducerRuntimeClient
    {
        private readonly Exception _exception = exception;

        public Task<TokenEstimation> EstimateTokensAsync(string prompt, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<LlamaCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<LlamaGenerationResult> GenerateAsync(
            string requestId,
            string prompt,
            LlamaGenerationOptions? options,
            CancellationToken cancellationToken)
        {
            throw _exception;
        }
    }

    private sealed class LogSink
    {
        public List<LogEntry> Entries { get; } = [];
    }

    private static string GetSingleHttpLogMessage(LogSink sink)
    {
        var httpLogs = sink.Entries
            .Where(entry => entry.Category == "Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware")
            .ToList();

        httpLogs.Should().ContainSingle();
        return httpLogs[0].Message;
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message, string? Exception);

    private sealed class SinkLoggerProvider(LogSink sink) : ILoggerProvider
    {
        private readonly LogSink _sink = sink;

        public ILogger CreateLogger(string categoryName) => new SinkLogger(categoryName, _sink);

        public void Dispose()
        {
        }
    }

    private sealed class SinkLogger(string categoryName, LogSink sink) : ILogger
    {
        private readonly string _categoryName = categoryName;
        private readonly LogSink _sink = sink;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _sink.Entries.Add(new LogEntry(_categoryName, logLevel, formatter(state, exception), exception?.ToString()));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

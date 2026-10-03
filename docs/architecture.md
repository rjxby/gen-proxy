# Architecture

`gen-proxy` is a stateless HTTP orchestration layer between clients and one or two `llama-runtime` gRPC backends.

It validates Responses-style requests, normalizes input into a runtime prompt, estimates token fit, reduces oversized prompts when configured, calls the generation runtime, and returns a normalized Responses-style envelope.

## Layers

1. HTTP host (`Backend/Api/Host`): owns endpoint mapping, request validation, auth, rate limiting, middleware, OpenAPI, and dependency setup.
2. Service layer (`Backend/Api/Services.*`): owns response generation orchestration, prompt budgeting flow, prompt reduction contracts, and response-format enforcement.
3. Integration layer (`Backend/Api/Integrations.*`): owns `llama-runtime` gRPC transport, capability discovery, token estimation, generation calls, runtime errors, and metrics.

The local stack runner under `Backend/Tools/GenProxy.StackRunner` manages development and smoke runs. It is built separately from `Backend/GenProxy.sln`. API projects and API tests do not reference the tool.

## Request lifecycle

- `POST /v1/responses` accepts a Responses-compatible request body.
- Validation enforces the current API boundary: a required `model`, exactly one structured user message, one or more `input_text` content parts, supported response formats, request limits, and rejected unsupported fields.
- `ResponseCreateRequestMapper` normalizes valid input text parts into a deterministic prompt.
- `ResponseGenerationService` creates a response id, records request metrics, checks required runtime capabilities for JSON schema output, and estimates token usage before generation.
- The generation runtime is called through `IGenerationRuntimeClient`.
- The runtime response is wrapped into the public Responses-style shape by `ResponsesFactory`.

## Prompt budgeting and reduction

- Token estimation is performed against the configured generation runtime before generation.
- If the prompt fits, generation proceeds without reduction.
- If the prompt does not fit, the prompt reduction pipeline runs ordered reducers.
- The optional LLM summarizer uses the prompt-reducer runtime when `PromptReducerRuntime:Enabled` is true.
- Leading truncation remains the fallback reducer.
- Empty or whitespace-only summaries leave the original prompt unchanged so fallback can run. If reduction leaves no non-whitespace input, the service returns `422` before re-estimation or generation.
- After a reducer reports a reduction, the service re-estimates the reduced prompt.
- If the reduced prompt still does not fit, the request fails with `422`.
- Cancellation stops reduction before another reducer can run. Reducer deadlines remain timeout failures and return `504`, rather than becoming prompt-reduction failures with `502`.

## Runtime contract

- Gen Proxy requires one generation `llama-runtime` gRPC service.
- Gen Proxy may use a second prompt-reducer runtime for prompt summarization.
- Runtime addresses are configured with `GenerationRuntime:*` and `PromptReducerRuntime:*` options and must use HTTPS when enabled.
- The gRPC client supports `EstimateTokens`, `GetCapabilities`, and `Generate`.
- Each call sends a runtime-specific deadline. `Timeouts:EstimateTokens` and `Timeouts:GetCapabilities` default to 10 seconds; `Timeouts:Generate` defaults to 120 seconds. The Host also applies `ResponsesTimeout:Timeout`, a 180-second limit for the complete request. Runtime deadlines and the overall timeout return `504`; client cancellation propagates to upstream calls.
- `response_format.type=json_schema` requires generation runtime structured JSON output capability support before generation and is translated to the `llama-runtime v0.7.0` `json` response format with the raw schema payload.
- Schema responses must include runtime trace metadata showing structured output was applied and satisfied, and the generated content must parse as a JSON object.
- Request-level generation options are forwarded to the runtime; support for `temperature`, `top_p`, and `max_output_tokens` is runtime-defined.

## Security, observability, and failures

- Protected requests require the exact API key in `X-API-Key`.
- `Authorization` headers and bearer prefixes are not accepted.
- Development can explicitly opt out of API-key requirements.
- Requests are rate limited by API key or client address.
- The request body limit applies to every accepted Responses route, including trailing slashes, before JSON binding. It covers both Content-Length and chunked bodies. Normalized input size is checked before service execution.
- For HTTP/1.1 chunked requests, Kestrel counts chunk framing toward the body limit, so the JSON payload must leave room for that framing.
- HTTP request and response body logging is disabled by default and only enabled with `ResponsesLogging:LogBodies=true`.
- Upstream runtime failures are normalized into HTTP problem responses.
- OpenAPI/Swagger is available in Development and documents the current Responses API wire shape.

## Local stack

- `make stack-run` starts compatible generation and summarizer runtimes, waits for readiness, then starts the API.
- The stack runner downloads and caches `llama-runtime` release artifacts, writes runtime logs, and records each process ID, UTC start time, and executable path atomically. It verifies that identity before stopping a process and again before escalating to a forced stop. Legacy PID-only files do not prove ownership and never authorize stopping a process.
- The local stack workflow defaults to `llama-runtime v0.7.0` unless `LLAMA_RUNTIME_VERSION` is set explicitly.
- `make smoke` and `make smoke-budget` use the same stack runner for repeatable local verification.

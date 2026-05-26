# Gen Proxy

HTTP reverse proxy for token budgeting and response generation over [`llama-runtime`](https://github.com/rjxby/llama-runtime) gRPC backends.

## Releases

GitHub releases now ship a single macOS ARM64 executable named `gen-proxy-osx-arm64`. Docker is not part of CI or release packaging.

Download the binary from the latest release, make it executable, and provide runtime settings with environment variables:

```bash
chmod +x ./gen-proxy-osx-arm64
ApiKeys__Keys__0=public-api-key \
GenerationRuntime__Address=https://localhost:50051 \
PromptReducerRuntime__Address=https://localhost:50052 \
./gen-proxy-osx-arm64
```

Defaults for request limits, runtime addresses, and prompt-reduction behavior are compiled into the binary. Outside Development, you still need to supply at least one API key through environment variables or another standard ASP.NET Core configuration source.

Compatibility note: the current local stack workflow is pinned to `llama-runtime v0.5.0`.

By default, `gen-proxy` does not log request or response bodies in ASP.NET Core HTTP logs. If you set `ResponsesLogging:LogBodies=true`, `gen-proxy` will include request and response bodies in HTTP logs globally.

## Documentation

- [docs/architecture.md](docs/architecture.md) - current layer boundaries, request lifecycle, runtime contract, and local stack behavior.
- [docs/roadmap.md](docs/roadmap.md) - implemented baseline and planned routing, telemetry, model profile, and operational work.

## Authentication

All protected requests must send the exact API key value in the `X-API-Key` header.

```http
X-API-Key: your-api-key
```

Supported behavior:
- `X-API-Key` is the only accepted API key header.
- `Authorization` is not supported.
- `Bearer` prefixes are not supported.

Configure allowed keys in `Backend/Api/Host/appsettings*.json` during local development or with environment variables such as `ApiKeys__Keys__0`.
If you need unauthenticated local development, set `ApiKeys:AllowUnauthenticatedInDevelopment=true` explicitly in the `Development` environment.

## Responses API

`POST /v1/responses`

Request body:

```json
{
  "model": "stories15m",
  "input": [
    {
      "type": "message",
      "role": "user",
      "content": [
        {
          "type": "input_text",
          "text": "Write a short answer."
        }
      ]
    }
  ],
  "response_format": {
    "type": "text"
  },
  "max_output_tokens": 512,
  "metadata": {
    "trace_id": "123"
  }
}
```

Current behavior:
- `model` is required.
- `model` is echoed back in the response and used for logs/metrics, but it does not currently select or route between different upstream runtimes.
- `input` is required and supports exactly one structured user message item.
- The input message must use `type: "message"`, `role: "user"`, and one or more `input_text` content parts.
- Top-level string input is not supported.
- Multi-turn structured input and non-user structured roles are rejected in this stage.
- `response_format.type` is optional and supports `text` and `json_schema`.
- `json_schema` requests use the OpenAI-compatible nested `json_schema` object and are sent to `llama-runtime v0.5.0` as runtime response format `json` plus the raw schema payload.
- `json_schema` requests are rejected with `400` if the configured generation runtime does not advertise structured JSON output support through `GetCapabilities`.
- `json_schema` requests are rejected with `502` unless the runtime reports that structured output was applied and satisfied, and the returned content is a valid JSON object.
- The runtime supports a strict JSON Schema subset and remains the source of truth for schema-subset validation.
- Example structured format:
  `{"type":"json_schema","json_schema":{"name":"result","schema":{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false},"strict":true}}`
- `temperature`, `top_p`, and `max_output_tokens` are forwarded to the runtime generate call, and support for those fields is runtime-defined.
- In the current local `llama-runtime` stack, request-level generation uses greedy defaults. Non-default `temperature` and `top_p` are rejected.
- `max_output_tokens` is currently a placeholder compatibility field and is accepted only when it matches the runtime's configured `Llama:Native:GenerationMaxNewTokens` value. The local stack default is `512`.
- `tools`, `tool_choice`, and `stream=true` are rejected with `400` in this stage.
- `metadata` is accepted for Responses API compatibility but does not affect runtime behavior.
- `usage.output_tokens` and `usage.total_tokens` are returned from upstream runtime usage when available.

Example response:

```json
{
  "id": "resp_123",
  "object": "response",
  "created_at": 1712000000,
  "status": "completed",
  "model": "runtime-model",
  "output": [
    {
      "id": "msg_123",
      "type": "message",
      "status": "completed",
      "role": "assistant",
      "content": [
        {
          "type": "output_text",
          "text": "Hello"
        }
      ]
    }
  ],
  "output_text": "Hello",
  "usage": {
    "input_tokens": 42,
    "output_tokens": 7,
    "total_tokens": 49
  }
}
```

## Runtime Configuration

`gen-proxy` requires one or two reachable [`llama-runtime`](https://github.com/rjxby/llama-runtime) gRPC services behind it.

The host supports separate runtime settings for generation and an optional prompt-reduction runtime:

- `GenerationRuntime:Address`
  Used for token estimation and final response generation.
- `GenerationRuntime:ApiKey`
  Optional outbound `x-api-key` sent to the generation runtime.
- `PromptReducerRuntime:Address`
  Used by the prompt-reduction pipeline to shorten oversized prompts before generation when the reducer runtime is enabled. Ignored when `PromptReducerRuntime:Enabled` is `false`.
- `PromptReducerRuntime:ApiKey`
  Optional outbound `x-api-key` sent to the prompt-reducer runtime. Ignored when `PromptReducerRuntime:Enabled` is `false`.
- `PromptReducerRuntime:Enabled`
  Boolean toggle for external prompt reduction. When `false`, `gen-proxy` skips the reducer runtime and uses leading truncation as the only reduction strategy.
- `ResponsesLogging:LogBodies`
  Boolean toggle for request and response body logging in ASP.NET Core HTTP logs. Default is `false`. When `true`, request and response bodies are written to HTTP logs verbatim.

Optional prompt-reduction prompt text can be configured in `PromptReduction:SummarizationPromptTemplate`.
Runtime addresses must use `https://`.

Example:

```json
{
  "GenerationRuntime": {
    "Address": "https://localhost:50051",
    "ApiKey": ""
  },
  "PromptReducerRuntime": {
    "Enabled": true,
    "Address": "https://localhost:50052",
    "ApiKey": ""
  },
  "PromptReduction": {
  },
  "ResponsesLogging": {
    "LogBodies": false
  }
}
```

## Architecture Conventions

- `Backend/Api/Host` is the HTTP edge. Namespaces should mirror the folder layout for `Endpoints`, `Middleware`, `Security`, `Validation`, and `Configurations`.
- Setup modules remain under `Backend/Api/Host/Configurations` and represent composition-root wiring, not service/domain logic.
- Prompt reducers must declare explicit execution order through `IPromptReducer.Order`. Lower values run first, and the current pipeline stops at the first reducer that reports a reduction. If the reduced prompt still exceeds budget after re-estimation, the request fails with `422` rather than continuing to later reducers.
- Unit and integration tests are CI-grade checks. `GenProxy.StackRunner` provides local smoke verification modes and should not replace layer-focused automated tests.

## Local Development

`gen-proxy` now uses a local-only workflow. The `Makefile` is the entrypoint for both a proxy-only run and a full local stack that starts two `llama-runtime` processes first.

GitHub Actions follows the same Docker-free approach: CI runs restore, build, and tests, while tagged releases build the macOS binary and publish it as a release asset.

Copy `.env.example` to `.env`, fill in any overrides you want, and keep `.env` out of git.

```bash
cp .env.example .env
```

If `.env` exists, `make` loads and exports those variables automatically.

Example `.env` values:

```env
ApiKeys__Keys__0=public-api-key
GenerationRuntime__Address=https://localhost:50051
GenerationRuntime__ApiKey=runtime-generation-key
PromptReducerRuntime__Enabled=true
PromptReducerRuntime__Address=https://localhost:50052
PromptReducerRuntime__ApiKey=runtime-reducer-key
ResponsesLogging__LogBodies=false
ASPNETCORE_ENVIRONMENT=Development
MAIN_MODEL_PATH=/absolute/path/to/main-model.gguf
MAIN_MODEL_ID=main-local-model
SUMMARIZER_MODEL_PATH=/absolute/path/to/summarizer-model.gguf
SUMMARIZER_MODEL_ID=summarizer-local-model
```

Run only `gen-proxy` against already running runtimes:

```bash
make run
```

Run the full local stack:

```bash
MAIN_MODEL_PATH=/absolute/path/to/main-model.gguf \
MAIN_MODEL_ID=main-local-model \
SUMMARIZER_MODEL_PATH=/absolute/path/to/summarizer-model.gguf \
SUMMARIZER_MODEL_ID=summarizer-local-model \
make stack-run
```

`make stack-run` will:
- download and use `llama-runtime v0.5.0` by default unless `LLAMA_RUNTIME_VERSION` is set explicitly
- cache release artifacts in `.runtime-cache/`
- write runtime logs to `.runtime-logs/`
- write PID files and runtime state to `.runtime-run/`
- supervise the runtimes and API with a dedicated .NET runner instead of a shell script
- start the generation runtime on `localhost:50051`
- start the summarizer runtime on `localhost:50052`
- bind both runtimes on local HTTPS endpoints backed by the ASP.NET Core development certificate
- stop existing managed processes, including the API and both runtimes, before restarting them
- fail if a runtime does not stay healthy for a short post-start window
- start `gen-proxy` locally over HTTPS with `dotnet run`
- stop the API and both managed runtimes when `stack-run` exits, is interrupted, or startup fails

The managed API now runs on `https://localhost:7001` by default.

### Manual Demo

For the quickest local demo, start the stack in one terminal and send prompts from another.

Terminal 1:

```bash
MAIN_MODEL_PATH=/absolute/path/to/main-model.gguf \
MAIN_MODEL_ID=main-local-model \
SUMMARIZER_MODEL_PATH=/absolute/path/to/summarizer-model.gguf \
SUMMARIZER_MODEL_ID=summarizer-local-model \
make stack-run
```

Terminal 2 with the Make wrapper:

```bash
make demo PROMPT="Explain what this proxy does in one paragraph."
make demo DEMO_REQUEST_ARGS=--json PROMPT="Return whether the demo is reachable."
printf 'Summarize this request.\nKeep it to two bullet points.\n' | make demo
```

`make demo` is intentionally small. It only wraps the local demo request helper, does not start the stack, does not wait for readiness, and does not replace `make smoke`.

It defaults to:
- `GEN_PROXY_BASE_URL=https://localhost:7001`
- `GEN_PROXY_API_KEY=dev-local-key`

If you start the stack with a different public API key, set the same value before calling the helper:

```bash
make demo PUBLIC_API_KEY=public-api-key PROMPT="Hello from the local stack."
```

Direct script usage remains available if you want the lower-level helper:

```bash
./scripts/demo-request.sh "Explain what this proxy does in one paragraph."
./scripts/demo-request.sh --json "Return whether the demo is reachable."
printf 'Summarize this request.\nKeep it to two bullet points.\n' | ./scripts/demo-request.sh
```

The `--json` demo path sends the same user prompt with `response_format.type=json_schema` and a minimal schema requiring a string `answer` field. The output shape is controlled by `response_format`; the helper does not rewrite the prompt.

Equivalent raw `curl` request:

```bash
curl --silent --show-error --insecure \
  --header 'Content-Type: application/json' \
  --header 'X-API-Key: dev-local-key' \
  --data '{"model":"stories15m","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"Explain what this proxy does in one paragraph."}]}],"response_format":{"type":"text"},"max_output_tokens":512}' \
  https://localhost:7001/v1/responses
```

Equivalent raw JSON-schema `curl` request with the same prompt:

```bash
curl --silent --show-error --insecure \
  --header 'Content-Type: application/json' \
  --header 'X-API-Key: dev-local-key' \
  --data '{"model":"stories15m","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"Explain what this proxy does in one paragraph."}]}],"response_format":{"type":"json_schema","json_schema":{"name":"demo_answer","schema":{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false},"strict":true}},"max_output_tokens":512}' \
  https://localhost:7001/v1/responses
```

`make smoke` and `make smoke-budget` are self-contained for local HTTPS, but they require a trusted ASP.NET Core development certificate because the managed runtimes also start on HTTPS.

If you want to open the local API manually in a browser or use other HTTPS clients against it, install and trust the ASP.NET Core development certificate:

```bash
dotnet dev-certs https --trust
```

Do this before running `make run`, `make stack-run`, `make smoke`, or `make smoke-budget`.

Run the default self-contained smoke flow:

```bash
make smoke
```

`make smoke` starts the managed local stack, waits for the HTTPS API to come up, runs the basic smoke checks including a minimal JSON schema output validation, and then stops the stack automatically.

Run the constrained-budget smoke flow:

```bash
make smoke-budget
```

`make smoke-budget` starts the managed local stack with a smaller generation context, waits for the HTTPS API to come up, runs the normal smoke checks plus an oversized-prompt `422` check, and then stops the stack automatically.

You can also run the stack runner directly:

```bash
dotnet run --project Backend/Tools/GenProxy.StackRunner/GenProxy.StackRunner.csproj -- smoke basic
dotnet run --project Backend/Tools/GenProxy.StackRunner/GenProxy.StackRunner.csproj -- smoke budget
dotnet run --project Backend/Tools/GenProxy.StackRunner/GenProxy.StackRunner.csproj -- smoke all
```

Optional overrides:

```bash
MAIN_MODEL_PATH=/absolute/path/to/main-model.gguf \
MAIN_MODEL_ID=main-local-model \
SUMMARIZER_MODEL_PATH=/absolute/path/to/summarizer-model.gguf \
SUMMARIZER_MODEL_ID=summarizer-local-model \
LLAMA_RUNTIME_VERSION=v0.5.0 \
LLAMA_RUNTIME_API_KEY=runtime-local-key \
GEN_PROXY_BASE_URL=https://localhost:7001 \
MAIN_WORKER_COUNT=4 \
SUMMARIZER_WORKER_COUNT=1 \
API_STARTUP_TIMEOUT=60 \
make stack-run
```

Set `LLAMA_RUNTIME_VERSION` only when you intentionally want to override the stack runner default. Releases in this line are validated against `v0.5.0`.

Optional smoke overrides:

```bash
ApiKeys__Keys__0=public-api-key \
GEN_PROXY_BASE_URL=https://localhost:7001 \
SMOKE_RUNTIME_STARTUP_TIMEOUT=240 \
SMOKE_MAIN_CONTEXT_SIZE=768 \
SMOKE_SUMMARIZER_CONTEXT_SIZE=4096 \
make smoke
```

For `make smoke-budget`, the stack runner constrains the main generation runtime while keeping the summarizer runtime on a larger context window so the reducer can process the oversized prompt before the API returns the expected `422`.

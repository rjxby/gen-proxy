# Gen Proxy

Stateless HTTP orchestration layer for token budgeting, prompt reduction, and response generation over [`llama-runtime`](https://github.com/rjxby/llama-runtime) gRPC backends.

## Releases

GitHub releases ship a single macOS ARM64 executable named `gen-proxy-osx-arm64`.

Download the binary from the latest release, make it executable, and provide runtime settings with environment variables:

```bash
chmod +x ./gen-proxy-osx-arm64
ApiKeys__Keys__0=public-api-key \
GenerationRuntime__Address=https://localhost:50051 \
PromptReducerRuntime__Address=https://localhost:50052 \
./gen-proxy-osx-arm64
```

Defaults for request limits, runtime addresses, and prompt-reduction behavior are compiled into the binary. Outside Development, you still need to supply at least one API key through environment variables or another standard ASP.NET Core configuration source.

Compatibility note: the current local stack workflow is pinned to `llama-runtime v0.7.1`.

## Documentation

- [Architecture](docs/architecture.md) covers the system diagram, layer boundaries, request lifecycle, runtime contract, and local stack internals.
- [Backlog](docs/backlog.md) tracks planned work with priorities, dependencies, and acceptance criteria, separate from the implemented baseline.

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
- `model` does not select a runtime. The response uses the runtime's model name when available and falls back to the requested value.
- `input` is required and supports exactly one structured user message item.
- The input message must use `type: "message"`, `role: "user"`, and one or more `input_text` content parts.
- Top-level string input is not supported.
- Multi-turn structured input and non-user structured roles are rejected in this stage.
- `response_format.type` is optional and supports `text` and `json_schema`.
- `json_schema` requests use the OpenAI-compatible nested `json_schema` object.
- `json_schema` requests are rejected with `400` if the configured generation runtime does not support structured JSON output.
- `json_schema` requests are rejected with `502` unless the runtime reports that structured output was applied and satisfied, and the returned content is a valid JSON object.
- The runtime supports a strict JSON Schema subset and remains the source of truth for schema-subset validation.
- Example structured format:
  `{"type":"json_schema","json_schema":{"name":"result","schema":{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false},"strict":true}}`
- Support for `temperature`, `top_p`, and `max_output_tokens` depends on the configured runtime.
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

## Runtime configuration

`gen-proxy` requires one or two reachable [`llama-runtime`](https://github.com/rjxby/llama-runtime) gRPC services behind it.

The host supports separate runtime settings for generation and an optional prompt-reduction runtime:

- `GenerationRuntime:Address`
  Used for token estimation and final response generation.
- `GenerationRuntime:ApiKey`
  Optional outbound `x-api-key` sent to the generation runtime.
- `PromptReducerRuntime:Address`
  Runtime for shortening oversized prompts. Ignored when `PromptReducerRuntime:Enabled` is `false`.
- `PromptReducerRuntime:ApiKey`
  Optional outbound `x-api-key` sent to the prompt-reducer runtime. Ignored when `PromptReducerRuntime:Enabled` is `false`.
- `PromptReducerRuntime:Enabled`
  Boolean toggle for external prompt reduction. When `false`, `gen-proxy` skips the reducer runtime and uses leading truncation as the only reduction strategy.
- `ResponsesLogging:LogBodies`
  Boolean toggle for request and response body logging in ASP.NET Core HTTP logs. Default is `false`. When `true`, logs can contain prompt and completion text, up to 4096 bytes per body.

Optional prompt-reduction prompt text can be configured in `PromptReduction:SummarizationPromptTemplate`.
Enabled runtime addresses must be absolute `https://` URIs.

Each runtime has independent operation timeouts under `GenerationRuntime:Timeouts` or `PromptReducerRuntime:Timeouts`. `EstimateTokens` and `GetCapabilities` default to `00:00:10`; `Generate` defaults to `00:02:00`. The overall Responses request limit, `ResponsesTimeout:Timeout`, defaults to `00:03:00`. All timeouts must be positive and no longer than one day.

Timeouts return `504` problem responses with a `trace_id`. ASP.NET Core disables the overall timeout while a debugger is attached; runtime operation timeouts still apply.

Environment overrides include `GenerationRuntime__Timeouts__Generate=00:02:00` and `ResponsesTimeout__Timeout=00:03:00`.

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
  "ResponsesLogging": {
    "LogBodies": false
  }
}
```

## Local development

Use the .NET 10 SDK. Restore, build, and run the API unit and integration tests with:

```bash
dotnet restore Backend/GenProxy.sln
dotnet build Backend/GenProxy.sln -c Release --no-restore
dotnet test Backend/GenProxy.sln -c Release --no-build
```

For local inference, provide GGUF model files and trust the ASP.NET Core development certificate before running the API or stack:

```bash
dotnet dev-certs https --trust
```

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

Run `gen-proxy` against already running local runtimes:

```bash
make run
```

`make run` sets the runtime addresses from `GENERATION_RUNTIME_PORT` and `SUMMARIZER_RUNTIME_PORT`, enables the reducer, and uses `LLAMA_RUNTIME_API_KEY` for both runtimes. The key defaults to `runtime-local-key`. To use remote runtimes, separate runtime keys, or disable the reducer, set the [runtime configuration](#runtime-configuration) and run the API directly:

```bash
dotnet run --launch-profile https --project Backend/Api/Host/GenProxy.Api.Host.csproj
```

Direct `dotnet run` does not load `.env`; export the settings in your shell first.

Run the full local stack:

```bash
MAIN_MODEL_PATH=/absolute/path/to/main-model.gguf \
MAIN_MODEL_ID=main-local-model \
SUMMARIZER_MODEL_PATH=/absolute/path/to/summarizer-model.gguf \
SUMMARIZER_MODEL_ID=summarizer-local-model \
make stack-run
```

The default endpoints are `https://localhost:50051` for generation, `https://localhost:50052` for summarization, and `https://localhost:7001` for the API. The runner stops the stack on exit, interruption, startup failure, or an unexpected runtime exit. See [local stack internals](docs/architecture.md#local-stack) for startup, cache, logs, and process ownership.

Before starting, check `.runtime-run/*.pid`; startup stops the processes tracked there. Use a separate checkout and distinct `GENERATION_RUNTIME_PORT`, `SUMMARIZER_RUNTIME_PORT`, and `GEN_PROXY_BASE_URL` values for a parallel stack. If processes from an older runner remain active, stop them manually before restarting. See [process ownership](docs/architecture.md#process-ownership) for the PID record format.

### Manual demo

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

`make demo` sends a request to an already running stack. Use `make smoke` for automated checks.

The helper passes prompt text literally, including quotes, backticks, dollar signs, and newlines.

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

### Smoke checks

Use `make smoke` for automated live verification, or `make stack-run` followed by `make demo PROMPT="..."` for a manual request. These use the same local settings and runtime cache. No model hashes or separate E2E report are required. See [live verification](docs/quality.md#live-verification).

Run the default self-contained smoke flow:

```bash
make smoke
```

`make smoke` starts the managed local stack, waits for the HTTPS API to come up, runs the basic smoke checks including a minimal JSON schema output validation, and then stops the stack automatically. Smoke refuses to replace an active tracked stack; use `make demo` against it or a separate checkout with distinct ports.

Run the constrained-budget smoke flow:

```bash
make smoke-budget
```

`make smoke-budget` starts the managed local stack with a smaller generation context, runs the oversized-prompt `422` check, and stops the stack automatically. It runs only the budget scenario. Use `smoke all` below to run both suites.

You can also run the stack runner directly:

```bash
dotnet run --project Backend/Tools/GenProxy.StackRunner/GenProxy.StackRunner.csproj -- smoke basic
dotnet run --project Backend/Tools/GenProxy.StackRunner/GenProxy.StackRunner.csproj -- smoke budget
dotnet run --project Backend/Tools/GenProxy.StackRunner/GenProxy.StackRunner.csproj -- smoke all
```

### Local overrides

```bash
MAIN_MODEL_PATH=/absolute/path/to/main-model.gguf \
MAIN_MODEL_ID=main-local-model \
SUMMARIZER_MODEL_PATH=/absolute/path/to/summarizer-model.gguf \
SUMMARIZER_MODEL_ID=summarizer-local-model \
LLAMA_RUNTIME_VERSION=v0.7.1 \
LLAMA_RUNTIME_API_KEY=runtime-local-key \
GEN_PROXY_BASE_URL=https://localhost:7001 \
MAIN_WORKER_COUNT=4 \
SUMMARIZER_WORKER_COUNT=1 \
API_STARTUP_TIMEOUT=60 \
make stack-run
```

Set `LLAMA_RUNTIME_VERSION` only when you intentionally want to override the stack runner default. The default is `v0.7.1`. Run the [smoke checks](docs/quality.md#live-verification) to verify an override with your local models.

Optional smoke overrides:

```bash
ApiKeys__Keys__0=public-api-key \
GEN_PROXY_BASE_URL=https://localhost:7001 \
SMOKE_RUNTIME_STARTUP_TIMEOUT=240 \
SMOKE_MAIN_CONTEXT_SIZE=768 \
SMOKE_SUMMARIZER_CONTEXT_SIZE=4096 \
make smoke
```

For `make smoke-budget`, the stack runner constrains the main generation runtime while keeping the summarizer runtime on a larger context window so oversized prompts exercise the expected `422` path instead of reaching final generation.

## Agent quality checks

Use `make verify-fast` while editing and `make verify` before finishing a code change. The fast command selects affected tests and falls back to the full suites for shared or unknown changes. Neither command starts inference servers. Both reject zero discovered tests.

See [quality checks](docs/quality.md) for fixture review, live smoke checks, and agent reporting rules. Prompt-reduction evals are deferred to [GP-014](docs/backlog.md#gp-014-prompt-reduction-evals).

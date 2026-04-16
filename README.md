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

Compatibility note: the current local stack workflow is pinned to `llama-runtime v0.1.2`.

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
  "model": "gpt-5.1",
  "input": "Write a short answer.",
  "max_output_tokens": 128,
  "metadata": {
    "trace_id": "123"
  }
}
```

Current behavior:
- `model` is required.
- `model` is echoed back in the response and used for logs/metrics, but it does not currently select or route between different upstream runtimes.
- `input` is required.
- `max_output_tokens` is accepted for Responses API compatibility but currently ignored by the runtime.
- `metadata` is accepted for Responses API compatibility but currently ignored by the runtime.
- This endpoint is a partial Responses API compatibility surface. `output_tokens` and `total_tokens` are currently returned as `null` because the upstream runtime does not yet provide that accounting.

Example response:

```json
{
  "id": "resp_123",
  "object": "response",
  "created_at": 1712000000,
  "status": "completed",
  "model": "gpt-5.1",
  "output": [
    {
      "id": "msg_123",
      "type": "message",
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
    "output_tokens": null,
    "total_tokens": null
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
  Used by the prompt-reduction pipeline to shorten oversized prompts before generation when the reducer runtime is enabled.
- `PromptReducerRuntime:ApiKey`
  Optional outbound `x-api-key` sent to the prompt-reducer runtime.
- `PromptReduction:UsePromptReducerRuntime`
  Boolean toggle for external prompt reduction. When `false`, `gen-proxy` skips the reducer runtime and uses leading truncation as the only reduction strategy.

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
    "Address": "https://localhost:50052",
    "ApiKey": ""
  },
  "PromptReduction": {
    "UsePromptReducerRuntime": true
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
PromptReducerRuntime__Address=https://localhost:50052
PromptReducerRuntime__ApiKey=runtime-reducer-key
PromptReduction__UsePromptReducerRuntime=true
ASPNETCORE_ENVIRONMENT=Development
```

Run only `gen-proxy` against already running runtimes:

```bash
make run
```

Run the full local stack:

```bash
MAIN_MODEL_PATH=/absolute/path/to/main-model.gguf \
SUMMARIZER_MODEL_PATH=/absolute/path/to/summarizer-model.gguf \
make stack-run
```

`make stack-run` will:
- download and use `llama-runtime v0.1.2` by default unless `LLAMA_RUNTIME_VERSION` is set explicitly
- cache release artifacts in `.runtime-cache/`
- write runtime logs to `.runtime-logs/`
- write PID files and runtime state to `.runtime-run/`
- supervise the runtimes and API with a dedicated .NET runner instead of a shell script
- start the generation runtime on `localhost:50051`
- start the summarizer runtime on `localhost:50052`
- bind both runtimes on local HTTPS endpoints backed by the ASP.NET Core development certificate
- stop existing managed runtime processes before restarting them
- fail if a runtime does not stay healthy for a short post-start window
- start `gen-proxy` locally over HTTPS with `dotnet run`
- stop the API and both managed runtimes when `stack-run` exits, is interrupted, or startup fails

The managed API now runs on `https://localhost:7001` by default.

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

`make smoke` starts the managed local stack, waits for the HTTPS API to come up, runs the basic status-only smoke checks, and then stops the stack automatically.

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
SUMMARIZER_MODEL_PATH=/absolute/path/to/summarizer-model.gguf \
LLAMA_RUNTIME_VERSION=v0.1.2 \
LLAMA_RUNTIME_API_KEY=runtime-local-key \
GEN_PROXY_BASE_URL=https://localhost:7001 \
MAIN_WORKER_COUNT=4 \
SUMMARIZER_WORKER_COUNT=1 \
API_STARTUP_TIMEOUT=60 \
make stack-run
```

Set `LLAMA_RUNTIME_VERSION` only when you intentionally want to override the tested default. Releases in this line are validated against `v0.1.2`.

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

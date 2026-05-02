# Gen Proxy

Stateless HTTP orchestration layer for token budgeting, prompt reduction, and response generation over `llama-runtime` gRPC backends.

## Commands

- Run the API against already running runtimes: `make run`
- Start the full local stack: `make stack-run`
- Send a demo request to a running stack: `make demo`
- Run basic local smoke checks: `make smoke`
- Run constrained-budget smoke checks: `make smoke-budget`
- Run all managed tests: `dotnet test Backend/GenProxy.sln --no-restore`
- Run unit tests only: `dotnet test Backend/Tests/GenProxy.Api.UnitTests/GenProxy.Api.UnitTests.csproj --no-restore`
- Run integration tests only: `dotnet test Backend/Tests/GenProxy.Api.IntegrationTests/GenProxy.Api.IntegrationTests.csproj --no-restore`

## Project Structure

- `Backend/Api/Host/`: HTTP edge, endpoint mapping, validation, auth, rate limiting, middleware, OpenAPI, and composition root setup.
- `Backend/Api/Services/Contracts/`: service abstractions, response generation contracts, prompt reduction contracts, and service-level exceptions.
- `Backend/Api/Services/Implementation/`: response generation orchestration and prompt reduction pipeline implementations.
- `Backend/Api/Integrations/Contracts/`: runtime client contracts, runtime-facing models, metrics, and integration exceptions.
- `Backend/Api/Integrations/Implementation/`: `llama-runtime` gRPC transport and client adapters.
- `Backend/Tests/`: xUnit unit and integration tests.
- `Backend/Tools/GenProxy.StackRunner/`: local stack supervisor for downloading and running compatible `llama-runtime` releases with the API.
- `docs/`: architecture notes and product roadmap.

## Invariants

- Gen Proxy does not run inference directly; inference stays behind `llama-runtime` gRPC services.
- Gen Proxy is stateless for conversation state. It validates and normalizes a single request, then returns a normalized response.
- `POST /v1/responses` currently supports exactly one structured user message and one or more `input_text` parts.
- `tools`, `tool_choice`, multi-turn input, and `stream=true` are intentionally rejected in the current API stage.
- Runtime addresses must be absolute HTTPS URIs when configured.
- The generation runtime is required. The prompt-reducer runtime is optional and controlled by `PromptReducerRuntime:Enabled`.
- Request and response bodies are not logged by default. Only enable body logging intentionally with `ResponsesLogging:LogBodies=true`.
- Prompt reducers declare deterministic execution order with `IPromptReducer.Order`; lower values run first.
- If prompt reduction still does not fit the runtime budget after re-estimation, the request fails with `422`.
- `response_format.type=json_object` depends on runtime capability discovery and must be validated against runtime trace and parsed JSON object output.

## Change Guidance

- Preserve layer boundaries: Host handles HTTP concerns, Services orchestrate domain behavior, Integrations adapt gRPC runtimes.
- Prefer existing options binding and environment-variable naming patterns over new configuration paths.
- Keep setup modules under `Backend/Api/Host/Configurations` as composition-root wiring, not business logic.
- Keep runtime-specific behavior behind integration contracts and adapters.
- Keep prompt and generated-text payloads out of normal structured logs.
- Add or update xUnit tests for behavior changes.
- Treat `GenProxy.StackRunner` as local smoke infrastructure, not a replacement for focused unit and integration coverage.
- The local stack workflow is pinned to `llama-runtime v0.4.0` unless `LLAMA_RUNTIME_VERSION` is set explicitly.

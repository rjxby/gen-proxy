# Repository Instructions

This repository is a stateless HTTP proxy for token budgeting, prompt reduction, and response generation over `llama-runtime` gRPC backends. Optimize for small, correct changes that preserve the current API contract and layer boundaries.

## Use These Commands

- Run against existing runtimes: `make run`
- Start the full local stack: `make stack-run`
- Send a demo request: `make demo`
- Run smoke checks: `make smoke`
- Run constrained-budget smoke checks: `make smoke-budget`
- Run managed tests: `dotnet test Backend/GenProxy.sln --no-restore`

## Repo Boundaries

- `Backend/Api/Host/`: HTTP endpoints, validation, middleware, security, rate limiting, OpenAPI, and dependency setup.
- `Backend/Api/Services.*`: response generation orchestration, prompt reduction contracts, and prompt reducer implementations.
- `Backend/Api/Integrations.*`: `llama-runtime` gRPC clients, runtime contracts, runtime models, metrics, and integration exceptions.
- `Backend/Tests/`: xUnit unit and integration tests.
- `Backend/Tools/GenProxy.StackRunner/`: local stack runner and smoke workflow for compatible `llama-runtime` releases.

## Invariants To Preserve

- Gen Proxy does not execute inference directly and does not own conversation state.
- The public generation surface is `POST /v1/responses`.
- Current request support is intentionally narrow: one structured user message with `input_text` parts.
- `tools`, `tool_choice`, streaming, multi-turn input, and non-user structured roles remain rejected until explicitly implemented.
- Runtime endpoints must use HTTPS when enabled.
- Keep prompt and generated response payloads out of default logs.
- Prompt reduction must re-estimate the reduced prompt before generation.
- JSON-object responses require runtime capability support, satisfied runtime trace metadata, and valid JSON object content.

## Change Expectations

- Prefer existing options binding and environment-variable conventions over ad hoc config.
- Keep HTTP concerns in Host, orchestration in Services, and gRPC-specific behavior in Integrations.
- Add or update xUnit tests for behavior changes.
- The local stack workflow defaults to `llama-runtime v0.4.0`; change that compatibility note only when the stack runner and docs are validated together.

# Gen Proxy Roadmap

Gen Proxy is the stateless orchestration and routing layer between Scrullud and one or more `llama-runtime` gRPC backends.

It does not execute tools, own conversation state, or run inference directly. Its responsibilities are to validate canonical requests, estimate token fit, reduce oversized prompts, call the configured runtime, normalize responses, collect operational signals, and eventually route across multiple runtimes.

## Current Implemented Baseline

The original roadmap included several early stages that are now implemented. They are kept here as baseline behavior instead of future work.

### Responses API boundary

- `POST /v1/responses` is the single public generation endpoint.
- Requests require `model` and structured `input`.
- Current input support is intentionally narrow: exactly one `message` item with role `user` and one or more `input_text` content parts.
- `response_format`, `temperature`, `top_p`, `max_output_tokens`, and `metadata` are accepted.
- `tools`, `tool_choice`, and `stream=true` are explicitly rejected.
- Responses are returned in a Responses-style envelope with `id`, `object`, `created_at`, `status`, `model`, `output`, `output_text`, and `usage`.

### Validation and normalization

- Request validation rejects missing or invalid model names, invalid structured input, unsupported content types, unsupported response formats, invalid generation overrides, oversized normalized input, oversized metadata, `tools`, `tool_choice`, and streaming.
- Valid input text parts are normalized into a deterministic prompt by joining content parts with newlines.
- Validation failures return ASP.NET validation problem responses with field-level errors.

### Runtime client and single-runtime generation

- The app is configured with one generation runtime and an optional prompt-reducer runtime.
- Runtime addresses are validated at startup and must use HTTPS when enabled.
- The gRPC client supports:
  - `EstimateTokens`
  - `GetCapabilities`
  - `Generate`
- Generation forwards response format and supported request-level generation options.
- Upstream runtime failures are normalized into HTTP problem responses.

### Token budgeting and prompt reduction

- The generation flow estimates tokens before generation.
- If a prompt does not fit, it runs the prompt reduction pipeline and re-estimates the reduced prompt.
- If reduction still does not fit, the request fails with `422`.
- The prompt reduction pipeline supports ordered reducers and stops after the first successful reduction.
- Current reducers:
  - LLM summarizer through the optional prompt-reducer runtime
  - leading truncation fallback
- Prompt reduction emits counters tagged by strategy.

### Response normalization and structured JSON object handling

- Plain runtime output is wrapped as an assistant message with `output_text`.
- Runtime usage is copied into the Responses API usage envelope when available.
- `response_format.type=json_object` checks runtime capabilities before generation.
- JSON-object responses must include a runtime trace showing structured output was applied and satisfied.
- JSON-object responses are parsed and must be valid JSON objects; otherwise the request fails with `502`.

### Security, limits, logging, and OpenAPI

- Protected endpoints require the exact API key in `X-API-Key`.
- `Authorization` and bearer prefixes are not supported.
- Development can explicitly opt out of API-key requirements.
- Requests are rate limited per API key or client address.
- Request body size and normalized input size are limited.
- HTTP request/response body logging is disabled by default and can be enabled with `ResponsesLogging:LogBodies=true`.
- Swagger/OpenAPI is available in Development and documents the current Responses API wire shape.

## Remaining Roadmap

## Step 1 - Add runtime registry

### Goal

Allow Gen Proxy to manage more than one generation runtime before routing.

### What to implement

- Runtime registry configuration with stable runtime ids.
- Enabled/disabled runtime state.
- Startup registration from config.
- Runtime health checks.
- Runtime availability status.
- Diagnostics endpoint for the registry.

Suggested config:

```json
{
  "runtimes": [
    {
      "id": "local-gemma",
      "endpoint": "https://localhost:50051",
      "model_id": "gemma-3-27b-it",
      "enabled": true
    },
    {
      "id": "local-stories",
      "endpoint": "https://localhost:50052",
      "model_id": "stories-15m",
      "enabled": true
    }
  ]
}
```

### Verify at end

- Proxy loads all configured runtimes.
- Disabled runtimes are ignored.
- Unhealthy runtimes are not selected.
- Diagnostics show current runtime state.

### Done means

Proxy can reason about available generation runtimes before choosing a target.

## Step 2 - Add capability cache

### Goal

Cache model and runtime capabilities instead of querying only inside request handling.

### What to implement

- Fetch `GetCapabilities` on startup and periodically.
- Cache returned capabilities per runtime.
- Mark stale capabilities.
- Refresh capabilities after runtime restart.
- Expose a capability snapshot through diagnostics.

Capability fields to cache:

- model id
- context size
- supports structured output
- supports JSON object output
- supports speculative decoding
- tokenizer family
- streaming support when the runtime exposes it

### Verify at end

- Proxy can show capabilities for every runtime.
- Routing can access the cache.
- If a runtime is down, capability status becomes unavailable.
- If a runtime changes model, the cache updates.

### Done means

Proxy does not hardcode or re-fetch basic model capability assumptions for every routing decision.

## Step 3 - Introduce multi-runtime routing

### Goal

Pick a runtime/model deterministically and explainably.

### What to implement

- `RoutingEngine`.
- Candidate filtering by enabled state, health, and hard capabilities.
- Token-fit checks per candidate runtime.
- Routing hints and profile preferences.
- Selected runtime trace.
- Rejection reasons for skipped runtimes.

Hard filters:

- unavailable runtime is excluded
- `json_object` requires JSON-object capability
- future `json_schema` requests require schema-capable runtime
- future `stream=true` requests require streaming-capable runtime
- context overflow requires reduction or failure

Routing trace example:

```json
{
  "selected_runtime": "local-gemma",
  "selected_model": "gemma-3-27b-it",
  "candidates": ["local-gemma", "local-kimi"],
  "rejected": [
    {
      "runtime": "local-stories",
      "reason": "context_size_too_small"
    }
  ],
  "reduction_used": false
}
```

### Verify at end

- Routing chooses the expected runtime for text.
- Routing chooses a JSON-capable runtime for JSON object output.
- Routing rejects unhealthy runtimes.
- Routing explains every rejection.

### Done means

Every runtime choice is deterministic and inspectable.

## Step 4 - Formalize token budgeting as a reusable service

### Goal

Move the current inline token-fit logic into a reusable service that works across candidate runtimes.

### What to implement

- `TokenBudgetService`.
- Token estimation for each candidate runtime.
- Reserved output-token handling.
- Fit status object shared by routing and reduction.
- Tests for single-runtime and multi-runtime fit decisions.

Suggested object:

```json
{
  "runtime_id": "local-gemma",
  "model_id": "gemma-3-27b-it",
  "input_tokens": 6420,
  "reserved_output_tokens": 512,
  "context_size": 8192,
  "max_allowed_input_tokens": 7680,
  "fits": true
}
```

### Verify at end

- Small request fits.
- Oversized request does not fit.
- Reserved output tokens are respected.
- Routing and reduction use the same budget result.

### Done means

Token budgeting is explicit, reusable, and no longer coupled to the single-runtime generation service.

## Step 5 - Expand canonical request mapping

### Goal

Support richer canonical inputs without changing the public boundary later.

### What to implement

- Request mapper for multi-turn `system`, `developer`, `user`, and `assistant` messages.
- Stable prompt serialization with turn labels.
- Support for tool definitions as data once tool-call parsing is added.
- Support for structured-output schema metadata once schema output is added.
- Tests proving token estimation and generation receive the same normalized prompt.

### Verify at end

- Multi-turn conversation maps to the expected prompt.
- Message ordering is preserved.
- Future tool and schema metadata have a clear mapping path.
- Existing single-user-message behavior remains compatible.

### Done means

The proxy can map the full canonical request shape to runtime inputs in one maintained place.

## Step 6 - Add richer response normalization

### Goal

Normalize runtime output into one stable response shape regardless of runtime details.

### What to implement

- Dedicated `ResponseNormalizer`.
- Plain text assistant messages.
- Runtime trace attachment when exposed publicly.
- Structured output validation beyond JSON object.
- Future tool-call parsing.
- Canonical error mapping for runtime and validation failures.

### Verify at end

- Plain text response normalizes correctly.
- JSON object response validates correctly.
- Future tool-call output becomes canonical `tool_call`.
- Runtime errors become canonical errors.

### Done means

Scrullud receives one stable response shape while runtime-specific details stay behind the adapter.

## Step 7 - Improve reduction trace and orchestration

### Goal

Make the existing prompt reduction pipeline fully observable and routing-aware.

### What to implement

- Reduction coordinator that works with the routing engine.
- Reducer runtime/model selection.
- Original and reduced token counts.
- Reduction trace in logs, metrics, and optionally response metadata.
- Clear fallback behavior when the reducer runtime fails or cannot fit the reduction prompt.

Reduction trace example:

```json
{
  "reduction_used": true,
  "reducer_runtime": "local-stories",
  "reducer_model": "stories-15m",
  "original_input_tokens": 24000,
  "reduced_input_tokens": 7200,
  "strategy": "llm_summarizer"
}
```

### Verify at end

- Oversized requests trigger reduction.
- Reduced requests are rerouted or generated only after re-estimation.
- Trace shows original and reduced token counts.
- Reduction failure returns a clear error or configured fallback.

### Done means

Token overflow handling is explicit, testable, observable, and compatible with multi-runtime routing.

## Step 8 - Add model profiles

### Goal

Support many open-weight models without scattering model-specific logic.

### What to implement

- Model profile config.
- Profile matching by model id.
- Routing preferences.
- Prompt-template selection.
- Known limitations and default generation options.

Example:

```json
{
  "profile_name": "generic-json-capable-medium",
  "match": ["gemma*", "kimi*"],
  "preferred_for": ["summarization", "structured_output"],
  "avoid_for": ["high_frequency_tool_calls"],
  "structured_output_reliability": "medium",
  "latency_tier": "medium",
  "prompt_template": "chatml"
}
```

### Verify at end

- New model can be assigned a profile.
- Routing uses profile preferences.
- Hard runtime capabilities still override profiles.
- Profile config changes do not require code changes.

### Done means

Model behavior is controlled by profiles and capabilities, not hardcoded branches.

## Step 9 - Add streaming support

### Goal

Allow clients to consume partial responses.

### What to implement

- Support `stream=true`.
- Runtime streaming client once `llama-runtime` exposes streaming.
- Streaming response normalizer.
- Streaming event types.
- Runtime capability validation for streaming.

Event types:

- `response.started`
- `response.output_text.delta`
- `response.tool_call.delta`
- `response.completed`
- `response.failed`

### Verify at end

- Text streaming works.
- Streamed errors are normalized.
- Non-streaming runtimes are rejected for streaming requests.
- Clients can reconstruct the final response from events.

### Done means

Streaming is supported without changing ownership boundaries.

## Step 10 - Add telemetry storage

### Goal

Persist data needed for debugging, benchmarking, and future routing decisions.

### What to implement

- Storage for request, routing, runtime attempt, reduction, and schema-validation events.
- Queryable runtime latency, failure, and structured-output metrics.
- Correlation with request id and trace id.
- Retention and privacy rules for stored data.

Suggested tables:

- `requests`
- `runtime_attempts`
- `routing_decisions`
- `reduction_events`
- `schema_validation_events`

### Verify at end

You can query:

- average latency per model
- schema success rate per model
- reduction frequency
- failure rate per runtime
- token estimate versus actual usage

### Done means

Proxy behavior can be evaluated from stored data, not just live logs and metrics.

## Step 11 - Add score-based routing

### Goal

Use telemetry to improve routing automatically while keeping hard filters first.

### What to implement

- Score calculation job.
- Minimum sample size rules.
- Safe default scores for new models.
- Score dimensions for reliability, latency, throughput, schema success rate, reduction success rate, runtime availability, and client-reported quality if Scrullud sends it.

Example:

```text
route_score =
  0.30 * reliability +
  0.25 * schema_success_rate +
  0.20 * latency_score +
  0.15 * throughput_score +
  0.10 * reduction_quality
```

### Verify at end

- Low-performing models are selected less often after enough samples.
- High-performing models are preferred after hard filters.
- New models use safe default scores.
- Every score-based decision remains explainable.

### Done means

Routing becomes data-informed without becoming opaque.

## Step 12 - Add diagnostics and developer UX

### Goal

Make Gen Proxy easy to operate and debug.

### What to implement

Endpoints:

- `GET /health`
- `GET /v1/runtimes`
- `GET /v1/runtimes/{id}/capabilities`
- `GET /v1/models`
- `GET /v1/routing/explain`
- `GET /v1/metrics`

Docs:

- runtime configuration docs
- routing docs
- reduction docs
- model profile docs
- telemetry docs
- request and response examples

### Verify at end

A developer can answer:

- which runtimes are online?
- what models are available?
- why was a model selected?
- did reduction happen?
- did structured output fail?
- is speculative decoding active?

### Done means

Gen Proxy is understandable without reading source code.

## Gen Proxy v1 Definition of Done

Gen Proxy v1 is done when:

- it accepts canonical requests
- it validates and normalizes requests
- it discovers and caches runtime capabilities
- it routes deterministically across configured runtimes
- it performs token budgeting across candidates
- it performs reduction when needed
- it does not execute tools
- it normalizes runtime responses
- it emits routing and reduction traces
- it stores telemetry needed for evaluation
- it has clear diagnostics

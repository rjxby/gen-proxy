# Gen Proxy backlog

This backlog tracks planned work for the stateless HTTP gateway over `llama-runtime` gRPC backends. The [README](../README.md) describes supported API behavior and configuration. The [architecture](architecture.md) describes the current implementation.

Priorities are proposed implementation order, with P1 before P2 before P3. All items are planned unless marked blocked. Dependencies identify prerequisites, not release dates. Checkboxes record acceptance criteria; complete an item only after its checks pass and its documentation reflects the shipped behavior.

Gen Proxy continues to delegate inference to runtimes. Clients own conversation state, tool execution, and the tool loop. Normal logs and telemetry must keep prompt text, generated text, and credentials out of stored records.

## Implemented baseline

These capabilities already exist and are not pending backlog items:

- `POST /v1/responses` accepts exactly one structured user message with one or more `input_text` parts and returns a Responses-style envelope.
- Validation and normalization enforce input, metadata, response-format, generation-option, and request-size limits. Unsupported tools, multi-turn input, and streaming are rejected.
- One required generation runtime and an optional prompt-reducer runtime use HTTPS gRPC. Runtime failures, deadlines, and request timeouts map to HTTP problem responses.
- Token estimation precedes generation. Ordered summarization and leading truncation can reduce oversized prompts; re-estimation must fit or the request fails with `422`.
- JSON schema output requires runtime capability support, a successful structured-output trace, and a parsed JSON object. The adapter translates the request to the pinned runtime format.
- API-key authentication, rate limiting, opt-in body logging, Development OpenAPI, local stack supervision, and smoke workflows are available.

See [quality checks](quality.md) for deterministic tests, contract fixtures, and live smoke verification. Prompt-reduction evals are deferred to [GP-014](#gp-014-prompt-reduction-evals). The local stack defaults to `llama-runtime v0.7.1` unless explicitly overridden.

## Work queue

| ID | Work item | Priority | Status | Depends on |
| --- | --- | --- | --- | --- |
| [GP-001](#gp-001-runtime-registry) | Runtime registry | P1 | Planned | None |
| [GP-002](#gp-002-capability-cache) | Capability cache | P1 | Planned | GP-001 |
| [GP-003](#gp-003-reusable-token-budgeting) | Reusable token budgeting | P1 | Planned | None for extraction; GP-001 for multiple runtimes |
| [GP-004](#gp-004-deterministic-runtime-routing) | Deterministic runtime routing | P1 | Planned | GP-001, GP-002, GP-003 |
| [GP-005](#gp-005-canonical-request-mapping) | Canonical request mapping | P2 | Planned | None |
| [GP-006](#gp-006-response-normalization) | Response normalization | P2 | Planned | GP-007 for tool-call output |
| [GP-007](#gp-007-tool-call-transport) | Tool-call transport | P2 | Planned | GP-003, GP-005; GP-004 for routing |
| [GP-008](#gp-008-reduction-coordination-and-traces) | Reduction coordination and traces | P2 | Planned | GP-003, GP-004 |
| [GP-009](#gp-009-model-profiles) | Model profiles | P2 | Planned | GP-002, GP-004 |
| [GP-010](#gp-010-streaming) | Streaming | P3 | Blocked on runtime streaming contract | GP-002, GP-006; runtime support |
| [GP-011](#gp-011-telemetry-storage) | Telemetry storage | P3 | Planned | GP-004, GP-008 |
| [GP-012](#gp-012-score-based-routing) | Score-based routing | P3 | Planned | GP-009, GP-011 |
| [GP-013](#gp-013-diagnostics-and-operator-documentation) | Diagnostics and operator documentation | P2 | Planned | Deliver alongside GP-001, GP-002, GP-004, GP-008 |
| [GP-014](#gp-014-prompt-reduction-evals) | Prompt-reduction evals | P3 | Deferred | None |

## GP-001 Runtime registry

Support multiple configured generation runtimes while preserving the existing single-runtime setup.

Scope:

- Register runtimes from configuration with stable IDs, model IDs, HTTPS endpoints, and enabled state.
- Track health and availability separately from configured enabled state.
- Expose registry state through protected diagnostics.
- Follow the existing options-binding and environment-variable naming conventions. Define the configuration contract before adding new settings.

Acceptance criteria:

- [ ] Every enabled configured runtime appears in the registry with a stable ID.
- [ ] Disabled and unhealthy runtimes cannot be selected for generation.
- [ ] Diagnostics distinguish disabled, healthy, and unavailable runtimes.
- [ ] Existing single-runtime configuration remains supported, with documented migration if needed.

## GP-002 Capability cache

Make runtime capabilities available to routing without fetching them for each decision.

Scope:

- Fetch capabilities at startup and refresh them periodically per runtime.
- Track freshness, failed refreshes, and runtime restarts.
- Cache model ID, context size, structured JSON support, speculative decoding support, tokenizer family, and streaming support when the runtime exposes these fields.
- Expose capability snapshots through diagnostics. Do not infer unsupported fields.

Acceptance criteria:

- [ ] Routing and diagnostics read the same capability snapshot.
- [ ] Runtime failure marks capability availability or freshness explicitly.
- [ ] A model change or runtime restart updates the cached capabilities.
- [ ] Tests cover refresh success, failure, stale data, and recovery.

## GP-003 Reusable token budgeting

Extract the current token-fit logic into a service shared by generation, routing, and reduction.

Scope:

- Return runtime ID, model ID, input tokens, reserved output tokens, context size, maximum allowed input tokens, and fit status.
- Estimate candidate runtimes with their own tokenizer and context limits.
- Preserve the runtime's output reservation and current request-level `max_output_tokens` constraints until the runtime contract changes.

Acceptance criteria:

- [ ] Small prompts fit and oversized prompts fail the fit check.
- [ ] Reserved output tokens reduce the available input budget correctly.
- [ ] Single-runtime behavior stays compatible.
- [ ] Routing and reduction use the same budget result for each candidate.

## GP-004 Deterministic runtime routing

Choose an eligible generation runtime and record why it was selected.

Scope:

- Filter candidates by enabled state, health, required capabilities, and token fit.
- Define how the requested `model`, routing hints, and preferences affect selection. The current requested model does not select a runtime.
- Record selected runtime and model, candidate IDs, rejection reasons, and whether reduction was used.
- Require structured JSON capability for schema requests and streaming capability once streaming is supported.

Acceptance criteria:

- [ ] Identical requests and runtime snapshots produce the same selection.
- [ ] Text and schema requests select eligible runtimes.
- [ ] Disabled, unhealthy, and incompatible candidates are excluded with recorded reasons.
- [ ] Context overflow leads to reduction or a clear failure before generation.
- [ ] No eligible candidate produces a documented error response.

## GP-005 Canonical request mapping

Extend input support while keeping one maintained mapping path to runtime prompts.

Scope:

- Map ordered `system`, `developer`, `user`, and `assistant` turns with stable serialization and turn labels.
- Carry request-local tool definitions as canonical data when GP-007 enables them.
- Preserve schema name and strictness alongside the existing raw schema payload.
- Keep runtime-specific prompt translation behind integration contracts and adapters.

Acceptance criteria:

- [ ] Multi-turn input preserves message order and roles.
- [ ] Token estimation and generation receive the same normalized prompt.
- [ ] Existing single-user-message requests remain compatible.
- [ ] Tool and schema metadata have explicit mappings and validation tests.
- [ ] README examples, OpenAPI, and reviewed contract fixtures describe the expanded input support.

## GP-006 Response normalization

Centralize normalization so clients receive a stable response shape across runtimes.

Scope:

- Normalize assistant text, usage, and runtime failures through a dedicated component.
- Define which runtime trace fields may be exposed publicly.
- Expand structured-output validation beyond the current JSON object check with a documented supported contract.
- Normalize tool-call output once GP-007 supplies the transport contract.

Acceptance criteria:

- [ ] Plain text and JSON object responses retain their supported envelope and usage behavior.
- [ ] Invalid structured output produces a documented error.
- [ ] Runtime and validation failures keep consistent public error mapping.
- [ ] Tool-call output has reviewed contract fixtures before it is enabled.
- [ ] Public traces omit prompt text, generated text, and credentials.

## GP-007 Tool-call transport

Accept tool definitions and return canonical tool calls. Clients execute tools and supply the results in a subsequent request.

Deliver this item in increments:

1. Add typed request-local function definitions with `type`, `name`, `description`, and JSON-object `parameters`. Require full schemas for custom tools. Reject bare tool names unless a later configured catalog resolves them. Define limits for count, names, descriptions, and schema size. Start with `auto`, `none`, and named function choice.
2. Carry definitions through canonical commands. Serialize deterministic tool instructions for runtimes without native tool support and include tool schemas in token budgeting and reduction decisions.
3. Parse model-emitted tool calls. Check that tool names exist in the request and arguments are valid JSON. Validate parameter schemas where supported. Return canonical tool-call items or normal assistant text.
4. Accept client-supplied tool results on later requests and preserve their ordering relative to prior assistant tool calls. Each request includes its own conversation context.
5. Add tool-call capability filtering and model-profile preferences. Use native runtime tool definitions when an upstream contract supports them.

Acceptance criteria:

- [ ] Valid definitions and supported choices are accepted; malformed or oversized definitions return field-level errors.
- [ ] Prompt-based translation is deterministic and token estimates include tool schema overhead.
- [ ] Returned calls reference declared tools and contain valid JSON arguments.
- [ ] A client can submit a tool result through the same endpoint on a later request.
- [ ] Gen Proxy executes no tools and makes no automatic follow-up model call to continue a tool loop.
- [ ] Tool-call routing rejects runtimes that cannot support the selected translation mode.

## GP-008 Reduction coordination and traces

Make reduction work across routing candidates and explain its effect on the token budget.

Scope:

- Coordinate candidate selection, reducer runtime/model selection, reduction, and re-estimation.
- Record original and reduced token counts, strategy, and reducer runtime/model IDs.
- Define fallback behavior for unavailable reducers, reducer prompt overflow, and summaries that remain too large.
- Expose counts and decisions through logs, metrics, and optional response metadata without storing payload text.

Acceptance criteria:

- [ ] Oversized prompts trigger the configured reduction policy.
- [ ] Generation or rerouting happens only after the reduced prompt is re-estimated.
- [ ] Traces record original and reduced token counts and the selected strategy.
- [ ] Reducer failures produce the documented fallback or error; cancellation and deadlines retain their existing semantics.
- [ ] Policy changes pass deterministic tests and the budget smoke check. Broader fact-retention evals remain deferred to GP-014.

## GP-009 Model profiles

Configure model preferences and prompt templates without scattering model-specific branches through the code.

Scope:

- Match profiles by model ID and define routing preferences, prompt templates, known limitations, and default generation options.
- Describe suitability for summarization, structured output, and tool calls.
- Keep runtime capabilities as hard requirements; profiles only rank eligible candidates.

Acceptance criteria:

- [ ] A new model can receive a profile through configuration.
- [ ] Routing uses profile preferences with deterministic tie-breaking.
- [ ] A profile cannot override missing runtime capabilities.
- [ ] Profile matching, defaults, and limitations are documented and tested.

## GP-010 Streaming

Blocked until a compatible `llama-runtime` streaming contract is available. Keep rejecting `stream=true` until the end-to-end contract is implemented and verified.

Scope:

- Add a streaming runtime client, capability validation, and response normalization.
- Define start, text delta, tool-call delta, completion, and failure events. Confirm exact wire names against the chosen public contract before implementation.
- Specify cancellation, disconnect, and partial-response error behavior.

Acceptance criteria:

- [ ] Clients receive text deltas and reconstruct the final response.
- [ ] Streamed failures use the documented event shape.
- [ ] Non-streaming runtimes cannot receive streaming requests.
- [ ] Disconnect and cancellation stop upstream work.
- [ ] Tool-call deltas are supported when tool transport is enabled.

## GP-011 Telemetry storage

Persist operational events for debugging, benchmarking, and routing evaluation while keeping conversation state client-owned.

Scope:

- Store request summaries, runtime attempts, routing decisions, reduction events, and schema-validation events.
- Correlate records with request and trace IDs.
- Define retention, access, payload exclusion, storage failure behavior, and the persistence boundary before implementation.
- Query model latency, failure rates, structured-output success, reduction frequency, and estimated versus actual tokens.

Acceptance criteria:

- [ ] Queries answer each of the operational questions above.
- [ ] Correlation links routing, reduction, and runtime attempts for one request.
- [ ] Records omit prompts, completions, and credentials by default, with privacy tests covering failures too.
- [ ] Retention and storage failure behavior are documented and tested.

## GP-012 Score-based routing

Use measured runtime performance to rank candidates after deterministic hard filtering.

Scope:

- Calculate scores from reliability, latency, throughput, schema success, reduction success, and availability. Include client-reported quality only when available.
- Define minimum sample sizes, score freshness, and safe defaults for new models.
- Select and document score weights using evaluation evidence.
- Include score inputs and fallback decisions in routing explanations.

Acceptance criteria:

- [ ] Scores affect selection only after hard capability, health, and fit checks.
- [ ] Better-performing eligible models rank higher once sample requirements are met.
- [ ] New models and missing or stale telemetry use safe defaults.
- [ ] Operators can inspect the evidence and weights behind a selection.

## GP-013 Diagnostics and operator documentation

Deliver diagnostics alongside registry, capability, routing, and reduction work so operators can inspect each new behavior.

Scope:

- Define protected endpoints for health, runtimes, per-runtime capabilities, available models, routing explanations, and metrics.
- Candidate routes are `GET /health`, `GET /v1/runtimes`, `GET /v1/runtimes/{id}/capabilities`, `GET /v1/models`, `GET /v1/routing/explain`, and `GET /v1/metrics`. Finalize their contracts before exposing them.
- Document runtime setup, routing, reduction, tool transport, profiles, telemetry, and request/response examples as each capability ships.

Acceptance criteria:

- [ ] Operators can identify online runtimes and available models.
- [ ] Operators can explain a selection, a reduction, and a structured-output failure.
- [ ] Diagnostics report speculative decoding state when the runtime exposes it.
- [ ] Authentication and privacy rules cover diagnostics and metrics.
- [ ] Documentation and OpenAPI match the shipped endpoints.

## GP-014 Prompt-reduction evals

Reintroduce dedicated model-quality evaluation when summarization-policy work needs broader evidence. The eval runner, scoring tests, and dataset are removed for now to keep local tooling and verification simpler. This item is deferred and outside the proposed v1 scope.

Scope:

- Add versioned reduction cases covering fact retention, misleading quoted instructions, multilingual context, and real regressions.
- Measure budget fit, required and forbidden facts, downstream structured answers, and latency through the production summarizer and runtime adapters.
- Pin model hashes, runtime versions, generation settings, and summarization templates. Record those inputs and case outcomes in a report without credentials or payload text.
- Validate cases and scoring offline; keep live model runs separate from ordinary verification.

Acceptance criteria:

- [ ] Offline checks reject malformed cases and test scoring failures without inference.
- [ ] Live runs detect lost facts, failed budget fit, invalid structured answers, and runtime failures.
- [ ] Reports include case outcomes and enough metadata to reproduce a run.
- [ ] Documentation explains what the chosen cases and scores establish, including their limits.

## Verification for completed items

- Update affected xUnit tests and demonstrate regression failures before bug fixes where possible.
- Run `make verify-fast` during development and `make verify` before finishing code changes.
- Run `make smoke` for runtime, generation, or structured-output changes and `make smoke-budget` for reduction changes. Use `make demo` for manual checks against an existing stack. Report commands, scenario counts, failures, and blocked prerequisites.
- For documentation-only changes, check links, referenced commands, and `git diff --check`.

Follow the [quality checks](quality.md) for exact commands, required evidence, and the distinction between deterministic and live verification.

## Proposed v1 completion criteria

The proposed v1 scope requires richer canonical requests and normalized responses, cached capabilities, deterministic routing across configured runtimes, shared token budgeting, prompt reduction, client-owned tool-call transport, routing and reduction traces, persisted operational telemetry, and diagnostics. Use the acceptance criteria above to track delivery. Streaming and score-based routing remain later work unless the release scope changes.

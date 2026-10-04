# Quality checks

## Fast feedback

Run `make verify-fast` while editing. It restores dependencies, builds Release, checks selected compiler and analyzer rules, enforces braces, and runs affected tests. Architecture and Unicode property tests always run. Service, runtime-client, validation, and request-mapping changes also run HTTP integration tests. Shared contracts, composition, build settings, unknown files, and an unchanged checkout trigger the full test selection.

The selector includes staged, unstaged, deleted, and untracked files. For changes already committed on a branch, use `make verify-fast VERIFY_BASE=origin/main`. Inspect the selection with `python3 scripts/verify.py --fast --base origin/main --plan`. Without a base, the selector considers local changes only. Its conservative rules live in scripts/verify.py and have their own tests.

Run `make verify` before finishing code changes. It runs both API test projects, builds each local tool separately, and executes standalone tool test projects. Tool tests remain outside the API solution. Neither verification command starts inference, fetches models, or stops local processes. The Makefile does not load the local app .env for deterministic verification, so app credentials and timeout settings cannot override test-host configuration. Test runs disable coverage instrumentation for feedback speed and reject zero discovered tests through both the test runner and TRX counters. Coverage collection remains available through the ordinary dotnet test commands.

The architecture tests enforce project dependencies, keep HTTP and protobuf types out of Services and Contracts, and keep tools outside the API solution. The Host can reference implementations for composition. Nullable and compiler warnings fail builds. CA2012 checks ValueTask use; CA2254 checks static logging templates. IDE0011 enforces braces. These choices follow llama-runtime's managed checks without introducing another analyzer package.

Privacy tests exercise successful, invalid, unauthorized, over-budget, and unavailable requests. They inspect captured logs, including exception text, for distinct prompt, output, and credential markers. Fixed-seed Unicode cases check valid runtime serialization, suffix preservation, decreasing truncation steps, and termination within the simulated budget.

## Reviewed HTTP contracts

Response, unsupported-stream error, and OpenAPI fixtures live in Backend/Tests/GenProxy.Api.IntegrationTests/Fixtures. Tests normalize response IDs, message IDs, timestamps, and trace IDs, then compare parsed JSON. A mismatch writes actual JSON to the OS temporary directory and fails. It never updates a reviewed fixture automatically.

Review the actual contract change and its compatibility before replacing a fixture. Existing endpoint tests still verify behavior behind those envelopes. Contract fixtures complement those tests.

## Deferred prompt-reduction evals

Dedicated prompt-reduction evals are deferred to [GP-014](backlog.md#gp-014-prompt-reduction-evals). There is currently no eval runner, dataset, or eval gate in verification. Use deterministic tests and the budget smoke check for the existing reduction checks.

## Live verification

Use the existing Makefile commands for local HTTP-to-runtime checks. Configure `MAIN_MODEL_PATH`, `MAIN_MODEL_ID`, `SUMMARIZER_MODEL_PATH`, and `SUMMARIZER_MODEL_ID` in `.env`, as shown in [the README](../README.md#local-development). The stack runner downloads and caches its default `llama-runtime v0.7.1` release when needed. Model files must already exist.

```bash
make smoke
make smoke-budget
```

`make smoke` starts the stack, waits for readiness, and checks four scenarios: a completed response with nonblank output and token usage, JSON schema output with exactly one boolean `ok` field, unauthorized rejection, and invalid-request rejection. `make smoke-budget` starts a constrained stack and checks that an oversized prompt returns `422` after reduction. Both commands fail on a failed scenario and clean up their processes on completion, failure, or interruption. The budget check covers rejection; it does not measure fact retention or summarization quality.

For a manual check, keep the stack running in one terminal:

```bash
make stack-run
```

Then send a request from another terminal:

```bash
make demo PROMPT="What is 2 + 2? Answer briefly."
make demo DEMO_REQUEST_ARGS=--json PROMPT="What is 2 + 2?"
```

The demo prints the HTTP response and fails on an HTTP error. Inspect the returned answer; a successful HTTP status alone does not establish answer correctness. `Ctrl+C` in the stack terminal stops its managed processes.

Smoke refuses to start while an active or unverified process record exists in `.runtime-run/`. Use `make demo` against a running stack, or use a separate checkout and distinct ports for another stack. Smoke checks do not require model hashes or a separate JSON report. Results appear in the terminal and stack logs remain in `.runtime-logs/`. Report the commands, passed scenario counts, and any failures or missing prerequisites.

Local tooling tests remain outside the API solution. `make verify-fast` runs them for tooling changes, and `make verify` always runs them. They check response assertions and preservation of existing stack processes without inference. Dedicated fact-retention and summarization evals remain deferred to [GP-014](backlog.md#gp-014-prompt-reduction-evals).

## Agent evidence

For bug fixes, show a regression test failing before the fix and passing afterward where possible. Report exact commands, test counts, failures, and missing prerequisites. Explain fixture changes, skipped tests, analyzer suppressions, weakened assertions, and threshold changes. Use synchronization signals to order concurrency tests and timeouts to bound hangs.

rss-sum's consumer rules informed the fact-preservation and nonblank-output checks. Gen Proxy's dependency tests were also adapted to llama-runtime and rss-sum using each project's existing test framework.

#!/usr/bin/env bash

set -euo pipefail

GEN_PROXY_BASE_URL="${GEN_PROXY_BASE_URL:-https://localhost:7001}"
GEN_PROXY_API_KEY="${GEN_PROXY_API_KEY:-dev-local-key}"
MODEL="${MAIN_MODEL_ID:-stories15m}"
MAX_OUTPUT_TOKENS="512"

usage() {
  cat >&2 <<'EOF'
Usage:
  scripts/demo-request.sh [--text|--json] "Write a short answer."
  printf 'Write a short answer.\nIn two lines.\n' | scripts/demo-request.sh [--text|--json]

Environment overrides:
  GEN_PROXY_BASE_URL       Default: https://localhost:7001
  GEN_PROXY_API_KEY        Default: dev-local-key
  MAIN_MODEL_ID            Default: stories15m
EOF
}

json_escape() {
  local value="$1"
  value=${value//\\/\\\\}
  value=${value//\"/\\\"}
  value=${value//$'\n'/\\n}
  value=${value//$'\r'/\\r}
  value=${value//$'\t'/\\t}
  value=${value//$'\f'/\\f}
  value=${value//$'\b'/\\b}
  printf '%s' "$value"
}

RESPONSE_FORMAT_TYPE="text"
RESPONSE_FORMAT_FLAG=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --text)
      if [[ "$RESPONSE_FORMAT_FLAG" == "json" ]]; then
        echo "error: --text and --json cannot be used together." >&2
        usage
        exit 64
      fi
      RESPONSE_FORMAT_TYPE="text"
      RESPONSE_FORMAT_FLAG="text"
      shift
      ;;
    --json)
      if [[ "$RESPONSE_FORMAT_FLAG" == "text" ]]; then
        echo "error: --text and --json cannot be used together." >&2
        usage
        exit 64
      fi
      RESPONSE_FORMAT_TYPE="json_schema"
      RESPONSE_FORMAT_FLAG="json"
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    --*)
      echo "error: unknown option: $1" >&2
      usage
      exit 64
      ;;
    *)
      break
      ;;
  esac
done

if [[ $# -gt 0 ]]; then
  PROMPT="$*"
elif [[ -n "${GEN_PROXY_DEMO_PROMPT:-}" ]]; then
  PROMPT="$GEN_PROXY_DEMO_PROMPT"
elif [[ ! -t 0 ]]; then
  PROMPT="$(cat)"
else
  echo "error: prompt is required." >&2
  usage
  exit 64
fi

if [[ -z "$PROMPT" ]]; then
  echo "error: prompt is required." >&2
  usage
  exit 64
fi

if ! command -v curl >/dev/null 2>&1; then
  echo "error: curl is required." >&2
  exit 69
fi

if [[ "$RESPONSE_FORMAT_TYPE" == "json_schema" ]]; then
  RESPONSE_FORMAT_JSON='"response_format":{"type":"json_schema","json_schema":{"name":"demo_answer","schema":{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false},"strict":true}}'
else
  RESPONSE_FORMAT_JSON='"response_format":{"type":"text"}'
fi

REQUEST_BODY=$(
  cat <<EOF
{"model":"$(json_escape "$MODEL")","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"$(json_escape "$PROMPT")"}]}],$RESPONSE_FORMAT_JSON,"max_output_tokens":$MAX_OUTPUT_TOKENS}
EOF
)

curl \
  --silent \
  --show-error \
  --fail-with-body \
  --http1.1 \
  --header "Content-Type: application/json" \
  --header "X-API-Key: ${GEN_PROXY_API_KEY}" \
  --data "$REQUEST_BODY" \
  --insecure \
  "${GEN_PROXY_BASE_URL%/}/v1/responses"

printf '\n'

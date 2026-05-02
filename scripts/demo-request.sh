#!/usr/bin/env bash

set -euo pipefail

GEN_PROXY_BASE_URL="${GEN_PROXY_BASE_URL:-https://localhost:7001}"
GEN_PROXY_API_KEY="${GEN_PROXY_API_KEY:-dev-local-key}"
MODEL="${MAIN_MODEL_ID:-stories15m}"
MAX_OUTPUT_TOKENS="512"

usage() {
  cat >&2 <<'EOF'
Usage:
  scripts/demo-request.sh "Write a short answer."
  printf 'Write a short answer.\nIn two lines.\n' | scripts/demo-request.sh

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

if [[ $# -gt 0 ]]; then
  PROMPT="$*"
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

REQUEST_BODY=$(
  cat <<EOF
{"model":"$(json_escape "$MODEL")","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"$(json_escape "$PROMPT")"}]}],"max_output_tokens":$MAX_OUTPUT_TOKENS}
EOF
)

curl \
  --silent \
  --show-error \
  --header "Content-Type: application/json" \
  --header "X-API-Key: ${GEN_PROXY_API_KEY}" \
  --data "$REQUEST_BODY" \
  --insecure \
  "${GEN_PROXY_BASE_URL%/}/v1/responses"

printf '\n'

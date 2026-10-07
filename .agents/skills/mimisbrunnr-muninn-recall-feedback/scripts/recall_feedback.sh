#!/usr/bin/env bash
# shellcheck shell=bash
# Run it: `recall_feedback.sh never-recalled --to DATE [--limit N]`, `miss-rate --from DATE --to DATE`,
# `reset`, `guard` (see `--help`). Each command builds its own request path and picks its own
# capability, so a caller passes dates, never a path or a token. Also sourceable: it then only defines
# `recall_feedback_guard`, the origin check, and `recall_feedback_curl`, the only function that sends a
# token. Runs on bash 3.2 (macOS) and GNU bash. Harness: tests/run_tests.py beside this skill.

# ${VAR:-default} supplies loopback when unset. The base URL is parsed and its **resolved host**
# asserted to be loopback — matching the raw string with a glob instead approves
# `http://localhost:5141@192.0.2.1/` (the loopback prefix, non-loopback host via userinfo), which is
# exactly the bypass this guard exists to close. Mirrors context_memory_client.base_url().
#
# The check runs in a *function*, not a `( ... )` subshell: a subshell's `exit 1` returns to the
# caller with a non-zero status nobody tested, so the requests that followed it ran regardless.
#
# The base reaches python through the environment, not argv: argv is readable in `ps` by every user
# on the host, and the branch that refuses a userinfo origin is reached precisely when the value
# carries a credential.
recall_feedback_guard() {
  RECALL_FEEDBACK_GUARD_BASE="${1:-${CONTEXT_MEMORY_BASE_URL:-http://localhost:5141}}" python3 - <<'PY'
import os, sys, urllib.parse
url = os.environ["RECALL_FEEDBACK_GUARD_BASE"]
# A base the parser cannot read — an NFKC-confusable netloc character, a non-numeric port, an
# unbalanced IPv6 bracket — raises ValueError whose message quotes the netloc, userinfo included.
# This heredoc's stderr is the caller's stderr, so an uncaught one writes the credential the guard
# exists to withhold into any agent or CI transcript. Mirrors the sibling composer's try/except
# around urlparse and .port.
try:
    parsed = urllib.parse.urlparse(url)
    parsed.port  # parsed for its ValueError on a malformed port
    hostname = parsed.hostname
except ValueError:
    print("refusing CONTEXT_MEMORY_BASE_URL: not a parseable bare HTTP(S) origin", file=sys.stderr)
    sys.exit(1)
# Report only whether each part is acceptable, never its value. The raw url is never echoed, for the
# same reason it is not passed as argv: printing it would write a credential to the terminal and any
# agent or CI transcript. That includes the scheme and the host: `admin:hunter2` parses with scheme
# `admin`, and a token pasted into the wrong variable becomes a hostname.
# `urlparse` moves `;params` out of the path, so `http://localhost:5141/;tok=x` reads as a bare
# origin unless the params are checked too (issue 182).
# The delimiters are refused themselves: `urlparse` reports `http://localhost:5141?` as an empty query,
# so an empty `?`, `#` or `;` passed as a bare origin (issue 188).
if parsed.scheme not in ("http", "https") or parsed.username or parsed.password \
        or parsed.path not in ("", "/") or parsed.params or parsed.query or parsed.fragment \
        or any(mark in url for mark in "?#;"):
    print("refusing CONTEXT_MEMORY_BASE_URL: must be a bare HTTP(S) origin with no "
          "credentials, path, params, query or fragment (scheme={}, userinfo={}, path={}, "
          "params={}, query={}, fragment={})".format(
              "valid" if parsed.scheme in ("http", "https") else "invalid",
              "present" if parsed.username or parsed.password else "absent",
              "present" if parsed.path not in ("", "/") else "absent",
              "present" if parsed.params or ";" in url else "absent",
              "present" if parsed.query or "?" in url else "absent",
              "present" if parsed.fragment or "#" in url else "absent"), file=sys.stderr)
    sys.exit(1)
if hostname not in ("localhost", "127.0.0.1", "::1"):
    print("refusing non-loopback CONTEXT_MEMORY_BASE_URL host (value not shown)", file=sys.stderr)
    sys.exit(1)
PY
}

# The path is concatenated after an approved origin, so it can move the request's host as surely as
# the origin can: `@evil.example/x` turns `http://localhost:5141` into a URL whose userinfo is
# `localhost:5141` and whose host is evil.example. Only an absolute path with no authority-bearing
# characters is accepted.
recall_feedback_path_ok() {
  case "$1" in
    //*) return 1 ;;
    /*) ;;
    *) return 1 ;;
  esac
  case "$1" in
    *@*|*\\*|*[[:space:]]*|*[[:cntrl:]]*) return 1 ;;
  esac
  return 0
}

# The capability arrives as a shell-function argument (`read`/`write`); the token is read
# from the environment and reaches curl on stdin (`-H @-`) from the
# `printf` builtin. Never a curl argv element: a process's argv is readable in `ps` by every user on the
# host, so `-H "Authorization: Bearer $TOKEN"` puts the credential in the process table. Never a file
# either: a mode-600 temp file still put the token on disk, where a SIGKILL leaves it behind,
# contradicting SKILL.md's "never written to a file" (issue 184).
recall_feedback_curl() {
  # The third argument names a capability, never a credential: the token is read here from the
  # runtime environment, so a call site never expands a secret into an argument (review #14).
  local method="$1" path="$2" capability="$3" token
  case "$capability" in
    read) token="${CONTEXT_MEMORY_READ_TOKEN:-}" ;;
    write) token="${CONTEXT_MEMORY_WRITE_TOKEN:-}" ;;
    *) echo "recall-feedback: the capability must be 'read' or 'write'; no request was sent." >&2
       return 1 ;;
  esac
  local base="${CONTEXT_MEMORY_BASE_URL:-http://localhost:5141}"
  # One read of the base, judged and then used, so the guard approves the URL that is actually sent.
  if ! recall_feedback_guard "$base"; then
    echo "recall-feedback: origin refused; no request was sent." >&2
    return 1
  fi
  if ! recall_feedback_path_ok "$path"; then
    echo "recall-feedback: path must be absolute and carry no '@', '\\', whitespace or authority; no request was sent." >&2
    return 1
  fi
  if [ -z "$token" ]; then
    echo "recall-feedback: no ${capability} token in the environment; no request was sent." >&2
    return 1
  fi
  # --noproxy '*' is not optional: curl honours http_proxy/ALL_PROXY from the environment, and a
  # proxy set for outbound traffic would otherwise receive a loopback request *and* its header.
  # --fail-with-body keeps a 4xx body visible instead of collapsing a refusal to an empty success.
  # -q must stay the FIRST argument: curl reads ~/.curlrc unless -q opens the command line, and a
  # default config can add a header, a proxy or --location, so the guard would approve one request
  # and curl would send another. `builtin` keeps a same-named function or an external `printf` (whose
  # argv would carry the token) out of the pipe. The pipeline's status is curl's.
  builtin printf 'Authorization: Bearer %s\n' "$token" |
    curl -q -sS --noproxy '*' --fail-with-body \
      -X "$method" -H @- \
      "${base%/}${path}"
}

recall_feedback_usage() {
  cat <<'USAGE'
Usage: recall_feedback.sh <command> [options]

  never-recalled --to DATE [--limit N]   memories never recalled up to DATE (asOf); limit 1-10000, default 500
  miss-rate --from DATE --to DATE        retrievals, misses and miss rate over the window
  reset                                  delete the recall-feedback baseline (write token)
  guard                                  check CONTEXT_MEMORY_BASE_URL only; sends nothing

DATE is YYYY-MM-DD or a UTC instant YYYY-MM-DDTHH:MM:SS[.fff]Z.
Tokens come from CONTEXT_MEMORY_READ_TOKEN (queries) and CONTEXT_MEMORY_WRITE_TOKEN (reset).
USAGE
}

# Dates reach a query string, so only the two ISO 8601 shapes the endpoints accept pass; anything else,
# including a `+hh:mm` offset that would need URL-encoding, is refused before a request.
recall_feedback_date_ok() {
  [[ "$1" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}(T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?Z)?$ ]]
}

recall_feedback_main() {
  local command="${1:-}" from="" to="" limit=""
  [ $# -gt 0 ] && shift
  case "$command" in
    -h|--help|help) recall_feedback_usage; return 0 ;;
    never-recalled|miss-rate|reset|guard) ;;
    "") recall_feedback_usage >&2; return 2 ;;
    *) echo "recall-feedback: unknown command; run with --help. No request was sent." >&2; return 2 ;;
  esac
  while [ $# -gt 0 ]; do
    case "$1" in
      --from|--to|--limit)
        if [ $# -lt 2 ] || [ -z "$2" ]; then
          echo "recall-feedback: $1 needs a value; no request was sent." >&2
          return 2
        fi
        case "$1" in --from) from="$2" ;; --to) to="$2" ;; --limit) limit="$2" ;; esac
        shift 2 ;;
      *) echo "recall-feedback: unknown option for $command; run with --help. No request was sent." >&2
         return 2 ;;
    esac
  done
  case "$command" in
    never-recalled)
      if [ -n "$from" ]; then
        echo "recall-feedback: never-recalled takes --to only; no request was sent." >&2
        return 2
      fi
      if ! recall_feedback_date_ok "$to"; then
        echo "recall-feedback: --to must be an ISO 8601 date; no request was sent." >&2
        return 2
      fi
      limit="${limit:-500}"
      if ! [[ "$limit" =~ ^[0-9]{1,5}$ ]] || [ "$limit" -lt 1 ] || [ "$limit" -gt 10000 ]; then
        echo "recall-feedback: --limit must be 1-10000; no request was sent." >&2
        return 2
      fi
      recall_feedback_curl GET "/api/context/recall-feedback/never-recalled?asOf=${to}&limit=${limit}" read ;;
    miss-rate)
      if ! recall_feedback_date_ok "$from" || ! recall_feedback_date_ok "$to"; then
        echo "recall-feedback: --from and --to must both be ISO 8601 dates; no request was sent." >&2
        return 2
      fi
      recall_feedback_curl GET "/api/context/recall-feedback/miss-rate?from=${from}&to=${to}" read ;;
    reset|guard)
      if [ -n "$from$to$limit" ]; then
        echo "recall-feedback: $command takes no options; no request was sent." >&2
        return 2
      fi
      if [ "$command" = reset ]; then
        recall_feedback_curl POST "/api/context/recall-feedback/reset" write
      elif recall_feedback_guard; then
        echo "origin approved"
      else
        return 1
      fi ;;
  esac
}

# Executed, not sourced: dispatch the command line. Sourcing defines the functions and runs nothing.
if [ "${BASH_SOURCE[0]}" = "$0" ]; then
  recall_feedback_main "$@"
  exit $?
fi

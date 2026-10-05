# shellcheck shell=bash
# Sourced, not executed: `source .agents/skills/mimisbrunnr-muninn-recall-feedback/scripts/recall_feedback.sh`.
# Defines `recall_feedback_guard`, the origin check, and `recall_feedback_curl`, the only function that
# sends a token. Runs on bash 3.2 (macOS) and GNU bash. Harness: tests/run_tests.py beside this skill.

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
# Report only parsed, non-secret parts. The raw url is never echoed, for the same reason it is not
# passed as argv: printing it would write a credential to the terminal and any agent or CI transcript.
if parsed.scheme not in ("http", "https") or parsed.username or parsed.password \
        or parsed.path not in ("", "/") or parsed.query or parsed.fragment:
    # The path, query and fragment are reported as present/absent, never quoted: a token pasted into
    # the base URL lands in one of them as readily as in the userinfo.
    print("refusing CONTEXT_MEMORY_BASE_URL: must be a bare HTTP(S) origin with no "
          "credentials, path, query or fragment (got scheme={!r}, userinfo={}, path={}, query={}, "
          "fragment={})".format(
              parsed.scheme, "present" if parsed.username or parsed.password else "absent",
              "present" if parsed.path not in ("", "/") else "absent",
              "present" if parsed.query else "absent",
              "present" if parsed.fragment else "absent"), file=sys.stderr)
    sys.exit(1)
if hostname not in ("localhost", "127.0.0.1", "::1"):
    print("refusing non-loopback CONTEXT_MEMORY_BASE_URL host: {}".format(hostname),
          file=sys.stderr)
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

# The token arrives as a shell-function argument, never as a curl `-H` argument: a process's argv is
# readable in `ps` by every user on the host for its lifetime, so `-H "Authorization: Bearer $TOKEN"`
# puts the credential in the process table. curl reads the header from a mode-600 file instead.
recall_feedback_curl() {
  local method="$1" path="$2" token="$3"
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
    echo "recall-feedback: no token supplied; no request was sent." >&2
    return 1
  fi
  # The send runs in a subshell only so its traps cannot disturb the operator's shell: the refusals
  # above have already returned. The EXIT trap unlinks the header file on success, failure and an
  # interrupt alike — a signal converted to `exit` still runs it, a bare signal would not.
  # --noproxy '*' is not optional: curl honours http_proxy/ALL_PROXY from the environment, and a
  # proxy set for outbound traffic would otherwise receive a loopback request *and* its header.
  # --fail-with-body keeps a 4xx body visible instead of collapsing a refusal to an empty success.
  # -q must stay the FIRST argument: curl reads ~/.curlrc unless -q opens the command line, and a
  # default config can add a header, a proxy or --location, so the guard would approve one request
  # and curl would send another.
  (
    header=""
    trap 'rm -f "$header"' EXIT
    trap 'exit 129' HUP
    trap 'exit 130' INT
    trap 'exit 143' TERM
    header="$(mktemp)" || exit 1
    chmod 600 "$header"
    printf 'Authorization: Bearer %s\n' "$token" >"$header"
    curl -q -sS --noproxy '*' --fail-with-body \
      -X "$method" -H "@$header" \
      "${base%/}${path}"
  )
}

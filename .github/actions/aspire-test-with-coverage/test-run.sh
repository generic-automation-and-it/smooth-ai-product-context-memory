#!/usr/bin/env bash
# Engine-free harness for run.sh: `dotnet`, `nc` and `curl` are replaced by stubs on PATH, so the
# real script runs end to end without Aspire, Docker or an SDK. The `dotnet test` stub records each
# project it is handed, which is how tier order and project discovery are asserted.
#
#   bash .github/actions/aspire-test-with-coverage/test-run.sh
set -u

here="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "${here}/../../.." && pwd)"
run_script="${here}/run.sh"
scratch="$(mktemp -d "${TMPDIR:-/tmp}/aspire-run-test.XXXXXX")"
trap 'rm -rf "${scratch}"' EXIT

passed=0
failed=0

pass() { passed=$((passed + 1)); echo "ok   - $1"; }
fail() { failed=$((failed + 1)); echo "FAIL - $1"; }

make_stubs() {
  local bin=$1
  mkdir -p "${bin}"
  cat > "${bin}/dotnet" <<'STUB'
#!/usr/bin/env bash
case "$1" in
  run) exec sleep 120 ;;
  test) printf '%s\n' "$2" >> "${STUB_LOG}"; exit 0 ;;
  tool)
    if [ "$2" = run ]; then
      for arg in "$@"; do
        case "${arg}" in -targetdir:*) target="${arg#-targetdir:}" ;; esac
      done
      mkdir -p "${target}"
      printf 'Summary\n  Line coverage: %s%%\n' "${STUB_LINE_COVERAGE}" > "${target}/Summary.txt"
    fi
    exit 0 ;;
esac
exit 0
STUB
  printf '#!/usr/bin/env bash\nexit 0\n' > "${bin}/nc"
  printf '#!/usr/bin/env bash\nexit 0\n' > "${bin}/curl"
  chmod +x "${bin}/dotnet" "${bin}/nc" "${bin}/curl"
}

# run_case <workdir> <line-coverage> [extra env...]; sets case_status, case_log, case_output.
run_case() {
  local workdir=$1
  local coverage=$2
  shift 2
  local bin="${workdir}/.stub-bin"
  make_stubs "${bin}"
  case_log="${workdir}/dotnet-test.log"
  : > "${case_log}"
  case_output="$(cd "${workdir}" && env -u COVERAGE_THRESHOLD "$@" PATH="${bin}:${PATH}" STUB_LOG="${case_log}" \
    STUB_LINE_COVERAGE="${coverage}" ARTIFACTS_ROOT="${workdir}/artifacts" \
    bash "${run_script}" 2>&1)"
  case_status=$?
}

tier_of() {
  case "$1" in
    *.UnitTest/*) echo 0 ;;
    *.ComponentTest/*) echo 1 ;;
    *.IntegrationTest/*) echo 2 ;;
    *) echo 9 ;;
  esac
}

# Case 1: the repository's real test tree, discovered and run in L0 → L1 → L2 order.
real="${scratch}/real"
mkdir -p "${real}"
ln -s "${repo_root}/tests" "${real}/tests"
run_case "${real}" 99.0
if [ "${case_status}" -eq 0 ]; then pass "run.sh succeeds against the real test tree"; else fail "run.sh exited ${case_status}: ${case_output}"; fi

previous_tier=0
ordered=true
while IFS= read -r project; do
  tier="$(tier_of "${project}")"
  if [ "${tier}" -lt "${previous_tier}" ] || [ "${tier}" -eq 9 ]; then ordered=false; fi
  previous_tier="${tier}"
done < "${case_log}"
if [ "${ordered}" = true ]; then pass "tiers run L0 unit, then L1 component, then L2 integration"; else fail "tier order: $(tr '\n' ' ' < "${case_log}")"; fi

missing=""
for expected in "${repo_root}"/tests/*.UnitTest/*.csproj "${repo_root}"/tests/*.ComponentTest/*.csproj "${repo_root}"/tests/*.IntegrationTest/*.csproj; do
  relative="tests/${expected#"${repo_root}/tests/"}"
  grep -Fxq "${relative}" "${case_log}" || missing="${missing} ${relative}"
done
if [ -z "${missing}" ]; then pass "every tests/*.{UnitTest,ComponentTest,IntegrationTest} project ran"; else fail "not run:${missing}"; fi

if grep -q 'TestFramework' "${case_log}"; then fail "a TestFramework project was run as a suite"; else pass "TestFramework projects are not run as suites"; fi

# Case 2: a new project is picked up without editing run.sh; a TestFramework-named one is not.
synthetic="${scratch}/synthetic"
for project in Alpha.UnitTest Beta.UnitTest Alpha.ComponentTest Alpha.IntegrationTest Shared.TestFramework.UnitTest; do
  mkdir -p "${synthetic}/tests/${project}"
  : > "${synthetic}/tests/${project}/${project}.csproj"
done
run_case "${synthetic}" 99.0
if grep -Fxq 'tests/Beta.UnitTest/Beta.UnitTest.csproj' "${case_log}"; then pass "a newly added test project is discovered"; else fail "Beta.UnitTest not run: $(tr '\n' ' ' < "${case_log}")"; fi
if grep -q 'TestFramework' "${case_log}"; then fail "TestFramework-named project ran"; else pass "a TestFramework-named project is excluded"; fi

# Case 3: a tier with no projects is a misconfiguration, not a pass.
empty="${scratch}/empty"
mkdir -p "${empty}/tests/Alpha.UnitTest" "${empty}/tests/Alpha.ComponentTest"
: > "${empty}/tests/Alpha.UnitTest/Alpha.UnitTest.csproj"
: > "${empty}/tests/Alpha.ComponentTest/Alpha.ComponentTest.csproj"
run_case "${empty}" 99.0
if [ "${case_status}" -ne 0 ] && [ ! -s "${case_log}" ]; then pass "an empty tier fails before any test runs"; else fail "empty tier: status ${case_status}, ran $(tr '\n' ' ' < "${case_log}")"; fi

# Case 4: the default line-coverage floor is 85%, and an unparseable summary fails closed.
run_case "${synthetic}" 84.9
if [ "${case_status}" -ne 0 ] && printf '%s' "${case_output}" | grep -q 'below the 85% threshold'; then pass "84.9% line coverage fails the default floor"; else fail "84.9% coverage: status ${case_status}"; fi
run_case "${synthetic}" 85.0
if [ "${case_status}" -eq 0 ]; then pass "85.0% line coverage meets the default floor"; else fail "85.0% coverage: status ${case_status}: ${case_output}"; fi
run_case "${synthetic}" 84.9 COVERAGE_THRESHOLD=80
if [ "${case_status}" -eq 0 ]; then pass "COVERAGE_THRESHOLD overrides the default floor"; else fail "override: status ${case_status}"; fi
run_case "${synthetic}" 'n/a'
if [ "${case_status}" -ne 0 ] && printf '%s' "${case_output}" | grep -q 'Could not parse line coverage'; then pass "an unparseable coverage summary fails closed"; else fail "unparseable summary: status ${case_status}"; fi

echo
echo "${passed} passed, ${failed} failed"
[ "${failed}" -eq 0 ]

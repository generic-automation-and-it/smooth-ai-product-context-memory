#!/usr/bin/env bash

set -u

build_configuration="${BUILD_CONFIGURATION:-Release}"
artifacts_root="${ARTIFACTS_ROOT:-artifacts}"
timeout_seconds="${DEPENDENCY_TIMEOUT_SECONDS:-120}"
results_directory="${artifacts_root}/testresults"
coverage_directory="${artifacts_root}/coverage"
# Scoped to the four product assemblies, not `[SmoothAiProductContextMemory.*]*`.
#
# That wildcard also swept in the test harness (SmoothAiProductContextMemory.TestFramework and
# .TestFramework.Aspire) and the Aspire orchestrator, whose code the suite exercises incidentally
# rather than deliberately — a fixture is only as covered as the tests that happen to call it. Those
# projects in the denominator make the floor a measure of the harness, not of the product.
#
# Narrowing this RAISES the number rather than risking the floor, which was worth measuring rather
# than assuming. Run locally over the Application and Infrastructure component projects (the ones
# that exercise the harness most), the harness sat at 34.08% line coverage against the product's
# 51.65% — below the product, so removing it from the denominator lifts the metric. The 50% floor is
# therefore unchanged and still meaningful; it is now a claim about the store rather than about the
# store plus its scaffolding.
#
# Naming the assemblies is also a tripwire: a new product project has to be added here deliberately
# rather than silently entering or leaving the denominator.
product_assemblies=(
  SmoothAiProductContextMemory.Domain
  SmoothAiProductContextMemory.Application
  SmoothAiProductContextMemory.Infrastructure
  SmoothAiProductContextMemory.Host
)
coverage_includes="$(printf 'Include=[%s]*,' "${product_assemblies[@]}")"
coverage_collect="XPlat Code Coverage;Format=cobertura;${coverage_includes}ExcludeByFile=**/*.g.cs,**/obj/**,**/Migrations/*.cs,**/*ModelSnapshot.cs"
aspire_pid=""

cleanup() {
  if [ -z "${aspire_pid}" ]; then
    echo "No Aspire PID captured; skipping teardown."
    return
  fi

  if ! kill -0 "${aspire_pid}" 2>/dev/null; then
    echo "Aspire host ${aspire_pid} is no longer running."
    return
  fi

  if kill "${aspire_pid}" 2>/dev/null; then
    for _ in $(seq 1 15); do
      if ! kill -0 "${aspire_pid}" 2>/dev/null; then
        echo "Aspire host ${aspire_pid} stopped."
        return
      fi
      sleep 1
    done

    echo "WARNING: Aspire host ${aspire_pid} is still running after teardown signal."
  else
    echo "WARNING: Failed to send termination signal to Aspire host ${aspire_pid}."
  fi
}

trap cleanup EXIT

check_aspire_alive() {
  kill -0 "${aspire_pid}" 2>/dev/null
}

ensure_aspire_alive() {
  if ! check_aspire_alive; then
    record_failure "Aspire host exited before the action completed."
  fi
}

wait_for_tcp() {
  local port=$1
  local name=$2
  local elapsed=0

  echo "Waiting for ${name} on port ${port}..."
  while ! nc -z 127.0.0.1 "${port}" 2>/dev/null; do
    if ! check_aspire_alive; then
      echo "ERROR: Aspire host exited unexpectedly while waiting for ${name} on port ${port}"
      return 1
    fi

    sleep 2
    elapsed=$((elapsed + 2))

    if [ "${elapsed}" -ge "${timeout_seconds}" ]; then
      echo "ERROR: Timed out after ${timeout_seconds}s waiting for ${name} on port ${port}"
      return 1
    fi
  done

  echo "${name} is accepting connections on port ${port} (${elapsed}s)"
}

wait_for_http() {
  local url=$1
  local name=$2
  local elapsed=0

  echo "Waiting for ${name} HTTP health at ${url}..."
  while ! curl -sf --max-time 2 "${url}" > /dev/null 2>&1; do
    if ! check_aspire_alive; then
      echo "ERROR: Aspire host exited unexpectedly while waiting for ${name} HTTP health"
      if [ "${name}" = "MinIO" ]; then
        dump_minio_diagnostics
      fi
      return 1
    fi

    sleep 2
    elapsed=$((elapsed + 2))

    if [ "${elapsed}" -ge "${timeout_seconds}" ]; then
      echo "ERROR: Timed out after ${timeout_seconds}s waiting for ${name} HTTP health"
      if [ "${name}" = "MinIO" ]; then
        dump_minio_diagnostics
      fi
      return 1
    fi
  done

  echo "${name} HTTP health OK at ${url} (${elapsed}s)"
}

dump_minio_diagnostics() {
  echo "==== MinIO diagnostics ===="
  echo "curl -sv http://127.0.0.1:9002/minio/health/live"
  curl -sv --max-time 5 "http://127.0.0.1:9002/minio/health/live" || true
  echo
  echo "nc 127.0.0.1:9002"
  nc -zv 127.0.0.1 9002 || true
  echo
  if command -v docker >/dev/null 2>&1; then
    echo "docker ps -a --filter name=mimisbrunnr-testcontainer-blob"
    docker ps -a --filter name=mimisbrunnr-testcontainer-blob || true
    echo
    echo "docker logs mimisbrunnr-testcontainer-blob (tail 80)"
    docker logs --tail 80 mimisbrunnr-testcontainer-blob 2>&1 || true
  fi
  echo "==== end MinIO diagnostics ===="
}

run_test_project() {
  local project_path=$1

  dotnet test "${project_path}" \
    --configuration "${build_configuration}" \
    --no-build \
    --results-directory "${results_directory}" \
    "--collect:${coverage_collect}"
}

record_failure() {
  local message=$1
  failures+=("${message}")
  echo "ERROR: ${message}"
}

failures=()

export ASPNETCORE_URLS="http://localhost:19888"
export ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL="http://localhost:19889"
export ASPIRE_ALLOW_UNSECURED_TRANSPORT="true"

dotnet run \
  --project tests/SmoothAiProductContextMemory.TestFramework.Aspire \
  --configuration "${build_configuration}" \
  --no-build &

aspire_pid=$!
echo "Aspire host started with PID ${aspire_pid}."

wait_for_tcp 15432 "PostgreSQL" || exit 1
wait_for_tcp 9002 "MinIO" || { dump_minio_diagnostics; exit 1; }
wait_for_http "http://127.0.0.1:9002/minio/health/live" "MinIO" || exit 1
echo "All Aspire test dependencies are healthy."

dotnet tool restore || exit 1

rm -rf "${results_directory}" "${coverage_directory}"
mkdir -p "${results_directory}" "${coverage_directory}"

echo "Phase 1 — integration tests..."
run_test_project tests/SmoothAiProductContextMemory.Host.IntegrationTest/SmoothAiProductContextMemory.Host.IntegrationTest.csproj \
  || record_failure "Host integration tests failed."
ensure_aspire_alive

echo "Phase 2 — component tests (parallel)..."
run_test_project tests/SmoothAiProductContextMemory.Application.ComponentTest/SmoothAiProductContextMemory.Application.ComponentTest.csproj &
app_component_pid=$!
run_test_project tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest/SmoothAiProductContextMemory.Infrastructure.ComponentTest.csproj &
infra_component_pid=$!
wait "${app_component_pid}"   || record_failure "Application component tests failed."
wait "${infra_component_pid}" || record_failure "Infrastructure component tests failed."
ensure_aspire_alive

echo "Phase 3 — unit tests (parallel)..."
run_test_project tests/SmoothAiProductContextMemory.Domain.UnitTest/SmoothAiProductContextMemory.Domain.UnitTest.csproj &
domain_unit_pid=$!
run_test_project tests/SmoothAiProductContextMemory.Application.UnitTest/SmoothAiProductContextMemory.Application.UnitTest.csproj &
app_unit_pid=$!
run_test_project tests/SmoothAiProductContextMemory.Infrastructure.UnitTest/SmoothAiProductContextMemory.Infrastructure.UnitTest.csproj &
infra_unit_pid=$!
run_test_project tests/SmoothAiProductContextMemory.Host.UnitTest/SmoothAiProductContextMemory.Host.UnitTest.csproj &
host_unit_pid=$!
run_test_project tests/SmoothAiProductContextMemory.AppHost.UnitTest/SmoothAiProductContextMemory.AppHost.UnitTest.csproj &
apphost_unit_pid=$!
wait "${domain_unit_pid}" || record_failure "Domain unit tests failed."
wait "${app_unit_pid}"    || record_failure "Application unit tests failed."
wait "${infra_unit_pid}"  || record_failure "Infrastructure unit tests failed."
wait "${host_unit_pid}"   || record_failure "Host unit tests failed."
wait "${apphost_unit_pid}" || record_failure "AppHost unit tests failed."
ensure_aspire_alive

dotnet tool run reportgenerator \
  "-reports:${results_directory}/**/coverage.cobertura.xml" \
  "-targetdir:${coverage_directory}" \
  "-reporttypes:HtmlInline_AzurePipelines;Cobertura;TextSummary;MarkdownSummaryGithub" \
  || record_failure "Coverage report generation failed."

# Coverage floor: a drop below it fails the gate, so the suite cannot quietly regress. Overridable
# with COVERAGE_THRESHOLD; the value is a regression guard, not a target.
coverage_threshold="${COVERAGE_THRESHOLD:-50}"
summary_file="${coverage_directory}/Summary.txt"
if [ -f "${summary_file}" ]; then
  line_coverage="$(grep -oE 'Line coverage: [0-9.]+%' "${summary_file}" | grep -oE '[0-9.]+' | head -n 1)"
  if [ -n "${line_coverage}" ]; then
    if awk "BEGIN { exit !(${line_coverage} >= ${coverage_threshold}) }"; then
      echo "Coverage OK: ${line_coverage}% (threshold ${coverage_threshold}%)."
    else
      record_failure "Line coverage ${line_coverage}% is below the ${coverage_threshold}% threshold."
    fi
  else
    record_failure "Could not parse line coverage from ${summary_file}."
  fi
else
  record_failure "Coverage summary file ${summary_file} is missing."
fi

if [ "${#failures[@]}" -gt 0 ]; then
  printf 'Aspire test with coverage failed:\n'
  printf ' - %s\n' "${failures[@]}"
  exit 1
fi

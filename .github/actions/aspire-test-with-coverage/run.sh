#!/usr/bin/env bash

set -u

build_configuration="${BUILD_CONFIGURATION:-Release}"
artifacts_root="${ARTIFACTS_ROOT:-artifacts}"
timeout_seconds="${DEPENDENCY_TIMEOUT_SECONDS:-120}"
results_directory="${artifacts_root}/testresults"
coverage_directory="${artifacts_root}/coverage"
coverage_collect="XPlat Code Coverage;Format=cobertura;Include=[SmoothAiProductContextMemory.*]*;ExcludeByFile=**/*.g.cs,**/obj/**,**/Migrations/*.cs,**/*ModelSnapshot.cs"
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
      return 1
    fi

    sleep 2
    elapsed=$((elapsed + 2))

    if [ "${elapsed}" -ge "${timeout_seconds}" ]; then
      echo "ERROR: Timed out after ${timeout_seconds}s waiting for ${name} HTTP health"
      dump_minio_diagnostics
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
    echo "docker ps -a --filter name=project-test-blob"
    docker ps -a --filter name=project-test-blob || true
    echo
    echo "docker logs project-test-blob (tail 80)"
    docker logs --tail 80 project-test-blob 2>&1 || true
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
wait_for_tcp 16379 "Redis" || exit 1
wait_for_http "http://127.0.0.1:19091/__admin/health" "WireMock" || exit 1
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
wait "${domain_unit_pid}" || record_failure "Domain unit tests failed."
wait "${app_unit_pid}"    || record_failure "Application unit tests failed."
wait "${infra_unit_pid}"  || record_failure "Infrastructure unit tests failed."
wait "${host_unit_pid}"   || record_failure "Host unit tests failed."
ensure_aspire_alive

dotnet tool run reportgenerator \
  "-reports:${results_directory}/**/coverage.cobertura.xml" \
  "-targetdir:${coverage_directory}" \
  "-reporttypes:HtmlInline_AzurePipelines;Cobertura;TextSummary;MarkdownSummaryGithub" \
  || record_failure "Coverage report generation failed."

if [ "${#failures[@]}" -gt 0 ]; then
  printf 'Aspire test with coverage failed:\n'
  printf ' - %s\n' "${failures[@]}"
  exit 1
fi

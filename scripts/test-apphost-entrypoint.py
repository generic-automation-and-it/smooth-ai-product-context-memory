#!/usr/bin/env python3
"""Engine-free lifecycle tests. Build AppHost first, then run this script."""

import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest


def record(event):
    with open(os.environ["MOCK_LOG"], "a", encoding="utf-8") as stream:
        stream.write(json.dumps(event) + "\n")


def mock_engine():
    args = sys.argv[1:]
    record(args)
    state = json.loads(os.environ["MOCK_STATE"])
    if args == ["version"]:
        return int(state.get("engine_unavailable", False))
    if args[:2] == ["container", "ls"]:
        if state.get("list_failure"):
            return 1
        print("\n".join(state["containers"]))
    elif args[:2] == ["volume", "ls"]:
        print("\n".join(state["volumes"]))
    elif args[:2] == ["container", "inspect"]:
        name = args[-1]
        if name.endswith("-controller"):
            if state.get("missing_controller"):
                return 1
            print(state.get("controller_id", "a" * 64), "true")
        elif name == state.get("inspect_failure"):
            return 1
        else:
            print("id-" + name, state["containers"][name])
    elif args[:2] == ["volume", "inspect"]:
        print(state["volumes"].get(args[-1], "test true"))
    elif args[0] in ("stop", "rm") or args[:2] in (
        ["volume", "rm"], ["volume", "create"]
    ):
        if "--force" in args:
            return 90
        if args[0] == "stop" and state.get("stop_failure"):
            return 1
    else:
        raise AssertionError("Unexpected engine call: " + repr(args))
    return 0


def mock_apphost():
    if sys.argv[1:] == ["--validate-configuration"]:
        record(["validate"])
        return subprocess.call([
            os.environ["TEST_DOTNET"], os.environ["TEST_APPHOST_DLL"],
            "--validate-configuration",
        ])

    def shutdown(_signum, _frame):
        record(["child-term"])
        time.sleep(0.7)
        record(["child-exit"])
        sys.exit(0)

    signal.signal(signal.SIGTERM, shutdown)
    record(["child-start"])
    while True:
        time.sleep(0.05)


class EntrypointTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        for name in ("docker", "SmoothAiProductContextMemory.AppHost"):
            if (Path("/app") / name).exists():
                raise RuntimeError("Refusing to run with /app binaries that would shadow mocks")
        cls.root = Path(__file__).resolve().parent.parent
        cls.dll = cls.root / (
            "src/SmoothAiProductContextMemory.AppHost/bin/"
            + os.environ.get("BUILD_CONFIGURATION", "Debug") + "/net10.0/"
            "SmoothAiProductContextMemory.AppHost.dll"
        )
        if not cls.dll.exists():
            raise RuntimeError("Build the AppHost project before running these tests")
        cls.dotnet = shutil.which("dotnet")
        if not cls.dotnet:
            raise RuntimeError("dotnet is required for authoritative preflight tests")

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="apphost-entrypoint-tests-")
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.log = self.directory / "engine.jsonl"
        for name in ("docker", "SmoothAiProductContextMemory.AppHost"):
            (self.directory / name).symlink_to(Path(__file__).resolve())
        self.state = {
            "containers": {
                "mimisbrunnr-test-" + suffix: "test true"
                for suffix in ("host", "postgres", "blob-well", "seq")
            },
            "volumes": {
                "mimisbrunnr-test-" + suffix + "-data": "test true"
                for suffix in ("postgres", "blob-well", "seq")
            },
        }
        self.env = {
            "PATH": str(self.directory) + os.pathsep + os.defpath,
            "HOME": str(self.directory),
            "DOTNET_ROOT": str(Path(self.dotnet).resolve().parent),
            "TEST_DOTNET": self.dotnet,
            "TEST_APPHOST_DLL": str(self.dll),
            "MOCK_LOG": str(self.log),
            "HOSTNAME": "a" * 12,
            "InstallationConfiguration__Id": "test",
            "PostgresConfiguration__Password": "test-postgres-password",
            "BlobConfiguration__AccessKey": "test-access",
            "BlobConfiguration__SecretKey": "test-secret",
            "HostConfiguration__Image": "registry.example/api@sha256:" + "a" * 64,
            "EngineConfiguration__BindAddress": "172.17.0.1",
        }
        self.process = None
        self.addCleanup(self.stop_process)

    def stop_process(self):
        if self.process and self.process.poll() is None:
            os.killpg(self.process.pid, signal.SIGKILL)
            self.process.communicate(timeout=5)

    def start(self, command):
        self.env["MOCK_STATE"] = json.dumps(self.state)
        self.process = subprocess.Popen(
            ["/bin/sh", str(self.root / "scripts/apphost-container-entrypoint.sh"), command],
            env=self.env, cwd=self.directory,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
            start_new_session=True,
        )
        return self.process

    def events(self):
        if not self.log.exists():
            return []
        return [json.loads(line) for line in self.log.read_text().splitlines()]

    def mutations(self):
        return [event for event in self.events() if event[0] in ("stop", "rm")
                or event[:2] in (["volume", "rm"], ["volume", "create"])]

    def assert_rejected_without_mutation(self, command):
        self.log.unlink(missing_ok=True)
        process = self.start(command)
        stdout, stderr = process.communicate(timeout=15)
        self.assertNotEqual(process.returncode, 0, stdout + stderr)
        self.assertEqual(self.mutations(), [], stdout + stderr)
        self.assertNotIn(["child-start"], self.events())

    def test_every_foreign_resource_rejects_every_command_before_mutation(self):
        for collection in ("containers", "volumes"):
            for name in self.state[collection]:
                for command in ("run", "stop", "reset"):
                    with self.subTest(name=name, command=command):
                        self.state[collection][name] = "foreign true"
                        self.assert_rejected_without_mutation(command)
                        self.state[collection][name] = "test true"

    def test_authoritative_invalid_configuration_precedes_engine_access(self):
        invalid = {
            "PostgresConfiguration__Password": "   ",
            "BlobConfiguration__AccessKey": "",
            "BlobConfiguration__SecretKey": "",
            "HostConfiguration__Image": "registry.example/api@sha256:" + "z" * 64,
            "HostConfiguration__Port": "70000",
            "PostgresConfiguration__Port": "not-a-port",
        }
        for key, value in invalid.items():
            with self.subTest(key=key):
                old = self.env.get(key)
                self.env[key] = value
                self.assert_rejected_without_mutation("run")
                self.assertEqual(self.events(), [["validate"]])
                if old is None:
                    del self.env[key]
                else:
                    self.env[key] = old

    def test_duplicate_ports_rejected_before_engine_access(self):
        self.env["HostConfiguration__Port"] = "5432"
        self.assert_rejected_without_mutation("run")
        self.assertEqual(self.events(), [["validate"]])

    def test_duplicate_or_missing_canonical_controller_rejects_all_commands(self):
        for key, value in (("controller_id", "b" * 64), ("missing_controller", True)):
            for command in ("run", "stop", "reset"):
                with self.subTest(key=key, command=command):
                    self.state[key] = value
                    self.assert_rejected_without_mutation(command)
            del self.state[key]

    def test_bad_hostname_and_timeout_reject_before_mutation(self):
        for key, value in (("HOSTNAME", "custom-host"),
                           ("ControllerConfiguration__StopTimeoutSeconds", "0"),
                           ("ControllerConfiguration__StopTimeoutSeconds", "-1"),
                           ("ControllerConfiguration__StopTimeoutSeconds", "3601")):
            old = self.env.get(key)
            for command in ("run", "stop", "reset"):
                with self.subTest(key=key, value=value, command=command):
                    self.env[key] = value
                    self.assert_rejected_without_mutation(command)
            if old is None:
                del self.env[key]
            else:
                self.env[key] = old

    def test_unsecured_tcp_engine_rejected_without_mutation(self):
        self.env["DOCKER_HOST"] = "tcp://engine.example:2375"
        for command in ("run", "stop", "reset"):
            self.assert_rejected_without_mutation(command)

    def test_inspection_errors_are_not_treated_as_missing_resources(self):
        for key, value in (
            ("engine_unavailable", True), ("list_failure", True),
            ("inspect_failure", "mimisbrunnr-test-seq"),
        ):
            for command in ("run", "stop", "reset"):
                with self.subTest(key=key, command=command):
                    self.state[key] = value
                    self.assert_rejected_without_mutation(command)
            del self.state[key]

    def test_stop_is_api_first_and_preserves_volumes_without_credentials(self):
        for key in tuple(self.env):
            if key.startswith(("PostgresConfiguration", "BlobConfiguration", "HostConfiguration")):
                del self.env[key]
        process = self.start("stop")
        stdout, stderr = process.communicate(timeout=10)
        self.assertEqual(process.returncode, 0, stdout + stderr)
        expected = []
        for name in self.state["containers"]:
            expected += [["stop", "--time", "30", "id-" + name], ["rm", "id-" + name]]
        self.assertEqual(self.mutations(), expected)

    def test_stop_failure_does_not_remove_or_stop_dependencies(self):
        self.state["stop_failure"] = True
        process = self.start("stop")
        process.communicate(timeout=10)
        self.assertNotEqual(process.returncode, 0)
        self.assertEqual(self.mutations(), [["stop", "--time", "30", "id-mimisbrunnr-test-host"]])

    def test_reset_removes_only_owned_volumes_after_containers(self):
        process = self.start("reset")
        stdout, stderr = process.communicate(timeout=10)
        self.assertEqual(process.returncode, 0, stdout + stderr)
        self.assertEqual(self.mutations()[-3:], [
            ["volume", "rm", name] for name in self.state["volumes"]
        ])

    def test_missing_resources_are_successful_noops(self):
        self.state["containers"] = {}
        self.state["volumes"] = {}
        process = self.start("reset")
        stdout, stderr = process.communicate(timeout=10)
        self.assertEqual(process.returncode, 0, stdout + stderr)
        self.assertEqual(self.mutations(), [])

    def test_sigterm_waits_for_child_before_cleanup(self):
        process = self.start("run")
        deadline = time.monotonic() + 15
        while ["child-start"] not in self.events():
            if process.poll() is not None or time.monotonic() >= deadline:
                self.fail("Child did not start")
            time.sleep(0.05)
        process.send_signal(signal.SIGTERM)
        time.sleep(0.2)
        self.assertIsNone(process.poll(), "Parent exited before child cleanup")
        process.send_signal(signal.SIGTERM)
        stdout, stderr = process.communicate(timeout=10)
        self.assertEqual(process.returncode, 0, stdout + stderr)
        events = self.events()
        self.assertEqual(events.count(["child-term"]), 1)
        child_start = events.index(["child-start"])
        child_exit = events.index(["child-exit"])
        self.assertFalse(any(event[0] in ("stop", "rm") for event in events[child_start:child_exit]))
        self.assertEqual(events[child_exit + 1:][-8:][0],
                         ["stop", "--time", "30", "id-mimisbrunnr-test-host"])

    def test_start_creates_missing_volumes_after_preflight(self):
        self.state["volumes"] = {}
        process = self.start("run")
        deadline = time.monotonic() + 15
        while ["child-start"] not in self.events():
            if process.poll() is not None or time.monotonic() >= deadline:
                self.fail("Child did not start")
            time.sleep(0.05)
        process.send_signal(signal.SIGINT)
        stdout, stderr = process.communicate(timeout=10)
        self.assertEqual(process.returncode, 0, stdout + stderr)
        creations = [event for event in self.events() if event[:2] == ["volume", "create"]]
        self.assertEqual([event[-1] for event in creations], [
            "mimisbrunnr-test-" + suffix + "-data"
            for suffix in ("postgres", "blob-well", "seq")
        ])
        self.assertNotIn(["volume", "rm"], [event[:2] for event in self.events()])


if __name__ == "__main__":
    invoked_name = Path(sys.argv[0]).name
    if invoked_name == "docker":
        sys.exit(mock_engine())
    if invoked_name == "SmoothAiProductContextMemory.AppHost":
        sys.exit(mock_apphost())
    unittest.main(verbosity=2)

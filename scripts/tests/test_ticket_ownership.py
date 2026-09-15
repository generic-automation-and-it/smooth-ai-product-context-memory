#!/usr/bin/env python3
"""Operational checks; --postgres adds a disposable, synthetic PostgreSQL fixture."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
import uuid


SCRIPTS = Path(__file__).resolve().parents[1]
CHECK = SCRIPTS / "check-ticket-ownership.sh"
POSTGRES = "--postgres" in sys.argv
if POSTGRES:
    sys.argv.remove("--postgres")
GROUPS = [str(uuid.UUID(int=i)) for i in range(1, 4)]
HOSTILE = " ' ; DROP TABLE public.memory_group; -- $ticket$ $(id) \\\n\t\x1b[31m"


class TransportTests(unittest.TestCase):
    def test_status_and_incomplete_output(self):
        with tempfile.TemporaryDirectory() as directory:
            docker = Path(directory) / "docker"
            docker.write_text('#!/bin/sh\nprintf "%s\\n" "$MOCK_OUTPUT"\nexit "$MOCK_EXIT"\n')
            docker.chmod(0o700)
            for output, docker_exit, expected in [
                ('{}\nCHECK_EXIT=0', 0, 0),
                ('{}\nCHECK_EXIT=1', 0, 1),
                ('{}\nCHECK_EXIT=2', 0, 2),
                ('{}\nCHECK_EXIT=0', 3, 2),
                ('{}\nCHECK_EXIT=0', 125, 2),
                ('', 0, 2),
                ('{}', 0, 2),
                ('{}\nCHECK_EXIT=9', 0, 2),
            ]:
                with self.subTest(output=output, docker_exit=docker_exit):
                    env = dict(os.environ, PATH=directory + os.pathsep + os.environ["PATH"],
                               MOCK_OUTPUT=output, MOCK_EXIT=str(docker_exit))
                    result = subprocess.run(["bash", str(CHECK)], env=env, capture_output=True, text=True)
                    self.assertEqual(expected, result.returncode)

    def test_arguments_and_container_credentials(self):
        with tempfile.TemporaryDirectory() as directory:
            docker = Path(directory) / "docker"
            docker.write_text('''#!/bin/sh
[ "$1" = exec ] && [ "$2" = -i ] && [ "$3" = safe-container ] || exit 98
shift 3
export POSTGRES_USER=fixture-user POSTGRES_PASSWORD=fixture-secret
exec "$@"
''')
            docker.chmod(0o700)
            psql = Path(directory) / "psql"
            psql.write_text('''#!/bin/sh
[ "$*" = '-X -w -qAt -v ON_ERROR_STOP=1 -f -' ] || exit 97
[ "$PGUSER" = fixture-user ] && [ "$PGPASSWORD" = fixture-secret ] || exit 96
[ "$PGDATABASE" = "$EXPECTED_DATABASE" ] || exit 95
[ "$PGHOST" = 127.0.0.1 ] && [ "$PGPORT" = 5432 ] || exit 94
case "$PGOPTIONS" in *default_transaction_read_only=on*) ;; *) exit 93 ;; esac
printf '{}\\nCHECK_EXIT=0\\n'
''')
            psql.chmod(0o700)
            database = "host=untrusted dbname=' ; $(false) / --\n"
            env = dict(os.environ, PATH=directory + os.pathsep + os.environ["PATH"],
                       EXPECTED_DATABASE=database)
            result = subprocess.run(["bash", str(CHECK), "safe-container", database],
                                    env=env, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertNotIn("fixture-secret", result.stdout + result.stderr)

    def test_invalid_arguments_fail_closed(self):
        for args in [["--privileged"], ["bad;container"], [""], ["safe", ""], ["a", "b", "c"]]:
            with self.subTest(args=args):
                result = subprocess.run(["bash", str(CHECK), *args], capture_output=True, text=True)
                self.assertEqual(2, result.returncode)


@unittest.skipUnless(POSTGRES, "use --postgres for isolated Docker fixture")
class PostgreSQLTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        name = "ticket-ownership-check-" + uuid.uuid4().hex
        result = subprocess.run([
            "docker", "run", "--detach", "--name", name, "--network", "none",
            "--env", "POSTGRES_HOST_AUTH_METHOD=trust",
            "--env", "POSTGRES_DB=ownership_fixture",
            "docker.io/apache/age:release_PG17_1.7.0",
        ], check=True, capture_output=True, text=True)
        cls.container = result.stdout.strip()
        # Only this successfully created container ID is eligible for cleanup.
        cls.addClassCleanup(cls.cleanup)
        for _ in range(60):
            ready = subprocess.run(["docker", "exec", cls.container, "pg_isready", "-U", "postgres"],
                                   capture_output=True)
            if ready.returncode == 0:
                break
            time.sleep(1)
        else:
            raise RuntimeError("Isolated PostgreSQL did not become ready")
        cls.sql('''CREATE TABLE public.memory_group (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            uuid uuid NOT NULL UNIQUE, tickets jsonb);
            CREATE TABLE public."__EFMigrationsHistory" ("MigrationId" text);
            CREATE TABLE public.memory (id integer, data jsonb);
            CREATE TABLE public.memory_version (id integer, data jsonb);
            INSERT INTO public.memory VALUES (1, '{"preserve":"memory"}');
            INSERT INTO public.memory_version VALUES (1, '{"preserve":"history"}');''')

    @classmethod
    def cleanup(cls):
        subprocess.run(["docker", "rm", "--force", "--volumes", cls.container],
                       check=True, capture_output=True)

    @classmethod
    def sql(cls, sql, *, variables=None, check=True):
        args = ["docker", "exec", "-i", cls.container, "psql", "-X", "-qAt",
                "-U", "postgres", "-d", "ownership_fixture", "-v", "ON_ERROR_STOP=1"]
        for name, value in (variables or {}).items():
            args.extend(["-v", f"{name}={value}"])
        return subprocess.run(args, input=sql, text=True, capture_output=True, check=check)

    def setUp(self):
        self.sql("TRUNCATE public.memory_group;")

    def seed(self, arrays):
        for group, tickets in zip(GROUPS, arrays):
            encoded = json.dumps(tickets).replace("'", "''")
            self.sql(f"INSERT INTO public.memory_group(uuid,tickets) VALUES ('{group}', '{encoded}');")

    def snapshot(self):
        return self.sql("SELECT jsonb_agg(to_jsonb(g) ORDER BY id) FROM public.memory_group g;").stdout

    def check(self, expected, database="ownership_fixture"):
        before = self.snapshot()
        result = subprocess.run(["bash", str(CHECK), self.container, database], capture_output=True, text=True)
        self.assertEqual(expected, result.returncode, result.stderr)
        self.assertEqual(before, self.snapshot(), "Checker changed fixture data")
        return json.loads(result.stdout) if result.stdout else None

    def test_empty_and_unique(self):
        self.assertEqual({"duplicates": [], "malformed": []}, self.check(0))
        self.seed([[], [{"provider": "jira", "key": "ABC-1"}]])
        self.check(0)

    def test_duplicate_distinct_groups(self):
        ticket = {"provider": "jira", "key": "ABC-1"}
        self.seed([[ticket, ticket], [ticket], [ticket]])
        self.assertEqual([dict(ticket, groupUuids=GROUPS)], self.check(1)["duplicates"])

    def test_same_group_repeats(self):
        ticket = {"provider": "jira", "key": "ABC-1"}
        self.seed([[ticket, dict(ticket, url="different metadata"), ticket]])
        self.check(0)

    def test_case_and_whitespace_are_exact(self):
        self.seed([[{"provider": "Jira", "key": "ABC-1"}],
                   [{"provider": "jira", "key": "ABC-1"}],
                   [{"provider": "Jira", "key": " ABC-1 "}]])
        self.check(0)

    def test_hostile_and_long_identity(self):
        ticket = {"provider": HOSTILE, "key": HOSTILE + "x" * 10000}
        self.seed([[ticket], [ticket]])
        self.assertEqual([dict(ticket, groupUuids=GROUPS[:2])], self.check(1)["duplicates"])

    def test_malformed_shapes(self):
        for malformed in [None, {}, "array?", 3, [None], [5], [[]], [{}],
                          [{"provider": None, "key": "1"}],
                          [{"provider": "jira", "key": 1}],
                          [{"provider": "jira"}]]:
            with self.subTest(malformed=malformed):
                self.sql("TRUNCATE public.memory_group;")
                self.seed([malformed])
                self.assertEqual(GROUPS[0], self.check(2)["malformed"][0]["groupUuid"])
        self.sql("UPDATE public.memory_group SET tickets = NULL;")
        self.check(2)

    def test_malformed_does_not_hide_duplicates(self):
        ticket = {"provider": "jira", "key": "ABC-1"}
        self.seed([[ticket, False], [ticket]])
        report = self.check(2)
        self.assertEqual(1, len(report["duplicates"]))
        self.assertEqual(2, report["malformed"][0]["position"])

    def test_connection_and_query_errors(self):
        self.check(2, "no_such_database")
        self.check(2, "host=untrusted dbname=ownership_fixture")
        self.sql("ALTER TABLE public.memory_group RENAME TO hidden_group;")
        try:
            result = subprocess.run(["bash", str(CHECK), self.container, "ownership_fixture"],
                                    capture_output=True, text=True)
            self.assertEqual(2, result.returncode)
        finally:
            self.sql("ALTER TABLE public.hidden_group RENAME TO memory_group;")

    def test_sql_transaction_rejects_writes(self):
        sql = (SCRIPTS / "check-ticket-ownership.sql").read_text()
        result = self.sql(sql.replace("ROLLBACK;", "DELETE FROM public.memory;\nROLLBACK;"), check=False)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("read-only transaction", result.stderr)
        self.assertEqual("1\n", self.sql("SELECT count(*) FROM public.memory;").stdout)

    def test_runbook_refuses_partial_graph_installation(self):
        doc = SCRIPTS.parent / "docs/hlds/003-graph-edges-on-age/ticket-migration-runbook.md"
        repair = "BEGIN;\n" + doc.read_text().split("```sql\nBEGIN;\n", 1)[1].split("```", 1)[0]
        ticket = {"provider": "jira", "key": "ABC-1"}
        self.seed([[ticket], [ticket]])
        variables = dict(provider="jira", key="ABC-1", keeper=GROUPS[0], loser=GROUPS[1],
                         owners="{" + ",".join(GROUPS[:2]) + "}", before=json.dumps([ticket]), removed="1")
        self.sql('CREATE SCHEMA memory_graph; CREATE TABLE memory_graph."Ticket" (id bigint);')
        before = self.snapshot()
        try:
            result = self.sql(repair + "\nCOMMIT;", variables=variables, check=False)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("Not a pre-ticket-migration database", result.stderr)
            self.assertEqual(before, self.snapshot())
        finally:
            self.sql('DROP TABLE memory_graph."Ticket"; DROP SCHEMA memory_graph;')

    def test_runbook_rollback_commit_and_stale_guard(self):
        doc = SCRIPTS.parent / "docs/hlds/003-graph-edges-on-age/ticket-migration-runbook.md"
        repair = doc.read_text().split("```sql\nBEGIN;\n", 1)[1].split("```", 1)[0]
        repair = "BEGIN;\n" + repair
        target = {"provider": HOSTILE, "key": "exact "}
        retained = [{"provider": "other", "key": "first", "unknown": {"x": [1, 2]}},
                    {"provider": HOSTILE, "key": "exact", "url": "keep whitespace distinction"}]
        losing = [retained[0], target, retained[1], dict(target, url="remove all occurrences")]
        self.seed([[target], losing])
        variables = dict(provider=HOSTILE, key="exact ", keeper=GROUPS[0], loser=GROUPS[1],
                         owners="{" + ",".join(GROUPS[:2]) + "}", before=json.dumps(losing), removed="2")
        before = self.snapshot()
        self.sql(repair + "\nROLLBACK;", variables=variables)
        self.assertEqual(before, self.snapshot())
        failed = self.sql(repair + "\nCOMMIT;", variables=dict(variables, removed="1"), check=False)
        self.assertNotEqual(0, failed.returncode)
        self.assertEqual(before, self.snapshot())
        for changed in [dict(before="[]"), dict(owners="{" + GROUPS[0] + "}")]:
            failed = self.sql(repair + "\nCOMMIT;", variables=dict(variables, **changed), check=False)
            self.assertNotEqual(0, failed.returncode)
            self.assertEqual(before, self.snapshot())
        self.sql(repair + "\nCOMMIT;", variables=variables)
        result = self.sql(f"SELECT tickets FROM public.memory_group WHERE uuid = '{GROUPS[1]}';")
        self.assertEqual(retained, json.loads(result.stdout))
        self.check(0)
        failed = self.sql(repair + "\nCOMMIT;", variables=variables, check=False)
        self.assertNotEqual(0, failed.returncode)
        self.assertEqual('{"preserve": "memory"}\n', self.sql("SELECT data FROM public.memory;").stdout)
        self.assertEqual('{"preserve": "history"}\n', self.sql("SELECT data FROM public.memory_version;").stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)

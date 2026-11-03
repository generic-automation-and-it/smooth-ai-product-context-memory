"""Offline compatibility checks for the service's shared atomicity implementation."""
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.agents/skills/_mimisbrunnr-shared/scripts'))
import atomicity


class SharedAtomicityTests(unittest.TestCase):
    def test_service_conditions_and_independent_claims(self):
        cases = json.loads((Path(__file__).parent / 'atomicity-fixtures.json').read_text(encoding='utf-8'))['cases']
        for case in cases:
            with self.subTest(case=case['id']):
                self.assertEqual(atomicity.classify(case['statement'])['verdict'], case['verdict'])

    def test_main_universal_quantifier_fix_is_preserved(self):
        for text in ('All of the retries are idempotent.', 'Each of the workers reads the same queue.'):
            self.assertEqual(atomicity.classify(text)['verdict'], 'simple')


if __name__ == '__main__':
    unittest.main()

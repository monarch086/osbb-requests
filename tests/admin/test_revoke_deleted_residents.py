import importlib.util
import json
from pathlib import Path
import unittest

source = Path(__file__).resolve().parents[2] / 'scripts' / 'revoke-deleted-residents.py'
spec = importlib.util.spec_from_file_location('revoke_deleted', source)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class RevokeDeletedTests(unittest.TestCase):
    def run_migration(self, apply=False, changed=False):
        self.calls = []
        profile = {'Id': 'reference', 'UserId': 99, 'BuildingId': 'complex', 'Stage': 'Approved', 'Version': 7,
                   'PersonalDataDeleted': True, 'PersonalDataDeletedAt': '2026-10-25T10:00:00Z'}
        def aws(service, operation, region, payload):
            self.calls.append((operation, payload))
            if operation == 'query':
                return {'Items': [{'payload': {'S': json.dumps(profile)}}]}
            if operation == 'get-item':
                current = dict(profile, PersonalDataDeleted=False) if changed else profile
                return {'Item': {'payload': {'S': json.dumps(current)}, 'version': {'N': '7'}}}
            return {}
        return module.migrate('data', 'complex', 'eu-central-1', apply, aws)

    def test_dry_run_does_not_change_access(self):
        self.assertEqual(1, self.run_migration())
        self.assertEqual(['query', 'get-item'], [operation for operation, _ in self.calls])

    def test_apply_updates_profile_copies_and_revokes_membership_in_one_transaction(self):
        self.assertEqual(1, self.run_migration(True))
        writes = self.calls[-1][1]['TransactItems']
        self.assertEqual(4, len(writes))
        profile = json.loads(writes[0]['Update']['ExpressionAttributeValues'][':next']['S'])
        self.assertEqual('Deleted', profile['Stage'])
        self.assertEqual(8, profile['Version'])
        self.assertEqual('2026-10-25T10:00:00Z', profile['PersonalDataDeletedAt'])
        self.assertFalse(writes[-1]['Update']['ExpressionAttributeValues'][':no']['BOOL'])
        self.assertTrue(all('Delete' not in item for item in writes))

    def test_restored_profile_is_never_revoked_from_stale_directory(self):
        self.assertEqual(0, self.run_migration(True, changed=True))
        self.assertNotIn('transact-write-items', [operation for operation, _ in self.calls])

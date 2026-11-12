import importlib.util
import json
from pathlib import Path
import unittest
from unittest.mock import patch

source = Path(__file__).resolve().parents[2] / 'scripts' / 'revoke-deleted-residents.py'
spec = importlib.util.spec_from_file_location('revoke_deleted', source)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class RevokeDeletedTests(unittest.TestCase):
    def test_legacy_write_cli_is_disabled(self):
        with patch('sys.argv', ['legacy-script', '--apply']), patch('sys.stderr'):
            with self.assertRaises(SystemExit):
                module.main()

    def run_migration(self, apply=False, changed=False, residents_table=None):
        self.calls = []
        profile = {'Id': 'reference', 'UserId': 99, 'BuildingId': 'complex', 'Stage': 'Approved', 'Version': 7,
                   'PersonalDataDeleted': True, 'PersonalDataDeletedAt': '2026-10-25T10:00:00Z',
                   'Email': 'resident@example.com'}
        def aws(service, operation, region, payload):
            self.calls.append((operation, payload))
            if operation == 'query':
                return {'Items': [{'payload': {'S': json.dumps(profile)}}]}
            if operation == 'get-item':
                if payload['Key']['sk']['S'] == 'MEMBERSHIP':
                    return {'Item': {'pk': {'S': 'USER#99'}, 'sk': {'S': 'MEMBERSHIP'},
                                     'buildingId': {'S': 'complex'}, 'active': {'BOOL': True},
                                     'reviewedBy': {'S': 'operator'}}}
                current = dict(profile, PersonalDataDeleted=False) if changed else profile
                return {'Item': {'payload': {'S': json.dumps(current)}, 'version': {'N': '7'}}}
            return {}
        return module.migrate('data', 'complex', 'eu-central-1', apply, aws, residents_table=residents_table)

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
        self.assertIsNone(profile['Email'])
        self.assertEqual('2026-10-25T10:00:00Z', profile['PersonalDataDeletedAt'])
        self.assertFalse(writes[-1]['Update']['ExpressionAttributeValues'][':no']['BOOL'])
        self.assertTrue(all('Delete' not in item for item in writes))

    def test_apply_also_clears_mirror_atomically(self):
        self.assertEqual(1, self.run_migration(True, residents_table='residents'))
        writes = self.calls[-1][1]['TransactItems']
        mirror = next(write['Put'] for write in writes if write.get('Put', {}).get('TableName') == 'residents' and '#USER#' in write['Put']['Item']['pk']['S'] and '#RECORD#' not in write['Put']['Item']['pk']['S'])
        self.assertEqual('7', mirror['ExpressionAttributeValues'][':version']['N'])
        self.assertNotIn('directory', mirror['Item'])
        self.assertIsNone(json.loads(mirror['Item']['payload']['S'])['Email'])
        related = [write['Put']['Item'] for write in writes if write.get('Put', {}).get('TableName') == 'residents' and '#RECORD#' in write['Put']['Item']['pk']['S']]
        self.assertTrue({'META', 'MEMBERSHIP', 'REGISTRATION'}.issubset({item['sourceSk']['S'] for item in related}))
        membership = next(item['recordData']['M'] for item in related if item['sourceSk']['S'] == 'MEMBERSHIP')
        self.assertFalse(membership['active']['BOOL'])
        self.assertEqual('operator', membership['reviewedBy']['S'])


    def test_restored_profile_is_never_revoked_from_stale_directory(self):
        self.assertEqual(0, self.run_migration(True, changed=True))
        self.assertNotIn('transact-write-items', [operation for operation, _ in self.calls])

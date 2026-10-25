import importlib.util
import json
from pathlib import Path
import unittest

source = Path(__file__).resolve().parents[2] / 'scripts' / 'backfill-request-priority.py'
spec = importlib.util.spec_from_file_location('priority', source)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class PriorityMigrationTests(unittest.TestCase):
    def test_key_preserves_utc_precision_and_numeric_priority_order(self):
        row = {'Id': 7, 'CreatedAt': '2026-10-25T12:00:00.1234567+02:00', 'Priority': 'High'}
        self.assertEqual('2#2026-10-25T10:00:00.1234567+00:00#00000000000000000007', module.priority_key(row))
        row['Priority'] = 'Urgent'
        self.assertTrue(module.priority_key(row).startswith('3#'))
        row.pop('Priority')
        self.assertTrue(module.priority_key(row).startswith('1#'))

    def test_migration_is_paged_scoped_idempotent_and_guards_payload(self):
        record = {'Id': 7, 'BuildingId': 'complex', 'CreatedAt': '2026-10-25T10:00:00Z', 'Priority': 'Urgent'}
        def item(value, order=None):
            row = {'pk': {'S': 'USER#42'}, 'sk': {'S': 'REQUEST#7'}, 'payload': {'S': json.dumps(value)}, 'requestBuildingId': {'S': value['BuildingId']}}
            if order:
                row['requestPriorityCreatedAt'] = {'S': order}
            return row
        pages = [{'Items': [item(record), item(dict(record, BuildingId='other'))],
                  'LastEvaluatedKey': {'pk': {'S': 'USER#42'}, 'sk': {'S': 'REQUEST#7'}}},
                 {'Items': [item(record, module.priority_key(record))]}]
        for apply in [False, True]:
            calls = []
            remaining = list(pages)
            def aws(service, operation, region, payload):
                calls.append((operation, payload))
                return remaining.pop(0) if operation == 'scan' else {}
            self.assertEqual(1, module.migrate('data', 'complex', 'eu-central-1', apply, aws))
            scans = [payload for operation, payload in calls if operation == 'scan']
            self.assertIn('ExclusiveStartKey', scans[1])
            updates = [payload for operation, payload in calls if operation == 'update-item']
            self.assertEqual(1 if apply else 0, len(updates))
            if apply:
                self.assertEqual('#p = :previous', updates[0]['ConditionExpression'])
                self.assertEqual(module.priority_key(record), updates[0]['ExpressionAttributeValues'][':order']['S'])
                self.assertNotIn('payload =', updates[0]['UpdateExpression'])

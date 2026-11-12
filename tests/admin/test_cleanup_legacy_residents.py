import copy
import importlib.util
import json
from pathlib import Path
import unittest
from unittest.mock import patch

source = Path(__file__).resolve().parents[2] / 'scripts' / 'cleanup-legacy-residents.py'
spec = importlib.util.spec_from_file_location('cleanup_residents', source)
cleanup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cleanup)


class CleanupTests(unittest.TestCase):
    def setUp(self):
        self.profile = {'Id': 'a' * 32, 'UserId': 42, 'BuildingId': 'complex', 'Stage': 'Approved', 'Version': 3}
        self.old = {'pk': {'S': 'USER#42'}, 'sk': {'S': 'REGISTRATION'},
                    'version': {'N': '3'}, 'payload': {'S': json.dumps(self.profile)}}
        self.new = cleanup.manage.related_resident_put('residents', self.old, 'complex')['Put']['Item']
        self.calls = []
        self.items = [self.old]
        self.target = self.new
        self.notifications = []

    def aws(self, service, operation, region, payload):
        self.calls.append((operation, payload))
        if operation == 'query':
            return {'Items': self.notifications}
        if operation == 'scan':
            return {'Items': self.items}
        if operation == 'get-item':
            return {'Item': self.target} if self.target else {}
        if operation == 'transact-write-items':
            return {}
        raise AssertionError(operation)

    def run_cleanup(self, apply=False, limit=None):
        return cleanup.cleanup('old', 'residents', 'complex', 'region', apply, limit, self.aws)

    def test_dry_run_does_not_delete_and_ignores_unrelated_records(self):
        self.items.append({'pk': {'S': 'USER#42'}, 'sk': {'S': 'REQUEST#123'}})
        result = self.run_cleanup()
        self.assertEqual(1, result['eligible'])
        self.assertEqual(1, result['examined'])
        self.assertNotIn('transact-write-items', [call[0] for call in self.calls])

    def test_newer_matching_profile_is_eligible_without_comparing_personal_data(self):
        current = copy.deepcopy(self.old)
        current['version'] = {'N': '4'}
        current['payload'] = {'S': json.dumps(dict(self.profile, Version=4, FirstName='Інше'))}
        self.target = cleanup.manage.related_resident_put('residents', current, 'complex')['Put']['Item']
        self.assertTrue(cleanup.verify(self.old, self.target, 'complex', 'profiles'))
        current['payload'] = {'S': json.dumps(dict(self.profile, Version=4, UserId=43))}
        self.target = cleanup.manage.related_resident_put('residents', current, 'complex')['Put']['Item']
        self.assertFalse(cleanup.verify(self.old, self.target, 'complex', 'profiles'))

    def test_same_version_with_differences_and_missing_copy_never_delete(self):
        self.target['recordData']['M']['payload'] = {'S': json.dumps(dict(self.profile, FirstName='Інше'))}
        self.assertEqual(1, self.run_cleanup(True, 10)['unsafe'])
        self.assertNotIn('transact-write-items', [call[0] for call in self.calls])
        self.target = None
        self.assertEqual(1, self.run_cleanup(True, 10)['missing'])

    def test_apply_uses_guarded_transaction_and_limit(self):
        self.items = [self.old, copy.deepcopy(self.old)]
        result = self.run_cleanup(True, 1)
        self.assertEqual(1, result['deleted'])
        self.assertEqual(1, result['limited'])
        transaction = next(call[1] for call in self.calls if call[0] == 'transact-write-items')['TransactItems']
        self.assertEqual('old', transaction[0]['Delete']['TableName'])
        self.assertEqual('residents', transaction[1]['ConditionCheck']['TableName'])
        self.assertIn({'S': json.dumps(self.profile)}, transaction[0]['Delete']['ExpressionAttributeValues'].values())
        self.assertIn(self.new['recordData'], transaction[1]['ConditionCheck']['ExpressionAttributeValues'].values())

    def test_incomplete_preflight_prevents_all_deletions(self):
        self.items = [self.old, {'pk': {'S': 'USER#43'}, 'sk': {'S': 'REGISTRATION'},
                                 'version': {'N': '1'}, 'payload': {'S': '{}'}}]
        result = self.run_cleanup(True, 10)
        self.assertEqual(1, result['unsafe'])
        self.assertNotIn('transact-write-items', [call[0] for call in self.calls])

    def test_legacy_broadcast_cursor_blocks_entire_run(self):
        self.notifications = [{'payload': {'S': json.dumps({'ComplexId': 'complex', 'IsBroadcast': True})},
                               'audienceCursor': {'S': json.dumps({'pk': 'BUILDING#complex', 'sk': 'RESIDENT#1'})}}]
        with self.assertRaisesRegex(RuntimeError, 'broadcast cursor'):
            self.run_cleanup(True, 10)
        self.assertNotIn('scan', [call[0] for call in self.calls])

    def test_changed_history_is_never_accepted(self):
        old = {'pk': {'S': 'REGISTRATION#' + 'a' * 32}, 'sk': {'S': 'EVENT#DECIDED'}, 'actor': {'S': 'a'}}
        target = cleanup.manage.related_resident_put('residents', old, 'complex')['Put']['Item']
        self.assertTrue(cleanup.verify(old, target, 'complex', 'history'))
        target['recordData']['M']['actor'] = {'S': 'b'}
        self.assertFalse(cleanup.verify(old, target, 'complex', 'history'))

    def test_scan_and_notification_query_follow_all_pages(self):
        def aws(service, operation, region, request):
            self.calls.append((operation, request))
            if operation == 'query':
                return {'Items': [], 'LastEvaluatedKey': {'pk': {'S': 'p'}, 'sk': {'S': 'd'}}} if 'ExclusiveStartKey' not in request else {'Items': []}
            if operation == 'scan':
                return {'Items': [], 'LastEvaluatedKey': {'pk': {'S': 'p'}, 'sk': {'S': 's'}}} if 'ExclusiveStartKey' not in request else {'Items': [self.old]}
            if operation == 'get-item':
                return {'Item': self.new}
            raise AssertionError(operation)
        result = cleanup.cleanup('old', 'residents', 'complex', 'region', aws=aws)
        self.assertEqual(1, result['eligible'])
        self.assertEqual(2, sum(op == 'query' for op, _ in self.calls))
        self.assertEqual(2, sum(op == 'scan' for op, _ in self.calls))

    def test_cli_requires_explicit_confirmations_before_aws_calls(self):
        with patch('sys.argv', ['cleanup', '--stage', 'dev', '--region', 'eu-central-1', '--apply']), \
             patch('sys.stderr'), patch.object(cleanup, 'cleanup') as action:
            with self.assertRaises(SystemExit):
                cleanup.main()
        action.assert_not_called()


if __name__ == '__main__':
    unittest.main()

import copy
import importlib.util
import json
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('migration', Path(__file__).resolve().parents[2] / 'scripts' / 'migrate-residents-table.py')
migration = importlib.util.module_from_spec(spec)
spec.loader.exec_module(migration)


def profile(**changes):
    data = {'Id': 'a' * 32, 'UserId': 99, 'BuildingId': 'complex', 'Stage': 'Approved', 'Version': 4,
            'FirstName': 'Іван', 'LastName': 'Ґалаґан', 'Phone': '+380500000000', 'BuildingNumber': '4Б',
            'CreatedAt': '2026-01-01T00:00:00Z', 'UpdatedAt': '2026-10-20T11:00:00.1234567+00:00'}
    data.update(changes)
    return data


def item(data):
    return {'pk': {'S': 'USER#' + str(data['UserId'])}, 'sk': {'S': 'REGISTRATION'},
            'version': {'N': str(data['Version'])}, 'payload': {'S': json.dumps(data, ensure_ascii=False)}}


class FakeAws:
    def __init__(self, data, existing=None):
        self.source = item(data)
        self.target = existing
        self.writes = []
        self.race = False

    def __call__(self, service, operation, region, payload):
        if operation == 'scan':
            return {'Items': [copy.deepcopy(self.source)]}
        if operation == 'get-item':
            found = self.source if payload['TableName'] == 'source' else self.target
            return {'Item': copy.deepcopy(found)} if found else {}
        if operation == 'transact-write-items':
            self.writes.append(copy.deepcopy(payload))
            if self.race:
                self.race = False
                updated = json.loads(self.source['payload']['S'])
                updated.update(Version=5, FirstName='Нове')
                self.source = item(updated)
                raise RuntimeError('conditional conflict')
            self.target = copy.deepcopy(payload['TransactItems'][1]['Put']['Item'])
            return {}
        raise AssertionError(operation)


class ResidentMigrationTests(unittest.TestCase):
    def test_dry_run_does_not_write_and_apply_is_idempotent(self):
        aws = FakeAws(profile())
        result = migration.migrate('source', 'target', 'complex', 'eu-central-1', aws=aws)
        self.assertEqual(1, result['missing'])
        self.assertEqual([], aws.writes)
        result = migration.migrate('source', 'target', 'complex', 'eu-central-1', True, aws)
        self.assertEqual(1, result['written'])
        check, put = aws.writes[0]['TransactItems']
        self.assertIn(':payload', check['ConditionCheck']['ConditionExpression'])
        self.assertIn('#version < :version', put['Put']['ConditionExpression'])
        self.assertTrue(aws.target['dateOrder']['S'].startswith('2026-10-20T11:00:00.1234567+00:00'))
        again = migration.migrate('source', 'target', 'complex', 'eu-central-1', True, aws)
        self.assertEqual(1, again['current'])
        self.assertEqual(1, len(aws.writes))

    def test_newer_profile_is_reloaded_after_concurrent_change(self):
        aws = FakeAws(profile())
        aws.race = True
        result = migration.migrate('source', 'target', 'complex', 'eu-central-1', True, aws)
        self.assertEqual(1, result['written'])
        self.assertEqual('5', aws.target['version']['N'])
        self.assertEqual('Нове', json.loads(aws.target['payload']['S'])['FirstName'])

    def test_stale_mirror_updated_only_if_version_is_older(self):
        current = profile()
        old = migration.manage.resident_mirror_put('target', profile(Version=3), 2)['Put']['Item']
        aws = FakeAws(current, old)
        self.assertEqual(1, migration.migrate('source', 'target', 'complex', 'eu-central-1', True, aws)['stale'])
        self.assertEqual('4', aws.target['version']['N'])

    def test_deleted_personal_data_is_never_copied(self):
        aws = FakeAws(profile(Stage='Deleted', PersonalDataDeleted=True, Email='resident@example.com'))
        migration.migrate('source', 'target', 'complex', 'eu-central-1', True, aws)
        copied = json.loads(aws.target['payload']['S'])
        self.assertIsNone(copied['FirstName'])
        self.assertIsNone(copied['Phone'])
        self.assertIsNone(copied['Email'])
        self.assertNotIn('directory', aws.target)

    def test_same_version_different_data_is_conflict(self):
        bad = migration.manage.resident_mirror_put('target', profile(FirstName='Інше'), 3)['Put']['Item']
        aws = FakeAws(profile(), bad)
        result = migration.migrate('source', 'target', 'complex', 'eu-central-1', True, aws)
        self.assertEqual(1, result['conflicts'])
        self.assertEqual([], aws.writes)

    def test_scan_pages_are_all_processed_with_aggregate_counts(self):
        first = item(profile(UserId=1))
        second = item(profile(UserId=2, Stage='Pending'))
        foreign = item(profile(UserId=3, BuildingId='other'))
        scans = []
        def aws(service, operation, region, payload):
            if operation == 'scan':
                scans.append(payload)
                return {'Items': [first, foreign], 'LastEvaluatedKey': {'pk': {'S': 'next'}}} if len(scans) == 1 else {'Items': [second]}
            if operation == 'get-item':
                return {}
            raise AssertionError(operation)
        result = migration.migrate('source', 'target', 'complex', 'eu-central-1', aws=aws)
        self.assertEqual(2, result['examined'])
        self.assertEqual(2, result['missing'])
        self.assertEqual({'pk': {'S': 'next'}}, scans[1]['ExclusiveStartKey'])

    def test_foreign_complex_skipped_and_bad_key_rejected(self):
        aws = FakeAws(profile(BuildingId='other'))
        self.assertEqual(0, migration.migrate('source', 'target', 'complex', 'eu-central-1', True, aws)['examined'])
        aws = FakeAws(profile())
        aws.source['pk']['S'] = 'USER#100'
        with self.assertRaises(RuntimeError):
            migration.migrate('source', 'target', 'complex', 'eu-central-1', True, aws)

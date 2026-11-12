import copy
import importlib.util
import json
from pathlib import Path
import threading
import unittest

spec = importlib.util.spec_from_file_location('migration', Path(__file__).resolve().parents[2] / 'scripts' / 'migrate-resident-records.py')
migration = importlib.util.module_from_spec(spec)
spec.loader.exec_module(migration)


def record(pk, sk, **attributes):
    return {'pk': {'S': pk}, 'sk': {'S': sk}, **attributes}


class FakeAws:
    def __init__(self):
        self.reference = 'a' * 32
        self.meta = record('REGISTRATION#' + self.reference, 'META',
                           payload={'S': json.dumps({'Id': self.reference, 'BuildingId': 'complex'})}, version={'N': '4'})
        self.items = [self.meta,
                      record('REGISTRATION#' + self.reference, 'EVENT#DECIDED', actor={'S': 'admin@example.org'}, decision={'S': 'Approved'}),
                      record('REGISTRATION#' + self.reference, 'EVENT#TYPE#1', payload={'S': '{"actor":"admin"}'}),
                      record('USER#99', 'MEMBERSHIP', buildingId={'S': 'complex'}, active={'BOOL': True}),
                      record('USER#99', 'PROFILE_DIALOG', payload={'S': json.dumps({'ComplexId': 'complex', 'UserId': 99, 'Active': False})}, version={'N': '3'}),
                      record('USER#99', 'REGISTRATION', payload={'S': json.dumps({'UserId': 99, 'BuildingId': 'complex'})}, version={'N': '4'}),
                      record('BUILDING#complex', 'REGISTRATION#' + self.reference, payload={'S': json.dumps({'Id': self.reference, 'UserId': 99, 'BuildingId': 'complex'})}, version={'N': '4'}),
                      record('BUILDING#complex', 'RESIDENT#' + str(99).zfill(20), payload={'S': json.dumps({'Id': self.reference, 'UserId': 99, 'BuildingId': 'complex'})}, version={'N': '4'}),
                      record('USER#99', 'REQUEST#1', payload={'S': 'not a resident record'})]
        self.target = {}
        self.writes = []
        self.race = False
        self.race_writes_target = False
        self.lock = threading.Lock()

    def __call__(self, service, operation, region, payload):
        with self.lock:
            if operation == 'scan':
                if payload['FilterExpression'].startswith('begins_with(pk, :reg) AND sk'):
                    return {'Items': [copy.deepcopy(self.meta)]}
                return {'Items': copy.deepcopy(self.items)}
            if operation == 'get-item':
                if payload['TableName'] == 'target':
                    found = self.target.get(payload['Key']['pk']['S'])
                else:
                    found = next((x for x in self.items if x['pk'] == payload['Key']['pk'] and x['sk'] == payload['Key']['sk']), None)
                return {'Item': copy.deepcopy(found)} if found else {}
            if operation == 'transact-write-items':
                self.writes.append(copy.deepcopy(payload))
                if self.race:
                    self.race = False
                    if self.race_writes_target:
                        entry = payload['TransactItems'][1]['Put']['Item']
                        self.target[entry['pk']['S']] = copy.deepcopy(entry)
                    raise RuntimeError('transaction conflict')
                entry = payload['TransactItems'][1]['Put']['Item']
                self.target[entry['pk']['S']] = copy.deepcopy(entry)
                return {}
            raise AssertionError(operation)


class RelatedResidentMigrationTests(unittest.TestCase):
    def test_all_related_records_are_exactly_copied_and_idempotent(self):
        aws = FakeAws()
        dry = migration.migrate('source', 'target', 'complex', 'eu-central-1', aws=aws)
        self.assertEqual(8, dry['missing'])
        self.assertEqual([], aws.writes)
        done = migration.migrate('source', 'target', 'complex', 'eu-central-1', True, aws)
        self.assertEqual(8, done['written'])
        self.assertEqual([1, 1, 1, 1, 1, 1, 1, 1], [done[k] for k in ('metadata', 'decisions', 'history', 'membership', 'dialogs', 'current_profiles', 'pending_entries', 'resident_entries')])
        for transaction in aws.writes:
            check, put = transaction['TransactItems']
            self.assertEqual('attribute_not_exists(pk)', put['Put']['ConditionExpression'])
            self.assertIn('attribute_exists(pk)', check['ConditionCheck']['ConditionExpression'])
            original = next(x for x in aws.items if x['pk'] == check['ConditionCheck']['Key']['pk'] and x['sk'] == check['ConditionCheck']['Key']['sk'])
            self.assertEqual(original, put['Put']['Item']['recordData']['M'])
        self.assertEqual(8, migration.migrate('source', 'target', 'complex', 'eu-central-1', True, aws)['current'])
        self.assertEqual(8, len(aws.writes))

    def test_inconsistent_target_is_not_overwritten(self):
        aws = FakeAws()
        destination = migration.manage.related_resident_put('target', aws.meta, 'complex', immutable=True)['Put']['Item']
        destination['recordData']['M']['version'] = {'N': '999'}
        aws.target[destination['pk']['S']] = destination
        result = migration.migrate('source', 'target', 'complex', 'eu-central-1', aws=aws)
        self.assertEqual(1, result['conflicts'])
        self.assertEqual([], aws.writes)

    def test_missing_metadata_is_reported_without_copying_orphan(self):
        aws = FakeAws()
        aws.items = [record('REGISTRATION#orphan', 'EVENT#DECIDED', actor={'S': 'admin'})]
        result = migration.migrate('source', 'target', 'complex', 'eu-central-1', True, aws)
        self.assertEqual(1, result['unresolved'])
        self.assertEqual([], aws.writes)

    def test_transaction_retry_is_safe(self):
        aws = FakeAws()
        aws.race = True
        result = migration.migrate('source', 'target', 'complex', 'eu-central-1', True, aws)
        self.assertEqual(8, result['written'])
        self.assertEqual(0, result['conflicts'])

    def test_concurrent_writer_is_not_reported_as_missing(self):
        aws = FakeAws()
        aws.race = True
        aws.race_writes_target = True
        result = migration.migrate('source', 'target', 'complex', 'eu-central-1', True, aws)
        self.assertEqual(1, result['current'])
        self.assertEqual(0, result['missing'] - result['written'])
        self.assertEqual(0, result['conflicts'])

    def test_foreign_membership_and_dialog_are_not_copied(self):
        aws = FakeAws()
        aws.items = [record('USER#1', 'MEMBERSHIP', buildingId={'S': 'other'}, active={'BOOL': True}),
                     record('USER#1', 'PROFILE_DIALOG', payload={'S': '{"ComplexId":"other","UserId":1}'})]
        result = migration.migrate('source', 'target', 'complex', 'eu-central-1', True, aws)
        self.assertEqual(0, result['examined'])
        self.assertEqual([], aws.writes)

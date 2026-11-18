from copy import deepcopy
import importlib.util
import json
from pathlib import Path
import unittest

source = Path(__file__).resolve().parents[2] / 'scripts' / 'backfill-resident-directory.py'
spec = importlib.util.spec_from_file_location('backfill_resident_directory', source)
backfill = importlib.util.module_from_spec(spec)
spec.loader.exec_module(backfill)


def profile(user=42, **fields):
    resident = dict(Id=f'{user:032x}', UserId=user, BuildingId='test', Version=5, Stage='Approved',
                    UpdatedAt='2026-10-20T11:00:00.1234567Z', FirstName='Ім’я', BuildingNumber='4Б', **fields)
    return {'pk': {'S': f'COMPLEX#test#USER#{user}'}, 'complexId': {'S': 'test'},
            'version': {'N': '5'}, 'payload': {'S': json.dumps(resident)}}


class Dynamo:
    def __init__(self, *items):
        self.items = {item['pk']['S']: item for item in items}
        self.transactions = []
        self.race = False
        self.stale_index = False

    def __call__(self, service, operation, region, request):
        if operation == 'scan':
            assert request['ConsistentRead']
            # Deliver every item on its own page.
            items = list(self.items.values())
            start = request.get('ExclusiveStartKey')
            index = list(self.items).index(start['pk']['S']) + 1 if start else 0
            return {'Items': deepcopy(items[index:index + 1]),
                    'LastEvaluatedKey': {'pk': items[index]['pk']} if index + 1 < len(items) else None}
        if operation == 'get-item':
            assert request['ConsistentRead']
            return {'Item': deepcopy(self.items.get(request['Key']['pk']['S']))}
        if operation == 'transact-write-items':
            self.transactions.append(request)
            check = request['TransactItems'][0]['ConditionCheck']
            row = self.items[check['Key']['pk']['S']]
            if self.race:
                replacement = json.loads(row['payload']['S'])
                replacement.update(Version=6, Stage='Deleted', PersonalDataDeleted=True)
                row.update(version={'N': '6'}, payload={'S': json.dumps(replacement)})
                raise RuntimeError('Conditional check failed')
            assert check['ExpressionAttributeValues'][':payload'] == row['payload']
            assert check['ExpressionAttributeValues'][':version'] == row['version']
            for write in request['TransactItems'][1:]:
                if 'Put' in write:
                    assert write['Put']['ConditionExpression'] == 'attribute_not_exists(pk)'
                    item = write['Put']['Item']
                    assert item['pk']['S'] not in self.items
                    self.items[item['pk']['S']] = deepcopy(item)
                elif write['Update']['UpdateExpression'].startswith('SET '):
                    update = write['Update']
                    item = self.items[update['Key']['pk']['S']]
                    assert 'sourceVersion = :version' in update['ConditionExpression']
                    for name, attribute in update['ExpressionAttributeNames'].items():
                        assert attribute not in item
                        item[attribute] = update['ExpressionAttributeValues'][name.replace('#', ':')]
                else:
                    update = write['Update']
                    key = update['Key']['pk']['S']
                    count = self.items.setdefault(key, {'pk': {'S': key}})
                    resident_type = update['ExpressionAttributeNames']['#type']
                    before = int(count.get(resident_type, {'N': '0'})['N'])
                    count[resident_type] = {'N': str(before + 1)}
            return {}
        if operation == 'describe-table':
            return {'Table': {'GlobalSecondaryIndexes': [
                {'IndexName': name, 'IndexStatus': 'ACTIVE'} for name in backfill.INDEXES]}}
        if operation == 'query':
            group = request['ExpressionAttributeValues'][':group']
            sort = backfill.INDEXES[request['IndexName']]
            rows = sorted((row for row in self.items.values() if row.get('listGroup') == group),
                          key=lambda row: row[sort]['S'])
            if self.stale_index:
                rows = rows[1:]
            start = request.get('ExclusiveStartKey')
            index = [row['pk'] for row in rows].index(start['pk']) + 1 if start else 0
            return {'Items': [{key: value for key, value in row.items()
                               if key != 'sourceVersion' and (key not in backfill.INDEXES.values() or key == sort)}
                              for row in rows[index:index + 1]],
                    'LastEvaluatedKey': {'pk': rows[index]['pk']} if index + 1 < len(rows) else None}
        raise AssertionError(operation)


class BackfillTests(unittest.TestCase):
    def run_migration(self, fake, **kwargs):
        return backfill.migrate('residents', 'test', 'eu-central-1', aws=fake, **kwargs)

    def test_dry_run_then_apply_then_verify_are_idempotent(self):
        fake = Dynamo(profile(), profile(43, ResidentType='Owner'))
        dry = self.run_migration(fake)
        self.assertEqual(4, dry['missing_rows'])
        self.assertEqual([], fake.transactions)
        applied = self.run_migration(fake, apply=True, check_indexes=True)
        self.assertEqual(2, applied['written'])
        self.assertEqual(0, applied['counter_mismatches'])
        self.assertEqual(0, applied['index_group_mismatches'])
        self.assertEqual(0, self.run_migration(fake, apply=True)['written'])
        self.assertEqual(2, len(fake.transactions))

    def test_backfill_enriches_existing_rows_without_incrementing_counts(self):
        fake = Dynamo(profile())
        self.run_migration(fake, apply=True)
        keys = {'listNameOrder': {'B': 'AA=='}, 'listNameOrderDescending': {'B': 'AQ=='}}
        result = self.run_migration(fake, apply=True, name_key_generator=lambda profiles: [keys])
        self.assertEqual(0, result['counter_mismatches'])
        self.assertEqual(0, result['changed_rows'])
        self.assertEqual(1, result['written'])
        updates = fake.transactions[-1]['TransactItems'][1:]
        self.assertEqual(2, len(updates))
        self.assertTrue(all('DIRECTORY_ROW' in w['Update']['Key']['pk']['S'] for w in updates))
        repeat = self.run_migration(fake, apply=True, name_key_generator=lambda profiles: [keys])
        self.assertEqual(0, repeat['written'])

    def test_race_never_creates_rows_or_increments_counts_for_deleted_profile(self):
        fake = Dynamo(profile())
        fake.race = True
        result = self.run_migration(fake, apply=True)
        self.assertEqual(1, result['concurrent_changes'])
        self.assertEqual(0, result['eligible'])
        self.assertEqual(0, result['written'])
        self.assertEqual(1, len(fake.items))

    def test_reports_partial_projection_and_counter_drift_without_overwriting(self):
        fake = Dynamo(profile())
        self.run_migration(fake, apply=True)
        del fake.items['COMPLEX#test#DIRECTORY_ROW#BUILDING#42']
        fake.items['COMPLEX#test#DIRECTORY_COUNT#ALL']['FamilyMember']['N'] = '2'
        result = self.run_migration(fake, apply=True, check_indexes=True)
        self.assertEqual(1, result['missing_rows'])
        self.assertEqual(1, result['partial_profiles'])
        self.assertEqual(1, result['counter_mismatches'])
        self.assertEqual(2, result['index_group_mismatches'])
        self.assertEqual(0, result['written'])

    def test_verification_detects_index_lag_with_correct_base_rows(self):
        fake = Dynamo(profile())
        self.run_migration(fake, apply=True)
        fake.stale_index = True
        result = self.run_migration(fake, check_indexes=True)
        self.assertEqual(0, result['missing_rows'])
        self.assertEqual(4, result['index_group_mismatches'])

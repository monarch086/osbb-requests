import copy
import importlib.util
import json
from pathlib import Path
import unittest

source = Path(__file__).resolve().parents[2] / 'scripts' / 'backfill-resident-phones.py'
spec = importlib.util.spec_from_file_location('backfill_phones', source)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class PhoneBackfillTests(unittest.TestCase):
    def setUp(self):
        self.profile = {'BuildingId': 'complex', 'UserId': 42, 'Stage': 'Approved', 'Version': 4, 'Phone': '+380671234567'}
        self.item = {'pk': {'S': 'COMPLEX#complex#USER#42'}, 'complexId': {'S': 'complex'},
                     'version': {'N': '4'}, 'payload': {'S': json.dumps(self.profile)}}
        self.existing = None
        self.calls = []

    def aws(self, service, operation, region, request):
        self.calls.append((operation, request))
        if operation == 'scan':
            prefix = request['ExpressionAttributeValues'][':prefix']['S']
            return {'Items': [self.item] if '#USER#' in prefix else []}
        if operation == 'get-item':
            return {'Item': self.existing} if self.existing else {}
        if operation == 'transact-write-items':
            return {}
        raise AssertionError(operation)

    def test_dry_run_and_guarded_apply(self):
        result = module.migrate('residents', 'complex', 'region', aws=self.aws)
        self.assertEqual(1, result['missing'])
        self.assertNotIn('transact-write-items', [op for op, _ in self.calls])
        result = module.migrate('residents', 'complex', 'region', apply=True, aws=self.aws)
        self.assertEqual(1, result['written'])
        tx = [req for op, req in self.calls if op == 'transact-write-items'][0]['TransactItems']
        self.assertIn('#v = :version', tx[0]['ConditionCheck']['ConditionExpression'])
        self.assertEqual(self.item['payload'], tx[0]['ConditionCheck']['ExpressionAttributeValues'][':payload'])
        self.assertEqual('attribute_not_exists(pk)', tx[1]['Put']['ConditionExpression'])

    def test_existing_association_is_not_overwritten_and_mismatch_is_conflict(self):
        self.existing = module.manage.phone_association_put('residents', self.profile)['Put']['Item']
        self.assertEqual(1, module.migrate('residents', 'complex', 'region', True, self.aws)['current'])
        self.existing = dict(self.existing, residentId={'N': '99'})
        self.assertEqual(1, module.migrate('residents', 'complex', 'region', True, self.aws)['conflicts'])
        self.assertNotIn('transact-write-items', [op for op, _ in self.calls])

    def test_pending_deleted_and_invalid_profiles_are_not_written(self):
        for stage, deleted in [('Pending', False), ('Approved', True)]:
            profile = dict(self.profile, Stage=stage, PersonalDataDeleted=deleted)
            self.item['payload'] = {'S': json.dumps(profile)}
            self.assertEqual(0, module.migrate('residents', 'complex', 'region', True, self.aws)['eligible'])
        self.item['payload'] = {'S': json.dumps(dict(self.profile, Phone='invalid'))}
        self.assertEqual(1, module.migrate('residents', 'complex', 'region', True, self.aws)['invalid'])
        self.assertNotIn('transact-write-items', [op for op, _ in self.calls])

    def test_orphaned_association_is_reported(self):
        association = module.manage.phone_association_put('residents', self.profile)['Put']['Item']
        def aws(service, operation, region, request):
            if operation == 'scan':
                prefix = request['ExpressionAttributeValues'][':prefix']['S']
                return {'Items': [] if '#USER#' in prefix else [association]}
            if operation == 'get-item':
                return {}
            raise AssertionError(operation)
        self.assertEqual(1, module.migrate('residents', 'complex', 'region', aws=aws)['orphaned'])

    def test_source_version_mismatch_is_rejected(self):
        self.item = copy.deepcopy(self.item)
        self.item['version'] = {'N': '3'}
        self.assertEqual(1, module.migrate('residents', 'complex', 'region', True, self.aws)['invalid'])


if __name__ == '__main__':
    unittest.main()

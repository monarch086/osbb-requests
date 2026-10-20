import copy
import importlib.util
import json
from pathlib import Path
import unittest

source = Path(__file__).resolve().parents[2] / 'scripts' / 'backfill-residents.py'
spec = importlib.util.spec_from_file_location('backfill_residents', source)
backfill = importlib.util.module_from_spec(spec)
spec.loader.exec_module(backfill)


class ResidentBackfillTests(unittest.TestCase):
    def profile(self, stage='Approved', building='building'):
        return dict(backfill.manage.key('USER#99', 'REGISTRATION'), version={'N': '5'}, payload={'S': json.dumps({
            'UserId': 99, 'BuildingId': building, 'Stage': stage, 'Phone': '+380500000000'
        })})

    def test_dry_run_skips_other_buildings_and_unapproved_profiles_across_pages(self):
        calls = []

        def aws(service, operation, region, request):
            calls.append((operation, request))
            if operation == 'get-item':
                return {}
            if 'ExclusiveStartKey' not in request:
                return {'Items': [self.profile('Pending'), self.profile(building='other')],
                        'LastEvaluatedKey': backfill.manage.key('USER#98', 'REGISTRATION')}
            return {'Items': [self.profile()]}

        self.assertEqual(1, backfill.migrate('data', 'building', 'region', aws=aws))
        self.assertEqual(['scan', 'scan', 'get-item'], [c[0] for c in calls])

    def test_apply_preserves_source_and_guards_both_source_and_destination(self):
        source = self.profile()
        before = copy.deepcopy(source)
        writes = []

        def aws(service, operation, region, request):
            if operation == 'scan':
                return {'Items': [source]}
            if operation == 'get-item':
                return {}
            writes.extend(request['TransactItems'])
            return {}

        self.assertEqual(1, backfill.migrate('data', 'building', 'region', True, aws))
        self.assertEqual(before, source)
        self.assertEqual(source['payload'], writes[1]['Put']['Item']['payload'])
        self.assertEqual(source['version'], writes[1]['Put']['Item']['version'])
        self.assertIn(':previous', writes[0]['ConditionCheck']['ConditionExpression'])
        self.assertEqual('attribute_not_exists(pk)', writes[1]['Put']['ConditionExpression'])
        self.assertEqual('RESIDENT#00000000000000000099', writes[1]['Put']['Item']['sk']['S'])

    def test_rerun_does_not_overwrite_existing_directory_entry(self):
        operations = []

        def aws(service, operation, region, request):
            operations.append(operation)
            return {'Items': [self.profile()]} if operation == 'scan' else {'Item': {'version': {'N': '6'}}}

        self.assertEqual(0, backfill.migrate('data', 'building', 'region', True, aws))
        self.assertNotIn('transact-write-items', operations)

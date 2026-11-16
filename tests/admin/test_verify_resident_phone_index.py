import importlib.util
from pathlib import Path
import unittest

source = Path(__file__).resolve().parents[2] / 'scripts' / 'verify-resident-phone-index.py'
spec = importlib.util.spec_from_file_location('verify_phone_index', source)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class PhoneIndexVerificationTests(unittest.TestCase):
    def test_follows_index_pagination_and_checks_item_identity(self):
        item = {'pk': {'S': 'COMPLEX#complex#PHONE_ENTRY#42#+380671234567'},
                'phoneLookup': {'S': 'COMPLEX#complex#PHONE#+380671234567'}, 'complexId': {'S': 'complex'}}
        calls = []
        def aws(service, operation, region, request):
            calls.append((operation, request))
            if operation == 'scan':
                return {'Items': [item]}
            if operation == 'query' and 'ExclusiveStartKey' not in request:
                return {'Items': [{'pk': {'S': 'another'}}], 'LastEvaluatedKey': {'pk': {'S': 'cursor'}}}
            return {'Items': [{'pk': item['pk']}]}
        counts = module.verify('residents', 'complex', 'region', aws)
        self.assertEqual({'examined': 1, 'visible': 1, 'missing': 0, 'invalid': 0}, counts)
        self.assertEqual(2, len([1 for op, _ in calls if op == 'query']))
        self.assertTrue(calls[0][1]['ConsistentRead'])

    def test_missing_or_invalid_entries_are_reported(self):
        item = {'pk': {'S': 'COMPLEX#complex#PHONE_ENTRY#42#+380671234567'},
                'phoneLookup': {'S': 'COMPLEX#complex#PHONE#+380671234567'}, 'complexId': {'S': 'complex'}}
        invalid = dict(item, complexId={'S': 'other'})
        def aws(service, operation, region, request):
            return {'Items': [item, invalid]} if operation == 'scan' else {'Items': []}
        counts = module.verify('residents', 'complex', 'region', aws)
        self.assertEqual(1, counts['missing'])
        self.assertEqual(1, counts['invalid'])


if __name__ == '__main__':
    unittest.main()

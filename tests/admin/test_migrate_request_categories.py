import importlib.util
import json
from pathlib import Path
import unittest

source = Path(__file__).resolve().parents[2] / 'scripts' / 'migrate-request-categories.py'
spec = importlib.util.spec_from_file_location('categories', source)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class CategoryMigrationTests(unittest.TestCase):
    def test_mapping_covers_old_categories(self):
        for old, expected in [('електрика', 'Електрика МЗК'), ('сантехніка', 'Сантехніка МЗК'),
                              ('ліфт', 'Ліфти'), ('прибирання', 'Прибирання МЗК'),
                              ('місця загального користування', 'Інше'), ('інше', 'Інше')]:
            self.assertEqual(expected, module.CATEGORIES[old])

    def test_pagination_scope_version_and_idempotence(self):
        def item(category, complex_id='complex', version=True):
            record = {'BuildingId': complex_id, 'Category': category, 'Description': 'preserved'}
            result = {'pk': {'S': 'USER#1'}, 'sk': {'S': 'REQUEST#1'},
                      'payload': {'S': json.dumps(record)}, 'requestCategory': {'S': category}}
            if version:
                result['version'] = {'N': '3'}
            return result
        pages = [{'Items': [item('ліфт'), item('інше', 'other')], 'LastEvaluatedKey': {'pk': {'S': 'x'}}},
                 {'Items': [item('Інше'), item('прибирання', version=False)]}]
        for apply in (False, True):
            calls = []
            remaining = list(pages)
            def aws(service, operation, region, payload):
                calls.append((operation, payload))
                return remaining.pop(0) if operation == 'scan' else {}
            counts = module.migrate('data', 'complex', 'region', apply, aws)
            self.assertEqual(2, sum(counts.values()))
            self.assertIn('ExclusiveStartKey', [p for op, p in calls if op == 'scan'][1])
            updates = [p for op, p in calls if op == 'update-item']
            self.assertEqual(2 if apply else 0, len(updates))
            if apply:
                self.assertEqual('4', updates[0]['ExpressionAttributeValues'][':nextVersion']['N'])
                self.assertIn('#v = :version', updates[0]['ConditionExpression'])
                self.assertIn('attribute_not_exists(#v)', updates[1]['ConditionExpression'])
                record = json.loads(updates[0]['ExpressionAttributeValues'][':next']['S'])
                self.assertEqual('Ліфти', record['Category'])
                self.assertEqual('preserved', record['Description'])

    def test_unknown_category_is_not_silently_changed(self):
        item = {'pk': {'S': 'USER#1'}, 'sk': {'S': 'REQUEST#1'},
                'payload': {'S': json.dumps({'BuildingId': 'complex', 'Category': 'unknown'})}}
        with self.assertRaises(RuntimeError):
            module.migrate('data', 'complex', 'region', True, lambda *_: {'Items': [item]})

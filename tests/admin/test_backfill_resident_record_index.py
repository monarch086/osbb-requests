import importlib.util
from pathlib import Path
import unittest
from unittest.mock import Mock

spec = importlib.util.spec_from_file_location(
    'backfill_resident_record_index',
    Path(__file__).resolve().parents[2] / 'scripts' / 'backfill-resident-record-index.py',
)
backfill = importlib.util.module_from_spec(spec)
spec.loader.exec_module(backfill)


def record(source_pk, order):
    source = {'pk': {'S': source_pk}, 'sk': {'S': order}}
    return backfill.manage.related_resident_put('residents', source, 'complex')['Put']['Item']


class ResidentRecordIndexTests(unittest.TestCase):
    def test_only_directory_and_edit_events_are_indexed_by_new_writes(self):
        for pk, order, indexed in (
            ('BUILDING#complex', 'RESIDENT#42', True),
            ('REGISTRATION#id', 'EVENT#TYPE#event', True),
            ('REGISTRATION#id', 'EVENT#DECIDED', False),
            ('USER#42', 'REGISTRATION', False),
            ('BUILDING#other', 'RESIDENT#42', False),
        ):
            with self.subTest(pk=pk, order=order):
                row = record(pk, order)
                self.assertEqual(indexed, backfill.GROUP in row)
                if indexed:
                    self.assertEqual(row['recordGroup'], row[backfill.GROUP])

    def test_dry_run_and_apply_only_backfill_missing_attributes(self):
        directory = record('BUILDING#complex', 'RESIDENT#42')
        del directory[backfill.GROUP]
        history = record('REGISTRATION#id', 'EVENT#TYPE#event')
        profile = record('USER#42', 'REGISTRATION')
        pages = [
            {'Items': [profile, directory], 'LastEvaluatedKey': {'pk': directory['pk']}},
            {'Items': [history]},
        ]
        aws = Mock(side_effect=pages)
        report = backfill.run('residents', 'complex', 'eu-central-1', 'dry-run', aws)
        self.assertEqual({'directory': 1, 'history': 1, 'invalid': 0, 'missing_attributes': 1}, report)
        self.assertEqual(['scan', 'scan'], [call.args[1] for call in aws.call_args_list])
        self.assertEqual(pages[0]['LastEvaluatedKey'], aws.call_args_list[1].args[3]['ExclusiveStartKey'])

        aws = Mock(side_effect=[*pages, {}])
        report = backfill.run('residents', 'complex', 'eu-central-1', 'apply', aws)
        self.assertEqual(1, report['updated'])
        _, operation, _, update = aws.call_args.args
        self.assertEqual('update-item', operation)
        self.assertEqual({'pk': directory['pk']}, update['Key'])
        self.assertEqual('SET recordIndexGroup = :group', update['UpdateExpression'])
        self.assertIn('complexId = :complex', update['ConditionExpression'])
        self.assertIn('sourcePk = :sourcePk', update['ConditionExpression'])

    def test_verify_detects_missing_and_unexpected_index_entries(self):
        directory = record('BUILDING#complex', 'RESIDENT#42')
        history = record('REGISTRATION#id', 'EVENT#TYPE#event')
        unexpected = record('REGISTRATION#other', 'EVENT#TYPE#event')
        aws = Mock(side_effect=[{'Items': [directory, history]}, {'Items': [directory, unexpected]}])
        report = backfill.run('residents', 'complex', 'eu-central-1', 'verify', aws)
        self.assertEqual(1, report['missing_in_index'])
        self.assertEqual(1, report['unexpected_in_index'])
        self.assertEqual(backfill.INDEX, aws.call_args.args[3]['IndexName'])

    def test_apply_leaves_table_alone_if_existing_attribute_is_wrong(self):
        directory = record('BUILDING#complex', 'RESIDENT#42')
        history = record('REGISTRATION#id', 'EVENT#TYPE#event')
        del directory[backfill.GROUP]
        history[backfill.GROUP] = {'S': 'wrong'}
        aws = Mock(return_value={'Items': [directory, history]})
        report = backfill.run('residents', 'complex', 'eu-central-1', 'apply', aws)
        self.assertEqual(1, report['invalid'])
        self.assertEqual(1, report['missing_attributes'])
        self.assertEqual(1, aws.call_count)

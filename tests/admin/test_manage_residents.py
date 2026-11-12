import copy
import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch

source = Path(__file__).resolve().parents[2] / 'scripts' / 'manage-residents.py'
spec = importlib.util.spec_from_file_location('manage_residents', source)
admin = importlib.util.module_from_spec(spec)
spec.loader.exec_module(admin)


class ResidentAdministrationTests(unittest.TestCase):
    def setUp(self):
        self.registration = {
            'Id': 'a' * 32, 'UserId': 99, 'BuildingId': 'building', 'Stage': 'Pending', 'Version': 4,
            'Phone': '+380671234567', 'FirstName': 'Олена', 'LastName': 'Ковальчук',
            'CreatedAt': '2026-10-20T10:00:00Z', 'UpdatedAt': '2026-10-20T10:02:00Z', 'DecisionReason': None
        }

    def transaction(self, decision, reason=None):
        return admin.decision_transaction('data', self.registration, json.dumps(self.registration), decision,
                                          reason, 'operator', '2026-10-20T11:00:00Z')['TransactItems']

    def test_approval_grants_access_with_atomic_profile_review_and_audit(self):
        before = copy.deepcopy(self.registration)
        writes = self.transaction('Approved')
        self.assertEqual(before, self.registration)
        self.assertEqual(7, len(writes))
        profile = writes[0]['Update']
        replacement = json.loads(profile['ExpressionAttributeValues'][':next']['S'])
        self.assertEqual('Approved', replacement['Stage'])
        self.assertEqual(5, replacement['Version'])
        self.assertEqual('4', profile['ExpressionAttributeValues'][':version']['N'])
        self.assertIn(':previous', profile['ConditionExpression'])
        self.assertTrue(writes[3]['Put']['Item']['active']['BOOL'])
        self.assertEqual('building', writes[3]['Put']['Item']['buildingId']['S'])
        self.assertEqual('operator', writes[4]['Put']['Item']['actor']['S'])
        self.assertEqual('BUILDING#building', writes[2]['Delete']['Key']['pk']['S'])
        resident = writes[5]['Put']['Item']
        self.assertEqual('BUILDING#building', resident['pk']['S'])
        self.assertEqual('RESIDENT#' + str(99).zfill(20), resident['sk']['S'])
        self.assertEqual(replacement, json.loads(resident['payload']['S']))

    def test_approval_enqueues_private_notification_atomically_without_ttl(self):
        writes = self.transaction('Approved')
        put = writes[6]['Put']
        item = put['Item']
        notification = json.loads(item['payload']['S'])
        self.assertEqual('NOTIFICATIONS#building', item['pk']['S'])
        self.assertEqual('DUE#2026-10-20T11:00:00.0000000+00:00#registration-' + 'a' * 32 + '-v5', item['sk']['S'])
        self.assertEqual('registration-' + 'a' * 32 + '-v5', notification['Id'])
        self.assertEqual(99, notification['OwnerId'])
        self.assertEqual('building', notification['ComplexId'])
        self.assertIsNone(notification['RequestId'])
        self.assertIn('схвалено', notification['Text'])
        self.assertIn('20.10.2026 14:00 (Київ)', notification['Text'])
        self.assertIn('/new', notification['Text'])
        self.assertNotIn(self.registration['Phone'], notification['Text'])
        self.assertNotIn(self.registration['LastName'], notification['Text'])
        self.assertNotIn('expiresAt', item)
        self.assertNotIn('requestBuildingId', item)
        self.assertEqual('0', item['attempts']['N'])
        self.assertEqual('attribute_not_exists(pk)', put['ConditionExpression'])
        self.assertEqual(notification, json.loads(self.transaction('Approved')[6]['Put']['Item']['payload']['S']))

    def test_rejection_records_reason_and_does_not_grant_access(self):
        writes = self.transaction('Rejected', 'Уточніть прізвище')
        self.assertEqual(5, len(writes))
        replacement = json.loads(writes[0]['Update']['ExpressionAttributeValues'][':next']['S'])
        self.assertEqual('Уточніть прізвище', replacement['DecisionReason'])
        self.assertFalse(writes[3]['Put']['Item']['active']['BOOL'])
        self.assertEqual('Rejected', writes[4]['Put']['Item']['decision']['S'])

    def test_projection_order_matches_backend_ukrainian_sort_key(self):
        profile = dict(self.registration, Stage='Approved', Version=5, FirstName='Іван', LastName='Ґалаґан',
                       UpdatedAt='2026-10-20T11:00:00Z')
        item = admin.resident_mirror_put('residents', profile, 4)['Put']['Item']
        self.assertEqual('0#20000420000020000F200000200004200000200011#20000B200002200000200011#00000000000000000099', item['nameOrder']['S'])
        self.assertEqual('2026-10-20T11:00:00.0000000+00:00#00000000000000000099', item['dateOrder']['S'])

    def test_operator_decisions_mirror_in_same_transaction(self):
        for decision in ('Approved', 'Rejected'):
            writes = admin.decision_transaction('data', self.registration, json.dumps(self.registration), decision,
                                                None if decision == 'Approved' else 'Уточніть', 'operator',
                                                '2026-10-20T11:00:00Z', 'residents')['TransactItems']
            mirror = next(w['Put'] for w in writes if w.get('Put', {}).get('TableName') == 'residents' and '#RECORD#' not in w['Put']['Item']['pk']['S'])
            self.assertEqual('COMPLEX#building#USER#99', mirror['Item']['pk']['S'])
            self.assertEqual('4', mirror['ExpressionAttributeValues'][':version']['N'])
            self.assertIn('attribute_not_exists(pk)', mirror['ConditionExpression'])
            self.assertEqual(decision, json.loads(mirror['Item']['payload']['S'])['Stage'])
            self.assertEqual(decision == 'Approved', 'directory' in mirror['Item'])
            if decision == 'Approved':
                self.assertEqual('COMPLEX#building#Approved', mirror['Item']['directory']['S'])
                self.assertEqual('2026-10-20T11:00:00.0000000+00:00#00000000000000000099', mirror['Item']['dateOrder']['S'])
                self.assertEqual('FamilyMember', mirror['Item']['residentType']['S'])
            related = [w['Put']['Item'] for w in writes if w.get('Put', {}).get('TableName') == 'residents' and '#RECORD#' in w['Put']['Item']['pk']['S']]
            self.assertTrue({'META', 'REGISTRATION', 'MEMBERSHIP', 'EVENT#DECIDED'}.issubset({r['sourceSk']['S'] for r in related}))
            for entry in related:
                source = entry['recordData']['M']
                self.assertEqual(source['pk'], entry['sourcePk'])
                self.assertEqual(source['sk'], entry['sourceSk'])


    def test_rejection_requires_reason(self):
        with self.assertRaises(ValueError):
            self.transaction('Rejected')

    def test_cannot_decide_unsubmitted_draft(self):
        self.registration['Stage'] = 'AwaitingLastName'
        with self.assertRaises(ValueError):
            self.transaction('Approved')

    def test_pending_list_shows_names_phone_and_reference_without_telegram_id(self):
        item = {'pk': {'S': 'COMPLEX#building#USER#99'}, 'payload': {'S': json.dumps(self.registration)}}
        output = io.StringIO()
        with patch.object(admin, 'call_aws', side_effect=[{'Items': [item]}, {'Item': item}]) as aws, patch('sys.stdout', output):
            admin.list_pending('data', 'building', 'eu-central-1')
        self.assertEqual('ResidentsByDate', aws.call_args_list[0].args[3]['IndexName'])
        self.assertTrue(aws.call_args_list[1].args[3]['ConsistentRead'])
        visible = json.loads(output.getvalue())
        self.assertEqual('Олена', visible['first_name'])
        self.assertEqual('Ковальчук', visible['last_name'])
        self.assertEqual('+380671234567', visible['phone'])
        self.assertNotIn('UserId', visible)
        self.assertNotIn('telegram_id', visible)

    def test_decision_reads_new_table_and_writes_only_residents_and_outbox(self):
        payload = json.dumps(self.registration)
        meta = dict(admin.key('REGISTRATION#' + 'a' * 32, 'META'), payload={'S': payload}, version={'N': '4'})
        profile = dict(admin.key('USER#99', 'REGISTRATION'), payload={'S': payload}, version={'N': '4'})
        def related(row):
            return {'Item': {'complexId': {'S': 'building'}, 'recordData': {'M': row}}}
        with patch.object(admin, 'call_aws', side_effect=[related(meta), related(profile), {'Arn': 'operator'}, {}]) as aws, \
             patch('sys.argv', ['manage-residents.py', '--building-id', 'building', 'approve', 'a' * 32]), \
             patch('sys.stdout', io.StringIO()):
            admin.main()
        for call in aws.call_args_list[:2]:
            self.assertEqual('osbb-requests-dev-residents', call.args[3]['TableName'])
            self.assertTrue(call.args[3]['ConsistentRead'])
        writes = aws.call_args_list[-1].args[3]['TransactItems']
        tables = {next(iter(write.values()))['TableName'] for write in writes}
        self.assertEqual({'osbb-requests-dev-residents', 'osbb-requests-dev-command-data'}, tables)
        old = [next(iter(w.values())) for w in writes if next(iter(w.values()))['TableName'].endswith('-command-data')]
        self.assertEqual(1, len(old))
        self.assertTrue(old[0]['Item']['pk']['S'].startswith('NOTIFICATIONS#'))
        self.assertTrue(any(write.get('Put', {}).get('Item', {}).get('sourceSk', {}).get('S') == 'EVENT#DECIDED' for write in writes))

    def test_decision_rejects_inconsistent_profile_before_writing(self):
        payload = json.dumps(self.registration)
        def related(value):
            return {'Item': {'complexId': {'S': 'building'}, 'recordData': {'M': {'payload': {'S': value}}}}}
        with patch.object(admin, 'call_aws', side_effect=[related(payload), related('{}')]) as aws, \
             patch('sys.argv', ['manage-residents.py', '--building-id', 'building', 'approve', 'a' * 32]):
            with self.assertRaisesRegex(ValueError, 'inconsistent'):
                admin.main()
        self.assertEqual(2, aws.call_count)

    def test_cannot_approve_another_buildings_registration(self):
        result = {'Item': {'payload': {'S': json.dumps(self.registration)}}}
        with patch.object(admin, 'call_aws', return_value=result) as aws, patch('sys.argv',
                ['manage-residents.py', '--building-id', 'another-building', 'approve', 'a' * 32]):
            with self.assertRaises(ValueError):
                admin.main()
        self.assertEqual(1, aws.call_count)
        self.assertEqual('get-item', aws.call_args.args[1])


if __name__ == '__main__':
    unittest.main()

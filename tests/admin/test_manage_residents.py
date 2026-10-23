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

    def test_rejection_requires_reason(self):
        with self.assertRaises(ValueError):
            self.transaction('Rejected')

    def test_cannot_decide_unsubmitted_draft(self):
        self.registration['Stage'] = 'AwaitingLastName'
        with self.assertRaises(ValueError):
            self.transaction('Approved')

    def test_pending_list_shows_names_phone_and_reference_without_telegram_id(self):
        item = {'payload': {'S': json.dumps(self.registration)}}
        output = io.StringIO()
        with patch.object(admin, 'call_aws', return_value={'Items': [item]}), patch('sys.stdout', output):
            admin.list_pending('data', 'building', 'eu-central-1')
        visible = json.loads(output.getvalue())
        self.assertEqual('Олена', visible['first_name'])
        self.assertEqual('Ковальчук', visible['last_name'])
        self.assertEqual('+380671234567', visible['phone'])
        self.assertNotIn('UserId', visible)
        self.assertNotIn('telegram_id', visible)

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

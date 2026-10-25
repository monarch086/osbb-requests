import importlib.util
import json
from pathlib import Path
import unittest
from unittest.mock import patch
import urllib.error

source = Path(__file__).resolve().parents[2] / 'scripts' / 'configure-telegram-menu.py'
spec = importlib.util.spec_from_file_location('configure_telegram_menu', source)
menu = importlib.util.module_from_spec(spec)
spec.loader.exec_module(menu)


class TelegramMenuTests(unittest.TestCase):
    def test_configures_and_verifies_private_menu_for_default_and_ukrainian_language(self):
        calls = []

        def call(token, method, payload):
            calls.append((method, payload))
            if method == 'getMe':
                return {'username': 'ExpectedBot'}
            if method == 'getMyCommands':
                return menu.COMMANDS
            if method == 'getChatMenuButton':
                return {'type': 'commands'}
            return True

        menu.configure('secret', '@ExpectedBot', call)
        writes = [payload for method, payload in calls if method == 'setMyCommands']
        self.assertEqual(['', 'uk'], [p['language_code'] for p in writes])
        self.assertTrue(all(p['scope'] == {'type': 'all_private_chats'} for p in writes))
        self.assertEqual({'start', 'new', 'my', 'complex', 'delete', 'request', 'register', 'cancel', 'help'},
                         {item['command'] for item in menu.COMMANDS})
        self.assertNotIn('sendMessage', [method for method, _ in calls])

    def test_registered_residents_get_chat_scope_without_start_or_categories(self):
        calls = []
        def call(token, method, payload):
            calls.append((method, payload))
            if method == 'getMe':
                return {'username': 'ExpectedBot'}
            if method == 'getMyCommands':
                return menu.RESIDENT_COMMANDS if payload['scope']['type'] == 'chat' else menu.COMMANDS
            if method == 'getChatMenuButton':
                return {'type': 'commands'}
            return True
        menu.configure('secret', 'ExpectedBot', call, registered_chat_ids=[99, 100, 99])
        writes = [payload for method, payload in calls if method == 'setMyCommands']
        self.assertEqual(6, len(writes))
        for payload in writes:
            commands = {item['command'] for item in payload['commands']}
            self.assertNotIn('categories', commands)
            self.assertEqual(payload['scope']['type'] == 'all_private_chats', 'start' in commands)
            self.assertEqual(payload['scope']['type'] == 'chat', 'edit_profile' in commands)
            self.assertEqual(payload['scope']['type'] == 'chat', 'delete_profile' in commands)
        self.assertNotIn('sendMessage', [method for method, _ in calls])

    def test_registered_directory_reads_all_pages_and_only_current_complex_approved_profiles(self):
        def item(user, complex_id='osbb-requests-dev', stage='Approved'):
            return {'payload': {'S': json.dumps({'UserId': user, 'BuildingId': complex_id, 'Stage': stage})}}
        pages = [{'Items': [item(99), item(98, stage='Pending'), item(97, 'other')],
                  'LastEvaluatedKey': {'pk': {'S': 'BUILDING#osbb-requests-dev'}, 'sk': {'S': 'RESIDENT#99'}}},
                 {'Items': [item(100), item(99), item(-1)]}]
        from types import SimpleNamespace
        with patch.object(menu.subprocess, 'run', side_effect=[SimpleNamespace(returncode=0, stdout=json.dumps(page)) for page in pages]) as run:
            self.assertEqual([99, 100], menu.load_registered_chats('dev', 'eu-central-1'))
        self.assertEqual(2, run.call_count)
        self.assertIn('--exclusive-start-key', run.call_args_list[1].args[0])
        self.assertIn('query', run.call_args_list[0].args[0])
        self.assertIn('--no-paginate', run.call_args_list[0].args[0])
        self.assertNotIn('scan', run.call_args_list[0].args[0])

    def test_deleted_residents_get_guest_chat_menu(self):
        calls = []
        def call(token, method, payload):
            calls.append((method, payload))
            if method == 'getMe':
                return {'username': 'ExpectedBot'}
            if method == 'getMyCommands':
                return menu.COMMANDS
            if method == 'getChatMenuButton':
                return {'type': 'commands'}
            return True
        menu.configure('secret', 'ExpectedBot', call, unregistered_chat_ids=[99])
        writes = [payload for method, payload in calls if method == 'setMyCommands' and payload['scope']['type'] == 'chat']
        self.assertEqual(2, len(writes))
        for payload in writes:
            commands = {item['command'] for item in payload['commands']}
            self.assertIn('start', commands)
            self.assertNotIn('edit_profile', commands)
            self.assertNotIn('delete_profile', commands)

    def test_does_not_change_a_different_bot(self):
        calls = []

        def call(token, method, payload):
            calls.append(method)
            return {'username': 'AnotherBot'}

        with self.assertRaisesRegex(RuntimeError, 'username mismatch'):
            menu.configure('secret', 'ExpectedBot', call)
        self.assertEqual(['getMe'], calls)

    def test_reports_failed_verification(self):
        def call(token, method, payload):
            if method == 'getMe':
                return {'username': 'ExpectedBot'}
            return True if method == 'setMyCommands' else []

        with self.assertRaisesRegex(RuntimeError, 'verification failed'):
            menu.configure('secret', 'ExpectedBot', call)

    def test_transport_errors_never_expose_token_bearing_url(self):
        error = urllib.error.URLError('https://api.telegram.org/botSECRET/getMe')
        with patch.object(menu.urllib.request, 'urlopen', side_effect=error):
            with self.assertRaises(RuntimeError) as result:
                menu.call_telegram('SECRET', 'getMe', {})
        self.assertNotIn('SECRET', str(result.exception))
        self.assertIsNone(result.exception.__cause__)
        self.assertTrue(result.exception.__suppress_context__)

    def test_command_descriptions_and_names_satisfy_telegram_limits(self):
        self.assertEqual(len(menu.COMMANDS), len({c['command'] for c in menu.COMMANDS}))
        for command in menu.COMMANDS:
            self.assertRegex(command['command'], r'^[a-z0-9_]{1,32}$')
            self.assertTrue(1 <= len(command['description']) <= 256)

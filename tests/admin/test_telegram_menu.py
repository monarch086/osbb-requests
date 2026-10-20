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
        self.assertEqual({'start', 'new', 'my', 'request', 'categories', 'register', 'cancel', 'help'},
                         {item['command'] for item in menu.COMMANDS})
        self.assertNotIn('sendMessage', [method for method, _ in calls])

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

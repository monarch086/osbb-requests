#!/usr/bin/env python3
"""Configure the bot's private-chat command menu; dry-run by default. Token is read from SSM in memory."""
import argparse
import json
import re
import subprocess
import urllib.error
import urllib.request

COMMANDS = [
    {'command': 'start', 'description': 'Початок роботи та перевірка доступу'},
    {'command': 'new', 'description': 'Створити заявку для будинку або комплексу'},
    {'command': 'my', 'description': 'Переглянути власні заявки'},
    {'command': 'request', 'description': 'Деталі заявки: /request номер'},
    {'command': 'categories', 'description': 'Переглянути категорії заявок'},
    {'command': 'register', 'description': 'Зареєструватися для доступу до комплексу'},
    {'command': 'cancel', 'description': 'Скасувати незавершену реєстрацію або заявку'},
    {'command': 'help', 'description': 'Довідка та перелік команд'},
]


def call_telegram(token, method, payload):
    request = urllib.request.Request('https://api.telegram.org/bot' + token + '/' + method,
                                     data=json.dumps(payload).encode(),
                                     headers={'Content-Type': 'application/json'}, method='POST')
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            body = json.load(response)
        if body.get('ok') is not True:
            raise ValueError('Telegram rejected the operation')
        return body['result']
    except (OSError, ValueError, KeyError):
        # Transport exceptions can contain the token-bearing URL. Do not expose them.
        raise RuntimeError('Telegram menu operation failed: ' + method) from None


def configure(token, expected_username, call=call_telegram):
    identity = call(token, 'getMe', {})
    if identity.get('username', '').casefold() != expected_username.lstrip('@').casefold():
        raise RuntimeError('Bot username mismatch; menu was not changed.')
    for language in ('', 'uk'):
        selection = {'scope': {'type': 'all_private_chats'}, 'language_code': language}
        if call(token, 'setMyCommands', dict(selection, commands=COMMANDS)) is not True:
            raise RuntimeError('Telegram did not confirm command menu update.')
        received = call(token, 'getMyCommands', selection)
        if not isinstance(received, list) or any(not isinstance(item, dict) for item in received):
            raise RuntimeError('Telegram command menu verification failed.')
        if [{'command': item.get('command'), 'description': item.get('description')} for item in received] != COMMANDS:
            raise RuntimeError('Telegram command menu verification failed.')
    if call(token, 'setChatMenuButton', {'menu_button': {'type': 'commands'}}) is not True:
        raise RuntimeError('Telegram did not confirm menu button update.')
    if call(token, 'getChatMenuButton', {}).get('type') != 'commands':
        raise RuntimeError('Telegram menu button verification failed.')


def load_token(stage, region):
    result = subprocess.run(['aws', 'ssm', 'get-parameter', '--name',
                             '/osbb-requests/' + stage + '/telegram-bot-token', '--with-decryption',
                             '--region', region, '--output', 'json', '--no-cli-pager'],
                            capture_output=True, text=True, check=False)
    if result.returncode:
        raise RuntimeError('Cannot read the bot token from SSM. Check AWS credentials and permissions.')
    try:
        token = json.loads(result.stdout)['Parameter']['Value']
        if not isinstance(token, str) or not token:
            raise ValueError()
        return token
    except (ValueError, KeyError, TypeError):
        raise RuntimeError('Invalid bot token parameter response.') from None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stage', default='dev')
    parser.add_argument('--region', default='eu-central-1')
    parser.add_argument('--bot-username', required=True, help='Expected bot username; checked before any changes.')
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    if not re.fullmatch(r'[a-zA-Z0-9-]+', args.stage):
        parser.error('Invalid stage.')
    if not args.apply:
        print('Would configure private-chat command menu for @' + args.bot_username.lstrip('@') + ':')
        for command in COMMANDS:
            print('/' + command['command'] + ' — ' + command['description'])
        return
    configure(load_token(args.stage, args.region), args.bot_username)
    print('Command menu and menu button configured and verified for @' + args.bot_username.lstrip('@') + '.')
    print('No messages were sent. Bot token was not displayed.')


if __name__ == '__main__':
    try:
        main()
    except RuntimeError as error:
        raise SystemExit(str(error)) from None

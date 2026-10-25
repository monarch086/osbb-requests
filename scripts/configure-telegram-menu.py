#!/usr/bin/env python3
"""Configure the bot's private-chat command menu; dry-run by default. Token is read from SSM in memory."""
import argparse
import json
import re
import subprocess
from pathlib import Path
import urllib.error
import urllib.request

COMMANDS = json.loads((Path(__file__).resolve().parents[1] / 'src' / 'OsbbRequests.Bot' /
                       'Telegram' / 'command-menu.json').read_text(encoding='utf-8'))
RESIDENT_COMMANDS = [item for item in COMMANDS if item['command'] != 'start']


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


def configure(token, expected_username, call=call_telegram, registered_chat_ids=()):
    chat_ids = sorted(set(registered_chat_ids))
    if any(not isinstance(chat, int) or isinstance(chat, bool) or chat <= 0 for chat in chat_ids):
        raise RuntimeError('Invalid registered resident chat ID.')
    identity = call(token, 'getMe', {})
    if identity.get('username', '').casefold() != expected_username.lstrip('@').casefold():
        raise RuntimeError('Bot username mismatch; menu was not changed.')
    selections = [({'type': 'all_private_chats'}, COMMANDS)]
    selections.extend(({'type': 'chat', 'chat_id': chat}, RESIDENT_COMMANDS) for chat in chat_ids)
    for scope, commands in selections:
        for language in ('', 'uk'):
            selection = {'scope': scope, 'language_code': language}
            if call(token, 'setMyCommands', dict(selection, commands=commands)) is not True:
                raise RuntimeError('Telegram did not confirm command menu update.')
            received = call(token, 'getMyCommands', selection)
            if not isinstance(received, list) or any(not isinstance(item, dict) for item in received):
                raise RuntimeError('Telegram command menu verification failed.')
            if [{'command': item.get('command'), 'description': item.get('description')} for item in received] != commands:
                raise RuntimeError('Telegram command menu verification failed.')
    if call(token, 'setChatMenuButton', {'menu_button': {'type': 'commands'}}) is not True:
        raise RuntimeError('Telegram did not confirm menu button update.')
    if call(token, 'getChatMenuButton', {}).get('type') != 'commands':
        raise RuntimeError('Telegram menu button verification failed.')


def load_registered_chats(stage, region, complex_id=None):
    partition = 'BUILDING#' + (complex_id or 'osbb-requests-' + stage)
    cursor = None
    chats = set()
    while True:
        command = ['aws', 'dynamodb', 'query', '--table-name', 'osbb-requests-' + stage + '-command-data',
                   '--key-condition-expression', 'pk = :pk AND begins_with(sk, :prefix)',
                   '--expression-attribute-values', json.dumps({':pk': {'S': partition}, ':prefix': {'S': 'RESIDENT#'}}),
                   '--projection-expression', '#p', '--expression-attribute-names', '{"#p":"payload"}',
                   '--consistent-read', '--limit', '25', '--no-paginate', '--region', region, '--output', 'json', '--no-cli-pager']
        if cursor:
            command.extend(['--exclusive-start-key', json.dumps(cursor)])
        result = subprocess.run(command, capture_output=True, text=True, check=False)
        if result.returncode:
            raise RuntimeError('Cannot read the resident directory. Check AWS credentials and permissions.')
        try:
            page = json.loads(result.stdout)
            for item in page.get('Items', []):
                resident = json.loads(item['payload']['S'])
                chat = resident.get('UserId')
                if (resident.get('BuildingId') == partition[len('BUILDING#'):] and resident.get('Stage') == 'Approved'
                        and isinstance(chat, int) and not isinstance(chat, bool) and chat > 0):
                    chats.add(chat)
            cursor = page.get('LastEvaluatedKey')
        except (ValueError, KeyError, TypeError):
            raise RuntimeError('Invalid resident directory response.') from None
        if not cursor:
            return sorted(chats)


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
    parser.add_argument('--include-residents', action='store_true', help='Also configure chat menus for existing approved residents.')
    parser.add_argument('--complex-id', help='Override the default complex ID if BUILDING_ID is customized.')
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    if not re.fullmatch(r'[a-zA-Z0-9-]+', args.stage):
        parser.error('Invalid stage.')
    if not args.apply:
        print('Would configure private-chat command menu for @' + args.bot_username.lstrip('@') + ':')
        for command in COMMANDS:
            print('/' + command['command'] + ' — ' + command['description'])
        if args.include_residents:
            print('Would also configure existing approved residents: the same menu without /start.')
        return
    chats = load_registered_chats(args.stage, args.region, args.complex_id) if args.include_residents else []
    configure(load_token(args.stage, args.region), args.bot_username, registered_chat_ids=chats)
    print('Command menu and menu button configured and verified for @' + args.bot_username.lstrip('@') + '.')
    print('No messages were sent. Bot token was not displayed.')


if __name__ == '__main__':
    try:
        main()
    except RuntimeError as error:
        raise SystemExit(str(error)) from None

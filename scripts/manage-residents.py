#!/usr/bin/env python3
"""Review resident registrations with an operator's AWS credentials; no Telegram IDs in the UI."""
import argparse
import copy
from datetime import datetime, timezone
import json
import re
import subprocess
import tempfile
import unicodedata
from zoneinfo import ZoneInfo


def call_aws(service, operation, region, payload=None):
    command = ['aws', service, operation, '--region', region, '--output', 'json', '--no-cli-pager']
    with tempfile.NamedTemporaryFile(mode='w+', prefix='osbb-admin-', suffix='.json') as request:
        if payload is not None:
            json.dump(payload, request)
            request.flush()
            command.extend(['--cli-input-json', 'file://' + request.name])
        if operation in ('query', 'scan'):
            command.append('--no-paginate')
        result = subprocess.run(command, capture_output=True, text=True, check=False)
    if result.returncode:
        # AWS error text may echo personal data from an invalid request: do not print it.
        raise RuntimeError('AWS operation failed: ' + service + '/' + operation +
                           '. Check AWS credentials, permissions and whether the registration changed.')
    return json.loads(result.stdout) if result.stdout.strip() else {}


def key(pk, sk):
    return {'pk': {'S': pk}, 'sk': {'S': sk}}


def conditional_update(table, item_key, original, replacement):
    return {'Update': {
        'TableName': table, 'Key': item_key,
        'UpdateExpression': 'SET #payload = :next, #version = :nextVersion',
        'ConditionExpression': '#payload = :previous AND #version = :version',
        'ExpressionAttributeNames': {'#payload': 'payload', '#version': 'version'},
        'ExpressionAttributeValues': {
            ':previous': {'S': original}, ':next': {'S': json.dumps(replacement, ensure_ascii=False, separators=(',', ':'))},
            ':version': {'N': str(replacement['Version'] - 1)}, ':nextVersion': {'N': str(replacement['Version'])}
        }
    }}


def resident_mirror_put(table, registration, expected_version):
    """Match the backend projection used while old and new tables are written together."""
    building = registration['BuildingId']
    user_id = registration['UserId']
    item = {
        'pk': {'S': f'COMPLEX#{building}#USER#{user_id}'},
        'complexId': {'S': building},
        'payload': {'S': json.dumps(registration, ensure_ascii=False, separators=(',', ':'))},
        'version': {'N': str(registration['Version'])},
    }
    if registration['Stage'] in ('Pending', 'Approved') and not registration.get('PersonalDataDeleted', False):
        alphabet = 'абвгґдеєжзиіїйклмнопрстуфхцчшщьюя'
        upper_alphabet = 'АБВГҐДЕЄЖЗИІЇЙКЛМНОПРСТУФХЦЧШЩЬЮЯ'
        def order(value):
            text = unicodedata.normalize('NFC', (value or '').strip())
            def weight(c):
                if c in upper_alphabet:
                    return 0x200000 + upper_alphabet.index(c)
                if c in alphabet:
                    return 0x200000 + alphabet.index(c)
                return ord(c) + 32 if 'A' <= c <= 'Z' else ord(c)
            return ''.join(f'{weight(c):06X}' for c in text)
        last = registration.get('LastName') or ''
        first = registration.get('FirstName') or ''
        name = ('0#' + order(last) if last.strip() else '1#') + '#' + order(first) + '#' + str(user_id).zfill(20)
        timestamp = registration.get('ApprovedAt') or registration['UpdatedAt'] if registration['Stage'] == 'Approved' else registration['UpdatedAt']
        instant = datetime.fromisoformat(timestamp.replace('Z', '+00:00')).astimezone(timezone.utc)
        fraction = re.search(r'\.(\d{1,7})', timestamp)
        tick = (fraction.group(1).ljust(7, '0')[6] if fraction else '0')
        date = instant.strftime('%Y-%m-%dT%H:%M:%S.') + f'{instant.microsecond:06d}{tick}+00:00#{user_id:020d}'
        item.update(directory={'S': f'COMPLEX#{building}#{registration["Stage"]}'}, dateOrder={'S': date},
                    nameOrder={'S': name}, buildingNumber={'S': registration.get('BuildingNumber') or ''},
                    residentType={'S': registration.get('ResidentType') or 'FamilyMember'})
    return {'Put': {'TableName': table, 'Item': item,
                    'ConditionExpression': 'attribute_not_exists(pk) OR (#version = :version AND complexId = :complex)',
                    'ExpressionAttributeNames': {'#version': 'version'},
                    'ExpressionAttributeValues': {':version': {'N': str(expected_version)}, ':complex': {'S': building}}}}


def related_resident_put(table, source, building, expected_version=None, immutable=False):
    source_pk, source_sk = source['pk']['S'], source['sk']['S']
    result = {
        'pk': {'S': f'COMPLEX#{building}#RECORD#{source_pk}#{source_sk}'},
        'recordGroup': {'S': f'COMPLEX#{building}#{source_pk}'}, 'recordOrder': {'S': source_sk},
        'sourcePk': {'S': source_pk}, 'sourceSk': {'S': source_sk}, 'complexId': {'S': building},
        'recordData': {'M': copy.deepcopy(source)},
    }
    if 'version' in source:
        result['version'] = copy.deepcopy(source['version'])
    put = {'TableName': table, 'Item': result}
    if immutable:
        put['ConditionExpression'] = 'attribute_not_exists(pk)'
    elif expected_version is not None:
        put.update(ConditionExpression='attribute_not_exists(pk) OR (#v = :expected AND complexId = :complex)',
                   ExpressionAttributeNames={'#v': 'version'},
                   ExpressionAttributeValues={':expected': {'N': str(expected_version)}, ':complex': {'S': building}})
    else:
        put.update(ConditionExpression='attribute_not_exists(pk) OR complexId = :complex',
                   ExpressionAttributeValues={':complex': {'S': building}})
    return {'Put': put}


def related_resident_delete(table, source, building, expected_version):
    source_pk, source_sk = source['pk']['S'], source['sk']['S']
    return {'Delete': {'TableName': table,
                       'Key': {'pk': {'S': f'COMPLEX#{building}#RECORD#{source_pk}#{source_sk}'}},
                       'ConditionExpression': 'attribute_not_exists(pk) OR (#v = :expected AND complexId = :complex)',
                       'ExpressionAttributeNames': {'#v': 'version'},
                       'ExpressionAttributeValues': {':expected': {'N': str(expected_version)}, ':complex': {'S': building}}}}


def decision_transaction(table, registration, original_payload, decision, reason, actor, now, residents_table=None, user_snapshot=None, meta_snapshot=None):
    if decision not in ('Approved', 'Rejected'):
        raise ValueError('Unsupported decision.')
    if registration['Stage'] != 'Pending':
        raise ValueError('Registration is not pending.')
    if decision == 'Rejected' and not reason:
        raise ValueError('A rejection reason is required.')
    updated = copy.deepcopy(registration)
    updated.update(Stage=decision, Version=registration['Version'] + 1, UpdatedAt=now, DecisionReason=reason)
    user_key = 'USER#' + str(registration['UserId'])
    registration_key = 'REGISTRATION#' + registration['Id']
    membership = key(user_key, 'MEMBERSHIP')
    membership.update(buildingId={'S': registration['BuildingId']}, active={'BOOL': decision == 'Approved'},
                      registrationId={'S': registration['Id']}, reviewedBy={'S': actor}, reviewedAt={'S': now})
    audit = key(registration_key, 'EVENT#DECIDED')
    audit.update(decision={'S': decision}, actor={'S': actor}, createdAt={'S': now}, reason={'S': reason or ''})
    writes = [
        conditional_update(table, key(user_key, 'REGISTRATION'), original_payload, updated),
        conditional_update(table, key(registration_key, 'META'), original_payload, updated),
        {'Delete': {
            'TableName': table, 'Key': key('BUILDING#' + registration['BuildingId'], registration_key),
            'ConditionExpression': '#version = :version',
            'ExpressionAttributeNames': {'#version': 'version'},
            'ExpressionAttributeValues': {':version': {'N': str(registration['Version'])}}
        }},
        {'Put': {
            'TableName': table, 'Item': membership,
            'ConditionExpression': 'attribute_not_exists(pk) OR buildingId = :building',
            'ExpressionAttributeValues': {':building': {'S': registration['BuildingId']}}
        }},
        {'Put': {'TableName': table, 'Item': audit, 'ConditionExpression': 'attribute_not_exists(pk)'}}
    ]
    if decision == 'Approved':
        resident = key('BUILDING#' + registration['BuildingId'], 'RESIDENT#' + str(registration['UserId']).zfill(20))
        resident.update(payload={'S': json.dumps(updated, ensure_ascii=False, separators=(',', ':'))},
                        version={'N': str(updated['Version'])})
        writes.append({'Put': {'TableName': table, 'Item': resident}})
        timestamp = datetime.fromisoformat(now.replace('Z', '+00:00')).astimezone(timezone.utc)
        due_time = timestamp.strftime('%Y-%m-%dT%H:%M:%S.') + f'{timestamp.microsecond:06d}0+00:00'
        local_date = timestamp.astimezone(ZoneInfo('Europe/Kyiv')).strftime('%d.%m.%Y %H:%M')
        notification_id = 'registration-' + registration['Id'] + '-v' + str(updated['Version'])
        notification = {
            'Id': notification_id, 'ComplexId': registration['BuildingId'], 'OwnerId': registration['UserId'],
            'RequestId': None, 'RequestVersion': None, 'CreatedAt': now,
            'Text': 'Вашу реєстрацію в ЖК «Зелений острів 2» схвалено.\n'
                    'Вам надано доступ до бота комплексу.\n'
                    f'Підтверджено: {local_date} (Київ)\n\n'
                    'Створити заявку: /new\nВаші заявки: /my\nЗаявки комплексу: /complex'
        }
        pending = key('NOTIFICATIONS#' + registration['BuildingId'], 'DUE#' + due_time + '#' + notification_id)
        pending.update(payload={'S': json.dumps(notification, ensure_ascii=False, separators=(',', ':'))}, attempts={'N': '0'})
        writes.append({'Put': {'TableName': table, 'Item': pending, 'ConditionExpression': 'attribute_not_exists(pk)'}})
    if residents_table:
        meta = copy.deepcopy(meta_snapshot or key(registration_key, 'META'))
        meta.update(payload={'S': json.dumps(updated, ensure_ascii=False, separators=(',', ':'))},
                    version={'N': str(updated['Version'])})
        user_profile = copy.deepcopy(user_snapshot or key(user_key, 'REGISTRATION'))
        user_profile.update(payload=meta['payload'], version=meta['version'])
        pending_key = key('BUILDING#' + registration['BuildingId'], registration_key)
        writes.append(resident_mirror_put(residents_table, updated, registration['Version']))
        writes.append(related_resident_put(residents_table, meta, registration['BuildingId'], registration['Version']))
        writes.append(related_resident_put(residents_table, user_profile, registration['BuildingId'], registration['Version']))
        writes.append(related_resident_delete(residents_table, pending_key, registration['BuildingId'], registration['Version']))
        writes.append(related_resident_put(residents_table, membership, registration['BuildingId']))
        writes.append(related_resident_put(residents_table, audit, registration['BuildingId'], immutable=True))
        if decision == 'Approved':
            writes.append(related_resident_put(residents_table, resident, registration['BuildingId']))
    return {'TransactItems': writes}


def related_key(building, pk, sk):
    return {'pk': {'S': f'COMPLEX#{building}#RECORD#{pk}#{sk}'}}


def get_related(table, building, pk, sk, region):
    result = call_aws('dynamodb', 'get-item', region, {
        'TableName': table, 'Key': related_key(building, pk, sk), 'ConsistentRead': True
    })
    item = result.get('Item')
    if not item:
        return None
    if item.get('complexId', {}).get('S') != building:
        raise ValueError('Invalid resident record complex.')
    return item['recordData']['M']


def list_pending(table, building, region):
    cursor = None
    while True:
        query = {
            'TableName': table, 'IndexName': 'ResidentsByDate',
            'KeyConditionExpression': 'directory = :directory',
            'ExpressionAttributeValues': {':directory': {'S': 'COMPLEX#' + building + '#Pending'}},
            'Limit': 50
        }
        if cursor:
            query['ExclusiveStartKey'] = cursor
        result = call_aws('dynamodb', 'query', region, query)
        for candidate in result.get('Items', []):
            current = call_aws('dynamodb', 'get-item', region, {
                'TableName': table, 'Key': {'pk': candidate['pk']}, 'ConsistentRead': True
            }).get('Item')
            if not current:
                continue
            registration = json.loads(current['payload']['S'])
            if (registration['BuildingId'] != building or registration['Stage'] != 'Pending' or
                    current['pk']['S'] != f"COMPLEX#{building}#USER#{registration['UserId']}"):
                continue
            print(json.dumps({
                'reference': registration['Id'], 'first_name': registration['FirstName'],
                'last_name': registration['LastName'], 'phone': registration['Phone'],
                'submitted_at': registration['UpdatedAt']
            }, ensure_ascii=False))
        cursor = result.get('LastEvaluatedKey')
        if not cursor:
            return


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stage', default='dev')
    parser.add_argument('--region', default='eu-central-1')
    parser.add_argument('--building-id', help='Defaults to osbb-requests-<stage>.')
    subcommands = parser.add_subparsers(dest='action', required=True)
    subcommands.add_parser('list', help='Print pending names, phones and registration references (private data).')
    for action in ['approve', 'reject']:
        command = subcommands.add_parser(action)
        command.add_argument('reference', help='Registration reference from list; not a Telegram ID.')
        command.add_argument('--reason', required=action == 'reject')
    args = parser.parse_args()
    if not re.fullmatch(r'[a-zA-Z0-9-]+', args.stage):
        parser.error('Invalid stage.')
    reason = getattr(args, 'reason', None)
    if reason is not None:
        reason = reason.strip()
        if not 1 <= len(reason) <= 500:
            parser.error('Reason must contain 1 to 500 characters.')
    table = 'osbb-requests-' + args.stage + '-command-data'
    building = args.building_id or 'osbb-requests-' + args.stage
    residents_table = 'osbb-requests-' + args.stage + '-residents'
    if args.action == 'list':
        list_pending(residents_table, building, args.region)
        return
    if not re.fullmatch(r'[a-f0-9]{32}', args.reference):
        parser.error('Invalid registration reference.')
    meta_snapshot = get_related(residents_table, building, 'REGISTRATION#' + args.reference, 'META', args.region)
    if not meta_snapshot:
        raise ValueError('Registration not found.')
    original_payload = meta_snapshot['payload']['S']
    registration = json.loads(original_payload)
    if registration['BuildingId'] != building or registration['Id'] != args.reference:
        raise ValueError('Registration belongs to another building or has an invalid reference.')
    decision = 'Approved' if args.action == 'approve' else 'Rejected'
    if registration['Stage'] == decision:
        print('This decision has already been recorded.')
        return
    user_snapshot = get_related(residents_table, building, 'USER#' + str(registration['UserId']), 'REGISTRATION', args.region)
    if not user_snapshot or user_snapshot.get('payload', {}).get('S') != original_payload:
        raise ValueError('Resident profile is missing or inconsistent.')
    actor = call_aws('sts', 'get-caller-identity', args.region)['Arn']
    transaction = decision_transaction(table, registration, original_payload, decision, reason, actor,
                                       datetime.now(timezone.utc).isoformat(), residents_table,
                                       user_snapshot, meta_snapshot)
    call_aws('dynamodb', 'transact-write-items', args.region, transaction)
    print('Access approved.' if decision == 'Approved' else 'Registration rejected.')
    print('Telegram approval notification queued for delivery.' if decision == 'Approved' else
          'The resident can check the decision using /start.')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, RuntimeError) as error:
        raise SystemExit(str(error)) from None

#!/usr/bin/env python3
"""Review resident registrations with an operator's AWS credentials; no Telegram IDs in the UI."""
import argparse
from base64 import b64decode
import copy
from datetime import datetime, timezone
from pathlib import Path
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


def phone_association_put(table, registration):
    phone = registration.get('Phone')
    if not phone:
        return None
    phone = re.sub(r'[ ()-]', '', phone.strip())
    if phone.startswith('0'):
        phone = '+38' + phone
    elif not phone.startswith('+'):
        phone = '+' + phone
    if not re.fullmatch(r'\+[1-9][0-9]{7,14}', phone):
        return None
    building, user = registration['BuildingId'], registration['UserId']
    item = {'pk': {'S': f'COMPLEX#{building}#PHONE_ENTRY#{user}#{phone}'},
            'phoneLookup': {'S': f'COMPLEX#{building}#PHONE#{phone}'},
            'complexId': {'S': building}, 'residentId': {'N': str(user)}}
    return {'Put': {'TableName': table, 'Item': item,
                    'ConditionExpression': 'attribute_not_exists(pk) OR (complexId = :complex AND residentId = :resident)',
                    'ExpressionAttributeValues': {':complex': {'S': building}, ':resident': {'N': str(user)}}}}


def resident_name_keys(registrations):
    names = [{key: profile.get(key) for key in ('Id', 'FirstName', 'LastName')} for profile in registrations]
    project = Path(__file__).resolve().parents[1] / 'src' / 'OsbbRequests.ResidentNameKeys' / 'OsbbRequests.ResidentNameKeys.csproj'
    # Names stay on stdin. Diagnostics may contain profile data and are suppressed.
    result = subprocess.run(['dotnet', 'run', '--project', str(project), '--verbosity', 'quiet', '-p:RestoreLockedMode=true'],
                            input=json.dumps(names), text=True, capture_output=True, check=False)
    if result.returncode:
        raise RuntimeError('Failed to generate compact resident name keys.')
    keys = json.loads(result.stdout)
    if len(keys) != len(names):
        raise RuntimeError('Incomplete resident name keys.')
    return [{'listNameOrder': {'B': pair['Ascending']},
             'listNameOrderDescending': {'B': pair['Descending']}}
            if len(b64decode(pair['Ascending'])) <= 1024 else {} for pair in keys]


def directory_approval_writes(table, registration, name_keys=None):
    if registration.get('PersonalDataDeleted', False):
        return []
    prefix = f'COMPLEX#{registration["BuildingId"]}'
    building = registration.get('BuildingNumber') or 'Unspecified'
    resident_type = registration.get('ResidentType') or 'FamilyMember'
    if isinstance(resident_type, int):
        resident_type = ('FamilyMember', 'Owner', 'Tenant', 'Employee')[resident_type]
    timestamp = registration.get('ApprovedAt') or registration['UpdatedAt']
    instant = datetime.fromisoformat(timestamp.replace('Z', '+00:00')).astimezone(timezone.utc)
    elapsed = instant - datetime.min.replace(tzinfo=timezone.utc)
    fraction = re.search(r'\.(\d{1,7})', timestamp)
    last_tick = int(fraction.group(1).ljust(7, '0')[6]) if fraction else 0
    ticks = (elapsed.days * 86400 + elapsed.seconds) * 10_000_000 + elapsed.microseconds * 10 + last_tick
    reference = registration['Id']
    writes = []
    for kind, group in (('ALL', 'ALL'), ('BUILDING', 'BUILDING#' + building)):
        row = {
            'pk': {'S': f'{prefix}#DIRECTORY_ROW#{kind}#{registration["UserId"]}'},
            'listGroup': {'S': f'{prefix}#APPROVED#{group}'},
            'listOrder': {'S': f'{ticks:019d}#{reference}'},
            'listOrderDescending': {'S': f'{3155378975999999999 - ticks:019d}#{reference}'},
            'registrationId': {'S': reference}, 'residentType': {'S': resident_type},
            'buildingNumber': {'S': building}, 'sourceVersion': {'N': str(registration['Version'])},
        }
        for source, target in (('FirstName', 'firstName'), ('LastName', 'lastName'), ('ApartmentNumber', 'apartmentNumber')):
            if registration.get(source) is not None:
                row[target] = {'S': registration[source]}
        row.update(name_keys or {})
        writes.append({'Put': {'TableName': table, 'Item': row, 'ConditionExpression': 'attribute_not_exists(pk)'}})
        update = {'TableName': table, 'Key': {'pk': {'S': f'{prefix}#DIRECTORY_COUNT#{group}'}},
                  'UpdateExpression': 'ADD #type :one', 'ExpressionAttributeNames': {'#type': resident_type},
                  'ExpressionAttributeValues': {':one': {'N': '1'}}}
        if name_keys == {}:
            update['UpdateExpression'] = 'SET #blocked = :true ' + update['UpdateExpression']
            update['ExpressionAttributeNames']['#blocked'] = 'nameOrderUnsupported'
            update['ExpressionAttributeValues'][':true'] = {'BOOL': True}
        writes.append({'Update': update})
    return writes


def decision_transaction(table, registration, original_payload, decision, reason, actor, now, residents_table=None, user_snapshot=None, meta_snapshot=None, residents_only=False, name_keys=None):
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
            writes.extend(directory_approval_writes(residents_table, updated, name_keys))
            writes.append(related_resident_put(residents_table, resident, registration['BuildingId']))
            phone_put = phone_association_put(residents_table, updated)
            if phone_put:
                writes.append(phone_put)
    if residents_only:
        if not residents_table:
            raise ValueError('Residents table is required.')
        writes = [w for w in writes if next(iter(w.values()))['TableName'] == residents_table or
                  next(iter(w.values())).get('Item', {}).get('pk', {}).get('S', '').startswith('NOTIFICATIONS#')]
        for write in writes:
            action = next(iter(write.values()))
            if action['TableName'] == residents_table and ':expected' in action.get('ExpressionAttributeValues', {}):
                action['ConditionExpression'] = action['ConditionExpression'].replace('attribute_not_exists(pk) OR ', '')
            if action['TableName'] == residents_table and ':version' in action.get('ExpressionAttributeValues', {}):
                action['ConditionExpression'] = action['ConditionExpression'].replace('attribute_not_exists(pk) OR ', '')
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
    name_keys = resident_name_keys([registration])[0] if decision == "Approved" else None
    transaction = decision_transaction(table, registration, original_payload, decision, reason, actor,
                                       datetime.now(timezone.utc).isoformat(), residents_table,
                                       user_snapshot, meta_snapshot, residents_only=True, name_keys=name_keys)
    call_aws('dynamodb', 'transact-write-items', args.region, transaction)
    print('Access approved.' if decision == 'Approved' else 'Registration rejected.')
    print('Telegram approval notification queued for delivery.' if decision == 'Approved' else
          'The resident can check the decision using /start.')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, RuntimeError) as error:
        raise SystemExit(str(error)) from None

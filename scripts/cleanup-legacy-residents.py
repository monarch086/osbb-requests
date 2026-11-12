#!/usr/bin/env python3
"""Safely remove verified legacy resident records; dry-run unless explicitly enabled."""
import argparse
import importlib.util
import json
from pathlib import Path
import re

spec = importlib.util.spec_from_file_location('manage_residents', Path(__file__).with_name('manage-residents.py'))
manage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manage)


def candidate_kind(item, complex_id):
    pk, sk = item['pk']['S'], item['sk']['S']
    if re.fullmatch(r'REGISTRATION#[a-f0-9]{32}', pk):
        if sk == 'META':
            return 'metadata'
        if sk == 'EVENT#DECIDED' or sk.startswith('EVENT#TYPE#'):
            return 'history'
    if re.fullmatch(r'USER#[1-9][0-9]*', pk):
        return {'REGISTRATION': 'profiles', 'MEMBERSHIP': 'membership', 'PROFILE_DIALOG': 'dialogs'}.get(sk)
    if pk == 'BUILDING#' + complex_id:
        if re.fullmatch(r'REGISTRATION#[a-f0-9]{32}', sk):
            return 'pending'
        if re.fullmatch(r'RESIDENT#[0-9]{20}', sk):
            return 'directory'
    return None


def identity(item, kind, complex_id):
    pk, sk = item['pk']['S'], item['sk']['S']
    if kind == 'membership':
        return (pk, sk) if item.get('buildingId', {}).get('S') == complex_id else None
    if kind == 'history':
        return pk, sk
    payload = json.loads(item['payload']['S'])
    user = payload.get('UserId')
    if not isinstance(user, int) or isinstance(user, bool) or user <= 0:
        return None
    if kind == 'dialogs':
        return (pk, sk, user) if payload.get('ComplexId') == complex_id and pk == 'USER#' + str(user) else None
    reference = payload.get('Id')
    if payload.get('BuildingId') != complex_id or not isinstance(reference, str) or not re.fullmatch(r'[a-f0-9]{32}', reference):
        return None
    expected = {'metadata': ('REGISTRATION#' + reference, 'META'),
                'profiles': ('USER#' + str(user), 'REGISTRATION'),
                'pending': ('BUILDING#' + complex_id, 'REGISTRATION#' + reference),
                'directory': ('BUILDING#' + complex_id, 'RESIDENT#' + str(user).zfill(20))}.get(kind)
    return (pk, sk, user, reference) if (pk, sk) == expected else None


def verify(source, target, complex_id, kind):
    pk, sk = source['pk']['S'], source['sk']['S']
    expected_pk = f'COMPLEX#{complex_id}#RECORD#{pk}#{sk}'
    if (target.get('pk', {}).get('S') != expected_pk or target.get('complexId', {}).get('S') != complex_id or
            target.get('sourcePk', {}).get('S') != pk or target.get('sourceSk', {}).get('S') != sk or
            target.get('recordGroup', {}).get('S') != f'COMPLEX#{complex_id}#{pk}' or
            target.get('recordOrder', {}).get('S') != sk):
        return False
    current = target.get('recordData', {}).get('M')
    if not current:
        return False
    old_id = identity(source, kind, complex_id)
    if old_id is None or old_id != identity(current, kind, complex_id):
        return False
    if source == current:
        return True
    if kind not in ('metadata', 'profiles', 'pending', 'directory', 'dialogs'):
        return False
    if any(current.get(k) != value for k, value in source.items() if k not in ('payload', 'version')):
        return False
    old_version = int(source['version']['N'])
    new_version = int(current['version']['N'])
    old_payload = json.loads(source['payload']['S'])
    new_payload = json.loads(current['payload']['S'])
    return (old_version > 0 and new_version > old_version and
            old_payload.get('Version') == old_version and new_payload.get('Version') == new_version and
            target.get('version', {}).get('N') == current['version']['N'])


def guard_attributes(item, prefix):
    names, values, conditions = {}, {}, []
    for index, (name, value) in enumerate(sorted(item.items())):
        alias, parameter = f'#{prefix}{index}', f':{prefix}{index}'
        names[alias], values[parameter] = name, value
        conditions.append(alias + ' = ' + parameter)
    return names, values, ' AND '.join(conditions)


def delete_transaction(source_table, target_table, source, target):
    names, values, condition = guard_attributes(source, 's')
    target_names, target_values, target_condition = guard_attributes(target, 't')
    return {'TransactItems': [
        {'Delete': {'TableName': source_table, 'Key': {'pk': source['pk'], 'sk': source['sk']},
                    'ConditionExpression': condition, 'ExpressionAttributeNames': names,
                    'ExpressionAttributeValues': values}},
        {'ConditionCheck': {'TableName': target_table, 'Key': {'pk': target['pk']},
                            'ConditionExpression': target_condition, 'ExpressionAttributeNames': target_names,
                            'ExpressionAttributeValues': target_values}}
    ]}


def ensure_no_legacy_broadcasts(table, complex_id, region, aws=manage.call_aws):
    cursor = None
    while True:
        query = {'TableName': table, 'ConsistentRead': True,
                 'KeyConditionExpression': 'pk = :pk AND begins_with(sk, :due)',
                 'ExpressionAttributeValues': {':pk': {'S': 'NOTIFICATIONS#' + complex_id}, ':due': {'S': 'DUE#'}}}
        if cursor:
            query['ExclusiveStartKey'] = cursor
        page = aws('dynamodb', 'query', region, query)
        for item in page.get('Items', []):
            payload = json.loads(item['payload']['S'])
            if not isinstance(payload, dict) or payload.get('ComplexId') != complex_id:
                raise RuntimeError('Invalid pending notification; cleanup blocked.')
            if payload.get('IsBroadcast') and payload.get('AdministratorEvent') is None:
                raw = item.get('audienceCursor')
                if raw is not None:
                    value = json.loads(raw['S'])
                    if not isinstance(value, dict) or 'sk' in value:
                        raise RuntimeError('Legacy or invalid broadcast cursor; cleanup blocked.')
        cursor = page.get('LastEvaluatedKey')
        if not cursor:
            return


def cleanup_pass(source_table, target_table, complex_id, region, apply=False, max_deletes=None, aws=manage.call_aws):
    counts = {'examined': 0, 'eligible': 0, 'deleted': 0, 'missing': 0, 'unsafe': 0, 'limited': 0}
    cursor = None
    while True:
        query = {'TableName': source_table, 'ConsistentRead': True, 'Limit': 500}
        if cursor:
            query['ExclusiveStartKey'] = cursor
        page = aws('dynamodb', 'scan', region, query)
        for source in page.get('Items', []):
            kind = candidate_kind(source, complex_id)
            if kind is None:
                continue
            counts['examined'] += 1
            pk, sk = source['pk']['S'], source['sk']['S']
            target_key = {'pk': {'S': f'COMPLEX#{complex_id}#RECORD#{pk}#{sk}'}}
            target = aws('dynamodb', 'get-item', region, {'TableName': target_table, 'Key': target_key, 'ConsistentRead': True}).get('Item')
            if not target:
                counts['missing'] += 1
                continue
            try:
                valid = verify(source, target, complex_id, kind)
            except (KeyError, ValueError, TypeError, json.JSONDecodeError):
                valid = False
            if not valid:
                counts['unsafe'] += 1
                continue
            counts['eligible'] += 1
            if apply:
                if counts['deleted'] >= max_deletes:
                    counts['limited'] += 1
                    continue
                aws('dynamodb', 'transact-write-items', region, delete_transaction(source_table, target_table, source, target))
                counts['deleted'] += 1
        cursor = page.get('LastEvaluatedKey')
        if not cursor:
            return counts


def cleanup(source_table, target_table, complex_id, region, apply=False, max_deletes=None, aws=manage.call_aws):
    if apply and (not isinstance(max_deletes, int) or isinstance(max_deletes, bool) or max_deletes < 1):
        raise ValueError('A positive deletion limit is required.')
    ensure_no_legacy_broadcasts(source_table, complex_id, region, aws)
    preflight = cleanup_pass(source_table, target_table, complex_id, region, aws=aws)
    if not apply or preflight['missing'] or preflight['unsafe']:
        return preflight
    ensure_no_legacy_broadcasts(source_table, complex_id, region, aws)
    return cleanup_pass(source_table, target_table, complex_id, region, True, max_deletes, aws)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stage', required=True)
    parser.add_argument('--region', required=True)
    parser.add_argument('--complex-id')
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--confirm-stage', help='Repeat the stage name to enable deletion.')
    parser.add_argument('--confirm-cutover', action='store_true', help='Confirm all legacy readers are disabled and backup is verified.')
    parser.add_argument('--max-deletes', type=int, help='Required positive limit for each apply run.')
    args = parser.parse_args()
    if not re.fullmatch(r'[a-zA-Z0-9-]+', args.stage):
        parser.error('Invalid stage.')
    if args.apply and (args.confirm_stage != args.stage or not args.confirm_cutover or not args.max_deletes or args.max_deletes < 1):
        parser.error('--apply requires --confirm-stage matching --stage, --confirm-cutover and positive --max-deletes.')
    prefix = 'osbb-requests-' + args.stage
    result = cleanup(prefix + '-command-data', prefix + '-residents', args.complex_id or prefix,
                     args.region, args.apply, args.max_deletes)
    print(json.dumps({'mode': 'apply' if args.apply else 'dry-run', **result}))
    if result['missing'] or result['unsafe']:
        raise SystemExit('Some records were skipped; investigate before deleting them.')


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, ValueError, KeyError, TypeError, json.JSONDecodeError):
        raise SystemExit('Cleanup stopped; check AWS permissions, pending broadcasts and record consistency.') from None

#!/usr/bin/env python3
"""Copy resident metadata, decisions, history, membership and profile dialogs; dry-run by default."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import importlib.util
import json
from pathlib import Path
import re

spec = importlib.util.spec_from_file_location('manage_residents', Path(__file__).with_name('manage-residents.py'))
manage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manage)


def scan(aws, table, region, expression, values):
    cursor = None
    while True:
        request = {'TableName': table, 'ConsistentRead': True, 'Limit': 1000,
                   'FilterExpression': expression, 'ExpressionAttributeValues': values}
        if cursor:
            request['ExclusiveStartKey'] = cursor
        result = aws('dynamodb', 'scan', region, request)
        yield from result.get('Items', [])
        cursor = result.get('LastEvaluatedKey')
        if not cursor:
            break


def source_guard(table, item):
    names, values, conditions = {}, {}, ['attribute_exists(pk)', 'attribute_exists(sk)']
    for index, (name, value) in enumerate(item.items()):
        if name in ('pk', 'sk'):
            continue
        alias, parameter = '#a' + str(index), ':v' + str(index)
        names[alias], values[parameter] = name, value
        conditions.append(alias + ' = ' + parameter)
    guard = {'TableName': table, 'Key': {'pk': item['pk'], 'sk': item['sk']},
             'ConditionExpression': ' AND '.join(conditions)}
    if names:
        guard['ExpressionAttributeNames'] = names
        guard['ExpressionAttributeValues'] = values
    return {'ConditionCheck': guard}


def migrate(source, target, building, region, apply=False, aws=manage.call_aws):
    result = {'examined': 0, 'current': 0, 'missing': 0, 'written': 0, 'conflicts': 0, 'unresolved': 0,
              'metadata': 0, 'decisions': 0, 'history': 0, 'membership': 0, 'dialogs': 0, 'other_events': 0,
              'current_profiles': 0, 'pending_entries': 0, 'resident_entries': 0}
    references = {}
    meta_expr = 'begins_with(pk, :reg) AND sk = :meta'
    meta_values = {':reg': {'S': 'REGISTRATION#'}, ':meta': {'S': 'META'}}
    for item in scan(aws, source, region, meta_expr, meta_values):
        pk = item['pk']['S']
        profile = json.loads(item['payload']['S'])
        if pk != 'REGISTRATION#' + profile['Id']:
            raise RuntimeError('Registration identity mismatch.')
        references[pk] = profile['BuildingId']

    def process(item):
        local = dict.fromkeys(result, 0)
        source_key = {'pk': item['pk'], 'sk': item['sk']}
        pk, sk = item['pk']['S'], item['sk']['S']
        for attempt in range(3):
            if pk.startswith('REGISTRATION#'):
                if sk != 'META' and not sk.startswith('EVENT#'):
                    local['unresolved'] += 1
                    return local
                owner = references.get(pk)
                if owner is None:
                    meta = aws('dynamodb', 'get-item', region, {'TableName': source,
                               'Key': {'pk': {'S': pk}, 'sk': {'S': 'META'}}, 'ConsistentRead': True}).get('Item')
                    if not meta:
                        local['unresolved'] += 1
                        return local
                    profile = json.loads(meta['payload']['S'])
                    if pk != 'REGISTRATION#' + profile['Id']:
                        raise RuntimeError('Registration identity mismatch.')
                    owner = profile['BuildingId']
                if owner != building:
                    return local
                kind = ('metadata' if sk == 'META' else 'decisions' if sk == 'EVENT#DECIDED'
                        else 'history' if sk.startswith('EVENT#TYPE#') else 'other_events')
            elif pk.startswith('USER#') and sk in ('MEMBERSHIP', 'PROFILE_DIALOG', 'REGISTRATION'):
                if sk == 'MEMBERSHIP':
                    owner, kind = item.get('buildingId', {}).get('S'), 'membership'
                else:
                    profile = json.loads(item['payload']['S'])
                    owner = profile.get('ComplexId') if sk == 'PROFILE_DIALOG' else profile.get('BuildingId')
                    kind = 'dialogs' if sk == 'PROFILE_DIALOG' else 'current_profiles'
                    if pk != 'USER#' + str(profile.get('UserId')):
                        raise RuntimeError('Resident identity mismatch.')
                if not owner:
                    local['unresolved'] += 1
                    return local
                if owner != building:
                    return local
            elif pk == 'BUILDING#' + building and (sk.startswith('REGISTRATION#') or sk.startswith('RESIDENT#')):
                profile = json.loads(item['payload']['S'])
                if profile.get('BuildingId') != building:
                    raise RuntimeError('Directory complex mismatch.')
                if sk.startswith('REGISTRATION#'):
                    kind = 'pending_entries'
                    if sk != 'REGISTRATION#' + profile['Id']:
                        raise RuntimeError('Directory registration mismatch.')
                else:
                    kind = 'resident_entries'
                    if sk != 'RESIDENT#' + str(profile['UserId']).zfill(20):
                        raise RuntimeError('Directory resident mismatch.')
            else:
                return local
            local['examined'] = 1
            local[kind] = 1
            put = manage.related_resident_put(target, item, building, immutable=True)['Put']
            key = {'pk': put['Item']['pk']}
            existing = aws('dynamodb', 'get-item', region,
                           {'TableName': target, 'Key': key, 'ConsistentRead': True}).get('Item')
            if existing:
                if existing == put['Item']:
                    local['current'] = 1
                    return local
                if attempt < 2:
                    item = aws('dynamodb', 'get-item', region,
                               {'TableName': source, 'Key': source_key, 'ConsistentRead': True}).get('Item')
                    if item:
                        continue
                local['conflicts'] = 1
                return local
            local['missing'] = 1
            if not apply:
                return local
            transaction = {'TransactItems': [source_guard(source, item), {'Put': put}]}
            try:
                aws('dynamodb', 'transact-write-items', region, transaction)
                local['written'] = 1
                return local
            except RuntimeError:
                local['missing'] = 0
                if attempt == 2:
                    raise
                item = aws('dynamodb', 'get-item', region,
                           {'TableName': source, 'Key': source_key, 'ConsistentRead': True}).get('Item')
                if not item:
                    local['missing'] = 0
                    local['conflicts'] = 1
                    return local
        raise RuntimeError('Could not reconcile record.')

    expression = ('begins_with(pk, :reg) OR (begins_with(pk, :user) AND (sk = :membership OR sk = :dialog OR sk = :profile)) '
                  'OR (pk = :building AND (begins_with(sk, :reg) OR begins_with(sk, :resident)))')
    values = {':reg': {'S': 'REGISTRATION#'}, ':user': {'S': 'USER#'}, ':profile': {'S': 'REGISTRATION'},
              ':membership': {'S': 'MEMBERSHIP'}, ':dialog': {'S': 'PROFILE_DIALOG'},
              ':building': {'S': 'BUILDING#' + building}, ':resident': {'S': 'RESIDENT#'}}
    with ThreadPoolExecutor(max_workers=8) as executor:
        for local in executor.map(process, scan(aws, source, region, expression, values)):
            for key, value in local.items():
                result[key] += value
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stage', required=True)
    parser.add_argument('--region', required=True)
    parser.add_argument('--complex-id')
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    if not re.fullmatch(r'[a-zA-Z0-9-]+', args.stage):
        parser.error('Invalid stage.')
    prefix = 'osbb-requests-' + args.stage
    result = migrate(prefix + '-command-data', prefix + '-residents', args.complex_id or prefix, args.region, args.apply)
    print(json.dumps({'mode': 'apply' if args.apply else 'dry-run', **result}))
    if result['conflicts'] or result['unresolved']:
        raise SystemExit('Conflicts or records with unknown ownership; investigate before switching reads.')


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, ValueError, KeyError, json.JSONDecodeError) as error:
        raise SystemExit('Migration failed; check configuration, permissions and record consistency.') from error

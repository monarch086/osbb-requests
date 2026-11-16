#!/usr/bin/env python3
"""Backfill resident phone associations; dry-run by default."""
import argparse
import importlib.util
import json
from pathlib import Path
import re

spec = importlib.util.spec_from_file_location('manage_residents', Path(__file__).with_name('manage-residents.py'))
manage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manage)


def migrate(table, building, region, apply=False, aws=manage.call_aws):
    counts = {'examined': 0, 'eligible': 0, 'current': 0, 'missing': 0, 'written': 0, 'invalid': 0, 'conflicts': 0, 'orphaned': 0}
    cursor = None
    while True:
        scan = {'TableName': table, 'ConsistentRead': True, 'Limit': 500,
                'FilterExpression': 'begins_with(pk, :prefix)',
                'ExpressionAttributeValues': {':prefix': {'S': 'COMPLEX#' + building + '#USER#'}}}
        if cursor:
            scan['ExclusiveStartKey'] = cursor
        page = aws('dynamodb', 'scan', region, scan)
        for item in page.get('Items', []):
            counts['examined'] += 1
            try:
                profile = json.loads(item['payload']['S'])
                user = profile['UserId']
                if (not isinstance(user, int) or isinstance(user, bool) or user <= 0 or
                        item['pk']['S'] != f'COMPLEX#{building}#USER#{user}' or
                        item['complexId']['S'] != building or profile['BuildingId'] != building or
                        int(item['version']['N']) != profile['Version']):
                    raise ValueError('Invalid profile.')
                if profile['Stage'] != 'Approved' or profile.get('PersonalDataDeleted'):
                    continue
                put = manage.phone_association_put(table, profile)
                if not put:
                    counts['invalid'] += 1
                    continue
            except (KeyError, ValueError, TypeError, json.JSONDecodeError):
                counts['invalid'] += 1
                continue
            counts['eligible'] += 1
            target = put['Put']['Item']
            existing = aws('dynamodb', 'get-item', region, {'TableName': table, 'Key': {'pk': target['pk']}, 'ConsistentRead': True}).get('Item')
            if existing:
                if existing == target:
                    counts['current'] += 1
                else:
                    counts['conflicts'] += 1
                continue
            counts['missing'] += 1
            if apply:
                association = put['Put']
                association['ConditionExpression'] = 'attribute_not_exists(pk)'
                association.pop('ExpressionAttributeValues', None)
                transaction = {'TransactItems': [
                    {'ConditionCheck': {'TableName': table, 'Key': {'pk': item['pk']},
                                        'ConditionExpression': '#v = :version AND #p = :payload AND complexId = :complex',
                                        'ExpressionAttributeNames': {'#v': 'version', '#p': 'payload'},
                                        'ExpressionAttributeValues': {':version': item['version'], ':payload': item['payload'], ':complex': item['complexId']}}},
                    {'Put': association}]}
                aws('dynamodb', 'transact-write-items', region, transaction)
                counts['written'] += 1
        cursor = page.get('LastEvaluatedKey')
        if not cursor:
            break
    cursor = None
    while True:
        scan = {'TableName': table, 'ConsistentRead': True, 'Limit': 500,
                'FilterExpression': 'begins_with(pk, :prefix)',
                'ExpressionAttributeValues': {':prefix': {'S': 'COMPLEX#' + building + '#PHONE_ENTRY#'}}}
        if cursor:
            scan['ExclusiveStartKey'] = cursor
        page = aws('dynamodb', 'scan', region, scan)
        for association in page.get('Items', []):
            try:
                user = int(association['residentId']['N'])
                if user <= 0 or association['complexId']['S'] != building:
                    raise ValueError('Invalid association.')
                profile_key = {'pk': {'S': f'COMPLEX#{building}#USER#{user}'}}
                item = aws('dynamodb', 'get-item', region, {'TableName': table, 'Key': profile_key, 'ConsistentRead': True}).get('Item')
                if not item:
                    raise ValueError('Missing profile.')
                profile = json.loads(item['payload']['S'])
                if (profile['UserId'] != user or profile['BuildingId'] != building or
                        profile['Stage'] != 'Approved' or profile.get('PersonalDataDeleted')):
                    raise ValueError('Profile not eligible.')
                expected = manage.phone_association_put(table, profile)
                if not expected or expected['Put']['Item'] != association:
                    raise ValueError('Association differs.')
            except (KeyError, ValueError, TypeError, json.JSONDecodeError):
                counts['orphaned'] += 1
        cursor = page.get('LastEvaluatedKey')
        if not cursor:
            return counts


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
    counts = migrate(prefix + '-residents', args.complex_id or prefix, args.region, args.apply)
    print(json.dumps({'mode': 'apply' if args.apply else 'dry-run', **counts}))
    if counts['invalid'] or counts['conflicts'] or counts['orphaned']:
        raise SystemExit('Invalid profiles or conflicting associations; investigate before switching reads.')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, RuntimeError, KeyError, TypeError, json.JSONDecodeError):
        raise SystemExit('Migration stopped; check configuration, permissions and profile consistency.') from None

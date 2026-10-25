#!/usr/bin/env python3
"""Backfill the priority/date index for one complex; dry-run by default."""
import argparse
import importlib.util
import json
import re
from datetime import datetime, timezone
from pathlib import Path

spec = importlib.util.spec_from_file_location('manage', Path(__file__).with_name('manage-residents.py'))
manage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manage)


def priority_key(record):
    rank = {'Urgent': 3, 'High': 2, 'Normal': 1, 'Low': 0}.get(record.get('Priority'), 1)
    date = datetime.fromisoformat(record['CreatedAt'].replace('Z', '+00:00')).astimezone(timezone.utc)
    # .NET O format uses seven fractional digits; preserve the original 100 ns digit.
    fraction = re.search(r'\.(\d+)', record['CreatedAt'])
    seventh = (fraction.group(1) + '0000000')[6] if fraction else '0'
    stamp = date.strftime('%Y-%m-%dT%H:%M:%S.') + f'{date.microsecond:06d}' + seventh + '+00:00'
    return str(rank) + '#' + stamp + '#' + str(record['Id']).zfill(20)


def migrate(table, complex_id, region, apply=False, aws=manage.call_aws):
    cursor = None
    count = 0
    while True:
        request = {'TableName': table, 'ConsistentRead': True, 'Limit': 100,
                   'FilterExpression': 'begins_with(sk, :prefix) AND attribute_exists(#p)',
                   'ExpressionAttributeNames': {'#p': 'payload'}, 'ExpressionAttributeValues': {':prefix': {'S': 'REQUEST#'}}}
        if cursor:
            request['ExclusiveStartKey'] = cursor
        page = aws('dynamodb', 'scan', region, request)
        for item in page.get('Items', []):
            if not item['pk']['S'].startswith('USER#'):
                continue
            record = json.loads(item['payload']['S'])
            if record.get('BuildingId') != complex_id:
                continue
            value = priority_key(record)
            if item.get('requestPriorityCreatedAt', {}).get('S') == value and item.get('requestBuildingId', {}).get('S') == complex_id:
                continue
            if apply:
                aws('dynamodb', 'update-item', region, {'TableName': table, 'Key': {'pk': item['pk'], 'sk': item['sk']},
                    'UpdateExpression': 'SET requestPriorityCreatedAt = :order, requestBuildingId = :complex',
                    'ConditionExpression': '#p = :previous', 'ExpressionAttributeNames': {'#p': 'payload'},
                    'ExpressionAttributeValues': {':order': {'S': value}, ':complex': {'S': complex_id}, ':previous': item['payload']}})
            count += 1
        cursor = page.get('LastEvaluatedKey')
        if not cursor:
            return count


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stage', default='dev')
    parser.add_argument('--region', default='eu-central-1')
    parser.add_argument('--complex-id')
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    if not re.fullmatch(r'[a-zA-Z0-9-]+', args.stage):
        parser.error('Invalid stage.')
    count = migrate('osbb-requests-' + args.stage + '-command-data', args.complex_id or 'osbb-requests-' + args.stage,
                    args.region, args.apply)
    print(('Updated' if args.apply else 'Would update') + ': ' + str(count) + ' requests. Payloads unchanged.')


if __name__ == '__main__':
    try:
        main()
    except RuntimeError as error:
        raise SystemExit(str(error)) from None

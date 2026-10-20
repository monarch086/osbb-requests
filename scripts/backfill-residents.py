#!/usr/bin/env python3
"""Backfill approved resident directory entries without changing registrations or access; dry-run by default."""
import argparse
import importlib.util
import json
import re
from pathlib import Path

spec = importlib.util.spec_from_file_location('manage', Path(__file__).with_name('manage-residents.py'))
manage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manage)


def migrate(table, building, region, apply=False, aws=manage.call_aws):
    cursor = None
    count = 0
    while True:
        request = {
            'TableName': table, 'Limit': 100, 'ConsistentRead': True,
            'FilterExpression': 'sk = :profile AND begins_with(pk, :user)',
            'ExpressionAttributeValues': {':profile': {'S': 'REGISTRATION'}, ':user': {'S': 'USER#'}}
        }
        if cursor:
            request['ExclusiveStartKey'] = cursor
        result = aws('dynamodb', 'scan', region, request)
        for item in result.get('Items', []):
            record = json.loads(item['payload']['S'])
            if record['BuildingId'] != building or record['Stage'] != 'Approved':
                continue
            directory_key = manage.key('BUILDING#' + building, 'RESIDENT#' + str(record['UserId']).zfill(20))
            existing = aws('dynamodb', 'get-item', region, {
                'TableName': table, 'Key': directory_key, 'ConsistentRead': True
            })
            if existing.get('Item'):
                continue
            if apply:
                resident = dict(directory_key, payload=item['payload'], version=item['version'])
                aws('dynamodb', 'transact-write-items', region, {'TransactItems': [
                    {'ConditionCheck': {
                        'TableName': table, 'Key': {'pk': item['pk'], 'sk': item['sk']},
                        'ConditionExpression': '#payload = :previous AND #version = :version',
                        'ExpressionAttributeNames': {'#payload': 'payload', '#version': 'version'},
                        'ExpressionAttributeValues': {':previous': item['payload'], ':version': item['version']}
                    }},
                    {'Put': {'TableName': table, 'Item': resident, 'ConditionExpression': 'attribute_not_exists(pk)'}}
                ]})
            count += 1
        cursor = result.get('LastEvaluatedKey')
        if not cursor:
            return count


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stage', default='dev')
    parser.add_argument('--region', default='eu-central-1')
    parser.add_argument('--building-id')
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    if not re.fullmatch(r'[a-zA-Z0-9-]+', args.stage):
        parser.error('Invalid stage.')
    count = migrate('osbb-requests-' + args.stage + '-command-data',
                    args.building_id or 'osbb-requests-' + args.stage, args.region, args.apply)
    print(('Migrated' if args.apply else 'Would migrate') + ': ' + str(count) + ' residents. Personal data not displayed.')


if __name__ == '__main__':
    try:
        main()
    except RuntimeError as error:
        raise SystemExit(str(error)) from None

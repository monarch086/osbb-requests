#!/usr/bin/env python3
"""One-time migration: add admin query attributes to existing request records; dry-run by default."""
import argparse
import importlib.util
import json
from pathlib import Path

spec = importlib.util.spec_from_file_location('manage', Path(__file__).with_name('manage-residents.py'))
manage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manage)
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--stage', default='dev')
parser.add_argument('--region', default='eu-central-1')
parser.add_argument('--apply', action='store_true')
args = parser.parse_args()
table = 'osbb-requests-' + args.stage + '-command-data'
cursor = None
count = 0
while True:
    request = {'TableName': table, 'Limit': 100,
               'FilterExpression': 'begins_with(sk, :prefix) AND attribute_exists(#payload) AND attribute_not_exists(requestBuildingId)',
               'ExpressionAttributeNames': {'#payload': 'payload'}, 'ExpressionAttributeValues': {':prefix': {'S': 'REQUEST#'}}}
    if cursor:
        request['ExclusiveStartKey'] = cursor
    result = manage.call_aws('dynamodb', 'scan', args.region, request)
    for item in result.get('Items', []):
        record = json.loads(item['payload']['S'])
        if not item['pk']['S'].startswith('USER#'):
            continue
        count += 1
        if args.apply:
            status = record['Status']
            if isinstance(status, int):
                status = ['New', 'InProgress', 'AwaitingClarification', 'Completed', 'Rejected'][status]
            manage.call_aws('dynamodb', 'update-item', args.region, {
                'TableName': table, 'Key': {'pk': item['pk'], 'sk': item['sk']},
                'UpdateExpression': 'SET requestBuildingId = :building, requestCreatedAt = :created, requestStatus = :status, requestCategory = :category, searchText = :search, #v = if_not_exists(#v, :version)',
                'ConditionExpression': '#payload = :previous AND attribute_not_exists(requestBuildingId)',
                'ExpressionAttributeNames': {'#payload': 'payload', '#v': 'version'},
                'ExpressionAttributeValues': {
                    ':building': {'S': record['BuildingId']}, ':created': {'S': record['CreatedAt'] + '#' + str(record['Id'])},
                    ':status': {'S': status}, ':category': {'S': record['Category']},
                    ':search': {'S': (record['Location'] + ' ' + record['Description']).lower()},
                    ':version': {'N': '1'}, ':previous': item['payload']
                }
            })
    cursor = result.get('LastEvaluatedKey')
    if not cursor:
        break
print(('Migrated' if args.apply else 'Would migrate') + ': ' + str(count) + ' requests. Personal data not displayed.')

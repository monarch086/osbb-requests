#!/usr/bin/env python3
"""Migrate request categories for one complex; dry-run by default."""
import argparse
import importlib.util
import json
import re
from collections import Counter
from pathlib import Path

spec = importlib.util.spec_from_file_location('manage', Path(__file__).with_name('manage-residents.py'))
manage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manage)

CATEGORIES = {
    'електрика': 'Електрика МЗК', 'електрика мзк': 'Електрика МЗК',
    'сантехніка': 'Сантехніка МЗК', 'сантехніка мзк': 'Сантехніка МЗК',
    'ліфт': 'Ліфти', 'ліфти': 'Ліфти',
    'прибирання': 'Прибирання МЗК', 'прибирання мзк': 'Прибирання МЗК',
    'місця загального користування': 'Інше', 'інше': 'Інше',
}


def migrate(table, complex_id, region, apply=False, aws=manage.call_aws):
    cursor = None
    counts = Counter()
    while True:
        request = {'TableName': table, 'ConsistentRead': True, 'Limit': 100,
                   'FilterExpression': 'begins_with(sk, :prefix) AND attribute_exists(#p)',
                   'ExpressionAttributeNames': {'#p': 'payload'},
                   'ExpressionAttributeValues': {':prefix': {'S': 'REQUEST#'}}}
        if cursor:
            request['ExclusiveStartKey'] = cursor
        page = aws('dynamodb', 'scan', region, request)
        for item in page.get('Items', []):
            if not item['pk']['S'].startswith('USER#'):
                continue
            record = json.loads(item['payload']['S'])
            if record.get('BuildingId') != complex_id:
                continue
            old = record.get('Category', '')
            category = CATEGORIES.get(old.strip().lower())
            if category is None:
                raise RuntimeError('Unknown request category; migration stopped. Inspect data before retrying.')
            if old == category and item.get('requestCategory', {}).get('S') == category:
                continue
            counts[(old, category)] += 1
            if apply:
                record['Category'] = category
                version = int(item.get('version', {}).get('N', '0'))
                condition = '#p = :previous AND #v = :version' if 'version' in item else '#p = :previous AND attribute_not_exists(#v)'
                values = {':previous': item['payload'], ':next': {'S': json.dumps(record, ensure_ascii=False, separators=(',', ':'))},
                          ':category': {'S': category}, ':nextVersion': {'N': str(version + 1)}}
                if 'version' in item:
                    values[':version'] = {'N': str(version)}
                aws('dynamodb', 'update-item', region, {'TableName': table, 'Key': {'pk': item['pk'], 'sk': item['sk']},
                    'UpdateExpression': 'SET #p = :next, requestCategory = :category, #v = :nextVersion',
                    'ConditionExpression': condition, 'ExpressionAttributeNames': {'#p': 'payload', '#v': 'version'},
                    'ExpressionAttributeValues': values})
        cursor = page.get('LastEvaluatedKey')
        if not cursor:
            return counts


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stage', required=True)
    parser.add_argument('--region', default='eu-central-1')
    parser.add_argument('--complex-id')
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    if not re.fullmatch(r'[a-zA-Z0-9-]+', args.stage):
        parser.error('Invalid stage.')
    counts = migrate('osbb-requests-' + args.stage + '-command-data', args.complex_id or 'osbb-requests-' + args.stage,
                     args.region, args.apply)
    for (old, new), count in sorted(counts.items()):
        print(f'{old} → {new}: {count}')
    print(('Updated' if args.apply else 'Would update') + f': {sum(counts.values())} requests.')


if __name__ == '__main__':
    try:
        main()
    except RuntimeError as error:
        raise SystemExit(str(error)) from None

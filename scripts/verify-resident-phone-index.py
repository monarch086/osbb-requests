#!/usr/bin/env python3
"""Verify existing phone associations are visible in the GSI without printing personal data."""
import argparse
import importlib.util
import json
from pathlib import Path
import re

spec = importlib.util.spec_from_file_location('manage_residents', Path(__file__).with_name('manage-residents.py'))
manage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manage)


def verify(table, complex_id, region, aws=manage.call_aws):
    counts = {'examined': 0, 'visible': 0, 'missing': 0, 'invalid': 0}
    cursor = None
    while True:
        scan = {'TableName': table, 'ConsistentRead': True, 'Limit': 500,
                'FilterExpression': 'begins_with(pk, :prefix)',
                'ExpressionAttributeValues': {':prefix': {'S': 'COMPLEX#' + complex_id + '#PHONE_ENTRY#'}}}
        if cursor:
            scan['ExclusiveStartKey'] = cursor
        page = aws('dynamodb', 'scan', region, scan)
        for item in page.get('Items', []):
            counts['examined'] += 1
            try:
                key = item['pk']['S']
                lookup = item['phoneLookup']['S']
                if item['complexId']['S'] != complex_id or not lookup.startswith('COMPLEX#' + complex_id + '#PHONE#'):
                    raise ValueError('Invalid association.')
            except (KeyError, TypeError, ValueError):
                counts['invalid'] += 1
                continue
            found = False
            index_cursor = None
            while True:
                query = {'TableName': table, 'IndexName': 'ResidentsByPhone',
                         'KeyConditionExpression': 'phoneLookup = :phone',
                         'ExpressionAttributeValues': {':phone': {'S': lookup}}, 'ProjectionExpression': 'pk'}
                if index_cursor:
                    query['ExclusiveStartKey'] = index_cursor
                result = aws('dynamodb', 'query', region, query)
                if any(row.get('pk', {}).get('S') == key for row in result.get('Items', [])):
                    found = True
                    break
                index_cursor = result.get('LastEvaluatedKey')
                if not index_cursor:
                    break
            counts['visible' if found else 'missing'] += 1
        cursor = page.get('LastEvaluatedKey')
        if not cursor:
            return counts


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stage', required=True)
    parser.add_argument('--region', required=True)
    parser.add_argument('--complex-id')
    args = parser.parse_args()
    if not re.fullmatch(r'[a-zA-Z0-9-]+', args.stage):
        parser.error('Invalid stage.')
    prefix = 'osbb-requests-' + args.stage
    counts = verify(prefix + '-residents', args.complex_id or prefix, args.region)
    print(json.dumps(counts))
    if counts['invalid'] or counts['missing']:
        raise SystemExit('Index incomplete or invalid; wait and investigate before switching reads.')


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, ValueError, KeyError, TypeError, json.JSONDecodeError):
        raise SystemExit('Verification stopped; check permissions and index status.') from None

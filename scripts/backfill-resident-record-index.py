#!/usr/bin/env python3
"""Backfill and verify the sparse index for resident directory rows and history events."""
import argparse
import importlib.util
import json
from pathlib import Path
import re

spec = importlib.util.spec_from_file_location('manage_residents', Path(__file__).with_name('manage-residents.py'))
manage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manage)

INDEX = 'ResidentRecordKeysByGroup'
GROUP = 'recordIndexGroup'


def scan(aws, table, region, **options):
    request = {'TableName': table, 'Limit': 500, **options}
    while True:
        page = aws('dynamodb', 'scan', region, request.copy())
        yield from page.get('Items', [])
        cursor = page.get('LastEvaluatedKey')
        if not cursor:
            return
        if cursor == request.get('ExclusiveStartKey'):
            raise RuntimeError('Repeated DynamoDB cursor.')
        request['ExclusiveStartKey'] = cursor


def inventory(aws, table, region, complex_id):
    prefix = f'COMPLEX#{complex_id}#'
    expected = set()
    missing = []
    counts = {'directory': 0, 'history': 0, 'invalid': 0}
    for row in scan(aws, table, region, ConsistentRead=True,
                    FilterExpression='begins_with(pk, :prefix)',
                    ExpressionAttributeValues={':prefix': {'S': prefix + 'RECORD#'}},
                    ProjectionExpression=f'pk, complexId, sourcePk, sourceSk, recordGroup, recordOrder, {GROUP}'):
        try:
            pk, source_pk, order = (row[field]['S'] for field in ('pk', 'sourcePk', 'sourceSk'))
            group = prefix + source_pk
            if (pk != f'{prefix}RECORD#{source_pk}#{order}' or row['complexId']['S'] != complex_id or
                    row['recordGroup']['S'] != group or row['recordOrder']['S'] != order):
                raise ValueError('Inconsistent resident record.')
            if not manage.is_resident_directory_or_history(complex_id, source_pk, order):
                if GROUP in row:
                    counts['invalid'] += 1
                continue
            key = (pk, group, order)
            if key in expected:
                raise ValueError('Duplicate resident record.')
            expected.add(key)
            counts['directory' if source_pk == f'BUILDING#{complex_id}' else 'history'] += 1
            if GROUP not in row:
                missing.append((key, source_pk))
            elif row[GROUP] != {'S': group}:
                counts['invalid'] += 1
        except (KeyError, TypeError, ValueError):
            counts['invalid'] += 1
    return expected, missing, counts


def backfill(aws, table, region, complex_id, missing):
    for (pk, group, order), source_pk in missing:
        aws('dynamodb', 'update-item', region, {
            'TableName': table,
            'Key': {'pk': {'S': pk}},
            'UpdateExpression': f'SET {GROUP} = :group',
            'ConditionExpression': (
                'complexId = :complex AND sourcePk = :sourcePk AND sourceSk = :order AND '
                'recordGroup = :group AND recordOrder = :order AND '
                f'(attribute_not_exists({GROUP}) OR {GROUP} = :group)'
            ),
            'ExpressionAttributeValues': {
                ':complex': {'S': complex_id}, ':sourcePk': {'S': source_pk},
                ':group': {'S': group}, ':order': {'S': order}
            }
        })


def run(table, complex_id, region, mode, aws=manage.call_aws):
    expected, missing, report = inventory(aws, table, region, complex_id)
    report['missing_attributes'] = len(missing)
    if mode == 'apply' and not report['invalid']:
        backfill(aws, table, region, complex_id, missing)
        report['updated'] = len(missing)
    if mode == 'verify':
        visible = {
            (row['pk']['S'], row[GROUP]['S'], row['recordOrder']['S'])
            for row in scan(aws, table, region, IndexName=INDEX,
                            FilterExpression=f'begins_with({GROUP}, :prefix)',
                            ExpressionAttributeValues={':prefix': {'S': f'COMPLEX#{complex_id}#'}},
                            ProjectionExpression=f'pk, {GROUP}, recordOrder')
        }
        report['missing_in_index'] = len(expected - visible)
        report['unexpected_in_index'] = len(visible - expected)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stage', required=True)
    parser.add_argument('--region', required=True)
    parser.add_argument('--complex-id')
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--apply', action='store_true')
    mode.add_argument('--verify', action='store_true')
    args = parser.parse_args()
    if not re.fullmatch(r'[a-zA-Z0-9-]+', args.stage):
        parser.error('Invalid stage.')
    prefix = 'osbb-requests-' + args.stage
    report = run(prefix + '-residents', args.complex_id or prefix, args.region,
                 'apply' if args.apply else 'verify' if args.verify else 'dry-run')
    print(json.dumps(report))
    if report['invalid'] or (args.verify and any(report[name] for name in
                                               ('missing_attributes', 'missing_in_index', 'unexpected_in_index'))):
        raise SystemExit(1)


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, ValueError, KeyError, TypeError):
        raise SystemExit('Backfill stopped. Check AWS permissions, index status and conflicting writes, then rerun.') from None

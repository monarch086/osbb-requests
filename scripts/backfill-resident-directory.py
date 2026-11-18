#!/usr/bin/env python3
"""Populate and verify compact resident directory rows and counters. Defaults to dry-run."""
import argparse
from collections import Counter, defaultdict
import importlib.util
import json
from pathlib import Path
import re

spec = importlib.util.spec_from_file_location('manage_residents', Path(__file__).with_name('manage-residents.py'))
manage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manage)

TYPES = ('FamilyMember', 'Owner', 'Tenant', 'Employee')
BUILDINGS = ('4Б', '4В', '4Г', '4Д', 'Unspecified')
INDEXES = {'ResidentDirectoryByDate': 'listOrder', 'ResidentDirectoryByDateDescending': 'listOrderDescending'}


def pages(aws, region, operation, parameters):
    cursor = None
    while True:
        request = dict(parameters)
        if cursor:
            request['ExclusiveStartKey'] = cursor
        page = aws('dynamodb', operation, region, request)
        yield from page.get('Items', [])
        next_cursor = page.get('LastEvaluatedKey')
        if not next_cursor:
            return
        if next_cursor == cursor:
            raise RuntimeError('Repeated DynamoDB cursor.')
        cursor = next_cursor


def source_writes(table, prefix, item):
    profile = json.loads(item['payload']['S'])
    user_id = profile['UserId']
    version = profile['Version']
    if (type(user_id) is not int or user_id <= 0 or type(version) is not int or
            item['pk']['S'] != f'{prefix}#USER#{user_id}' or
            item['complexId']['S'] != profile['BuildingId'] or
            f'COMPLEX#{profile["BuildingId"]}' != prefix or int(item['version']['N']) != version):
        raise ValueError('Inconsistent source profile.')
    if profile['Stage'] not in ('Approved', 4) or profile.get('PersonalDataDeleted'):
        return []
    if (profile.get('BuildingNumber') or 'Unspecified') not in BUILDINGS:
        raise ValueError('Invalid source building.')
    if profile.get('ResidentType') is not None and profile['ResidentType'] not in (*TYPES, 0, 1, 2, 3):
        raise ValueError('Invalid source resident type.')
    return manage.directory_approval_writes(table, profile)


def inventory(aws, region, table, prefix):
    # The base-table scan sees rows even when the GSIs have not caught up.
    fields = ('USER', 'DIRECTORY_ROW', 'DIRECTORY_COUNT')
    scan = {
        'TableName': table, 'ConsistentRead': True, 'Limit': 500,
        'FilterExpression': ' OR '.join(f'begins_with(pk, :p{i})' for i in range(len(fields))),
        'ExpressionAttributeValues': {f':p{i}': {'S': f'{prefix}#{kind}#'} for i, kind in enumerate(fields)},
    }
    sources, rows, counters = {}, {}, {}
    for item in pages(aws, region, 'scan', scan):
        pk = item['pk']['S']
        for kind, target in zip(fields, (sources, rows, counters)):
            if pk.startswith(f'{prefix}#{kind}#'):
                target[pk] = item
                break
    return sources, rows, counters


def plan(table, prefix, sources):
    profiles, expected_rows = {}, {}
    counts = defaultdict(Counter)
    invalid = 0
    for pk, source in sources.items():
        try:
            writes = source_writes(table, prefix, source)
        except (KeyError, ValueError, TypeError, AttributeError, OverflowError):
            invalid += 1
            continue
        if not writes:
            continue
        rows = [write['Put']['Item'] for write in writes if 'Put' in write]
        profiles[pk] = (writes, rows)
        for row in rows:
            expected_rows[row['pk']['S']] = row
        for write in writes:
            if 'Update' in write:
                update = write['Update']
                counts[update['Key']['pk']['S']][update['ExpressionAttributeNames']['#type']] += 1
    return profiles, expected_rows, counts, invalid


def backfill(aws, region, table, sources, rows, profiles):
    written = races = 0

    def read(key):
        return aws('dynamodb', 'get-item', region, {
            'TableName': table, 'Key': {'pk': key}, 'ConsistentRead': True,
        }).get('Item')

    for pk, (writes, expected) in profiles.items():
        if any(row['pk']['S'] in rows for row in expected):
            continue
        source = sources[pk]
        guard = {'ConditionCheck': {
            'TableName': table, 'Key': {'pk': source['pk']},
            'ConditionExpression': '#v = :version AND #p = :payload AND complexId = :complex',
            'ExpressionAttributeNames': {'#v': 'version', '#p': 'payload'},
            'ExpressionAttributeValues': {':version': source['version'], ':payload': source['payload'], ':complex': source['complexId']},
        }}
        try:
            aws('dynamodb', 'transact-write-items', region, {'TransactItems': [guard, *writes]})
            written += 1
        except RuntimeError:
            latest = read(source['pk'])
            unchanged = latest and all(latest.get(key) == source[key] for key in ('payload', 'version', 'complexId'))
            if unchanged and not any(read(row['pk']) for row in expected):
                raise
            races += 1
    return written, races


def compare_counters(prefix, expected, counters):
    keys = [f'{prefix}#DIRECTORY_COUNT#{kind}' for kind in ('ALL', *(f'BUILDING#{house}' for house in BUILDINGS))]
    mismatches = len(counters.keys() - set(keys))
    for key in keys:
        item = counters.get(key, {})
        try:
            mismatch = set(item) - {'pk', *TYPES} or any(
                int(item.get(typ, {'N': '0'})['N']) != expected.get(key, {}).get(typ, 0) for typ in TYPES)
        except (KeyError, ValueError, TypeError):
            mismatch = True
        mismatches += bool(mismatch)
    return mismatches


def verify_indexes(aws, region, table, prefix, expected):
    description = aws('dynamodb', 'describe-table', region, {'TableName': table})
    active = {i['IndexName'] for i in description['Table'].get('GlobalSecondaryIndexes', []) if i['IndexStatus'] == 'ACTIVE'}
    if not INDEXES.keys() <= active:
        raise RuntimeError('Both directory indexes must be ACTIVE.')
    mismatches = 0
    by_group = defaultdict(list)
    for row in expected.values():
        by_group[row['listGroup']['S']].append(row)
    for index, sort_key in INDEXES.items():
        other_sort_key = next(key for key in INDEXES.values() if key != sort_key)
        for kind in ('ALL', *(f'BUILDING#{house}' for house in BUILDINGS)):
            group = f'{prefix}#APPROVED#{kind}'
            ordered = sorted(by_group[group], key=lambda row: row[sort_key]['S'])
            projected = [{k: v for k, v in row.items() if k not in ('sourceVersion', other_sort_key)} for row in ordered]
            found = list(pages(aws, region, 'query', {
                'TableName': table, 'IndexName': index, 'ScanIndexForward': True,
                'KeyConditionExpression': 'listGroup = :group',
                'ExpressionAttributeValues': {':group': {'S': group}},
            }))
            mismatches += found != projected
    return mismatches


def migrate(table, complex_id, region, apply=False, check_indexes=False, aws=manage.call_aws):
    prefix = f'COMPLEX#{complex_id}'
    sources, rows, counters = inventory(aws, region, table, prefix)
    profiles, expected, totals, invalid = plan(table, prefix, sources)
    written = races = 0
    if apply and not invalid:
        written, races = backfill(aws, region, table, sources, rows, profiles)
        sources, rows, counters = inventory(aws, region, table, prefix)
        profiles, expected, totals, invalid = plan(table, prefix, sources)
    return {
        'eligible': len(profiles), 'written': written, 'concurrent_changes': races, 'invalid_profiles': invalid,
        'missing_rows': len(expected.keys() - rows.keys()),
        'partial_profiles': sum(sum(row['pk']['S'] in rows for row in pair) == 1 for _, pair in profiles.values()),
        'orphaned_rows': len(rows.keys() - expected.keys()),
        'changed_rows': sum(rows[key] != expected[key] for key in rows.keys() & expected.keys()),
        'counter_mismatches': compare_counters(prefix, totals, counters),
        'index_group_mismatches': verify_indexes(aws, region, table, prefix, expected) if check_indexes else None,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stage', required=True)
    parser.add_argument('--region', required=True)
    parser.add_argument('--complex-id')
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--confirm-stage')
    parser.add_argument('--verify-indexes', action='store_true')
    args = parser.parse_args()
    if not re.fullmatch(r'[a-zA-Z0-9-]+', args.stage):
        parser.error('Invalid stage.')
    if args.apply and args.confirm_stage != args.stage:
        parser.error('--apply requires --confirm-stage matching --stage.')
    name = f'osbb-requests-{args.stage}'
    result = migrate(f'{name}-residents', args.complex_id or name, args.region, args.apply, args.verify_indexes)
    print(json.dumps({'mode': 'apply' if args.apply else 'dry-run', **result}))
    if any(value for key, value in result.items() if key not in ('eligible', 'written')):
        raise SystemExit('Directory verification found differences. Repeat after concurrent changes settle or investigate remaining differences.')


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, KeyError, ValueError, TypeError):
        raise SystemExit('Directory backfill stopped. Check configuration, permissions, profile consistency and index status.') from None

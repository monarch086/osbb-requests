#!/usr/bin/env python3
"""Copy current resident profiles to the new table safely; dry-run by default."""
import argparse
import copy
from concurrent.futures import ThreadPoolExecutor
import importlib.util
import json
import re
from pathlib import Path

spec = importlib.util.spec_from_file_location('manage_residents', Path(__file__).with_name('manage-residents.py'))
manage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manage)


def safe_profile(profile):
    result = copy.deepcopy(profile)
    if profile.get('PersonalDataDeleted') or profile.get('Stage') == 'Deleted':
        for field in ('FirstName', 'LastName', 'Phone', 'Email', 'BuildingNumber', 'ApartmentNumber',
                      'DecisionReason', 'ConsentAt', 'ConsentVersion'):
            if field in result:
                result[field] = None
    return result


def migrate(source, target, building, region, apply=False, aws=manage.call_aws):
    counts = {'examined': 0, 'current': 0, 'missing': 0, 'stale': 0, 'written': 0, 'conflicts': 0}
    def process(source_item):
        local = dict.fromkeys(counts, 0)
        profile = json.loads(source_item['payload']['S'])
        if profile.get('BuildingId') != building:
            return local
        source_key = {'pk': source_item['pk'], 'sk': source_item['sk']}
        for attempt in range(3):
            profile = json.loads(source_item['payload']['S'])
            if (profile.get('BuildingId') != building or source_key['sk']['S'] != 'REGISTRATION' or
                source_key['pk']['S'] != 'USER#' + str(profile.get('UserId')) or
                int(source_item['version']['N']) != profile.get('Version')):
                raise RuntimeError('Source profile identity or version is invalid; migration stopped.')
            projected = safe_profile(profile)
            target_put = manage.resident_mirror_put(target, projected, profile['Version'])['Put']
            key = {'pk': target_put['Item']['pk']}
            existing = aws('dynamodb', 'get-item', region,
                           {'TableName': target, 'Key': key, 'ConsistentRead': True}).get('Item')
            if existing:
                version = int(existing['version']['N'])
                if existing.get('complexId', {}).get('S') != building:
                    raise RuntimeError('Target profile belongs to another complex; migration stopped.')
                if version >= profile['Version']:
                    if version == profile['Version']:
                        expected = target_put['Item']
                        actual_payload = json.loads(existing['payload']['S'])
                        required_payload = json.loads(expected['payload']['S'])
                        fields_match = all(actual_payload.get(k) == v for k, v in required_payload.items())
                        indexes = ('directory', 'dateOrder', 'nameOrder', 'buildingNumber', 'residentType')
                        if fields_match and all(existing.get(k) == expected.get(k) for k in indexes):
                            local['current'] += 1
                            break
                    if attempt < 2:
                        source_item = aws('dynamodb', 'get-item', region,
                                          {'TableName': source, 'Key': source_key, 'ConsistentRead': True}).get('Item')
                        if source_item:
                            continue
                    local['conflicts'] += 1
                    break
            category = 'stale' if existing else 'missing'
            if not apply:
                local[category] += 1
                break
            target_put['ConditionExpression'] = 'attribute_not_exists(pk) OR (#version < :version AND complexId = :complex)'
            target_put['ExpressionAttributeValues'][':version'] = {'N': str(profile['Version'])}
            transaction = {'TransactItems': [
                {'ConditionCheck': {'TableName': source, 'Key': source_key,
                                    'ConditionExpression': '#payload = :payload AND #version = :version',
                                    'ExpressionAttributeNames': {'#payload': 'payload', '#version': 'version'},
                                    'ExpressionAttributeValues': {':payload': source_item['payload'], ':version': source_item['version']}}},
                {'Put': target_put}
            ]}
            try:
                aws('dynamodb', 'transact-write-items', region, transaction)
                local[category] += 1
                local['written'] += 1
                break
            except RuntimeError:
                if attempt == 2:
                    raise
                source_item = aws('dynamodb', 'get-item', region,
                                  {'TableName': source, 'Key': source_key, 'ConsistentRead': True}).get('Item')
                if not source_item:
                    local['conflicts'] += 1
                    break
        local['examined'] += 1
        return local

    cursor = None
    with ThreadPoolExecutor(max_workers=8) as executor:
        while True:
            scan = {'TableName': source, 'Limit': 1000, 'ConsistentRead': True,
                    'FilterExpression': 'sk = :profile AND begins_with(pk, :user)',
                    'ExpressionAttributeValues': {':profile': {'S': 'REGISTRATION'}, ':user': {'S': 'USER#'}}}
            if cursor:
                scan['ExclusiveStartKey'] = cursor
            result = aws('dynamodb', 'scan', region, scan)
            for local in executor.map(process, result.get('Items', [])):
                for key, value in local.items():
                    counts[key] += value
            cursor = result.get('LastEvaluatedKey')
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
    result = migrate(prefix + '-command-data', prefix + '-residents', args.complex_id or prefix, args.region, args.apply)
    print(json.dumps({'mode': 'apply' if args.apply else 'dry-run', **result}, ensure_ascii=False))
    if result['conflicts']:
        raise SystemExit('Conflicts detected; resolve before switching reads.')


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, ValueError, KeyError, json.JSONDecodeError) as error:
        raise SystemExit('Migration failed; check configuration, permissions and profile consistency.') from error

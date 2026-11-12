#!/usr/bin/env python3
"""Revoke access for profiles deleted under the former retain-access behavior; dry-run by default."""
import argparse
import importlib.util
import json
import re
from datetime import datetime, timezone
from pathlib import Path

spec = importlib.util.spec_from_file_location('manage', Path(__file__).with_name('manage-residents.py'))
manage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manage)


def migrate(table, building, region, apply=False, aws=manage.call_aws, residents_table=None):
    cursor = None
    count = 0
    while True:
        request = {'TableName': table, 'ConsistentRead': True, 'Limit': 25,
                   'KeyConditionExpression': 'pk = :pk AND begins_with(sk, :prefix)',
                   'ExpressionAttributeValues': {':pk': {'S': 'BUILDING#' + building}, ':prefix': {'S': 'RESIDENT#'}}}
        if cursor:
            request['ExclusiveStartKey'] = cursor
        page = aws('dynamodb', 'query', region, request)
        for item in page.get('Items', []):
            candidate = json.loads(item['payload']['S'])
            if candidate.get('BuildingId') != building or not candidate.get('PersonalDataDeleted'):
                continue
            user = candidate['UserId']
            source_key = manage.key('USER#' + str(user), 'REGISTRATION')
            current = aws('dynamodb', 'get-item', region, {'TableName': table, 'Key': source_key, 'ConsistentRead': True}).get('Item')
            if not current:
                continue
            profile = json.loads(current['payload']['S'])
            if (profile.get('UserId') != user or profile.get('BuildingId') != building or
                    not profile.get('PersonalDataDeleted') or profile.get('Stage') != 'Approved'):
                continue
            if apply:
                next_profile = dict(profile, Stage='Deleted', Version=profile['Version'] + 1,
                                    UpdatedAt=datetime.now(timezone.utc).isoformat())
                for field in ('FirstName', 'LastName', 'Phone', 'Email', 'BuildingNumber', 'ApartmentNumber', 'DecisionReason'):
                    next_profile[field] = None
                writes = [manage.conditional_update(table, source_key, current['payload']['S'], next_profile)]
                for key in [manage.key('REGISTRATION#' + profile['Id'], 'META'),
                            manage.key('BUILDING#' + building, 'RESIDENT#' + str(user).zfill(20))]:
                    writes.append({'Put': {'TableName': table, 'Item': dict(key,
                        payload={'S': json.dumps(next_profile, ensure_ascii=False, separators=(',', ':'))},
                        version={'N': str(next_profile['Version'])}),
                        'ConditionExpression': 'attribute_not_exists(pk) OR #v = :v',
                        'ExpressionAttributeNames': {'#v': 'version'},
                        'ExpressionAttributeValues': {':v': {'N': str(profile['Version'])}}}})
                writes.append({'Update': {'TableName': table, 'Key': manage.key('USER#' + str(user), 'MEMBERSHIP'),
                    'UpdateExpression': 'SET #active = :no', 'ConditionExpression': 'buildingId = :complex',
                    'ExpressionAttributeNames': {'#active': 'active'},
                    'ExpressionAttributeValues': {':no': {'BOOL': False}, ':complex': {'S': building}}}})
                if residents_table:
                    membership_key = manage.key('USER#' + str(user), 'MEMBERSHIP')
                    membership = aws('dynamodb', 'get-item', region, {'TableName': table, 'Key': membership_key, 'ConsistentRead': True}).get('Item')
                    if not membership or membership.get('buildingId', {}).get('S') != building:
                        raise RuntimeError('Resident membership is unavailable.')
                    membership['active'] = {'BOOL': False}
                    meta = manage.key('REGISTRATION#' + profile['Id'], 'META')
                    meta.update(payload={'S': json.dumps(next_profile, ensure_ascii=False, separators=(',', ':'))},
                                version={'N': str(next_profile['Version'])})
                    writes.append(manage.resident_mirror_put(residents_table, next_profile, profile['Version']))
                    writes.append(manage.related_resident_put(residents_table, meta, building, profile['Version']))
                    user_profile = dict(current)
                    user_profile.update(pk=source_key['pk'], sk=source_key['sk'], payload=meta['payload'], version=meta['version'])
                    resident_entry = manage.key('BUILDING#' + building, 'RESIDENT#' + str(user).zfill(20))
                    resident_entry.update(payload=meta['payload'], version=meta['version'])
                    writes.append(manage.related_resident_put(residents_table, user_profile, building, profile['Version']))
                    writes.append(manage.related_resident_put(residents_table, resident_entry, building, profile['Version']))
                    writes.append(manage.related_resident_put(residents_table, membership, building))
                aws('dynamodb', 'transact-write-items', region, {'TransactItems': writes})
            count += 1
        cursor = page.get('LastEvaluatedKey')
        if not cursor:
            return count


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stage', default='dev')
    parser.add_argument('--region', default='eu-central-1')
    parser.add_argument('--building-id')
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    if args.apply:
        parser.error('Historical migration is disabled for runtime use; it depends on the legacy resident records.')
    if not re.fullmatch(r'[a-zA-Z0-9-]+', args.stage):
        parser.error('Invalid stage.')
    count = migrate('osbb-requests-' + args.stage + '-command-data', args.building_id or 'osbb-requests-' + args.stage,
                    args.region, args.apply, residents_table='osbb-requests-' + args.stage + '-residents')
    print(('Migrated' if args.apply else 'Would migrate') + ': ' + str(count) + '. Personal data not displayed.')


if __name__ == '__main__':
    try:
        main()
    except RuntimeError as error:
        raise SystemExit(str(error)) from None

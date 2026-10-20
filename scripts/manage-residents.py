#!/usr/bin/env python3
"""Review resident registrations with an operator's AWS credentials; no Telegram IDs in the UI."""
import argparse
import copy
from datetime import datetime, timezone
import json
import re
import subprocess
import tempfile


def call_aws(service, operation, region, payload=None):
    command = ['aws', service, operation, '--region', region, '--output', 'json', '--no-cli-pager']
    with tempfile.NamedTemporaryFile(mode='w+', prefix='osbb-admin-', suffix='.json') as request:
        if payload is not None:
            json.dump(payload, request)
            request.flush()
            command.extend(['--cli-input-json', 'file://' + request.name])
        if operation == 'query':
            command.append('--no-paginate')
        result = subprocess.run(command, capture_output=True, text=True, check=False)
    if result.returncode:
        # AWS error text may echo personal data from an invalid request: do not print it.
        raise RuntimeError('AWS operation failed: ' + service + '/' + operation +
                           '. Check AWS credentials, permissions and whether the registration changed.')
    return json.loads(result.stdout) if result.stdout.strip() else {}


def key(pk, sk):
    return {'pk': {'S': pk}, 'sk': {'S': sk}}


def conditional_update(table, item_key, original, replacement):
    return {'Update': {
        'TableName': table, 'Key': item_key,
        'UpdateExpression': 'SET #payload = :next, #version = :nextVersion',
        'ConditionExpression': '#payload = :previous AND #version = :version',
        'ExpressionAttributeNames': {'#payload': 'payload', '#version': 'version'},
        'ExpressionAttributeValues': {
            ':previous': {'S': original}, ':next': {'S': json.dumps(replacement, ensure_ascii=False, separators=(',', ':'))},
            ':version': {'N': str(replacement['Version'] - 1)}, ':nextVersion': {'N': str(replacement['Version'])}
        }
    }}


def decision_transaction(table, registration, original_payload, decision, reason, actor, now):
    if decision not in ('Approved', 'Rejected'):
        raise ValueError('Unsupported decision.')
    if registration['Stage'] != 'Pending':
        raise ValueError('Registration is not pending.')
    if decision == 'Rejected' and not reason:
        raise ValueError('A rejection reason is required.')
    updated = copy.deepcopy(registration)
    updated.update(Stage=decision, Version=registration['Version'] + 1, UpdatedAt=now, DecisionReason=reason)
    user_key = 'USER#' + str(registration['UserId'])
    registration_key = 'REGISTRATION#' + registration['Id']
    membership = key(user_key, 'MEMBERSHIP')
    membership.update(buildingId={'S': registration['BuildingId']}, active={'BOOL': decision == 'Approved'},
                      registrationId={'S': registration['Id']}, reviewedBy={'S': actor}, reviewedAt={'S': now})
    audit = key(registration_key, 'EVENT#DECIDED')
    audit.update(decision={'S': decision}, actor={'S': actor}, createdAt={'S': now}, reason={'S': reason or ''})
    return {'TransactItems': [
        conditional_update(table, key(user_key, 'REGISTRATION'), original_payload, updated),
        conditional_update(table, key(registration_key, 'META'), original_payload, updated),
        {'Delete': {
            'TableName': table, 'Key': key('BUILDING#' + registration['BuildingId'], registration_key),
            'ConditionExpression': '#version = :version',
            'ExpressionAttributeNames': {'#version': 'version'},
            'ExpressionAttributeValues': {':version': {'N': str(registration['Version'])}}
        }},
        {'Put': {
            'TableName': table, 'Item': membership,
            'ConditionExpression': 'attribute_not_exists(pk) OR buildingId = :building',
            'ExpressionAttributeValues': {':building': {'S': registration['BuildingId']}}
        }},
        {'Put': {'TableName': table, 'Item': audit, 'ConditionExpression': 'attribute_not_exists(pk)'}}
    ]}


def list_pending(table, building, region):
    cursor = None
    while True:
        query = {
            'TableName': table, 'KeyConditionExpression': 'pk = :pk AND begins_with(sk, :prefix)',
            'ExpressionAttributeValues': {':pk': {'S': 'BUILDING#' + building}, ':prefix': {'S': 'REGISTRATION#'}},
            'ConsistentRead': True, 'Limit': 50
        }
        if cursor:
            query['ExclusiveStartKey'] = cursor
        result = call_aws('dynamodb', 'query', region, query)
        for item in result.get('Items', []):
            registration = json.loads(item['payload']['S'])
            # Intentionally omit UserId from the operator-facing list.
            print(json.dumps({
                'reference': registration['Id'], 'first_name': registration['FirstName'],
                'last_name': registration['LastName'], 'phone': registration['Phone'],
                'submitted_at': registration['UpdatedAt']
            }, ensure_ascii=False))
        cursor = result.get('LastEvaluatedKey')
        if not cursor:
            return


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stage', default='dev')
    parser.add_argument('--region', default='eu-central-1')
    parser.add_argument('--building-id', help='Defaults to osbb-requests-<stage>.')
    subcommands = parser.add_subparsers(dest='action', required=True)
    subcommands.add_parser('list', help='Print pending names, phones and registration references (private data).')
    for action in ['approve', 'reject']:
        command = subcommands.add_parser(action)
        command.add_argument('reference', help='Registration reference from list; not a Telegram ID.')
        command.add_argument('--reason', required=action == 'reject')
    args = parser.parse_args()
    if not re.fullmatch(r'[a-zA-Z0-9-]+', args.stage):
        parser.error('Invalid stage.')
    reason = getattr(args, 'reason', None)
    if reason is not None:
        reason = reason.strip()
        if not 1 <= len(reason) <= 500:
            parser.error('Reason must contain 1 to 500 characters.')
    table = 'osbb-requests-' + args.stage + '-command-data'
    building = args.building_id or 'osbb-requests-' + args.stage
    if args.action == 'list':
        list_pending(table, building, args.region)
        return
    if not re.fullmatch(r'[a-f0-9]{32}', args.reference):
        parser.error('Invalid registration reference.')
    result = call_aws('dynamodb', 'get-item', args.region, {
        'TableName': table, 'Key': key('REGISTRATION#' + args.reference, 'META'), 'ConsistentRead': True
    })
    if 'Item' not in result:
        raise ValueError('Registration not found.')
    original_payload = result['Item']['payload']['S']
    registration = json.loads(original_payload)
    if registration['BuildingId'] != building:
        raise ValueError('Registration belongs to another building.')
    decision = 'Approved' if args.action == 'approve' else 'Rejected'
    if registration['Stage'] == decision:
        print('This decision has already been recorded.')
        return
    actor = call_aws('sts', 'get-caller-identity', args.region)['Arn']
    transaction = decision_transaction(table, registration, original_payload, decision, reason, actor,
                                       datetime.now(timezone.utc).isoformat())
    call_aws('dynamodb', 'transact-write-items', args.region, transaction)
    print('Access approved.' if decision == 'Approved' else 'Registration rejected.')
    print('The resident can check the decision using /start. No Telegram message was sent by this tool.')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, RuntimeError) as error:
        raise SystemExit(str(error)) from None

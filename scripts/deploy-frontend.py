#!/usr/bin/env python3
"""Build/upload the Angular admin panel to the private S3 origin from this stage's stack outputs."""
import argparse
import json
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--stage', default='dev')
parser.add_argument('--region', default='eu-central-1')
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
frontend = root / 'src' / 'Frontend'

def run(command, capture=False):
    result = subprocess.run(command, cwd=root, check=True, text=True, capture_output=capture)
    return json.loads(result.stdout) if capture else None

stack = run(['aws', 'cloudformation', 'describe-stacks', '--stack-name', 'osbb-requests-' + args.stage,
             '--region', args.region, '--output', 'json', '--no-cli-pager'], True)['Stacks'][0]
if stack['StackStatus'] not in ('CREATE_COMPLETE', 'UPDATE_COMPLETE'):
    raise SystemExit('Deploy the infrastructure successfully before uploading the frontend.')
outputs = {row['OutputKey']: row['OutputValue'] for row in stack['Outputs']}
run(['npm', 'run', 'build', '--prefix', str(frontend)])
dist = frontend / 'dist' / 'osbb-admin' / 'browser'
config = {'apiUrl': outputs['HttpApiUrl'].rstrip('/'), 'clientId': outputs['UserPoolClientId'], 'userPoolId': outputs['UserPoolId']}
(dist / 'assets' / 'app-config.json').write_text(json.dumps(config) + '\n')
# Upload immutable assets first and keep previous hashed assets for existing browser sessions.
run(['aws', 's3', 'sync', str(dist), 's3://' + outputs['FrontendBucketName'], '--region', args.region,
     '--exclude', 'index.html', '--exclude', 'assets/app-config.json', '--cache-control', 'public,max-age=31536000,immutable', '--only-show-errors'])
for name in ['assets/app-config.json', 'index.html']:
    run(['aws', 's3', 'cp', str(dist / name), 's3://' + outputs['FrontendBucketName'] + '/' + name, '--region', args.region,
         '--cache-control', 'no-store,max-age=0', '--only-show-errors'])
run(['aws', 'cloudfront', 'create-invalidation', '--distribution-id', outputs['FrontendDistributionId'],
     '--paths', '/', '/index.html', '/assets/app-config.json', '--output', 'json', '--no-cli-pager'], True)
print('Admin panel: ' + outputs['FrontendUrl'])

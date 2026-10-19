'use strict';

const path = require('node:path');
const { execFileSync } = require('node:child_process');

class DotnetPackagePlugin {
  constructor(serverless) {
    this.hooks = {
      'before:package:initialize': () => {
        serverless.cli.log('Packaging .NET Lambda projects.');
        execFileSync('bash', [path.join(serverless.config.serviceDir, 'scripts/package-dotnet.sh')], {
          cwd: serverless.config.serviceDir,
          stdio: 'inherit'
        });
      }
    };
  }
}

module.exports = DotnetPackagePlugin;

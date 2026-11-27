import assert from 'node:assert/strict';
import test from 'node:test';
import { isAllowedApiUrl } from '../../src/Frontend/src/app/api-url.ts';

test('allows a same-origin API for local Docker and HTTPS deployments', () => {
  assert.equal(isAllowedApiUrl('/', 'http://127.0.0.1:8083'), true);
  assert.equal(isAllowedApiUrl('/', 'http://localhost:8083'), true);
  assert.equal(isAllowedApiUrl('/', 'http://nuc.local:8083'), true);
  assert.equal(isAllowedApiUrl('/', 'https://panel.example'), true);
  assert.equal(isAllowedApiUrl('/', 'http://panel.example'), false);
});

test('allows the absolute HTTPS API and local development URLs', () => {
  assert.equal(isAllowedApiUrl('https://api.example', 'https://panel.example'), true);
  assert.equal(isAllowedApiUrl('http://localhost:8082', 'http://localhost:4201'), true);
  assert.equal(isAllowedApiUrl('http://api.example', 'http://localhost:4201'), false);
  assert.equal(isAllowedApiUrl('http://other.local:8083', 'http://nuc.local:8083'), false);
  assert.equal(isAllowedApiUrl('//api.example', 'https://panel.example'), false);
  assert.equal(isAllowedApiUrl('ftp://localhost', 'http://localhost:4201'), false);
});

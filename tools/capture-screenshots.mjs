import { chromium } from '@playwright/test';
import { fileURLToPath } from 'node:url';
import { writeFile } from 'node:fs/promises';
import assert from 'node:assert/strict';

// Run against a new local demo database. This creates invented requests through the real API.
const browser = await chromium.launch();
try {
  const page = await browser.newPage({ baseURL: 'http://127.0.0.1:5099', viewport: { width: 1500, height: 1320 }, deviceScaleFactor: 1 });
  await page.goto('http://127.0.0.1:5099/');
  await page.locator('#message').filter({ hasText: 'Ready.' }).waitFor();
  const post = async (path, data) => {
    const response = await page.request.post(path, { headers: { 'X-Workbench': '1' }, data });
    assert(response.ok(), await response.text());
    return response.json();
  };
  const versions = await (await page.request.get('/api/versions')).json();
  assert.equal(versions.length, 1, 'Use a fresh demo database for consistent screenshot fixtures.');
  await post('/api/session', { actorId: 'alex' });
  let oldRun = await post('/api/runs', { title: 'Replacement label printer' });
  async function complete(run, stepId) {
    return (await post(`/api/runs/${run.id}/actions`, { expectedRevision: run.revision, commandId: crypto.randomUUID(), stepId,
      action: 'complete', comment: 'Reviewed for this demonstration.', confirmed: true })).run;
  }
  oldRun = await complete(oldRun, 'intake');
  await post('/api/session', { actorId: 'morgan' });
  oldRun = await complete(oldRun, 'operations');
  const definition = structuredClone(versions[0].definition);
  definition.steps.push({ id: 'technical', name: 'Technical review', role: 'technical', requires: ['intake'], question: 'Is the equipment compatible with our setup?' });
  definition.steps.find(step => step.id === 'release').requires.push('technical');
  await post('/api/session', { actorId: 'casey' });
  const published = await post('/api/versions', { expectedVersion: 1, definition });
  await post('/api/session', { actorId: 'alex' });
  let newRun = await post('/api/runs', { title: 'Laptop setup request' });
  newRun = await complete(newRun, 'intake');
  oldRun = await (await page.request.get(`/api/runs/${oldRun.id}`)).json();
  assert.equal(oldRun.definitionVersion, 1);
  assert.equal(newRun.definitionVersion, 2);
  assert.equal(Object.values(newRun.steps).filter(status => status === 'active').length, 3);
  await writeFile(new URL('../docs/walkthrough.json', import.meta.url), JSON.stringify({ published, oldRun, newRun }, null, 2) + '\n');
  await post('/api/session', { actorId: 'casey' });
  await page.reload();
  await page.locator('#message').filter({ hasText: 'Ready.' }).waitFor();
  await page.locator('#designer-graph').getByRole('button', { name: /Ready to order/ }).click();
  await page.screenshot({ path: fileURLToPath(new URL('../docs/designer.png', import.meta.url)) });
  await page.locator('.designer-layout').screenshot({ path: fileURLToPath(new URL('../docs/workflow-detail.png', import.meta.url)) });
  await page.locator('#actor').selectOption('sam');
  await page.locator('#message').filter({ hasText: 'Acting as' }).waitFor();
  await page.getByRole('button', { name: 'Run a request', exact: true }).click();
  await page.locator('#runs button').filter({ hasText: 'Replacement label printer' }).click();
  await page.locator('#run-meta').filter({ hasText: 'Pinned to version 1' }).waitFor();
  await page.screenshot({ path: fileURLToPath(new URL('../docs/runner.png', import.meta.url)) });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.locator('#run-detail').scrollIntoViewIfNeeded();
  await page.evaluate(() => document.querySelector('#run-detail').scrollIntoView());
  await page.screenshot({ path: fileURLToPath(new URL('../docs/mobile.png', import.meta.url)) });
} finally { await browser.close(); }

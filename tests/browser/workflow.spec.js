import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';

async function ready(page) {
  await page.goto('/');
  await expect(page.locator('#message')).toContainText('Ready.');
}
async function actor(page, id) {
  await page.locator('#actor').selectOption(id);
  await expect(page.locator('#message')).toContainText('Acting as');
}
async function complete(page) {
  if (await page.locator('#question-label').isVisible()) await page.locator('#confirmed').check();
  await page.getByRole('button', { name: 'Complete step', exact: true }).click();
  await expect(page.locator('#message')).toContainText('Step committed.');
}
async function create(page, title) {
  await actor(page, 'alex');
  await page.locator('#request-title').fill(title);
  await page.getByRole('button', { name: 'Create request', exact: true }).click();
  await expect(page.locator('#message')).toContainText('Request created');
}

test('visual editing, immutable versions, parallel approvals, rework, history and export', async ({ page }) => {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await ready(page);
  const initialVersions = await (await page.request.get('/api/versions')).json();
  const initialVersion = initialVersions[0].version;
  await page.getByRole('button', { name: 'Run a request', exact: true }).click();
  await create(page, 'Sample printer request');
  await complete(page);
  await expect(page.locator('#runner-graph .graph-node[data-status="active"]')).toHaveCount(2);
  await actor(page, 'morgan');
  await complete(page);
  await expect(page.locator('#runner-graph').getByRole('button', { name: /Ready to order/ })).toHaveAttribute('data-status', 'waiting');
  await actor(page, 'sam');
  await page.getByRole('button', { name: 'Send back to intake' }).click();
  await expect(page.locator('#message')).toContainText('Comment must be');
  await page.locator('#action-comment').fill('Please include a delivery quote.');
  await page.getByRole('button', { name: 'Send back to intake' }).click();
  await expect(page.locator('#message')).toContainText('new cycle');
  await expect(page.locator('#run-meta')).toContainText('review cycle 2');
  await expect(page.locator('#history')).toContainText('completed Operations review');
  await actor(page, 'alex');
  await complete(page);

  await actor(page, 'casey');
  await page.getByRole('button', { name: 'Design a workflow', exact: true }).click();
  await page.getByRole('button', { name: 'Add review step' }).click();
  await page.getByLabel('Step name', { exact: true }).fill('Technical check');
  await page.locator('#designer-graph').getByRole('button', { name: /Ready to order/ }).click();
  await page.locator('#step-requires').getByLabel('Technical check').check();
  await page.getByRole('button', { name: `Publish version ${initialVersion + 1}` }).click();
  await expect(page.locator('#message')).toContainText(`Published version ${initialVersion + 1}`);

  await page.getByRole('button', { name: 'Run a request', exact: true }).click();
  await expect(page.locator('#run-meta')).toContainText(`Pinned to version ${initialVersion}`);
  await expect(page.locator('#runner-graph .graph-node')).toHaveCount(4);
  await actor(page, 'morgan'); await complete(page);
  await actor(page, 'sam'); await complete(page);
  await actor(page, 'casey'); await complete(page);
  await expect(page.locator('#run-status')).toHaveText('✓ Request complete');

  await create(page, 'Second sample printer');
  await expect(page.locator('#run-meta')).toContainText(`Pinned to version ${initialVersion + 1}`);
  await expect(page.locator('#runner-graph .graph-node')).toHaveCount(5);
  await complete(page);
  await expect(page.locator('#runner-graph .graph-node[data-status="active"]')).toHaveCount(3);
  await actor(page, 'morgan'); await complete(page);
  await actor(page, 'sam'); await complete(page);
  await expect(page.locator('#runner-graph').getByRole('button', { name: /Ready to order/ })).toHaveAttribute('data-status', 'waiting');
  await actor(page, 'taylor'); await complete(page);
  await actor(page, 'casey'); await complete(page);
  const downloadEvent = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Export request JSON' }).click();
  const download = await downloadEvent;
  const exported = JSON.parse(await readFile(await download.path(), 'utf8'));
  expect(exported.definitionVersion).toBe(initialVersion + 1);
  expect(exported.status).toBe('complete');
  expect(exported.history).toHaveLength(6);
  await page.reload();
  await expect(page.locator('#message')).toContainText('Ready.');
  await page.getByRole('button', { name: 'Run a request', exact: true }).click();
  await page.locator('#runs button').filter({ hasText: 'Sample printer request' }).click();
  await expect(page.locator('#run-meta')).toContainText(`Pinned to version ${initialVersion}`);
  await expect(page.locator('#run-meta')).toContainText('review cycle 2');
  await expect(page.locator('#history')).toContainText('Please include a delivery quote.');
  expect(errors).toEqual([]);
});

test('invalid graph is rejected and other roles can inspect but cannot publish', async ({ page }) => {
  await ready(page);
  await actor(page, 'casey');
  await page.locator('#designer-graph').getByRole('button', { name: /Operations review/ }).click();
  await page.locator('#step-requires').getByLabel('Ready to order').check();
  await page.locator('#publish').click();
  await expect(page.locator('#message')).toContainText('cycle');
  await expect(page.locator('#draft-state')).toContainText('Unpublished draft');
  await actor(page, 'sam');
  await expect(page.locator('#workflow-name')).toBeDisabled();
  await expect(page.locator('#publish')).toBeDisabled();
  await expect(page.locator('#step-name')).toBeDisabled();
});

test('mobile keeps wide graphs inside their scroll region', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await ready(page);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.getByRole('button', { name: 'Run a request', exact: true }).click();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await expect(page.locator('#runner-graph')).toBeVisible();
});

test('user-supplied titles are rendered as text', async ({ page }) => {
  await ready(page);
  await page.getByRole('button', { name: 'Run a request', exact: true }).click();
  const title = '<img src=x onerror="window.injected=true">';
  await create(page, title);
  await expect(page.locator('#run-title')).toHaveText(title);
  expect(await page.evaluate(() => window.injected)).toBeUndefined();
});

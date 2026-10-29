import { drawGraph } from './graph.js';

const $ = selector => document.querySelector(selector);
const state = { actor: null, actors: [], versions: [], draft: null, baseVersion: 1, dirty: false, selectedStep: '', runs: [], run: null, runStep: '', busy: false };
function el(tag, text, className) {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (className) node.className = className;
  return node;
}
function notice(text, error = false) { $('#message').textContent = text; $('#message').classList.toggle('error', error); }
async function api(path, data) {
  const response = await fetch(path, data === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Workbench': '1' }, body: JSON.stringify(data) });
  const result = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(result.detail || (response.status === 401 ? 'The demo session expired. Reload the page to choose an identity.' : `Request failed (${response.status}).`));
  return result;
}
async function task(action) {
  if (state.busy) return;
  state.busy = true;
  $('#workspace').inert = true;
  $('#actor').disabled = true;
  try { await action(); }
  catch (error) { notice(error.message || 'The operation could not finish. Reload before retrying.', true); }
  finally { state.busy = false; $('#workspace').inert = false; $('#actor').disabled = false; }
}
function roleName(role) { return role.charAt(0).toUpperCase() + role.slice(1); }
function show(view) {
  $('#designer').hidden = view !== 'designer';
  $('#runner').hidden = view !== 'runner';
  $('#show-designer').setAttribute('aria-pressed', String(view === 'designer'));
  $('#show-runner').setAttribute('aria-pressed', String(view === 'runner'));
}
$('#show-designer').addEventListener('click', () => show('designer'));
$('#show-runner').addEventListener('click', () => show('runner'));

function controls() {
  const designer = state.actor?.role === 'coordinator';
  $('#workflow-name').disabled = !designer;
  $('#step-fields').disabled = !designer;
  $('#add-step').disabled = !designer || state.draft.steps.length >= 10;
  $('#publish').disabled = !designer || !state.dirty;
  $('#publish').textContent = `Publish version ${state.versions[0].version + 1}`;
  $('#discard').disabled = !state.dirty;
  $('#draft-state').textContent = `${state.dirty ? 'Unpublished draft' : 'Published snapshot'} / based on v${state.baseVersion}`;
  $('#designer-permission').textContent = designer ? 'Coordinator can edit and publish. Draft edits update the preview immediately.' : 'Read-only for this identity. Choose Casey — Coordinator to edit and publish.';
  $('#create-button').disabled = state.actor?.role !== 'requester';
  $('#create-permission').textContent = state.actor?.role === 'requester' ? `New requests start on the current published version (v${state.versions[0].version}).` : 'Choose Alex — Requester to create a request.';
}
function dirty() { state.dirty = true; controls(); $('#definition-json').textContent = JSON.stringify(state.draft, null, 2); }
function drawDesigner() {
  drawGraph($('#designer-graph'), state.draft, null, state.selectedStep, id => { state.selectedStep = id; renderDesigner(); });
}
function renderDesigner() {
  $('#workflow-name').value = state.draft.name;
  $('#version').replaceChildren(...state.versions.map(version => {
    const option = el('option', `Version ${version.version} — ${version.definition.name}`);
    option.value = version.version;
    return option;
  }));
  $('#version').value = state.baseVersion;
  const step = state.draft.steps.find(item => item.id === state.selectedStep) || state.draft.steps[0];
  state.selectedStep = step.id;
  $('#step-id').value = step.id;
  $('#step-name').value = step.name;
  $('#step-role').replaceChildren(...state.actors.map(actor => {
    const option = el('option', roleName(actor.role)); option.value = actor.role; return option;
  }));
  $('#step-role').value = step.role;
  $('#step-question').value = step.question;
  $('#step-requires').replaceChildren(...state.draft.steps.filter(item => item.id !== step.id).map(item => {
    const label = el('label', undefined, 'check');
    const checkbox = el('input'); checkbox.type = 'checkbox'; checkbox.value = item.id; checkbox.checked = step.requires.includes(item.id);
    label.append(checkbox, el('span', item.name || item.id));
    return label;
  }));
  drawDesigner();
  $('#definition-json').textContent = JSON.stringify(state.draft, null, 2);
  controls();
}
function loadVersion(version) {
  state.draft = structuredClone(version.definition);
  state.baseVersion = version.version;
  state.selectedStep = state.draft.steps[0].id;
  state.dirty = false;
  renderDesigner();
}
$('#workflow-name').addEventListener('input', () => { state.draft.name = $('#workflow-name').value; dirty(); });
$('#step-form').addEventListener('submit', event => event.preventDefault());
$('#step-form').addEventListener('input', () => {
  if (state.actor?.role !== 'coordinator') return;
  const step = state.draft.steps.find(item => item.id === state.selectedStep);
  step.name = $('#step-name').value;
  step.role = $('#step-role').value;
  step.question = $('#step-question').value;
  step.requires = [...document.querySelectorAll('#step-requires input:checked')].map(input => input.value);
  dirty(); drawDesigner();
});
$('#version').addEventListener('change', () => {
  if (state.dirty) { $('#version').value = state.baseVersion; notice('Your draft has edits. Publish it or choose Discard draft before loading another version.', true); return; }
  loadVersion(state.versions.find(version => version.version === Number($('#version').value)));
});
$('#discard').addEventListener('click', () => { loadVersion(state.versions[0]); notice('Draft discarded. The latest published snapshot is loaded.'); });
$('#add-step').addEventListener('click', () => {
  if (state.draft.steps.length >= 10) return;
  let number = 1;
  while (state.draft.steps.some(step => step.id === `review-${number}`)) number++;
  const id = `review-${number}`;
  state.draft.steps.push({ id, name: 'Technical review', role: 'technical', requires: [state.draft.steps[0].id], question: 'Has the technical review been completed?' });
  state.selectedStep = id;
  dirty(); renderDesigner();
  notice('Review step added to the draft. Select the final step and include this review in its prerequisites before publishing.');
});
$('#remove-step').addEventListener('click', () => {
  if (state.draft.steps.some(step => step.requires.includes(state.selectedStep))) { notice('Remove this prerequisite from its dependent steps before removing the step itself.', true); return; }
  if (state.draft.steps.length <= 2) { notice('Keep at least two steps.', true); return; }
  state.draft.steps = state.draft.steps.filter(step => step.id !== state.selectedStep);
  state.selectedStep = state.draft.steps[0].id;
  dirty(); renderDesigner();
});
$('#publish').addEventListener('click', () => task(async () => {
  const published = await api('/api/versions', { expectedVersion: state.versions[0].version, definition: state.draft });
  state.versions = await api('/api/versions');
  loadVersion(published);
  notice(`Published version ${published.version}. Existing requests keep their original workflow version.`);
}));

function renderList() {
  $('#runs').replaceChildren(...state.runs.map(run => {
    const button = el('button'); button.type = 'button'; button.setAttribute('aria-pressed', String(run.id === state.run?.id));
    button.append(el('strong', run.title), el('small', `v${run.definitionVersion} / cycle ${run.cycle} / ${run.status}`));
    button.addEventListener('click', () => task(() => loadRun(run.id)));
    return button;
  }));
}
async function loadRun(id) {
  state.run = await api(`/api/runs/${id}`);
  state.runStep = '';
  renderRun(); renderList();
}
function renderRun() {
  const run = state.run;
  $('#run-detail').hidden = !run;
  $('#empty-run').hidden = Boolean(run);
  if (!run) return;
  $('#run-title').textContent = run.title;
  $('#run-meta').textContent = `Pinned to version ${run.definitionVersion} / revision ${run.revision} / review cycle ${run.cycle}`;
  $('#run-status').textContent = run.status === 'complete' ? '✓ Request complete' : 'In progress';
  const preferred = run.definition.steps.find(step => run.steps[step.id] === 'active' && step.role === state.actor.role)
    || run.definition.steps.find(step => run.steps[step.id] === 'active') || run.definition.steps.at(-1);
  if (!state.runStep || !run.definition.steps.some(step => step.id === state.runStep)) state.runStep = preferred.id;
  drawGraph($('#runner-graph'), run.definition, run.steps, state.runStep, id => { state.runStep = id; renderRun(); });
  const step = run.definition.steps.find(item => item.id === state.runStep);
  const active = run.steps[step.id] === 'active';
  const allowed = active && step.role === state.actor.role;
  $('#action-title').textContent = step.name;
  $('#action-help').textContent = allowed ? 'This step is active and assigned to your demo role.'
    : active ? `Assigned to ${roleName(step.role)}. Choose the matching demo person to act.`
    : run.steps[step.id] === 'complete' ? 'This step is complete in the current review cycle.'
    : `Waiting for: ${step.requires.filter(id => run.steps[id] !== 'complete').map(id => run.definition.steps.find(item => item.id === id).name).join(', ')}.`;
  $('#action-fields').disabled = !allowed;
  $('#question-label').hidden = !step.question;
  $('#question-text').textContent = step.question;
  $('#confirmed').checked = false;
  $('#action-comment').value = '';
  $('#send-back').disabled = !allowed || step.requires.length === 0;
  $('#history').replaceChildren(...[...run.history].reverse().map(entry => {
    const row = el('li');
    const stepName = run.definition.steps.find(item => item.id === entry.stepId)?.name;
    row.append(el('strong', `${entry.actor}: ${entry.action === 'created' ? 'created request' : entry.action === 'return' ? `sent back from ${stepName}` : `completed ${stepName}`}`));
    if (entry.comment) row.append(el('p', entry.comment));
    const answer = entry.confirmed === null ? '' : ` / confirmation: ${entry.confirmed ? 'yes' : 'no'}`;
    row.append(el('small', `Cycle ${entry.cycle}${answer} / ${new Date(entry.at).toLocaleString()}`));
    return row;
  }));
}
$('#create-run').addEventListener('submit', event => {
  event.preventDefault();
  task(async () => {
    state.run = await api('/api/runs', { title: $('#request-title').value });
    state.runStep = '';
    state.runs = await api('/api/runs');
    renderList(); renderRun();
    notice(`Request created on version ${state.run.definitionVersion}. Complete intake as Alex to open both review branches.`);
  });
});
async function act(action) {
  if (!state.run) return;
  await task(async () => {
    const result = await api(`/api/runs/${state.run.id}/actions`, {
      expectedRevision: state.run.revision, commandId: crypto.randomUUID(), stepId: state.runStep,
      action, comment: $('#action-comment').value, confirmed: $('#question-label').hidden ? null : $('#confirmed').checked
    });
    state.run = result.run; state.runStep = '';
    state.runs = await api('/api/runs');
    renderRun(); renderList();
    notice(action === 'return' ? 'Request sent back. Intake is open for a new cycle; earlier approvals remain in history.' : 'Step committed. State and history were saved together.');
  });
}
$('#action-form').addEventListener('submit', event => { event.preventDefault(); act('complete'); });
$('#send-back').addEventListener('click', () => act('return'));
$('#reload-run').addEventListener('click', () => task(async () => {
  state.runs = await api('/api/runs');
  if (state.run) await loadRun(state.run.id);
  renderList(); notice('Request and committed history reloaded.');
}));
$('#actor').addEventListener('change', () => task(async () => {
  state.actor = await api('/api/session', { actorId: $('#actor').value });
  if (state.run) await loadRun(state.run.id);
  controls();
  notice(`Acting as ${state.actor.name}. This is a fictional local demo identity.`);
}));
$('#export').addEventListener('click', () => {
  if (!state.run) return;
  const bytes = new Blob([JSON.stringify({ ...state.run, exportedAt: new Date().toISOString(), note: 'Local demonstration history, not a signed or tamper-evident approval record.' }, null, 2)], { type: 'application/json' });
  const url = URL.createObjectURL(bytes);
  const link = el('a'); link.href = url; link.download = `workflow-request-${state.run.id}.json`; link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
});

await task(async () => {
  const session = await api('/api/session');
  state.actors = session.actors;
  state.actor = session.actor || await api('/api/session', { actorId: 'casey' });
  $('#actor').replaceChildren(...state.actors.map(actor => { const option = el('option', actor.name); option.value = actor.id; return option; }));
  $('#actor').value = state.actor.id;
  [state.versions, state.runs] = await Promise.all([api('/api/versions'), api('/api/runs')]);
  loadVersion(state.versions[0]);
  renderList();
  if (state.runs.length) await loadRun(state.runs[0].id);
  notice('Ready. Explore the designer as Casey, then choose Alex to create and run a request.');
});

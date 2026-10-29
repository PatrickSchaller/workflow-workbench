const namespace = 'http://www.w3.org/2000/svg';
function svg(tag, attributes = {}) {
  const node = document.createElementNS(namespace, tag);
  for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, value);
  return node;
}

export function drawGraph(target, definition, states, selected, onSelect) {
  target.replaceChildren();
  const steps = definition.steps;
  const levels = new Map();
  for (let pass = 0; pass < steps.length; pass++) {
    for (const step of steps) {
      if (!levels.has(step.id) && step.requires.every(id => levels.has(id))) {
        levels.set(step.id, step.requires.length ? Math.max(...step.requires.map(id => levels.get(id))) + 1 : 0);
      }
    }
  }
  const unresolvedLevel = Math.max(0, ...levels.values()) + 1;
  for (const step of steps) if (!levels.has(step.id)) levels.set(step.id, unresolvedLevel);
  const groups = new Map();
  for (const step of steps) {
    const level = levels.get(step.id);
    if (!groups.has(level)) groups.set(level, []);
    groups.get(level).push(step);
  }
  const rows = Math.max(1, ...[...groups.values()].map(group => group.length));
  const width = (Math.max(0, ...levels.values()) + 1) * 220 + 8;
  const height = Math.max(240, rows * 100 + 48);
  const positions = new Map();
  for (const [level, group] of groups) group.forEach((step, index) =>
    positions.set(step.id, { x: level * 220 + 24, y: (height - group.length * 100) / 2 + index * 100 + 15 }));
  const canvas = document.createElement('div');
  canvas.className = 'graph-canvas';
  canvas.style.width = `${width}px`;
  canvas.style.height = `${height}px`;
  const connectors = svg('svg', { width, height, viewBox: `0 0 ${width} ${height}`, 'aria-hidden': 'true' });
  const markerId = `${target.id}-arrow`;
  const defs = svg('defs');
  const marker = svg('marker', { id: markerId, markerWidth: 8, markerHeight: 8, refX: 7, refY: 4, orient: 'auto' });
  marker.append(svg('path', { d: 'M0 0 L8 4 L0 8 Z', fill: '#8096ab' }));
  defs.append(marker);
  connectors.append(defs);
  for (const step of steps) {
    const end = positions.get(step.id);
    for (const prerequisite of step.requires) {
      const start = positions.get(prerequisite);
      if (!start) continue;
      const x1 = start.x + 180, y1 = start.y + 35, x2 = end.x - 6, y2 = end.y + 35;
      const bend = Math.max(25, Math.abs(x2 - x1) / 2);
      connectors.append(svg('path', { d: `M${x1} ${y1} C${x1 + bend} ${y1},${x2 - bend} ${y2},${x2} ${y2}`,
        fill: 'none', stroke: '#8096ab', 'stroke-width': 1.6, 'marker-end': `url(#${markerId})` }));
    }
  }
  canvas.append(connectors);
  for (const step of steps) {
    const position = positions.get(step.id);
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'graph-node';
    button.style.left = `${position.x}px`;
    button.style.top = `${position.y}px`;
    button.setAttribute('aria-pressed', String(step.id === selected));
    const state = states?.[step.id];
    if (state) button.dataset.status = state;
    const name = document.createElement('strong');
    name.textContent = step.name || step.id;
    const info = document.createElement('small');
    info.textContent = `${step.role} / ${state ? ({ active: '● Active', waiting: '○ Waiting', complete: '✓ Complete' })[state] : 'Configured'}`;
    button.append(name, info);
    button.addEventListener('click', () => onSelect(step.id));
    canvas.append(button);
  }
  target.append(canvas);
}

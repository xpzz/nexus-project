// Auditoria visual e de acessibilidade das telas, nos temas claro e escuro e em três resoluções.
// Uso: AXE=/caminho/axe-core/axe.min.js PLAYWRIGHT_MODULE=/caminho/playwright/index.mjs node tests/ui/audit.mjs [urlBase]  (PAGES='/a|/b' substitui a lista de páginas)
import { readFileSync } from 'node:fs';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const base = process.argv[2] ?? 'https://localhost:8443';
const axeSource = readFileSync(process.env.AXE ?? 'node_modules/axe-core/axe.min.js', 'utf8');
const pages = (process.env.PAGES ?? '/painel|/parque-ativo|/comparativo|/inventario|/inventario?oper=ConfirmedActive&tipo=notebook&flag=mdm|/governanca|/governanca/funis|/intune|/pendencias|/mam|/byod|/qualidade|/operacao|/configuracao/acesso|/erro').split('|');
const sizes = [[1366, 768], [1920, 1080], [1280, 720]];
const browser = await chromium.launch({ executablePath: '/opt/pw-browsers/chromium', args: ['--ignore-certificate-errors'] });
const results = { pages: 0, violations: [], overflow: [], errors: [], themes: [] };
const expectedBg = { light: 'rgb(245, 246, 248)', dark: 'rgb(16, 19, 24)' };
for (const scheme of ['light', 'dark']) {
  for (const [width, height] of sizes) {
    const ctx = await browser.newContext({ ignoreHTTPSErrors: true, colorScheme: scheme, viewport: { width, height } });
    const page = await ctx.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
    page.on('console', (m) => { if (m.type() === 'error' && !/favicon|Failed to load resource.*(404)/.test(m.text())) errors.push('console: ' + m.text()); });
    page.on('response', (r) => { if (r.status() >= 400 && !r.url().includes('favicon')) errors.push(`HTTP ${r.status()} ${r.url()}`); });
    for (const path of pages) {
      errors.length = 0;
      await page.goto(base + path, { waitUntil: 'load' });
      await page.waitForTimeout(250);
      results.pages++;
      const bg = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
      if (bg !== expectedBg[scheme]) results.themes.push(`${scheme} ${width} ${path}: fundo ${bg}`);
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      if (overflow > 1) results.overflow.push(`${scheme} ${width}x${height} ${path}: rolagem horizontal de ${overflow}px na página`);
      await page.evaluate(axeSource);
      const axe = await page.evaluate(async () => (await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa'] } })).violations.map(v => ({ id: v.id, impact: v.impact, nodes: v.nodes.length, sample: v.nodes[0]?.target?.join(' '), help: v.help })));
      for (const v of axe.filter(v => v.impact === 'serious' || v.impact === 'critical')) results.violations.push(`${scheme} ${width} ${path}: ${v.id} (${v.impact}, ${v.nodes}×) ${v.help} — ${v.sample}`);
      if (errors.length) results.errors.push(`${scheme} ${width} ${path}: ${[...new Set(errors)].join(' | ')}`);
    }
    await ctx.close();
  }
}
await browser.close();
const uniq = (a) => [...new Set(a)];
console.log(`páginas verificadas: ${results.pages}`);
for (const [k, label] of [['themes', 'TEMA'], ['overflow', 'ROLAGEM'], ['errors', 'ERROS'], ['violations', 'ACESSIBILIDADE']]) {
  const list = uniq(results[k]);
  console.log(`${label}: ${list.length}`);
  list.slice(0, 40).forEach((x) => console.log('  - ' + x));
}
process.exit(results.themes.length + results.errors.length ? 1 : 0);

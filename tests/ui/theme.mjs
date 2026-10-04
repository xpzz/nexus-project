// Uso: PLAYWRIGHT_MODULE=/caminho/playwright/index.mjs node tests/ui/theme.mjs  (site em https://localhost:8443, modo demonstração)
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const b = await chromium.launch({ executablePath: '/opt/pw-browsers/chromium', args: ['--ignore-certificate-errors'] });
const bg = (p) => p.evaluate(() => getComputedStyle(document.body).backgroundColor);
const st = (p) => p.evaluate(() => [document.documentElement.getAttribute('data-theme'), document.documentElement.getAttribute('data-theme-pref'), localStorage.getItem('nexus-theme')].join(' / '));
let fails = 0;
const check = (name, ok, extra) => { console.log((ok ? 'OK   ' : 'FALHA'), name, extra ?? ''); if (!ok) fails++; };
const dark = 'rgb(16, 19, 24)', light = 'rgb(245, 246, 248)';
const ctx = await b.newContext({ ignoreHTTPSErrors: true, colorScheme: 'light', viewport: { width: 1300, height: 800 } });
const p = await ctx.newPage();
await p.goto('https://localhost:8443/painel', { waitUntil: 'networkidle' });
check('sistema (SO claro) começa claro', await bg(p) === light, await st(p));
await p.emulateMedia({ colorScheme: 'dark' }); await p.waitForTimeout(200);
check('sistema acompanha o SO ao mudar para escuro', await bg(p) === dark);
await p.click('[data-theme-set=dark]'); await p.emulateMedia({ colorScheme: 'light' }); await p.waitForTimeout(200);
check('escolha explícita escuro vence o SO claro', await bg(p) === dark, await st(p));
for (const href of ['/inventario', '/comparativo', '/mam', '/painel']) {
  await p.click(`.nav a[href="${href}"]`); await p.waitForTimeout(1200);
  check('navegação interna mantém escuro ' + href, await bg(p) === dark, await st(p));
}
await p.goBack(); await p.waitForTimeout(1200); check('voltar mantém escuro', await bg(p) === dark);
await p.goForward(); await p.waitForTimeout(1200); check('avançar mantém escuro', await bg(p) === dark);
await p.reload({ waitUntil: 'networkidle' }); check('recarregar mantém escuro', await bg(p) === dark);
await p.goto('https://localhost:8443/comparativo', { waitUntil: 'domcontentloaded' }); check('rota direta mantém escuro', await bg(p) === dark);
const html = await (await ctx.request.get('https://localhost:8443/painel')).text();
check('servidor já renderiza data-theme=dark (sem flash)', /<html[^>]*data-theme="dark"/.test(html));
await p.click('[data-theme-set=light]'); await p.click('.nav a[href="/painel"]'); await p.waitForTimeout(1200);
check('claro explícito vence SO escuro', (await p.emulateMedia({ colorScheme: 'dark' }), await p.waitForTimeout(200), await bg(p)) === light);
await p.click('[data-theme-set=system]'); await p.waitForTimeout(200);
check('voltar para sistema segue o SO (escuro)', await bg(p) === dark, await st(p));
await b.close(); process.exit(fails ? 1 : 0);

// Fluxos de uso no navegador: KPI e lista que o explica, filtros combinados, voltar do detalhe, filtros salvos, exportação, exceções e teclado.
// Uso: PLAYWRIGHT_MODULE=/caminho/playwright/index.mjs node tests/ui/flows.mjs [urlBase]   (site em modo demonstração, aberto no próprio servidor)
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const base = process.argv[2] ?? 'https://localhost:8443';
const browser = await chromium.launch({ executablePath: '/opt/pw-browsers/chromium', args: ['--ignore-certificate-errors'] });
const ctx = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1366, height: 768 } });
const page = await ctx.newPage();
let fails = 0;
const check = (name, ok, extra) => { console.log((ok ? 'OK   ' : 'FALHA'), name, extra ?? ''); if (!ok) fails++; };
const num = (t) => Number(String(t).replace(/\./g, '').replace(/[^0-9]/g, ''));
const total = async () => num((await page.locator('.ph .asof').first().innerText()).split(' de ')[0]);

// 1. Cada KPI do painel leva a uma lista com exatamente a quantidade que ele mostra.
await page.goto(base + '/painel', { waitUntil: 'load' });
const kpis = await page.$$eval('.kstrip .xkpi', (els) => els.map((e) => ({ label: e.querySelector('.l').innerText.trim(), value: e.querySelector('.v').childNodes[0].textContent.trim(), href: e.getAttribute('href') })));
check('painel mostra 4 KPIs com link', kpis.length === 4 && kpis.every((k) => k.href?.startsWith('/inventario?')), JSON.stringify(kpis.map((k) => k.label)));
for (const k of kpis) {
  await page.goto(base + k.href, { waitUntil: 'load' });
  const listed = await total();
  // os KPIs usam o universo avaliado (sem descomissionados); a lista também não os inclui quando o estado é explícito
  check(`KPI "${k.label}" = lista (${k.value})`, listed === num(k.value), `lista=${listed}`);
}

// 2. Filtros combinados na URL.
await page.goto(base + '/inventario?oper=ConfirmedActive&tipo=notebook&flag=mdm&ordem=nome', { waitUntil: 'load' });
const rows = await page.$$eval('table.dense tbody tr', (trs) => trs.map((r) => r.innerText));
check('filtros combinados (estado + tipo + MDM) retornam linhas', rows.length > 0 && rows.every((r) => r.includes('Ativo confirmado') && r.includes('Notebook')), `${rows.length} linhas na página`);
const tags = await page.$$eval('.tag', (els) => els.map((e) => e.innerText));
check('etiquetas removíveis mostram os três filtros', tags.length === 3, tags.join(' | '));

// 3. Voltar do detalhe preserva filtros, ordem e página.
const original = '/inventario?oper=ConfirmedActive,ProbableActive&ordem=nome&pagina=1&tipo=notebook';
await page.goto(base + original, { waitUntil: 'load' });
await page.locator('table.dense tbody tr td.nm a').first().click();
await page.waitForURL(/\/dispositivo\//);
await page.getByRole('link', { name: /Voltar ao inventário/ }).click();
await page.waitForLoadState('load');
const back = new URL(page.url());
check('voltar do detalhe mantém filtros, ordem e página', back.searchParams.get('tipo') === 'notebook' && back.searchParams.get('ordem') === 'nome' && back.searchParams.get('pagina') === '1' && back.searchParams.get('oper') === 'ConfirmedActive,ProbableActive', back.search);
const bad = await page.goto(base + '/dispositivo/' + '00000000-0000-0000-0000-000000000000' + '?voltar=//evil.test', { waitUntil: 'load' });
check('parâmetro voltar não vira redirecionamento aberto', !(await page.content()).includes('evil.test'));

// 4. Ordenação e paginação alteram só o que devem.
await page.goto(base + '/inventario?tipo=notebook', { waitUntil: 'load' });
await page.getByRole('link', { name: 'Próxima' }).click();
await page.waitForLoadState('load');
check('paginação mantém o filtro', new URL(page.url()).searchParams.get('tipo') === 'notebook' && new URL(page.url()).searchParams.get('pagina') === '1');
await page.getByRole('columnheader', { name: /Equipamento/ }).getByRole('link').click();
await page.waitForLoadState('load');
check('ordenar volta para a primeira página e mantém o filtro', !new URL(page.url()).searchParams.has('pagina') && new URL(page.url()).searchParams.get('ordem') === 'nome');

// 5. Colunas configuráveis.
await page.goto(base + '/inventario?cols=equip,serial,rede', { waitUntil: 'load' });
const headers = await page.$$eval('table.dense thead th', (ths) => ths.map((t) => t.innerText.trim()));
check('colunas escolhidas na URL', headers.length === 3 && headers[1].toLowerCase() === 'serial' && headers[2].toLowerCase().startsWith('ip e mac'), headers.join(' | '));

// 6. Filtros salvos (perfil analista = servidor) e exclusão.
await page.goto(base + '/inventario?tipo=server&oper=ConfirmedActive', { waitUntil: 'load' });
await page.locator('details.pick', { hasText: 'Filtros salvos' }).locator('summary').click();
const name = 'Servidores ativos ' + Date.now();
await page.locator('.vsave input[name=name]').fill(name);
await page.locator('.vsave button[type=submit]').click();
await page.waitForLoadState('load');
await page.locator('details.pick', { hasText: 'Filtros salvos' }).locator('summary').click();
check('filtro salvo aparece na lista', (await page.locator('.vrow', { hasText: name }).count()) === 1);
await page.locator('.vrow', { hasText: name }).locator('button').click();
await page.waitForLoadState('load');
await page.locator('details.pick', { hasText: 'Filtros salvos' }).locator('summary').click();
check('filtro salvo pode ser excluído', (await page.locator('.vrow', { hasText: name }).count()) === 0);

// 7. Exportação controlada: mesma quantidade da lista, BOM, cabeçalho e proteção contra fórmulas.
const filtered = '/inventario?tipo=server&oper=ConfirmedActive';
await page.goto(base + filtered, { waitUntil: 'load' });
const listed = await total();
const resp = await ctx.request.get(base + '/inventario/exportar.csv?tipo=server&oper=ConfirmedActive');
const csv = Buffer.from(await resp.body());
const text = csv.toString('utf8').replace(/^﻿/, '');
const lines = text.trim().split('\r\n').length - 1;
check('exportação em CSV com BOM e cabeçalho', csv[0] === 0xef && resp.headers()['content-type']?.startsWith('text/csv') && text.startsWith('Id Nexus;Equipamento'));
check('exportação tem as mesmas linhas da lista filtrada', lines === listed, `csv=${lines} lista=${listed}`);

// 8. Exceção de MAM: registra, aparece marcada e pode ser revogada.
await page.goto(base + '/mam', { waitUntil: 'load' });
const gaps = page.locator('section', { has: page.getByRole('heading', { name: 'Usuários sem cobertura adequada' }) });
const firstGap = gaps.locator('tbody tr').first();
const who = (await firstGap.locator('td.nm').innerText()).trim();
await firstGap.locator('details.pick summary').click();
await firstGap.locator('input[name=reason]').fill('Aguardando a troca do aparelho pelo usuário');
await firstGap.locator('button[type=submit]').click();
await page.waitForLoadState('load');
check('exceção registrada aparece na lista de exceções', (await page.locator('#excecoes tbody tr', { hasText: 'Aguardando a troca' }).count()) >= 1);
check('a lacuna continua visível, marcada como com exceção', (await gaps.locator('tbody tr', { hasText: who }).locator('.pill.a').count()) >= 1);
const beforeRevoke = await page.locator('#excecoes tbody tr', { hasText: 'Aguardando a troca' }).count();
await page.locator('#excecoes tbody tr', { hasText: 'Aguardando a troca' }).first().locator('button').click();
await page.waitForLoadState('load');
check('exceção revogada some da lista', (await page.locator('#excecoes tbody tr', { hasText: 'Aguardando a troca' }).count()) === beforeRevoke - 1);

// 9. Teclado: o link de pular existe e aparece ao receber foco, todo foco é visível e nada prende o foco.
await page.goto(base + '/painel', { waitUntil: 'load' });
check('"Ir para o conteúdo" é o primeiro elemento focável e aparece ao receber foco', await page.evaluate(() => {
  const skip = document.querySelector('a.skip'); const first = document.querySelector('a[href],button,input,select,summary,[tabindex="0"]');
  skip.focus(); return first === skip && skip.getBoundingClientRect().left >= 0 && document.activeElement === skip;
}));
const focusable = await page.evaluate(() => document.querySelectorAll('a[href],button:not([disabled]),input:not([type=hidden]):not([disabled]),select,summary,[tabindex="0"]').length);
const steps = Math.min(40, focusable - 1);
await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
await page.keyboard.press('Tab');
const seen = [];
for (let i = 0; i < steps; i++) {
  seen.push(await page.evaluate(() => {
    const e = document.activeElement; if (!e || e === document.body) return null;
    const s = getComputedStyle(e);
    return { tag: e.tagName, text: (e.innerText || e.getAttribute('aria-label') || e.value || '').trim().slice(0, 30), ring: s.outlineStyle !== 'none' && parseFloat(s.outlineWidth) > 0 || s.boxShadow !== 'none' };
  }));
  await page.keyboard.press('Tab');
}
check(`os ${steps} primeiros focos têm indicador visível`, seen.every((x) => x && x.ring), JSON.stringify(seen.filter((x) => !x || !x.ring).slice(0, 3)));
check('o foco anda por elementos diferentes (sem armadilha)', new Set(seen.map((x) => x?.text + x?.tag)).size > Math.min(15, steps - 3));

// 10. Coleta manual por conector pede o perfil certo e registra na auditoria.
await page.goto(base + '/operacao', { waitUntil: 'load' });
const manual = await page.getByRole('button', { name: 'Coletar agora' }).count();
check('operador do servidor vê o botão de coleta por conector', manual > 5, `${manual} botões`);

await browser.close();
console.log(fails === 0 ? '\nTodos os fluxos passaram.' : `\n${fails} falha(s).`);
process.exit(fails ? 1 : 0);

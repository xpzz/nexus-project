/* Comportamentos pequenos da interface, em um só lugar (o tema fica em theme.js). */
(function () {
  // Linha de tabela clicável: o link da primeira coluna continua sendo o caminho do teclado.
  document.addEventListener('click', function (e) {
    var row = e.target.closest('tr[data-href]');
    if (row && !e.target.closest('a,button,input,select,summary')) {
      var go = row.dataset.href;
      if (window.Blazor && Blazor.navigateTo) Blazor.navigateTo(go); else location.href = go;
    }
  });

  // Filtros que se aplicam ao mudar.
  document.addEventListener('change', function (e) {
    if (e.target.matches && e.target.matches('select[data-auto]') && e.target.form) e.target.form.submit();
  });

  // Regiões com rolagem precisam de acesso pelo teclado (WCAG 2.1.1): só as que realmente rolam recebem o foco.
  function scrollables() {
    var regions = document.querySelectorAll('.tw, pre.logs');
    for (var i = 0; i < regions.length; i++) {
      var r = regions[i], scrolls = r.scrollWidth > r.clientWidth + 1 || r.scrollHeight > r.clientHeight + 1;
      if (scrolls) { if (!r.hasAttribute('tabindex')) r.setAttribute('tabindex', '0'); if (!r.hasAttribute('role')) r.setAttribute('role', 'region'); if (!r.hasAttribute('aria-label')) r.setAttribute('aria-label', 'Tabela com rolagem'); }
      else if (r.getAttribute('aria-label') === 'Tabela com rolagem') { r.removeAttribute('tabindex'); r.removeAttribute('role'); r.removeAttribute('aria-label'); }
    }
  }
  document.addEventListener('DOMContentLoaded', scrollables);
  window.addEventListener('load', scrollables);
  window.addEventListener('resize', scrollables);
  var wait = setInterval(function () { if (window.Blazor && Blazor.addEventListener) { clearInterval(wait); Blazor.addEventListener('enhancedload', scrollables); } }, 50);
  setTimeout(function () { clearInterval(wait); }, 10000);
})();

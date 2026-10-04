/* Azul Nexus: única fonte de verdade do tema.
   Preferência: "system" (padrão), "light" ou "dark", em localStorage e num cookie (o servidor já renderiza o <html> certo).
   Por que existe um observador: a navegação interna do Blazor sincroniza os atributos do <html> com o HTML do servidor
   e apagaria o data-theme; aqui o atributo é sempre restaurado antes da próxima pintura. */
(function () {
  var KEY = 'nexus-theme', root = document.documentElement;
  var valid = { system: 1, light: 1, dark: 1 };

  function read() {
    try { var v = localStorage.getItem(KEY); if (valid[v]) return v; } catch (e) { }
    var m = document.cookie.match(/(?:^|; )nexus-theme=(system|light|dark)/);
    return m ? m[1] : 'system';
  }

  function write(pref) {
    try { localStorage.setItem(KEY, pref); } catch (e) { }
    document.cookie = KEY + '=' + pref + '; path=/; max-age=31536000; samesite=lax';
  }

  function set(name, value) {
    // Só escreve quando muda: setAttribute com o mesmo valor também gera uma mutação e o observador entraria em laço.
    if (value === null) { if (root.hasAttribute(name)) root.removeAttribute(name); } else if (root.getAttribute(name) !== value) { root.setAttribute(name, value); }
  }

  function apply() {
    var pref = read();
    set('data-theme', pref === 'system' ? null : pref);
    set('data-theme-pref', pref);
    var buttons = document.querySelectorAll('[data-theme-set]');
    for (var i = 0; i < buttons.length; i++) {
      var pressed = buttons[i].getAttribute('data-theme-set') === pref ? 'true' : 'false';
      if (buttons[i].getAttribute('aria-pressed') !== pressed) buttons[i].setAttribute('aria-pressed', pressed);
    }
  }

  apply();
  new MutationObserver(apply).observe(root, { attributes: true, attributeFilter: ['data-theme', 'data-theme-pref'] });
  document.addEventListener('DOMContentLoaded', apply);
  window.addEventListener('pageshow', apply);
  window.addEventListener('storage', function (e) { if (e.key === KEY) apply(); });
  document.addEventListener('click', function (e) {
    var b = e.target.closest && e.target.closest('[data-theme-set]');
    if (!b) return;
    var pref = b.getAttribute('data-theme-set');
    if (valid[pref]) { write(pref); apply(); }
  });
  var wait = setInterval(function () {
    if (window.Blazor && Blazor.addEventListener) {
      clearInterval(wait);
      Blazor.addEventListener('enhancedload', apply);
    }
  }, 50);
  setTimeout(function () { clearInterval(wait); }, 10000);
})();

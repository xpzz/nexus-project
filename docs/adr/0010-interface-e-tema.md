# ADR-0010: interface orientada à investigação e tema único

Status: aceito.

## Tema

**Causa do defeito:** a navegação interna (enhanced navigation) do Blazor sincroniza os atributos do `<html>` com o HTML que vem do servidor. Esse HTML não carregava `data-theme`, então o atributo sumia a cada troca de página, enquanto o `localStorage` seguia com a escolha. Recarregar corrigia.

**Solução:** uma única fonte de verdade.

- Preferência `system`, `light` ou `dark`, gravada em `localStorage` e em cookie.
- `theme.js` roda no `<head>`, aplica antes da primeira pintura, restaura o atributo (observador e eventos `enhancedload` e `pageshow`) e escreve só quando o valor muda (um `setAttribute` com o mesmo valor também dispara o observador).
- O servidor lê o cookie e já renderiza `data-theme`: sem flash e sem depender de JavaScript.
- Tokens semânticos em um bloco só, com `light-dark()`; `color-scheme` decide. Tabelas, gráficos, menus, filtros e estados de erro usam os mesmos tokens.
- No modo sistema, o tema segue o SO mesmo depois de carregada a página. A escolha explícita vence o SO.

## Estrutura

Seis áreas na navegação lateral persistente: Visão geral, Parque ativo, Inventário, Governança Microsoft, MAM e BYOD, Qualidade dos dados e Operações. Tabelas são o instrumento principal: cabeçalho fixo, colunas configuráveis, filtros combináveis, tudo na URL.

## Regras de leitura

- Estado nunca depende só de cor: ícone e texto acompanham.
- Cada KPI traz definição, população, unidade, janela, atualização e a lista que o explica.
- Zero ocorrências, sem dados, coleta com falha e funcionalidade não disponível são quatro situações diferentes.
- Tendências só com histórico real e comparável: nenhuma semana anterior é inventada.
- Política atribuída não é proteção comprovada: cinco níveis de evidência por controle.

## Verificação

`tests/ui/theme.mjs`, `tests/ui/audit.mjs` (axe-core em claro e escuro, três resoluções) e `tests/ui/flows.mjs` rodam contra o site em modo demonstração.

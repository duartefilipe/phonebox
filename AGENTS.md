# Instruções para agentes neste repositório

## Não rodar os testes de UI (Playwright) sem pedido explícito

Os testes de `tools/ui-tests` (Playwright) são pesados: sobem um servidor de demonstração, abrem o Chromium e
levam vários minutos, o que atrapalha a máquina de trabalho.

**Não rode `npx playwright test` (nem `npm test` dentro de `tools/ui-tests`) por conta própria.** Isso vale também
para um único arquivo `.spec.js`. Rode só quando o usuário pedir, com estas palavras ou equivalentes: "rode os
testes de UI", "roda o playwright", "pode testar a interface".

Em vez disso, para validar mudanças de painel, configurações ou tradução:

- `dotnet test agent/PhoneBox.Agent.Tests` — rápido o suficiente e cobre o agente (configurações, normalização,
  API). Pode rodar sem pedir.
- `node --check <arquivo.js>` para erro de sintaxe.
- Leitura do código e dos dicionários (`node -e` carregando `i18n.js` e `locale-existing.js`) para conferir
  traduções, sem abrir navegador.

Quando uma mudança **precisaria** de teste de UI para ter certeza, escreva ou ajuste o `.spec.js` normalmente e
diga ao usuário que ficou pendente de rodar — não rode.

## Idiomas

A interface tem só três: inglês (`en`), espanhol (`es`) e português do Brasil (`pt-BR`). Não adicione outros.
Não há suporte a escrita da direita para a esquerda (RTL); não reintroduza regras `[dir="rtl"]`.

As listas de idiomas do Android, do `.csproj` e do `Config.cs` são geradas a partir de `i18n.js` por
`python tools/sync-locales.py`: edite `i18n.js` e rode o script, em vez de editar as listas à mão.

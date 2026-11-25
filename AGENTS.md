# Instruções para agentes neste repositório

## Testes de UI (Playwright): só antes do push para `main`

Os testes de `tools/ui-tests` são pesados: sobem um servidor de demonstração, abrem o Chromium e levam uns 5
minutos, o que atrapalha a máquina de trabalho. Mas são eles que o CI roda, então pular antes do push significa
quebrar a pipeline.

A regra, então, é de momento, não de permissão:

- **Durante o trabalho** (explorar, editar, validar uma mudança): **não** rode `npx playwright test`, nem para um
  único `.spec.js`. Use as checagens rápidas abaixo.
- **Antes de `git push` para `main`**: rode a **suíte completa** (`cd tools/ui-tests; npx playwright test`) junto
  com `python tools/check-release.py` e `dotnet test agent/PhoneBox.Agent.Tests`. Só empurre com os três verdes.
  Se algum falhar, conserte ou avise antes de empurrar — não empurre "para ver o que o CI diz".
- O usuário também pode pedir a qualquer momento ("rode os testes de UI").

Checagens rápidas, para usar enquanto se trabalha:

- `dotnet test agent/PhoneBox.Agent.Tests` — ~2 min, cobre o agente (configurações, normalização, API).
- `python tools/check-release.py` — segundos; confere versões, contagem de widgets/templates no README e na página
  de demonstração, e as listas de idiomas.
- `node --check <arquivo.js>` para erro de sintaxe.
- Leitura dos dicionários (`node -e` carregando `i18n.js` e `locale-existing.js`) para conferir traduções, sem
  abrir navegador.

Ao adicionar widget ou template, lembre que a contagem no `README.md` é verificada pelo `check-release.py`.

## Idiomas

A interface tem só três: inglês (`en`), espanhol (`es`) e português do Brasil (`pt-BR`). Não adicione outros.
Não há suporte a escrita da direita para a esquerda (RTL); não reintroduza regras `[dir="rtl"]`.

As listas de idiomas do Android, do `.csproj` e do `Config.cs` são geradas a partir de `i18n.js` por
`python tools/sync-locales.py`: edite `i18n.js` e rode o script, em vez de editar as listas à mão.

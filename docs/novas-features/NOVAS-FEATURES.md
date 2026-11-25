# Novas features: estado atual e próximos passos

Documento de passagem de trabalho: o que já foi feito neste fork e o que falta, para outra pessoa ou outro agente
continuar daqui. Atualize as caixas e a seção "Estado atual" conforme avançar.

> Este repositório é público. Não escreva aqui links secretos de agenda, tokens, senhas, e-mails nem IDs de agenda.

## Contexto

Fork do PhoneBox mantido em `duartefilipe/phonebox`, com histórico próprio (o primeiro commit é um "initial commit"
com o código já alterado). O projeto original fica como remote `upstream` (`ersinkoc/phonebox`) só para trazer
novidades.

Uso atual: um celular antigo (Xiaomi Mi 5X, Android 11 / LineageOS, tela 1920×1080, WebView do Google 159) preso na
mesa como painel do PC Windows, conectado por cabo USB. O agente roda no Windows a partir de uma cópia publicada,
iniciado pela pasta Inicializar do Windows.

## Já feito

| Feature | Onde está no código |
|---|---|
| **Página "PC desligado"**: uma página marcada com `offline: true` sai da troca normal de páginas e cobre o painel quando o PC fica inacessível (tempo em Geral › Tela, `unreachableNightDelayMin`). No modo hub, pode valer para todos os PCs ou para um PC (`offlineSource`). | `android/.../www/main.js` (`AWAY`, `setAway`, `swipePages`, `reconcileUnreachableNight`), `index.html` (`#away`), `style.css` (`#away`), `agent/PhoneBox.Agent/Config.cs` (`PageConfig.Offline`), `settings.html` (chave "PC desligado" no editor de páginas) |
| **Widget "Relógio pixel (LED)"** (`pixelclock`): matriz de LEDs com hora, data, clima com ícone, próximo evento rolando e jardim; estilo, densidade, alinhamento, partes e cores configuráveis. | `android/.../www/widgets/pixel.js`, entrada em `agent/PhoneBox.Agent/widgets.json`, traduções nos `locale-*.js` |
| **Agenda guardada no celular**: a última agenda boa fica no `localStorage` e aparece com o PC desligado; uma resposta de um agente que ainda não leu as agendas (`updated = 0`) não apaga a agenda e é repetida em 5 s. | `main.js` (`loadCalendar`) |
| **Aviso imediato de PC desligando** para o celular. | commit `7e6293d` |
| **Modo hub** (agente num servidor, Docker/Portainer, vários PCs e celulares; configurações com senha na porta 8767). | `agent/PhoneBox.Agent/Hub.cs`, `docker/`, `Dockerfile` |
| **Imagem do hub construída só no servidor**: sem workflow de publicação no GitHub; o compose constrói `phonebox-hub:local` do repositório. | `docker/docker-compose.yml`, `docker/README.md` |
| **Sincronização com o upstream** até `321c603` (tablet com mais linhas, Grok, contas extras do Claude Code, barra do Discord, tamanho de ícone por atalho, etc.). | commit `5b71c2a` |

## Pendente

### 1. Agenda por link iCal secreto (decidido)

Decisão: trocar a "ponte da agenda" (ver abaixo) pelo **endereço secreto no formato iCal** de cada agenda do Google,
cadastrado direto no PhoneBox. O agente busca o link a cada 10 minutos (`CalendarMonitor.cs`), sem login e sem
vencimento.

- [ ] O usuário copia, no Google Agenda (Configurações › a agenda › Integrar agenda), o **Endereço secreto no formato
      iCal** de cada agenda e cola em **Geral › Agendas** do PhoneBox (no hub, ou no agente do Windows enquanto o hub não
      sobe). **Não** pedir nem registrar os links no chat ou em arquivos do repositório.
- [ ] Agenda compartilhada: o endereço secreto só aparece com permissão de "fazer alterações e gerenciar
      compartilhamento"; sem ela, quem é dono da agenda precisa passar o link.
- [ ] Agenda assinada de fonte externa (`…@import.calendar.google.com`): o Google não mostra endereço secreto; usar o
      link ICS original da fonte.
- [ ] Depois de conferir que os eventos aparecem: remover do PhoneBox as agendas antigas que apontam para
      `http://127.0.0.1:8770/cal/N.ics`, apagar o atalho **"PhoneBox Agenda"** da pasta Inicializar do Windows e encerrar
      o processo `pythonw` da ponte. Sugerir ao usuário revogar o acesso do app em myaccount.google.com/permissions.

### 2. Subir o hub no Portainer

- [ ] Stack em **Portainer › Stacks › Add stack › Repository**, compose `docker/docker-compose.yml`, variável
      `PHONEBOX_PASSWORD` obrigatória (passo a passo em `docker/README.md`).
- [ ] No agente do Windows: ligar **Geral › Wi-Fi e Tailscale**, reiniciar, copiar o link de pareamento e adicionar o PC
      no hub em **Geral › Computadores**.
- [ ] Levar o layout: **Pages › Exportar** no agente do Windows e **Importar** no hub (Home, página "PC desligado",
      relógio pixel).
- [ ] Parear o celular com o hub pelo QR code (**Geral › Wi-Fi e Tailscale › Parear um celular por Wi-Fi**).
- [ ] Cadastrar as agendas (item 1) no hub, não no Windows, para a agenda seguir atualizando com o PC desligado.
- [ ] No celular, ligar o painel como tela inicial (opções de quiosque) para o app abrir sozinho sem o cabo de dados.
- [ ] Apagar o pacote antigo `phonebox-hub` do GitHub Packages (feito pelo usuário; o token local não tem
      `read:packages`).

Não funcionam através do hub (ver `docker/README.md`): trackpad, teclas e atalhos que rodam comandos no PC, ditado,
console de apps e iniciar jogos da Steam.

### 3. Outras ideias levantadas

- [ ] **Alexa**: sem integração. Caminho sugerido: Home Assistant (integração Alexa) + widget de Home Assistant do
      PhoneBox, e rotinas da Alexa chamando os webhooks do PhoneBox via um intermediário. Adiado pelo usuário.
- [ ] Tela "PC desligado" mais clara: hoje o brilho segue o da tela noturna (`night.brightness`).

## A "ponte da agenda" (a ser aposentada)

Programa Python fora deste repositório que usava o OAuth (somente leitura) de um app do Google Cloud em modo
"Testando" e servia cada agenda como ICS em `http://127.0.0.1:8770/cal/N.ics` para o agente do Windows. Problemas
que motivaram a troca pelo link iCal: o login vence a cada 7 dias com o app em "Testando", e ela só roda com o PC
ligado. Se for mantida em vez do link iCal, precisaria publicar o app no Google Cloud e rodá-la como container ao lado
do hub.

## Como compilar, instalar e testar

```powershell
# agente (feche o PhoneBox antes: os arquivos ficam em uso)
dotnet publish agent/PhoneBox.Agent -c Release -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained false -o <pasta-do-agente-publicado>

# testes do agente (~2700; um ou outro de tempo pode falhar sob carga e passa ao repetir sozinho)
dotnet test agent/PhoneBox.Agent.Tests

# app Android (JDK 17, Android SDK 35; android/local.properties com sdk.dir)
cd android; .\gradlew.bat assembleDebug
adb install -r app/build/outputs/apk/debug/app-debug.apk

# imagem do hub
$env:PHONEBOX_PASSWORD='teste'; docker compose -f docker/docker-compose.yml build
```

Para testar a página "PC desligado" de verdade: feche o agente, espere o tempo configurado (2 min) e capture a tela do
celular.

### Armadilhas conhecidas

- Ao matar o processo do agente, o servidor do `adb` que ele iniciou costuma travar: finalize também o processo `adb`
  e rode `adb start-server` antes de usar o `adb` de novo. Logo depois de reiniciar o agente, um `adb install` pode
  falhar com "protocol fault"; repetir após alguns segundos resolve.
- No PowerShell, `adb exec-out screencap -p > arquivo.png` corrompe a imagem (o redirecionamento grava texto). Use o
  Git Bash para capturar a tela.
- A WebView de sistema do LineageOS (Chromium 88) não entende as unidades `cq*` (container queries): capas e textos
  dos widgets somem ou se sobrepõem. O celular precisa da WebView do Google (Play Store) selecionada com
  `adb shell cmd webviewupdate set-webview-implementation com.google.android.webview`.
- O preview das configurações (`/panel/` com `PREVIEW`) mostra a página "PC desligado" como página comum; no painel
  real ela fica na camada `#away`.
- Ao trazer novidades do upstream: os históricos não têm base comum. Aplique `git diff <base-upstream> upstream/main`
  com `git apply -3` por cima do `main` deste repositório e resolva os conflitos mantendo o modo hub, a página
  "PC desligado", os links para `duartefilipe` e o changelog sem links de contribuidores. Nunca force push.

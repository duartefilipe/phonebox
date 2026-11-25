# PhoneBox hub (Docker / Portainer)

O hub é o agente do PhoneBox rodando num servidor da casa. Os celulares se conectam a ele pelo Wi-Fi, e ele lê os
dados de vários PCs, cada um com o agente normal instalado.

```
PC 1 (agente) ─┐
PC 2 (agente) ─┼──►  Hub (Docker)  ◄──►  Celulares (Wi-Fi; o cabo só carrega)
```

## 1. Subir o hub no Portainer

**Stacks › Add stack**, cole o conteúdo de [`docker-compose.yml`](docker-compose.yml) e troque `PHONEBOX_PASSWORD`.

- Use `network_mode: host`. Assim o QR code de pareamento mostra o IP real do servidor.
- Portas: **8766** para os celulares e **8767** para as configurações (`https://IP-DO-SERVIDOR:8767`).
- O certificado é próprio, então o navegador avisa na primeira vez. Aceite e entre com a senha.
- Sem `PHONEBOX_PASSWORD`, as configurações não abrem pela rede.

Se a imagem `ghcr.io/duartefilipe/phonebox-hub` estiver privada, deixe o pacote público no GitHub (Packages › phonebox-hub ›
Package settings) ou use a linha `build:` do compose para construir direto do repositório.

## 2. Adicionar os PCs

Em cada PC, com o agente instalado:

1. **Geral › Wi-Fi e Tailscale**: ligue a opção, salve e reinicie o agente.
2. **Parear um celular por Wi-Fi**: copie o link de pareamento.

No hub, em **Geral › Computadores**, cole o link e clique em **Adicionar computador**.

## 3. Conectar os celulares

No hub, em **Geral › Wi-Fi e Tailscale › Parear um celular por Wi-Fi**, escaneie o QR code com o app.

## 4. Escolher o PC de cada página e widget

- **Página:** no editor de páginas, escolha o **Computador**. Sem escolha, a página usa o primeiro.
- **Widget:** nas opções do widget, escolha outro computador. "Igual à página" segue a página.
- **PC desligado:**
  - Uma página marcada como **PC desligado**, com "todos os computadores desligados", cobre o painel quando nenhum PC responde.
  - Uma página marcada para "**PC X desligado**" substitui as páginas do PC X enquanto ele estiver desligado.
  - Widgets de um PC desligado, sem página própria, aparecem esmaecidos com o aviso.

## O que passa pelo hub

Métricas, mídia, volume, processos, OBS, Discord, apps e sessões de IA de cada PC chegam pelo hub. Casa (Home Assistant),
clima, agenda, feeds, câmeras, fotos, atalhos e regras ficam no próprio hub.

Ainda não funcionam através do hub: trackpad, teclas e atalhos que rodam comandos no PC, ditado, console de apps e
iniciar jogos da Steam. Esses recursos conferem o widget na configuração do próprio PC.

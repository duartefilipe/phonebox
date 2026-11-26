# PhoneBox hub (Docker / Portainer)

O hub é o agente do PhoneBox rodando num servidor da casa. Os celulares se conectam a ele pelo Wi-Fi, e ele lê os
dados de vários PCs, cada um com o agente normal instalado.

```
PC 1 (agente) ─┐
PC 2 (agente) ─┼──►  Hub (Docker)  ◄──►  Celulares (Wi-Fi; o cabo só carrega)
```

## 1. Subir o hub no Portainer

A imagem é construída no próprio servidor, a partir do código do repositório. Nenhuma imagem é publicada ou baixada de
um registro (o GitHub não constrói nada).

**Stacks › Add stack › Repository**:

| Campo | Valor |
|---|---|
| Repository URL | `https://github.com/duartefilipe/phonebox` |
| Repository reference | `refs/heads/main` |
| Compose path | `docker/docker-compose.yml` |
| Environment variables | `PHONEBOX_PASSWORD` = a senha das configurações (obrigatória; sem ela a stack não sobe) |

Clique em **Deploy the stack**. A primeira construção leva alguns minutos (baixa o .NET e compila o agente). Para
atualizar depois de um `git push`, abra a stack e use **Pull and redeploy**.

Com o **Web editor** em vez de Repository, cole o compose e troque `context: ..` pelo endereço do repositório, como no
comentário do arquivo.

- Use `network_mode: host`. Assim o QR code de pareamento mostra o IP real do servidor.
- Portas: **8766** para os celulares e **8767** para as configurações (`https://IP-DO-SERVIDOR:8767`).
- O certificado é próprio, então o navegador avisa na primeira vez. Aceite e entre com a senha.
- Sem `PHONEBOX_PASSWORD`, as configurações não abrem pela rede.

Para construir à mão no servidor, sem o Portainer: `docker compose -f docker/docker-compose.yml up -d --build` na pasta do
repositório.

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

## 5. Um só layout no hub e nos PCs

Ligado por padrão em **Geral › Computadores › "Manter um só layout no hub e nos computadores"**. As páginas ficam
iguais em todo lugar: edite no hub ou nas configurações do próprio PC e, em segundos, o outro lado acompanha. Alteradas
nos dois ao mesmo tempo, valem as páginas do hub.

- Viajam só as **páginas** e a **página inicial**. Agendas, atalhos, câmeras, chaves de Wi-Fi, Home Assistant e tudo o
  mais continuam de cada máquina.
- Um PC desligado é atualizado quando voltar a responder.
- Desligue a opção para cada máquina ter o seu próprio layout, como antes.

## Celular com cabo ligado num PC

Com o hub, o cabo pode servir só de energia. O hub diz aos celulares que o cabo **não** vence (a opção geral "o cabo
vence" do hub é ignorada); para um celular específico usar o PC do cabo, marque **Sim** para ele em **Geral ›
Celulares**, com a opção geral desligada. No PC, desligue **Geral › Tela › Assumir o celular pelo cabo**.

Prefira um carregador de tomada: a maioria dos PCs corta a energia da USB ao desligar ou dormir, e é justamente
quando a página "PC desligado" fica acesa a noite toda. Sem energia, a bateria acaba e o celular desliga.

## O que passa pelo hub

Métricas, mídia, volume, processos, OBS, Discord, apps e sessões de IA de cada PC chegam pelo hub — inclusive na
prévia ao vivo do editor de páginas do hub, que usa o mesmo caminho do celular. Casa (Home Assistant),
clima, agenda, feeds, câmeras, fotos, atalhos e regras ficam no próprio hub.

Ainda não funcionam através do hub: trackpad, teclas e atalhos que rodam comandos no PC, ditado, console de apps e
iniciar jogos da Steam. Esses recursos conferem o widget na configuração do próprio PC.

# ULTRAKILL CO-OP: BLOODLINK

Mod cooperativo para ULTRAKILL com BepInEx 5, LiteNetLib e integração com Unity Relay.

**Versão incluída: 0.112.0 — protocolo 56.** Todos os participantes devem usar a mesma versão.

## Conteúdo do repositório

- `Core/`, `Networking/`, `Combat/`, `Enemies/`, `Levels/`, `Players/`, `UI/`: código-fonte do mod.
- `Dependencies/`: dependências usadas pela integração Relay e avisos de terceiros.
- `Tests/`: verificações de protocolo, sincronização e transporte local.
- `tools/Build-Bloodlink.ps1`: compila e atualiza a pasta `release/`.
- `tools/Install-Bloodlink.ps1`: instala uma compilação local no jogo, com backup.
- `tools/doorstop_config.ini`: configuração do carregador BepInEx.
- `release/`: pacote já compilado para distribuir, incluindo configuração de carregamento e DLLs do mod.

Os arquivos do jogo e as pastas de configuração, saves, logs e cache não fazem parte deste repositório. As referências a `Assembly-CSharp`, `UnityEngine` e BepInEx são resolvidas na instalação local do desenvolvedor.

## Instalar o mod compilado

1. Instale BepInEx 5 x64 no ULTRAKILL.
2. Feche o jogo.
3. Copie todo o conteúdo de `release/` para a pasta onde está `ULTRAKILL.exe`, mesclando as pastas.
4. Confira `HideManagerGameObject = true` em `BepInEx/config/BepInEx.cfg`.
5. Abra o jogo e use MULTIPLAYER ou F7.

Copie também `doorstop_config.ini` e `winhttp.dll`, que ficam na raiz de `release/`. Sem o arquivo de configuração, o jogo pode iniciar sem carregar o BepInEx.

## Compilar

Requisitos: Windows, .NET SDK 8 ou posterior, ULTRAKILL instalado e BepInEx 5 x64 na pasta do jogo. O projeto do plugin usa `netstandard2.1`; as verificações usam .NET 8.

Na raiz do repositório:

```powershell
.\tools\Build-Bloodlink.ps1
```

Se o jogo estiver em outro local:

```powershell
.\tools\Build-Bloodlink.ps1 -GameDirectory 'D:\SteamLibrary\steamapps\common\ULTRAKILL'
```

Também é possível indicar `-BepInExDirectory` para a pasta que contém `BepInEx.dll` e `0Harmony.dll`.

A compilação gera `bin/Release/netstandard2.1/` e atualiza as DLLs em `release/BepInEx/plugins/`.

## Instalar uma compilação local

Com o jogo fechado, depois de compilar:

```powershell
.\tools\Install-Bloodlink.ps1
```

Use `-GameDirectory` se o jogo estiver em outro local. O instalador faz backup das DLLs substituídas, restaura `doorstop_config.ini` se necessário e grava o manifesto em `BepInEx/bloodlink-install.json`.

## Verificações sem abrir o jogo

```powershell
dotnet run --project Tests/Bloodlink.ProtocolChecks.csproj -c Release -- --sync-only
dotnet run --project Tests/Bloodlink.ProtocolChecks.csproj -c Release -- --respawn-enemy-only
```

As verificações locais não substituem a validação de renderização, física e comportamento das arenas dentro do jogo.

## Funcionalidades atuais

- Salas por IP/LAN e Unity Relay, entrada por código e reconexão.
- Sincronização de jogadores, inimigos, armas, dano, objetos e progressão.
- READY, checkpoints, espectador e recuperação de jogadores.
- Personalização do V1 com prévia interativa.
- Fogo amigo opcional controlado pelo host.
- HUD dos colegas com nome e barra de vida.

A integração Relay precisa de um projeto Unity Gaming Services com os serviços habilitados. O Project ID é configurável no arquivo do plugin. Este mod ainda tem fluxos visuais e de campanha que precisam ser validados em partida.

## Publicação

Esta pasta está organizada para ser a raiz do repositório GitHub. A pasta `release/` contém a build para distribuir aos jogadores. Os arquivos `bin/` e `obj/` são ignorados pelo Git e podem ser recriados.

Avisos das dependências Unity: `Dependencies/THIRD-PARTY-NOTICES.txt`.
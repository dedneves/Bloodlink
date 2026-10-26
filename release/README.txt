ULTRAKILL BLOODLINK — 0.112.0 / protocolo 56

Feche o jogo e copie BepInEx deste pacote para a pasta principal do ULTRAKILL, mesclando plugins.
Todos os participantes precisam usar 0.112.0 / protocolo 56.
Requer BepInEx 5 instalado e HideManagerGameObject = true no BepInEx.cfg.
Inclui o mod, LiteNetLib e dependências de Unity Relay. O jogo e o BepInEx base não estão incluídos.

Alterações
- Respawn prioriza um ponto com piso na arena enquanto suas portas estão bloqueadas, com detecção adicional de bosses iniciados por cutscenes.
- Recuperação tenta ponto da arena e companheiros quando o destino inicial não tem piso válido.
- Door.Optimize não desativa salas durante multiplayer.
- Sincronização genérica recusa alterações de contêineres protegidos; inimigos ativos reativam a hierarquia local necessária para aparecer.
- Fogo amigo opcional em HOST SETTINGS, desligado por padrão e controlado pelo host; tiros e socos são avaliados no host com verificação de alvo, alcance e obstáculos.
- HUD compacta de colegas com nome, visor V1 em moldura branca em relevo, barra vermelha e estado de vida.

Verificação sem abrir o jogo: compilação sem avisos/erros, 117 verificações de sincronização/geometria de fogo amigo e 24 de respawn/animação passaram. A geometria das arenas, apresentação da HUD e comportamento em combate ainda precisam de validação em partida.
Projeto-fonte preservado em outputs\ULTRAKILL.Bloodlink.

Carregador: copie também doorstop_config.ini e winhttp.dll deste pacote para a pasta do ULTRAKILL.exe. O BepInEx base deve estar instalado. Sem doorstop_config.ini, o jogo pode iniciar sem carregar o mod.

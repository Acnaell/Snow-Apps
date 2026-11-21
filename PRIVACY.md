# Política de privacidade do Snow

Última atualização: 21/11/2026.

**Resumo:** o Snow não tem telemetria, não cria conta, não coleta nem envia dados do seu PC para o autor ou para terceiros. Tudo o que ele mede (hardware, saúde dos discos, FPS, pontuações) fica no seu computador.

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it, except for the update check described below, which can be turned off.

## Acessos à internet
| Quando | Para onde | O que é enviado | Como desligar |
|---|---|---|---|
| Ao abrir o Snow (verificação de atualização) | `api.github.com` (página de versões do Snow) | Só a consulta da versão mais recente. Nenhum dado do seu PC. O GitHub recebe o seu IP, como em qualquer acesso a um site | Barra lateral → "Procurar atualização ao abrir" (desligado = nunca consulta sozinho) |
| Quando você clica em **Atualizar agora** | `github.com` (download do pacote oficial) | Só o pedido do arquivo | É sempre você quem pede |
| Quando você clica em **Buscar na Steam** (tela "Roda no meu PC?") | `store.steampowered.com` (API pública da loja) | Só o nome do jogo que você digitou | É sempre você quem pede |
| Quando você clica em **Fonte oficial** | Site do fabricante do jogo, no seu navegador | O que o seu navegador normalmente envia | É sempre você quem pede |

Valem as políticas de privacidade do [GitHub](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement) e da [Valve/Steam](https://store.steampowered.com/privacy_agreement/) para esses acessos.

## O que fica no seu computador
- Backups das alterações (para desfazer): `%ProgramData%\Snow\backups`
- Registro de atividades: `%ProgramData%\Snow\logs`
- Preferências (tema, verificação de atualização): `%ProgramData%\Snow`
- Medições do Desempenho e pontuações: `Documentos\SnowBench`

Para apagar tudo: use **Restaurar** para desfazer as alterações, feche o Snow e apague essas pastas e a pasta do Snow.

## Programas de terceiros
O Monitor de Desempenho pode usar o **Intel PresentMon** (que você mesmo baixa e coloca na pasta do Snow) para ler o FPS por eventos do Windows (ETW). O PresentMon roda só no seu PC e não acessa a internet.

## Contato
Dúvidas: abra uma issue em https://github.com/Acnaell/Snow-Apps/issues.

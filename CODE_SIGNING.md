# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## O que é assinado
Somente os executáveis do Snow gerados a partir deste repositório público (`Acnaell/Snow-Apps`) pelo GitHub Actions, em máquinas hospedadas pelo GitHub:

- `Snow.exe` (interface) e `snow-cli.exe` (linha de comando), nas versões win-x64, win-x86 e win-arm64.

Bibliotecas de terceiros não são assinadas com este certificado. O Snow não usa bibliotecas de terceiros no pacote (zero pacotes NuGet); o runtime do .NET embutido nos executáveis vem da Microsoft.

## Papéis
| Papel | Membros |
|---|---|
| Autores (committers) | [Thiago Neves (@Acnaell)](https://github.com/Acnaell) |
| Revisores | [Thiago Neves (@Acnaell)](https://github.com/Acnaell): revisa toda contribuição externa (pull requests) antes do merge |
| Aprovadores | [Thiago Neves (@Acnaell)](https://github.com/Acnaell): aprova manualmente cada pedido de assinatura no SignPath |

Todos os membros usam autenticação em dois fatores no GitHub e no SignPath.

## Como uma versão é assinada
1. A versão é gerada pelo workflow [`ci.yml`](.github/workflows/ci.yml) a partir do commit marcado, depois de passar nos testes em Windows 10 e Windows 11.
2. Os executáveis sem assinatura são enviados ao SignPath pelo próprio workflow.
3. O aprovador confere a versão e aprova o pedido manualmente.
4. Os executáveis assinados são empacotados (`Snow-<arquitetura>.zip`) e publicados na página de [Releases](https://github.com/Acnaell/Snow-Apps/releases), com o arquivo `SHA256SUMS.txt`.

## Privacidade
Veja a [política de privacidade](PRIVACY.md). Resumo: o Snow não envia nenhuma informação a outros sistemas em rede, a não ser quando o usuário pede ou nos casos descritos lá (todos podem ser desligados).

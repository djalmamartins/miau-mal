# Desenvolvimento

Instale SDK .NET 10 estável e Git. Python 3.12+ é usado apenas no smoke de desenvolvimento/CI; não é dependência do runtime. SDK é selecionado por global.json. O build completo atual roda em Windows, macOS e Linux; o alvo do produto é Windows.

```sh
git clone https://github.com/djalmamartins/miau-mal.git
cd miau-mal
dotnet restore Miau.slnx
dotnet build Miau.slnx -c Release --no-restore
dotnet test Miau.slnx -c Release --no-build
python scripts/smoke.py
```

No macOS, o comando pode ser `python3`. Windows: `pwsh -File scripts/verify.ps1` executa restore/build/test; rode também o smoke acima. Configure `DOTNET_CLI_TELEMETRY_OPTOUT=1` para desativar telemetria do SDK (distinta do produto, que não implementa telemetria).

```sh
dotnet run --project src/Miau.Api
# Em outro terminal:
curl http://127.0.0.1:11435/health
dotnet run --project src/Miau.Cli -- --help
dotnet run --project src/Miau.Cli -- --version
```

Health esperado: `{"status":"ok","version":"0.1.0-dev"}`. Ctrl+C encerra o host. A API não carrega modelos. A CLI compilada tem nome `miau`; não é instalada globalmente nesta fase. Não use `miau run`, `status` ou `serve`: ainda são backlog.

A porta 11435 deve estar livre. O smoke recusa rodar se já houver algo nessa porta e encerra somente o processo que iniciou. Não execute smoke simultaneamente com a API manual. Endereços/portas configuráveis pertencem à issue #15.

## Desktop

`src/Miau.Desktop` reserva a estrutura; não é projeto WinUI compilável nesta versão. O futuro projeto WinUI 3 exigirá Windows e ferramentas Windows App SDK. Não declarar validação de UI a partir do CI atual.

## Testes

Core: validação de paths/prompt/limites. Engine: proteção de dependências, sem inferência. API: integração HTTP em memória. Smoke: executáveis reais, códigos de saída e bind loopback mesmo com overrides genéricos.

Nenhum teste baixa modelos. Testes reais de engine ficam em v0.2 com fixture externa pequena e licenciada.

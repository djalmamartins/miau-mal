# miau-mal

**Inteligência Local.**

Seu modelo. Suas regras. No seu Windows.

Runtime local de IA para Windows, experimental, construído em C#/.NET 10. O objetivo de longo prazo é executar modelos GGUF via llama.cpp e substituir o Ollama no uso interno, com API local, CLI e aplicação nativa WinUI 3.

> MIAU is currently an internal experimental project.

**Versão: 0.1.0-dev — foundation.** Ainda não executa modelos. O repositório chama-se `miau-mal`; os projetos e namespaces permanecem `Miau.*`, e o nome planejado do comando é `miau`.

## Available now

- Solution .NET 10 com Core, contratos assíncronos de inferência e limite do projeto Engine.
- Host ASP.NET Core em `127.0.0.1:11435`; `GET /health` retorna liveness e versão.
- CLI com `--help` e `--version`; entradas não suportadas retornam código 2.
- Testes xUnit, smoke de processos, analyzers e GitHub Actions no Windows.
- Arquitetura, ADRs, roadmap e backlog organizado.
- Diretório Desktop reservado; ainda sem app WinUI compilável.

## Planned

Inferência real llama.cpp, carregar/descarregar GGUF, API compatível com OpenAI, streaming, gerenciador de modelos, CLI status/run/stop/serve/chat, WinUI 3, métricas CPU/RAM, integração Windows, GPU, embeddings/RAG. Agentes e ferramentas ficam em backlog pós-v1.0.

## Início rápido

Requer SDK .NET 10. Nenhum modelo ou engine precisa ser baixado nesta fase.

```sh
dotnet restore Miau.slnx
dotnet build Miau.slnx -c Release --no-restore
dotnet test Miau.slnx -c Release --no-build
dotnet run --project src/Miau.Api
```

Em outro terminal:

```sh
curl http://127.0.0.1:11435/health
dotnet run --project src/Miau.Cli -- --help
```

Resposta health: `{"status":"ok","version":"0.1.0-dev"}`. Isso não indica modelo carregado. Ctrl+C encerra a API. Veja [desenvolvimento e smoke tests](docs/development/getting-started.md).

## Arquitetura

```text
Client
  ↓
MIAU-MAL API / CLI / Desktop
  ↓
Miau.Engine (contratos em Miau.Core)
  ↓
llama.cpp (planejado)
  ↓
GGUF (planejado)
```

Core não depende da UI. O futuro adapter isola llama.cpp do restante do sistema. Não há banco de dados, telemetria, coleta externa, upload automático ou execução arbitrária de comandos. Binários e GGUF não são versionados.

## Organização

- [Project board](https://github.com/users/djalmamartins/projects/7)
- [Milestones](https://github.com/djalmamartins/miau-mal/milestones)
- [Issues](https://github.com/djalmamartins/miau-mal/issues)
- [Roadmap](ROADMAP.md) · [Arquitetura](ARCHITECTURE.md) · [ADRs](docs/decisions)
- [Contribuição](CONTRIBUTING.md) · [Segurança](SECURITY.md) · [Changelog](CHANGELOG.md)

Mais que IA. É o seu território.

A identidade gráfica será fornecida posteriormente. O mascote não foi redesenhado nesta fase.

## Licença

Projeto interno; todos os direitos reservados por enquanto. Nenhuma licença open source foi escolhida. Ver [LICENSE](LICENSE). A visibilidade do repositório não altera essa escolha. Dependências upstream e futuros modelos têm licenças próprias.

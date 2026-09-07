# Arquitetura — miau-mal

Status: foundation 0.1.0-dev. Runtime local para Windows baseado futuramente em llama.cpp e GGUF.

```text
Client
  ↓
MIAU-MAL API / CLI / Desktop
  ↓
Miau.Engine → contratos em Miau.Core
  ↓
llama.cpp (planejado)
  ↓
GGUF (planejado)
```

## Dependências

- Core: contratos, tipos imutáveis e validações; sem UI, HTTP ou pacotes externos.
- Engine → Core: limite do futuro adapter. Nesta fase não executa inferência.
- API → Core: host ASP.NET Core; liveness /health; loopback 127.0.0.1:11435.
- CLI → Core: ajuda e versão. Controle do runtime será via API.
- Desktop: diretório reservado para WinUI 3; projeto executável em fase posterior, apenas no Windows.
- Tests: xUnit; sem modelos, rede externa ou binários de inferência.

## Limites

Nenhum banco de dados, telemetria, download automático ou shell arbitrário.
Health significa processo HTTP vivo; não significa modelo carregado.
Uma futura instância do engine administrará um modelo por vez e publicará seu estado.
Cancelamento deve chegar ao adapter; falhas de load não podem deixar um processo órfão.
Concorrência, timeout, processos e distribuição de binários serão decididos no spike v0.2.

Decisões detalhadas ficam em docs/decisions. Não implementar inferência nesta foundation.

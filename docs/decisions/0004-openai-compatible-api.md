# ADR 0004 — OpenAI-compatible local API

Status: accepted for foundation; integration details provisional where stated.

## Context

Clientes existentes se beneficiam de um protocolo HTTP familiar sem exigir serviços externos.

## Decision

Planejar /v1/models e /v1/chat/completions com streaming gradual em v0.4. Implementar somente GET /health na foundation. Vincular o host explicitamente a 127.0.0.1:11435; overrides genéricos de endpoints ficam desabilitados.

## Consequences

Não declarar compatibilidade completa. Criar testes de contrato para erros, mensagens, streaming e cancelamento quando esses endpoints existirem. Health é liveness, não readiness de modelo. Qualquer futura exposição remota exige decisão explícita de autenticação e segurança.

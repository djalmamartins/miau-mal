# ADR 0003 — Use GGUF

Status: accepted for foundation; integration details provisional where stated.

## Context

llama.cpp usa GGUF para representar modelos de inferência. Importação e compatibilidade devem ser previsíveis.

## Decision

Aceitar GGUF como formato inicial. Descriptor valida apenas entrada/extensão; adapter futuro validará existência, conteúdo e compatibilidade. Nunca versionar pesos, incluindo shards.

## Consequences

Extensão não garante arquivo válido. Modelos continuam sujeitos a licenças próprias; suporte a arquitetura/quantização depende da versão fixada de llama.cpp. Metadados e gestão ficam em v0.5.

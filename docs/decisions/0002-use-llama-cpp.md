# ADR 0002 — Use llama.cpp

Status: accepted for foundation; integration details provisional where stated.

## Context

Precisamos executar GGUF localmente sem desenvolver um motor de inferência. A integração ainda não foi validada neste projeto.

## Decision

Adotar llama.cpp como engine atrás de IInferenceEngine. Hipótese inicial: processo filho llama-server acessado por HTTP loopback privado; comparar com P/Invoke/bindings na issue #8 antes de confirmar. Não adicionar adapter falso na foundation.

## Consequences

Processo isola falhas nativas, mas exige readiness, portas, timeout, cancelamento e cleanup. Bindings evitam HTTP interno, mas aumentam acoplamento ABI/ownership. Binários devem ter release/hash/checksum/licença fixados no spike #9. Nenhum processo, peso ou binário acompanha v0.1.

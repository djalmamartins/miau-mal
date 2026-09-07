# Miau.Engine

Limite do futuro adapter de `Miau.Core.IInferenceEngine`. A biblioteca está intencionalmente sem implementação nesta foundation. Não registra um engine falso, não inicia processos e não baixa binários/modelos.

Em v0.2, investigar `llama-server` como processo filho versus bindings nativos. O adapter deve controlar startup/readiness/timeouts/cancelamento/unload e liberar somente processos que criou. Core permanece independente de detalhes llama.cpp. Ver issues #8–#12.

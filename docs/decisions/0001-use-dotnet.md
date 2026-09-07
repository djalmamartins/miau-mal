# ADR 0001 — Use .NET 10

Status: accepted for foundation; integration details provisional where stated.

## Context

O produto é um runtime Windows com API, CLI e futura UI nativa. Precisamos compartilhar contratos e testes sem escrever um engine novo.

## Decision

Usar C# e .NET 10; Core sem pacotes externos, ASP.NET Core no host e xUnit nos testes. Nullable, analyzers e warnings como erros.

## Consequences

API/CLI/Core podem ser validados no macOS; WinUI requer Windows. global.json aceita patches e feature bands estáveis de .NET 10; CI registra o SDK efetivo. Dependências devem permanecer mínimas.

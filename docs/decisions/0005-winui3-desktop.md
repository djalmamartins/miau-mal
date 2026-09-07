# ADR 0005 — Use WinUI 3 desktop

Status: accepted for foundation; integration details provisional where stated.

## Context

O produto terá interface nativa premium para Windows; a identidade gráfica será fornecida depois.

## Decision

Usar WinUI 3 via Windows App SDK na fase Desktop. Nesta foundation reservar src/Miau.Desktop com plano de navegação; não criar executável ou placeholders de UI não validados.

## Consequences

A solution atual não contém projeto Desktop compilável. Em v0.6, selecionar versão estável do Windows App SDK e validar template/XAML/empacotamento no Windows. Não misturar dependências WinUI em Core/Engine. Cores preto/grafite/amarelo; mascote fornecido depois, sem redesenho agora.

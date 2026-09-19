# MIAU Desktop v1

Aplicativo desktop local para macOS Apple Silicon e Windows, construído do zero com .NET 10 + Avalonia.

## Requisitos
- .NET 10 SDK
- Ollama em http://127.0.0.1:11434
- modelo qwen2.5-coder:7b

## Executar
```bash
cd desktop/Miau.Desktop
dotnet restore
dotnet run
```

## Primeira versão funcional
Chat de agente, seleção de workspace, leitura/escrita de arquivos, busca, execução de comandos, git status/diff, loop de ferramentas, cancelamento e atividade visível.

> Segurança: o agente restringe ferramentas de arquivo ao workspace. Comandos de shell são poderosos e devem ser usados em projetos confiáveis.

# MIAU Coding Agent no VS Code

Fluxo: **VS Code → extensão MIAU → API local MIAU → Ollama → Qwen3-Coder → ferramentas do workspace**.

O agente é multiplataforma no nível do runtime .NET e da extensão: Windows x64 e macOS Apple Silicon (M1/M2/M3/M4). As ferramentas ficam restritas à pasta aberta no VS Code e permitem ler, pesquisar, criar e substituir arquivos, executar comandos, builds/testes e ler Git (status, diff e log). Comando Git adicional pode ser executado pela ferramenta de terminal quando solicitado.

## Pré-requisitos

- .NET 10 SDK
- VS Code
- Node.js/npm para compilar a extensão
- Git
- Ollama com um modelo Qwen3-Coder disponível localmente

Confira o nome exato instalado com `ollama list` e ajuste `miau.model` no VS Code. O padrão inicial é `qwen3-coder:30b`, mas não pressupõe que esse tag esteja instalado.

## Runtime

```sh
dotnet run --project src/Miau.Api
```

A API escuta somente em `127.0.0.1:11435`. O Ollama esperado por padrão fica em `http://127.0.0.1:11434`.

## Extensão

```sh
cd vscode-extension
npm install
npm run compile
```

Abra `vscode-extension` no VS Code e pressione F5 para iniciar um Extension Development Host. Abra um projeto nesse host e clique no ícone MIAU.

## Segurança

O agente resolve caminhos contra a raiz do workspace e rejeita escape por `../`. Ele pode executar comandos locais, portanto rode apenas em projetos confiáveis. Commit/push e operações Git destrutivas não devem ser feitos sem pedido explícito do usuário.

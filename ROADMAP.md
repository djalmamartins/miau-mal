# Roadmap — miau-mal

Versão atual: **0.1.0-dev**. Esta execução termina na foundation.
Cada funcionalidade requer issue pequena com critérios de aceite. Itens de planejamento devem ser divididos antes da implementação.

| Versão | Escopo |
| --- | --- |
| v0.1 Foundation | Solution .NET, Core, contratos Engine, testes, CI, documentação; API health e CLI mínima; reserva Desktop |
| v0.2 First Inference | Investigar integração, fixar binário llama.cpp, carregar GGUF, prompt real, unload |
| v0.3 Local API | Controle do runtime, host/porta, status/run CLI, logs estruturados |
| v0.4 OpenAI Compatibility | /v1/models, /v1/chat/completions, streaming e erros compatíveis |
| v0.5 Model Manager | Importar, listar, metadados, excluir, modelo padrão, diretório configurável |
| v0.6 Desktop | WinUI 3, dashboard, modelos, chat, API, configurações, logs, métricas CPU/RAM |
| v0.7 Windows Integration | Startup, tray, background, Service se fizer sentido, installer |
| v0.8 Hardware Acceleration | Detecção GPU, NVIDIA/CUDA, outros backends, seleção automática/manual |
| v0.9 Knowledge | Embeddings, documentos, indexação local, RAG |
| v1.0 Stable Runtime | Instalador, estratégia de update, logs/recovery, documentação, testes, estabilidade |

## Pós-v1.0

Backlog separado: agentes, programação, acesso a projetos, edição de arquivos, Git, ferramentas, MCP e automações. Nenhum desses recursos está disponível agora.

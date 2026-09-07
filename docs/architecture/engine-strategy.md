# Estratégia preliminar de inferência

Não implementada. A issue #8 deve concluir a comparação antes do spike #9.

| Opção | Benefício | Custo / risco |
| --- | --- | --- |
| Processo filho llama-server | Isola crash nativo; protocolo HTTP; binário substituível | Portas, readiness, startup, stdout/stderr, cancelamento e processo órfão |
| P/Invoke / bindings | Chamadas diretas, sem servidor interno | ABI, marshaling, memória nativa e crash no host |

Hipótese preferida: processo filho com argumentos fixos via ProcessStartInfo.ArgumentList, UseShellExecute=false e binário local verificado. Nunca construir comandos shell com prompts ou caminhos. O adapter deve administrar somente o processo que criou e escutar apenas loopback. Avaliar token interno para reduzir acesso por outros processos locais.

## Ciclo a validar em v0.2

1. Resolver versão/binário configurado e verificar checksum; não baixar automaticamente.
2. Validar modelo GGUF e compatibilidade com engine fixado.
3. Iniciar processo, consumir logs sem registrar prompts, aguardar readiness com prazo.
4. Gerar resposta real com cancelamento e erros explícitos.
5. Unload idempotente: terminar processo próprio, aguardar saída e liberar handles.
6. Testar startup inválido, porta ocupada, GGUF inválido, timeout, cancelamento, crash e shutdown no Windows.

Um modelo por instância inicialmente. Não prometer concorrência ou streaming antes dos testes respectivos. Não usar servidor llama como API pública diretamente: Miau.Api mantém o contrato externo e evita acoplar clientes à versão interna.

## Fontes técnicas consultadas

- [llama.cpp e formato GGUF](https://github.com/ggml-org/llama.cpp)
- [llama-server: opções, endpoints e exemplos](https://github.com/ggml-org/llama.cpp/tree/master/tools/server)
- [WinUI 3](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)

Essas referências descrevem capacidades upstream, não recursos já disponíveis em miau-mal. Nenhuma versão de llama.cpp foi selecionada ainda.

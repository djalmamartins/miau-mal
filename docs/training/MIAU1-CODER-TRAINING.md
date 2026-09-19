# MIAU1-Coder — preparação para treinamento

## Estado atual

O MIAU1-Coder v0 é um agente determinístico composto por JobEngine, AgentOrchestrator, protocolo MIAU v1, ferramentas, memória por projeto e um modelo coder substituível. O modelo base atual é `qwen2.5-coder:7b`; ele não controla conclusão nem segurança.

## Dados e curadoria

Memória não é dataset: memória apoia decisões locais futuras. Dataset não é benchmark: apenas experiências reais aprovadas podem virar material de treino, enquanto avaliações permanecem isoladas. Benchmark não é fine-tuning: nesta fase nenhum peso do modelo é alterado.

- `memory/{projectFingerprint}`: contexto recuperável; não altera pesos.
- `dataset/completed`: tarefas validadas, schema `miau-dataset-v1`.
- `dataset/recovery`: ação falha, erro e estratégia de recuperação.
- `dataset/rejected`: reservado para conclusões recusadas e violações de escopo.
- `dataset/review`: tarefas concluídas que ainda não atingiram o limiar de qualidade.

Registros positivos recebem score técnico de 0–100. Exportações para treino devem exigir score mínimo, revisão de segredos e amostragem manual de patches. JSONL é o formato canônico e pode ser convertido depois para instruction tuning, SFT ou LoRA/QLoRA.

## Critérios antes de fine-tuning

Não iniciar treinamento até existir um corpus revisado, diverso e sem segredos, com conjuntos separados de treino, validação e benchmark. Medir: completion rate, validade do protocolo, retry rate, falha de edição, sucesso de build/testes e violações de escopo.

O benchmark deve cobrir criação de arquivo, edição localizada, correção de bug, testes, refatoração, múltiplos arquivos, recuperação de build e recuperação de edição. Um modelo novo só substitui o baseline se melhorar essas métricas sem piorar segurança.

## Estratégia futura

Começar com LoRA/QLoRA sobre um coder compatível, manter versões imutáveis de dataset/prompt/protocolo e comparar contra `qwen2.5-coder:7b`. Rollback significa restaurar o adapter/modelo anterior; JobEngine e ferramentas permanecem inalterados.

# Métricas de evolução

`EvolutionService` agrega os JSONL locais existentes sem duplicá-los. Ausência de evidência produz zero. O histórico compacto fica em `ApplicationData/MIAU/evolution/history.jsonl` e só muda quando tarefas, sucesso ou qualidade mudam.

Habilidades expõem tentativas, sucessos, falhas e taxa derivadas de eventos. O painel roda apenas em `127.0.0.1:17891`; a rede procedural é uma metáfora visual, não neurônios ou pesos reais.

Endpoints somente leitura: `/api/evolution`, `/api/evolution/history`, `/api/dataset/stats`, `/api/memory`, `/api/recovery` e `/api/activity`.

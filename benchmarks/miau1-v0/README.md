# MIAU1-Coder v0 benchmark

Bateria determinística com dez categorias. Cada caso é copiado para um repositório Git temporário, executado pelo agente e validado por conteúdo e escopo. O workspace temporário é removido ao final.

```bash
dotnet run --project desktop/Miau.Desktop -- benchmark benchmarks/miau1-v0/cases.json qwen2.5-coder:7b
```

Os resultados ficam em `ApplicationData/MIAU/benchmarks`. O serviço usa um `IDatasetService` nulo: avaliações jamais entram no dataset de treinamento.

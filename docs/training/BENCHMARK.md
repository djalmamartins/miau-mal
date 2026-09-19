# Benchmark MIAU1-Coder v0

A bateria em `benchmarks/miau1-v0` cobre dez categorias. Cada caso nasce em workspace Git temporário e só passa quando fase, conteúdo e arquivos alterados correspondem aos critérios.

```bash
dotnet run --project desktop/Miau.Desktop -- benchmark benchmarks/miau1-v0/cases.json qwen2.5-coder:7b
```

O relatório registra resultado, duração, ferramentas, retries e score em `ApplicationData/MIAU/benchmarks`. A execução usa dataset nulo: benchmark nunca entra automaticamente no material de treinamento.

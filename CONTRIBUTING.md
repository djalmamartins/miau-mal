# Desenvolvimento incremental

Uma issue por vez, com escopo pequeno, testável e reversível.

1. Ler ROADMAP.md e ARCHITECTURE.md.
2. Ler a issue e critérios de aceite; inspecionar o código existente.
3. Criar branch `feature/issue-<numero>-descricao` ou `fix/issue-<numero>-descricao`.
4. Implementar somente o escopo da issue; atualizar testes críticos e documentação.
5. Executar restore, build Release (analyzers/warnings como erros), xUnit e smoke pertinente.
6. Commitar usando Conventional Commits e referenciar a issue.
7. Abrir PR com Summary, Changes, Tests, Acceptance Criteria e Known limitations.
8. Atualizar a issue com evidências; somente então iniciar a próxima.

Main deve permanecer estável. Não fechar issue por compilação apenas; usar critérios de aceite e CI Windows. Done somente após integração validada. Nenhum commit de pesos, binários grandes ou segredos. Não adicionar abstrações sem necessidade.

Esta primeira foundation usa commits separados por issue em branches sequenciais, reunidos em uma PR de entrega; as issues ficam em Review até integração. Futuras funcionalidades devem usar PR individual por issue.

A v0.1 termina na foundation. Não iniciar v0.2 automaticamente. Itens de planejamento de fases futuras precisam ser divididos em funcionalidades pequenas antes de implementar.

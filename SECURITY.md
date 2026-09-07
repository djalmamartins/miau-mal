# Segurança

O produto é local first e experimental. A foundation escuta apenas `127.0.0.1:11435`, sem telemetria, coleta externa, upload ou acesso automático a arquivos do usuário. Nenhum endpoint de inferência/execução de comandos existe.

O host não implementa autenticação nesta fase e não deve ser exposto via proxy/túnel. Loopback não autentica outros processos locais. Mudanças de exposição, CORS, autenticação e acesso a arquivos exigem decisão explícita antes de adicionar endpoints sensíveis.

Não enviar prompts, modelos, segredos ou dados pessoais em issues/logs. Para vulnerabilidades, use o canal privado do proprietário ou a opção de relato privado do GitHub se estiver habilitada; não publique detalhes exploráveis em issue pública. Nenhum canal privado específico foi provisionado nesta foundation.

Não versionar GGUF, credenciais ou binários. Releases futuras de llama.cpp deverão ser fixadas e verificadas; licenças dos modelos serão tratadas separadamente. O build restaura pacotes NuGet e o CI usa infraestrutura GitHub; isso não é telemetria do runtime.

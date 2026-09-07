# Miau.Desktop — estrutura reservada

Futura aplicação nativa WinUI 3 / Windows App SDK. Nesta foundation existe apenas este diretório: **não há projeto .csproj, executável, XAML ou UI funcional**. A solution compila API, CLI, Core e Engine. A criação do app WinUI compilável faz parte da fase v0.6, issue #24 e suas futuras subdivisões.

Navegação futura: Modelos, Chat, API, Terminal, Configurações, Logs; dashboard com CPU/RAM e estado do runtime.

Visual: Windows premium; dark, preto/grafite, accent amarelo; minimalista e legível. Mascote: gato preto mal-humorado de olhos amarelos. Identidade gráfica será fornecida posteriormente; não redesenhar agora.

Quando iniciar: escolher Windows App SDK estável, gerar template oficial WinUI 3 no Windows, separar shell/viewmodels da UI, consumir o runtime via API e criar um job Windows para XAML/build/empacotamento. Não adicionar WinUI a Core ou Engine. Ver ADR 0005.

# Guias do PythonScriptRunner

[English](../en/README.md) | Português

O PythonScriptRunner executa scripts Python a partir do .NET com um contrato tipado. Estes guias vão da configuração
mínima (só a biblioteca, sem banco de dados e sem broker de mensagens) até a completa, com fila, agendamento e painel
embutido.

| Guia | O que cobre |
|------|-------------|
| [Primeiros passos](getting-started.md) | As formas de uso, o que instalar e a primeira Etapa em poucos minutos |
| [Etapas](stages.md) | O contrato (entradas, saídas e os 12 tipos), o `stage.json`, Etapas em C#, limites, retry e segredos |
| [Trilhas](pipelines.md) | Encadear Etapas, ligações, saídas da trilha e tratamento de falhas |
| [SDK Python](python-sdk.md) | Escrever scripts com o `psrunner`, arquivos, erros e testes com pytest e com a linha de comando |
| [Persistência (PostgreSQL)](persistence.md) | Catálogo versionado, histórico de execuções, retenção e auditoria |
| [Fila, worker e agendamento (RabbitMQ)](queue-and-worker.md) | Execução em segundo plano, retry, cron, eventos e integração com outros sistemas |
| [Painel](dashboard.md) | A interface web embutida, os modos de acesso e os papéis |
| [Hospedagem](hosting.md) | Imagens de contêiner, volumes, limites de memória, Kubernetes, .NET Aspire e Docker Compose |
| [Referência de configuração](configuration.md) | Todas as opções, com o valor padrão |
| [Como funciona](how-it-works.md) | Isolamento de processos, ambientes Python, limites, modelo de segurança e observabilidade |

O [README](../../README.md) tem uma visão geral de tudo numa página só.

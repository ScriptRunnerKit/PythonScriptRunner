# Painel

[English](../en/dashboard.md) | Português

O `PythonScriptRunner.Dashboard` embute uma interface web na sua aplicação ASP.NET Core, no estilo do Hangfire. Ele tem
uma API interna, atualizações em tempo real por SignalR e uma interface Vue, tudo dentro do pacote: não há nada para
hospedar à parte. O painel precisa da [persistência](persistence.md) e da [fila](queue-and-worker.md), porque guarda
versões e histórico e executa testes e execuções nos workers.

## Configuração

```bash
dotnet add package PythonScriptRunner.Dashboard
```

```csharp
builder.Services
    .AddPythonScriptRunner()
    .AddEntityFrameworkCore(o => o.UseNpgsql(builder.Configuration.GetConnectionString("psr")!))
    .AddRabbitMq(o => o.ConnectionString = builder.Configuration.GetConnectionString("rabbitmq"))
    .AddDashboard(o => o.UseFixedLogin(login => login.BindConfiguration()));   // seção PythonScriptRunner:Dashboard:Login

var app = builder.Build();
app.MapPythonScriptRunnerDashboard("/psr");
```

O `MapPythonScriptRunnerDashboard("/psr")` serve:
- a interface em `/psr/`;
- a API em `/psr/api`;
- as atualizações em tempo real em `/psr/hubs/runs`.

Sem essa chamada não existe painel. Para rodá-lo fora da sua aplicação, use a imagem pronta do `DashboardHost` (veja
[Hospedagem](hosting.md)).

## O que dá para fazer

A interface está em português do Brasil, com temas claro e escuro. Uma versão em inglês está planejada.

**Editar uma Etapa.** Escreva o script num editor Monaco e monte o contrato com um construtor de campos: nome, tipo,
obrigatório, valor padrão e descrição. A aba Dependências define a versão do Python e os requisitos. Dá para testar um
rascunho num worker de verdade antes de publicar.

![Editor de Etapa](../images/dashboard-stage-editor.png)

![Adicionando uma saída ao contrato](../images/dashboard-contract.png)

**Montar uma trilha.** Arraste Etapas publicadas para um canvas e ligue saídas a entradas. Os pontos de ligação têm tipo,
e os problemas aparecem enquanto você edita. Uma visão em lista oferece a mesma edição sem o canvas.

![Editor visual de trilhas](../images/dashboard-pipeline-editor.png)

**Executar e acompanhar.** Dispare uma execução por um formulário gerado a partir do contrato. O fluxo mostra cada passo
mudando de status em tempo real. As abas mostram as entradas, as saídas (inclusive tabelas), os logs de cada tentativa,
os arquivos e uma linha do tempo. Execuções com falha podem ser reprocessadas a partir do passo que falhou, e as que estão
rodando podem ser canceladas.

![Formulário de disparo](../images/dashboard-run-form.png)

![Uma execução em andamento](../images/dashboard-run-live.png)

![Saídas de uma execução concluída](../images/dashboard-run-outputs.png)

A interface também tem:
- uma visão geral das últimas 24 horas;
- listas de execuções com filtros;
- agendamentos por cron, com prévia das próximas ocorrências;
- a auditoria;
- uma página de administração para o papel `Admin`.

## Modos de acesso

Escolha **um** modo principal:

| Modo | Configuração | Quando usar |
|------|--------------|-------------|
| Login fixo | `o.UseFixedLogin(l => l.BindConfiguration())`, ou `l.Users` em código | Poucos usuários conhecidos, sem provedor de identidade. |
| Autenticação do host | `o.UseHostAuthentication(r => { ... })` | A sua aplicação já autentica usuários (cookies, OpenID Connect, Microsoft Entra ID). |
| Filtro próprio | `o.UseAuthorizationFilter<MeuFiltro>()` | Uma regra sua: um `IDashboardAuthorizationFilter` devolve `DashboardUser(nome, papel)` ou `null`. |
| Requisições locais | `o.AllowLocalRequests = true` | Só em Development (fora dele a inicialização falha). Soma-se a um modo principal. |

## Papéis

Os quatro papéis são cumulativos:

| Papel | Pode |
|-------|------|
| `Reader` | Ver tudo: catálogo, execuções, logs, arquivos e agendamentos. |
| `Executor` | Também disparar, cancelar e reprocessar execuções. |
| `Author` | Também editar, testar, publicar, importar e agendar. |
| `Admin` | Também excluir e administrar retenção, segredos e auditoria. |

## Login fixo

Os usuários ficam na configuração. A senha nunca é guardada, só o hash dela (PBKDF2, no formato do ASP.NET Identity):

```json
"PythonScriptRunner": {
  "Dashboard": {
    "Login": {
      "Users": [
        { "Name": "ana", "Role": "Admin", "PasswordHash": "AQAAAAIAAYagAAAAE..." },
        { "Name": "bruno", "Role": "Reader", "PasswordHash": "AQAAAAIAAYagAAAAE..." }
      ]
    }
  }
}
```

Gere o hash com a ferramenta `psr`:

```bash
dotnet tool install -g PythonScriptRunner.Tool
psr hash-password                              # pede a senha duas vezes, sem eco
echo "senha" | psr hash-password --stdin       # em scripts
```

Como o login fixo se comporta:
- **Sessão:** um cookie `psr.session` HttpOnly (`SameSite=Lax`), renovado por 8 horas de atividade, com no máximo 24
  horas.
- **Bloqueio:** 5 falhas seguidas bloqueiam o usuário por 15 minutos, e um IP pode tentar 10 vezes por minuto.
- **Mudanças na configuração:** valem sem reiniciar. Um usuário removido ou uma senha trocada encerra a sessão, e um
  papel novo vale na hora.
- **Chaves do cookie:** ficam no banco (`psr.dashboard_keys`), então a sessão vale em todas as instâncias e sobrevive a
  reinícios.

## Autenticação do host

Associe cada papel a uma política de autorização da sua aplicação e informe ao painel onde fica a sua página de login:

```csharp
.AddDashboard(o => o.UseHostAuthentication(r =>
{
    r.Reader = p => p.RequireRole("psr-leitor");
    r.Executor = p => p.RequireRole("psr-executor");
    r.Author = p => p.RequireRole("psr-autor");
    r.Admin = p => p.RequireRole("psr-admin");
    r.LoginUrl = "/conta/entrar?returnUrl={returnUrl}";
}));
```

## A API

A API é interna: ela existe para a interface e pode mudar entre versões. Para integrar outros sistemas, use a biblioteca
.NET ou o [comando e os eventos do RabbitMQ](queue-and-worker.md#sistemas-escritos-em-outras-linguagens).
- **Alterações:** requisições que alteram algo exigem o cabeçalho `X-PSR-Request: 1`.
- **Edições:** usam `ETag` e `If-Match`.
- **Erros:** seguem o `ProblemDetails`, com um `code`.

Em Development, o documento OpenAPI fica em `/psr/api/openapi/v1.json`.

// Ambiente local completo (spec 009; ADR-0019): PostgreSQL, RabbitMQ, SMTP de teste (Mailpit), worker, painel e o
// painel Vue em modo dev. `aspire publish` gera o docker-compose com os Dockerfiles dos hosts (as mesmas imagens do GHCR).
var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("compose");

var postgres = builder.AddPostgres("postgres").WithImageTag("16-alpine").WithDataVolume("psr-postgres");
var database = postgres.AddDatabase("psr");
var rabbitmq = builder.AddRabbitMQ("rabbitmq").WithImageTag("3.13-management-alpine").WithManagementPlugin();

// SMTP de teste do CS-2: aceita qualquer usuário, sem TLS (D-172).
builder.AddContainer("mailpit", "axllent/mailpit", "v1.27.0")
    .WithEnvironment("MP_SMTP_AUTH_ACCEPT_ANY", "1")
    .WithEnvironment("MP_SMTP_AUTH_ALLOW_INSECURE", "1")
    .WithHttpEndpoint(targetPort: 8025, name: "http")
    .WithEndpoint(targetPort: 1025, name: "smtp", scheme: "tcp");

// Em desenvolvimento o psrunner vem do próprio repositório; nas imagens, do wheel embutido (D-169).
var psrunner = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "python", "psrunner"));

var worker = builder.AddProject<Projects.PythonScriptRunner_WorkerHost>("worker")
    .WithHttpEndpoint(name: "http")
    .WithHttpHealthCheck("/health/ready")
    .WithReference(database).WaitFor(database)
    .WithReference(rabbitmq).WaitFor(rabbitmq)
    .WithEnvironment("PSR_AUTO_MIGRATE", "true")
    .WithEnvironment("PythonScriptRunner__Secrets__SMTP_USUARIO", "psr")
    .WithEnvironment("PythonScriptRunner__Secrets__SMTP_SENHA", "psr-dev")
    .PublishAsDockerFile(c => c.WithDockerfile("../..", "hosts/PythonScriptRunner.WorkerHost/Dockerfile"));

// Login fixo com 4 usuários de desenvolvimento, um por papel, todos com a senha psr-dev (D-133, D-177).
var passwordHash = builder.Configuration["PythonScriptRunner:DevPasswordHash"]
    ?? throw new InvalidOperationException("PythonScriptRunner:DevPasswordHash is missing from the AppHost configuration.");
(string Name, string Role)[] users = [("leitor", "Reader"), ("executor", "Executor"), ("autor", "Author"), ("admin", "Admin")];
var dashboard = builder.AddProject<Projects.PythonScriptRunner_DashboardHost>("dashboard")
    .WithHttpEndpoint(name: "http")
    .WithExternalHttpEndpoints()
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/psr/";
        url.DisplayText = "Painel";
    })
    .WithHttpHealthCheck("/health/ready")
    .WithReference(database).WaitFor(database)
    .WithReference(rabbitmq).WaitFor(rabbitmq)
    .WithEnvironment("PSR_AUTO_MIGRATE", "true")
    .PublishAsDockerFile(c => c.WithDockerfile("../..", "hosts/PythonScriptRunner.DashboardHost/Dockerfile"));
for (var i = 0; i < users.Length; i++)
{
    dashboard
        .WithEnvironment($"PythonScriptRunner__Dashboard__Login__Users__{i}__Name", users[i].Name)
        .WithEnvironment($"PythonScriptRunner__Dashboard__Login__Users__{i}__Role", users[i].Role)
        .WithEnvironment($"PythonScriptRunner__Dashboard__Login__Users__{i}__PasswordHash", passwordHash);
}

if (builder.ExecutionContext.IsRunMode)
{
    worker.WithEnvironment("PythonScriptRunner__PsrunnerRequirement", psrunner);
    dashboard.WithEnvironment("PythonScriptRunner__PsrunnerRequirement", psrunner);

    // Painel Vue com hot reload, com proxy da API para o DashboardHost (no compose o painel vem embutido na imagem).
    builder.AddViteApp("panel", "../../panel")
        .WithNpm()
        .WithEnvironment("PSR_DASHBOARD_URL", dashboard.GetEndpoint("http"))
        .WaitFor(dashboard);
}

builder.Build().Run();

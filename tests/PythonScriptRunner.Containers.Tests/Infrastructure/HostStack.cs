using DotNet.Testcontainers.Networks;
using PythonScriptRunner.Dashboard;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

[assembly: AssemblyFixture(typeof(PythonScriptRunner.Containers.Tests.Infrastructure.HostStack))]

namespace PythonScriptRunner.Containers.Tests.Infrastructure;

/// <summary>
/// PostgreSQL and RabbitMQ on a private network (aliases <c>pg</c> and <c>rabbit</c>) and the two host images; each test
/// starts its hosts against its own database, the way they run in production (read-only file system, volumes as tmpfs).
/// </summary>
public sealed class HostStack : IAsyncLifetime
{
    public const int HttpPort = 8080;

    public const string Password = "senha-de-teste";

    private static readonly string[] Volumes = ["/var/lib/psr/envs", "/var/lib/psr/python", "/tmp/psr"];

    private readonly INetwork _network = new NetworkBuilder().Build();
    private PostgreSqlContainer? _postgres;
    private RabbitMqContainer? _rabbit;

    public string WorkerImage { get; private set; } = string.Empty;

    public string DashboardImage { get; private set; } = string.Empty;

    public PostgreSqlContainer Postgres => _postgres!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _network.CreateAsync(Ct);
        _postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithNetwork(_network).WithNetworkAliases("pg")
            .WithUsername("psr").WithPassword("psr").WithDatabase("psr")
            .Build();
        _rabbit = new RabbitMqBuilder("rabbitmq:3.13-alpine")
            .WithNetwork(_network).WithNetworkAliases("rabbit")
            .WithUsername("psr").WithPassword("psr")
            .Build();
        var images = Task.WhenAll(HostImages.ResolveAsync(HostImages.Worker, Ct), HostImages.ResolveAsync(HostImages.Dashboard, Ct));
        await Task.WhenAll(_postgres.StartAsync(Ct), _rabbit.StartAsync(Ct), images);
        (WorkerImage, DashboardImage) = (images.Result[0], images.Result[1]);
    }

    /// <summary>Creates an empty database for one test.</summary>
    public async Task<string> CreateDatabaseAsync(string name)
    {
        var result = await Postgres.ExecAsync(["psql", "-U", "psr", "-d", "psr", "-c", $"CREATE DATABASE {name}"], Ct);
        result.ExitCode.ShouldBe(0, result.Stderr);
        return name;
    }

    /// <summary>Runs a query in a database and returns the single value.</summary>
    public async Task<string> ScalarAsync(string database, string sql)
    {
        var result = await Postgres.ExecAsync(["psql", "-U", "psr", "-d", database, "-tAc", sql], Ct);
        result.ExitCode.ShouldBe(0, result.Stderr);
        return result.Stdout.Trim();
    }

    /// <summary>
    /// A host container (not started) with migrations on, waiting until <c>/health/ready</c> answers 200;
    /// <paramref name="privileged"/> runs it privileged and as root, so it owns a writable <c>/sys/fs/cgroup</c>: a
    /// delegable cgroup v2 (D-180), like a systemd service with <c>Delegate=yes</c>.
    /// </summary>
    public IContainer Host(string image, string database, IReadOnlyDictionary<string, string>? environment = null, bool privileged = false)
    {
        var builder = new ContainerBuilder(image)
            .WithNetwork(_network)
            .WithPrivileged(privileged)
            .WithEnvironment("ConnectionStrings__psr", $"Host=pg;Database={database};Username=psr;Password=psr")
            .WithEnvironment("ConnectionStrings__rabbitmq", "amqp://psr:psr@rabbit:5672/")
            .WithEnvironment("PSR_AUTO_MIGRATE", "true")
            .WithPortBinding(HttpPort, assignRandomHostPort: true)
            .WithCreateParameterModifier(parameters =>
            {
                // Testcontainers fills HostConfig (port bindings, network) before the modifiers run. The volumes run
                // Python (interpreters, venvs), so the tmpfs needs exec (Docker mounts tmpfs noexec by default).
                parameters.HostConfig!.ReadonlyRootfs = true;
                if (privileged)
                {
                    parameters.User = "0:0";
                }

                parameters.HostConfig.Tmpfs = Volumes.ToDictionary(v => v, _ => "uid=10001,gid=10001,exec");
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(HttpPort).ForPath("/health/ready")));
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            builder = builder.WithEnvironment(name, value);
        }

        return builder.Build();
    }

    /// <summary>Fixed login users of the dashboard image (one admin).</summary>
    public static IReadOnlyDictionary<string, string> DashboardUsers() => new Dictionary<string, string>
    {
        ["PythonScriptRunner__Dashboard__Login__Users__0__Name"] = "admin",
        ["PythonScriptRunner__Dashboard__Login__Users__0__Role"] = "Admin",
        ["PythonScriptRunner__Dashboard__Login__Users__0__PasswordHash"] = DashboardPasswords.Hash(Password),
    };

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }

        if (_rabbit is not null)
        {
            await _rabbit.DisposeAsync();
        }

        await _network.DisposeAsync();
    }
}

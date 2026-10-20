using Testcontainers.RabbitMq;

[assembly: AssemblyFixture(typeof(PythonScriptRunner.Testing.BrokerFixture))]

namespace PythonScriptRunner.Testing;

/// <summary>One RabbitMQ 3.13 container (the minimum supported version; D-111) for the whole test run; each test gets its own virtual host.</summary>
public sealed class BrokerFixture : IAsyncLifetime
{
    public const string Image = "rabbitmq:3.13-alpine";

    private readonly RabbitMqContainer _container = new RabbitMqBuilder(Image).Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Creates an empty virtual host and returns the AMQP URI to it.</summary>
    public async Task<string> CreateVirtualHostAsync()
    {
        var name = $"psr-{Guid.NewGuid():N}";
        await RunAsync("rabbitmqctl", "add_vhost", name);
        var user = new Uri(_container.GetConnectionString()).UserInfo.Split(':')[0];
        await RunAsync("rabbitmqctl", "set_permissions", "-p", name, user, ".*", ".*", ".*");
        return new UriBuilder(_container.GetConnectionString()) { Path = "/" + name }.Uri.ToString();
    }

    private async Task RunAsync(params string[] command)
    {
        var result = await _container.ExecAsync(command);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{string.Join(' ', command)} failed: {result.Stderr}");
        }
    }
}

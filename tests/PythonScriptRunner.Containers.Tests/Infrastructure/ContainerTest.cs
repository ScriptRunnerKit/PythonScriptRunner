namespace PythonScriptRunner.Containers.Tests.Infrastructure;

/// <summary>Base of the container tests: when a test fails, the logs of its containers are kept (<see cref="ContainerLogs"/>).</summary>
public abstract class ContainerTest : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await ContainerLogs.ReportAsync();
        GC.SuppressFinalize(this);
    }
}

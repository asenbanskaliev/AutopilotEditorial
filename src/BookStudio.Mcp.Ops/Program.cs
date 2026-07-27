using System.Reflection;
using System.Text;
using BookStudio.Mcp;
using BookStudio.Mcp.Protocol;
using BookStudio.Mcp.Transport;

namespace BookStudio.Mcp.Ops;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);

        McpHostOptions options;
        try
        {
            options = McpHostOptions.Parse(args);
        }
        catch (ArgumentException)
        {
            await Console.Error.WriteLineAsync("MCP_INVALID_HOST_OPTIONS").ConfigureAwait(false);
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        var runtime = new BookOpsRuntime(options.WorkspaceRoot);
        await using var features = new BookOpsFeatureRouter(
            runtime.GetService,
            runtime.DisposeAsync);
        var session = new McpSession(
            features,
            new McpImplementationInfo(
                "bookstudio-ops",
                GetVersion(),
                "BookStudio Operations MCP"));
        var server = new StdioJsonRpcServer(
            Console.In,
            Console.Out,
            Console.Error,
            session);

        return await server.RunAsync(shutdown.Token).ConfigureAwait(false);
    }

    private static string GetVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
                   ?.Split('+', 2)[0]
               ?? assembly.GetName().Version?.ToString()
               ?? "0.0.0";
    }
}

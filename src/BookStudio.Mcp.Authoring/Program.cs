using System.Reflection;
using System.Text;
using BookStudio.Mcp;
using BookStudio.Mcp.Prompts;
using BookStudio.Mcp.Protocol;
using BookStudio.Mcp.Transport;

namespace BookStudio.Mcp.Authoring;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

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

        var runtime = new BookAuthoringRuntime(options.WorkspaceRoot);
        var boundedFeatures = new BookAuthoringFeatureRouter(
            runtime.GetService,
            runtime.DisposeAsync);
        await using var features = new PromptEnabledFeatureRouter(
            boundedFeatures,
            BookAuthoringPromptCatalog.Catalog,
            BookAuthoringToolCatalog.SchemaResources,
            resourceCursorScope: "authoring-resources",
            resourcePageSize: 3);
        var session = new McpSession(
            features,
            new McpImplementationInfo(
                "bookstudio-authoring",
                GetVersion(),
                "BookStudio Authoring MCP"));
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

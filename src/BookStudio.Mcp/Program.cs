using System.Text;
using BookStudio.Mcp;
using BookStudio.Mcp.BookCore;
using BookStudio.Mcp.Prompts;
using BookStudio.Mcp.Protocol;
using BookStudio.Mcp.Security;
using BookStudio.Mcp.Transport;

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

var runtime = new BookCoreRuntime(options);
var boundedFeatures = new BookCoreFeatureRouter(
    runtime.GetQueryService,
    runtime.DisposeAsync);
var sandboxFeatures = new SandboxEnabledFeatureRouter(
    boundedFeatures,
    options,
    BookCoreToolCatalog.SchemaResources,
    resourceCursorScope: "core-sandbox-resources",
    resourcePageSize: 3);
await using var features = new PromptEnabledFeatureRouter(
    sandboxFeatures,
    BookCorePromptCatalog.Catalog,
    sandboxFeatures.Resources,
    resourceCursorScope: "resources",
    resourcePageSize: 3);
var session = new McpSession(features);
var server = new StdioJsonRpcServer(
    Console.In,
    Console.Out,
    Console.Error,
    session);

return await server.RunAsync(shutdown.Token).ConfigureAwait(false);

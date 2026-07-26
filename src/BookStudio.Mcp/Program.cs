using System.Text;
using BookStudio.Mcp.Protocol;
using BookStudio.Mcp.Transport;

Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

var session = new McpSession();
var server = new StdioJsonRpcServer(
    Console.In,
    Console.Out,
    Console.Error,
    session);

return await server.RunAsync(shutdown.Token).ConfigureAwait(false);

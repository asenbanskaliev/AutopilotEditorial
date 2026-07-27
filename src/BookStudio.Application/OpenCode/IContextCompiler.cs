namespace BookStudio.Application.OpenCode;

public interface IContextCompiler
{
    CompiledContextManifest Compile(
        ContextCompilationRequest request,
        CancellationToken cancellationToken = default);
}

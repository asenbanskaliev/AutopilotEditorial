using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Application.Authoring;

public sealed class ImageAdapterOrchestrator
{
    private readonly IImageAdapterRegistry _adapters;
    private readonly IImageAdapterRequestStore _requests;
    private readonly IAssetRegistryStore _assets;

    public ImageAdapterOrchestrator(
        IImageAdapterRegistry adapters,
        IImageAdapterRequestStore requests,
        IAssetRegistryStore assets)
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
    }

    public async ValueTask<ImageAdapterRequestState> ExecuteAsync(
        ImageAdapterRequest request,
        DateTimeOffset startedAtUtc,
        CancellationToken ct = default)
    {
        ValidateRequest(request);
        var submitted = await _requests.SubmitAsync(request, startedAtUtc, ct);
        if (submitted.Replayed && submitted.Request.Status is ImageAdapterRequestStatus.Completed
            or ImageAdapterRequestStatus.Failed or ImageAdapterRequestStatus.Cancelled)
            return submitted.Request;

        var adapter = _adapters.Resolve(request.AdapterId, request.AdapterVersion);
        ValidateAdapter(request, adapter);

        var state = submitted.Request;
        var maximumAttempts = Math.Max(1, request.RetryPolicy.MaximumAttempts);
        for (var attemptNumber = state.Attempts.Count + 1; attemptNumber <= maximumAttempts; attemptNumber++)
        {
            ct.ThrowIfCancellationRequested();
            var attemptId = DeterministicGuid(request.RequestId, $"attempt:{attemptNumber}");
            var execution = new ImageAdapterExecution(request, attemptNumber, attemptId, startedAtUtc);
            ImageAdapterAttemptResult result;
            try
            {
                result = await adapter.ExecuteAsync(execution, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await adapter.CancelAsync(new ImageAdapterCancellation(
                    request.RequestId, request.WorkspaceId, state.Revision,
                    "Execution cancelled by caller.", request.Actor, request.RequestFingerprint), CancellationToken.None);
                return await _requests.CancelAsync(new ImageAdapterCancellation(
                    request.RequestId, request.WorkspaceId, state.Revision,
                    "Execution cancelled by caller.", request.Actor, request.RequestFingerprint),
                    DateTimeOffset.UtcNow, CancellationToken.None);
            }
            catch (Exception ex)
            {
                result = ProviderFailure(ex, startedAtUtc);
            }

            ValidateAttemptResult(request, adapter, result);
            state = await _requests.RecordAttemptAsync(new ImageAdapterAttempt(
                request.RequestId, request.WorkspaceId, state.Revision, attemptId,
                attemptNumber, adapter.AdapterId, adapter.AdapterVersion, result,
                request.Actor, request.RequestFingerprint), result.CompletedAtUtc, ct);

            if (result.Succeeded)
            {
                var registered = new List<ImageAdapterRegisteredOutput>(result.Outputs.Count);
                foreach (var output in result.Outputs)
                {
                    var assetId = DeterministicGuid(request.RequestId, $"asset:{output.OutputId:D}:{output.ContentDigest}");
                    var registration = await _assets.RegisterAsync(new AssetRegistrationDraft(
                        assetId, request.ProjectId, request.WorkspaceId,
                        request.VisualBriefId, request.ExpectedVisualBriefRevision, request.ExpectedVisualBriefDigest,
                        request.AssetType, $"{adapter.AdapterId}@{adapter.AdapterVersion}",
                        output.StorageRoot, output.RelativePath, output.MediaFormat,
                        output.Width, output.Height, output.ColorProfile, output.ContentDigest,
                        result.ProviderEvidenceJson, request.GenerationParametersJson,
                        output.Provenance, request.Rights, request.Accessibility,
                        output.Relationships, request.Actor,
                        $"{request.RequestFingerprint}:asset:{output.OutputId:D}:{output.ContentDigest}"),
                        result.CompletedAtUtc, ct);
                    registered.Add(new ImageAdapterRegisteredOutput(
                        output.OutputId, registration.Asset.AssetId, registration.Asset.Revision,
                        registration.Asset.ContentDigest, registration.Asset.MessageId));
                }

                return await _requests.CompleteAsync(new ImageAdapterCompletion(
                    request.RequestId, request.WorkspaceId, state.Revision, attemptId,
                    registered, request.Actor, request.RequestFingerprint), result.CompletedAtUtc, ct);
            }

            var error = result.Error ?? throw new ImageAdapterValidationException(
                "A failed adapter attempt must provide normalized error evidence.");
            var retryable = error.Retryable
                && request.RetryPolicy.RetryableFailures.Contains(error.Kind)
                && attemptNumber < maximumAttempts;
            if (!retryable)
                return await _requests.FailAsync(new ImageAdapterFailure(
                    request.RequestId, request.WorkspaceId, state.Revision, attemptId,
                    error, request.Actor, request.RequestFingerprint), result.CompletedAtUtc, ct);
        }

        throw new ImageAdapterTransitionException("The bounded retry loop ended without a terminal request state.");
    }

    private static void ValidateRequest(ImageAdapterRequest request)
    {
        if (request.RequestId == Guid.Empty || request.ProjectId == Guid.Empty || request.VisualBriefId == Guid.Empty)
            throw new ImageAdapterValidationException("Request, project, and visual brief identifiers are required.");
        RequireText(request.WorkspaceId, request.ExpectedVisualBriefDigest, request.AdapterId,
            request.AdapterVersion, request.GenerationParametersJson, request.Actor, request.RequestFingerprint);
        if (request.ExpectedVisualBriefRevision < 1)
            throw new ImageAdapterValidationException("An exact positive visual brief revision is required.");
        if (request.RequiredCapabilities.Count == 0)
            throw new ImageAdapterValidationException("At least one required adapter capability is required.");
        if (request.RetryPolicy.MaximumAttempts is < 1 or > 10)
            throw new ImageAdapterValidationException("Retry attempts must be bounded between one and ten.");
        if (request.Operation == ImageOperationMode.ManualImport && string.IsNullOrWhiteSpace(request.ManualSourceIdentity))
            throw new ImageAdapterValidationException("Manual import requires a source identity.");
        if (request.Operation != ImageOperationMode.ManualImport && string.IsNullOrWhiteSpace(request.Prompt))
            throw new ImageAdapterValidationException("Generated image operations require a prompt.");
    }

    private static void ValidateAdapter(ImageAdapterRequest request, IImageAdapter adapter)
    {
        if (!StringComparer.Ordinal.Equals(adapter.AdapterId, request.AdapterId)
            || !StringComparer.Ordinal.Equals(adapter.AdapterVersion, request.AdapterVersion)
            || adapter.Kind != request.AdapterKind)
            throw new ImageAdapterValidationException("Resolved adapter identity does not match the request.");

        foreach (var capability in request.RequiredCapabilities)
        {
            var supported = capability switch
            {
                ImageCapability.Generate => adapter.Capabilities.Generate,
                ImageCapability.Variation => adapter.Capabilities.Variation,
                ImageCapability.Inpaint => adapter.Capabilities.Inpaint,
                ImageCapability.ImageToImage => adapter.Capabilities.ImageToImage,
                ImageCapability.Upscale => adapter.Capabilities.Upscale,
                ImageCapability.DeterministicSeed => adapter.Capabilities.DeterministicSeed,
                ImageCapability.Cancellation => adapter.Capabilities.Cancellation,
                ImageCapability.ManualImport => adapter.Capabilities.ManualImport,
                _ => false
            };
            if (!supported)
                throw new ImageAdapterValidationException($"Adapter does not support required capability '{capability}'.");
        }
    }

    private static void ValidateAttemptResult(
        ImageAdapterRequest request,
        IImageAdapter adapter,
        ImageAdapterAttemptResult result)
    {
        RequireText(result.ProviderEvidenceJson, result.ProviderEvidenceDigest);
        if (result.Succeeded && (result.Error is not null || result.Outputs.Count == 0))
            throw new ImageAdapterValidationException("Successful attempts require outputs and cannot contain an error.");
        if (!result.Succeeded && result.Error is null)
            throw new ImageAdapterValidationException("Failed attempts require normalized error evidence.");

        foreach (var output in result.Outputs)
        {
            RequireText(output.StorageRoot, output.RelativePath, output.MediaFormat,
                output.ColorProfile, output.ContentDigest, output.TechnicalMetadataJson,
                output.Provenance.EvidenceDigest);
            if (!StringComparer.Ordinal.Equals(output.StorageRoot, request.OutputPolicy.StorageRoot))
                throw new ImageAdapterValidationException("Adapter output escaped the governed storage root.");
            if (Path.IsPathRooted(output.RelativePath) || output.RelativePath.Contains("..", StringComparison.Ordinal))
                throw new ImageAdapterValidationException("Adapter output contains an unsafe relative path.");
            if (!request.OutputPolicy.AllowedMediaFormats.Contains(output.MediaFormat)
                || !adapter.Capabilities.MediaFormats.Contains(output.MediaFormat))
                throw new ImageAdapterValidationException("Adapter output media format is not allowed.");
            if (output.Width < request.OutputPolicy.MinimumWidth || output.Height < request.OutputPolicy.MinimumHeight
                || output.Width > request.OutputPolicy.MaximumWidth || output.Height > request.OutputPolicy.MaximumHeight
                || output.Bytes < 1 || output.Bytes > request.OutputPolicy.MaximumBytes)
                throw new ImageAdapterValidationException("Adapter output violates governed technical limits.");
        }
    }

    private static ImageAdapterAttemptResult ProviderFailure(Exception exception, DateTimeOffset at) =>
        new(false, [], [], new ImageAdapterError(
            "provider_exception", exception.Message, ImageFailureKind.ProviderTransient,
            true, exception.GetType().FullName ?? exception.GetType().Name,
            Hash(exception.ToString())), new ImageAdapterUsage(0, 0, null, null,
            TimeSpan.Zero, "{}"), "{}", Hash(exception.ToString()), at);

    private static Guid DeterministicGuid(Guid scope, string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{scope:D}:{value}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void RequireText(params string[] values)
    {
        if (values.Any(string.IsNullOrWhiteSpace))
            throw new ImageAdapterValidationException("Required adapter evidence is missing.");
    }
}

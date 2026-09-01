using System;
using SharpHPatchZ.Extension;

namespace SharpHPatchZ;

/// <summary>
/// Determines whether the patch process has been successful or not.
/// </summary>
public class PatchResult
{
    /// <summary>
    /// Containing an error if the patching process is faulty.
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// Whether the patching process is successful or faulty.
    /// </summary>
    public bool IsSuccessful { get; init; }

    public static implicit operator bool(PatchResult result) => result.IsSuccessful;
    public static implicit operator Exception?(PatchResult result) => result.Exception;
    public static implicit operator int(PatchResult result) => ExceptionHelper.TryGetReturnCodeFromError(result);

    public static implicit operator PatchResult(Exception? ex) => new() { Exception = ex, IsSuccessful = ex == null };
    public static implicit operator PatchResult(bool value) => new() { Exception = !value ? new Exception() : null, IsSuccessful = value };
}

using System;
using SharpHPatchZ.Extension;

namespace SharpHPatchZ;

public class PatchResult
{
    public Exception? Exception { get; init; }

    public bool IsSuccessful { get; init; }

    public static implicit operator bool(PatchResult result) => result.IsSuccessful;
    public static implicit operator Exception?(PatchResult result) => result.Exception;
    public static implicit operator int(PatchResult result) => ExceptionHelper.TryGetReturnCodeFromError(result);

    public static implicit operator PatchResult(Exception ex) => new() { Exception = ex, IsSuccessful = false };
    public static implicit operator PatchResult(bool value) => new() { Exception = !value ? new Exception() : null, IsSuccessful = value };
}

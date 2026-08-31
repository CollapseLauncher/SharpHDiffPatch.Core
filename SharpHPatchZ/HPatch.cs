using System;
using SharpHPatchZ.Extension;
using SharpHPatchZ.Header;
using SharpHPatchZ.IO.Reader;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpHPatchZ.Patch;

namespace SharpHPatchZ;

/// <summary>
/// A factory delegate which creates an instance of a <see cref="Stream"/> of the patch from specified <paramref name="position"/>.
/// </summary>
/// <param name="position">The offset position of the <see cref="Stream"/> to begin from.</param>
/// <returns>
/// Returns both <see cref="Stream"/> as Stream property and <see cref="bool"/> as LeaveOpen property.
/// Return LeaveOpen == <see langword="true"/> if you want to keep the source <see cref="Stream"/> opened.<br/>
/// Otherwise, set LeaveOpen == <see langword="false"/> to dispose the returned <see cref="Stream"/> instance after use.
/// </returns>
public delegate (Stream Stream, bool LeaveOpen) CreateStream(long position);

/// <summary>
/// A factory delegate which creates an instance of a <see cref="Stream"/> of the patch from specified <paramref name="position"/> asynchronously.
/// </summary>
/// <param name="position">The offset position of the <see cref="Stream"/> to begin from.</param>
/// <param name="token">A cancellation token for the cancellation event while async operation is happening.</param>
/// <returns>
/// Returns both <see cref="Stream"/> as Stream property and <see cref="bool"/> as LeaveOpen property.
/// Return LeaveOpen == <see langword="true"/> if you want to keep the source <see cref="Stream"/> opened.<br/>
/// Otherwise, set LeaveOpen == <see langword="false"/> to dispose the returned <see cref="Stream"/> instance after use.
/// </returns>
public delegate ValueTask<(Stream Stream, bool LeaveOpen)> CreateStreamAsync(long position, CancellationToken token);

/// <summary>
/// A static class containing main functionality of the SharpHPatchZ library.
/// </summary>
public static partial class HPatch
{
    /// <summary>
    /// Creates a context for the patch processing.
    /// </summary>
    /// <param name="createPatchStream">A factory delegate which creates a <see cref="Stream"/> instance of the patch file.</param>
    /// <param name="initializeOptions">Options during the initialization process. For more usage information, see <see cref="InitializeOptions"/>.</param>
    /// <returns>Returns a context and information struct about the patch file.</returns>
    public static HDiffInfo CreateInstance(CreateStream      createPatchStream,
                                           InitializeOptions initializeOptions = default)
    {
        try
        {
            (Stream stream, bool leaveOpen) = createPatchStream(0);
            using BittableStreamReader reader = new(stream, leaveOpen: leaveOpen);

            string    signature = reader.ReadStringToNull();
            HDiffInfo info      = default;
            HeaderReader.ReadHeaderSignature(signature, ref info);
            HeaderReader.ReadHDiffHeaderMetadata(ref info, reader, initializeOptions);

            return info;
        }
        catch (EndOfStreamException eofStream)
        {
            throw ExceptionHelper.ThrowHDiffEndOfFileOrData(eofStream);
        }
    }

    /// <summary>
    /// Creates a context for the patch processing.
    /// </summary>
    /// <param name="patchPath">A path to the patch file.</param>
    /// <param name="initializeOptions">Options during the initialization process. For more usage information, see <see cref="InitializeOptions"/>.</param>
    /// <returns>Returns a context and information struct about the patch file.</returns>
    public static HDiffInfo CreateInstance(string            patchPath,
                                           InitializeOptions initializeOptions = default)
    {
        try
        {
            (Stream stream, bool leaveOpen) = CreateFileStreamWrapper(patchPath, 0);
            using BittableStreamReader reader = new(stream, leaveOpen: leaveOpen);

            string    signature = reader.ReadStringToNull();
            HDiffInfo info      = default;
            HeaderReader.ReadHeaderSignature(signature, ref info);
            HeaderReader.ReadHDiffHeaderMetadata(ref info, reader, initializeOptions);

            return info;
        }
        catch (EndOfStreamException eofStream)
        {
            throw ExceptionHelper.ThrowHDiffEndOfFileOrData(eofStream);
        }
    }

    /// <summary>
    /// Creates a context for the patch processing asynchronously.
    /// </summary>
    /// <param name="createPatchStreamAsync">A factory delegate creates a <see cref="Stream"/> instance of the patch file from specified position/offset.</param>
    /// <param name="token">A cancellation token for the cancellation event while async operation is happening.</param>
    /// <returns>Returns a context and information struct about the patch file.</returns>
    public static ValueTask<HDiffInfo> CreateInstanceAsync(
        CreateStreamAsync createPatchStreamAsync,
        CancellationToken token = default)
        => CreateInstanceAsync(createPatchStreamAsync, default, token);

    /// <summary>
    /// Creates a context for the patch processing asynchronously.
    /// </summary>
    /// <param name="patchPath">A path to the patch file.</param>
    /// <param name="token">A cancellation token for the cancellation event while async operation is happening.</param>
    /// <returns>Returns a context and information struct about the patch file.</returns>
    public static ValueTask<HDiffInfo> CreateInstanceAsync(
        string            patchPath,
        CancellationToken token = default)
        => CreateInstanceAsync(patchPath, default, token);

    /// <summary>
    /// Creates a context for the patch processing asynchronously.
    /// </summary>
    /// <param name="createPatchStreamAsync">A factory delegate creates a <see cref="Stream"/> instance of the patch file from specified position/offset asynchronously.</param>
    /// <param name="initializeOptions">Options during the initialization process. For more usage information, see <see cref="InitializeOptions"/>.</param>
    /// <param name="token">A cancellation token for the cancellation event while async operation is happening.</param>
    /// <returns>Returns a context and information struct about the patch file.</returns>
    public static async ValueTask<HDiffInfo> CreateInstanceAsync(
        CreateStreamAsync createPatchStreamAsync,
        InitializeOptions initializeOptions,
        CancellationToken token = default)
    {
        (Stream stream, bool leaveOpen) = await createPatchStreamAsync(0, token);
#if NET6_0_OR_GREATER
        await
#endif
        using BittableStreamReader reader = new(stream, leaveOpen: leaveOpen);

        string    signature = await reader.ReadStringToNullAsync(token: token);
        HDiffInfo info      = default;
        HeaderReader.ReadHeaderSignature(signature, ref info);
        info = await HeaderReader.ReadHDiffHeaderMetadataAsync(info, reader, initializeOptions, token);

        return info;
    }

    /// <summary>
    /// Creates a context for the patch processing asynchronously.
    /// </summary>
    /// <param name="patchPath">A path to the patch file.</param>
    /// <param name="initializeOptions">Options during the initialization process. For more usage information, see <see cref="InitializeOptions"/>.</param>
    /// <param name="token">A cancellation token for the cancellation event while async operation is happening.</param>
    /// <returns>Returns a context and information struct about the patch file.</returns>
    public static async ValueTask<HDiffInfo> CreateInstanceAsync(
        string            patchPath,
        InitializeOptions initializeOptions,
        CancellationToken token = default)
    {
        (Stream stream, bool leaveOpen) = CreateFileStreamWrapper(patchPath, 0);
#if NET6_0_OR_GREATER
        await
#endif
            using BittableStreamReader reader = new(stream, leaveOpen: leaveOpen);

        string    signature = await reader.ReadStringToNullAsync(token: token);
        HDiffInfo info      = default;
        HeaderReader.ReadHeaderSignature(signature, ref info);
        info = await HeaderReader.ReadHDiffHeaderMetadataAsync(info, reader, initializeOptions, token);

        return info;
    }

    /// <summary>
    /// Performs patch routines to the specified Input and Output paths.
    /// </summary>
    /// <param name="info">The struct containing context and information about the patch file.</param>
    /// <param name="createPatchStream">A factory delegate which creates a <see cref="Stream"/> instance of the patch file.</param>
    /// <param name="inputPath">The specified Input path of a file or directory.</param>
    /// <param name="outputPath">The specified Output path of a file or directory to be written to</param>
    /// <param name="options">Options during the patching process. For more usage information, see <see cref="PatchOptions"/>.</param>
    /// <param name="progressCallback">A struct containing the specified delegated method to be used to report the progress of the patch process. Use <see cref="ProgressCallback.CreateFromManaged"/> to create the struct and pass the callback.</param>
    /// <param name="token">A cancellation token for the cancellation event while patching operation is happening.</param>
    /// <returns>
    /// Returns a result of the patching process. The returned result can be implicitly cast into a nullable <see cref="Exception"/> or <see cref="bool"/>.<br/>
    /// If cast into a <see cref="bool"/>, the <see langword="true"/> means that the patching process has been successful. Otherwise, failed and <see cref="PatchResult.Exception"/> would be not <see langword="null"/>.<br/>
    /// If cast into a nullable <see cref="Exception"/> and the result is <see langword="null"/>, meaning that the patching process has been successful. Otherwise, failed.
    /// </returns>
    public static PatchResult Patch(HDiffInfo         info,
                                    CreateStream      createPatchStream,
                                    string            inputPath,
                                    string            outputPath,
                                    PatchOptions      options          = default,
                                    ProgressCallback  progressCallback = default,
                                    CancellationToken token            = default)
    {
        try
        {
            using PatcherBase patcher = PatcherFactory.CreateFromInfo(ref info, createPatchStream, options, progressCallback);
            patcher.StartPatch(inputPath, outputPath, token);
            return true;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }



    /// <summary>
    /// Performs patch routines to the specified Input and Output paths.
    /// </summary>
    /// <param name="info">The struct containing context and information about the patch file.</param>
    /// <param name="patchPath">The specified Patch file path.</param>
    /// <param name="inputPath">The specified Input path of a file or directory.</param>
    /// <param name="outputPath">The specified Output path of a file or directory to be written to</param>
    /// <param name="options">Options during the patching process. For more usage information, see <see cref="PatchOptions"/>.</param>
    /// <param name="progressCallback">A struct containing the specified delegated method to be used to report the progress of the patch process. Use <see cref="ProgressCallback.CreateFromManaged"/> to create the struct and pass the callback.</param>
    /// <param name="token">A cancellation token for the cancellation event while patching operation is happening.</param>
    /// <returns>
    /// Returns a result of the patching process. The returned result can be implicitly cast into a nullable <see cref="Exception"/> or <see cref="bool"/>.<br/>
    /// If cast into a <see cref="bool"/>, the <see langword="true"/> means that the patching process has been successful. Otherwise, failed and <see cref="PatchResult.Exception"/> would be not <see langword="null"/>.<br/>
    /// If cast into a nullable <see cref="Exception"/> and the result is <see langword="null"/>, meaning that the patching process has been successful. Otherwise, failed.
    /// </returns>
    public static PatchResult Patch(HDiffInfo         info,
                                    string            patchPath,
                                    string            inputPath,
                                    string            outputPath,
                                    PatchOptions      options          = default,
                                    ProgressCallback  progressCallback = default,
                                    CancellationToken token            = default)
    {
        try
        {
            using PatcherBase patcher = PatcherFactory.CreateFromInfo(ref info,
                                                                      pos => CreateFileStreamWrapper(patchPath, pos),
                                                                      options,
                                                                      progressCallback);
            patcher.StartPatch(inputPath, outputPath, token);
            return true;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// Performs patch routines to the specified Input and Output paths asynchronously
    /// </summary>
    /// <param name="info">The struct containing context and information about the patch file.</param>
    /// <param name="createPatchStreamAsync">A factory delegate which creates a <see cref="Stream"/> instance of the patch file asynchronously.</param>
    /// <param name="inputPath">The specified Input path of a file or directory.</param>
    /// <param name="outputPath">The specified Output path of a file or directory to be written to</param>
    /// <param name="options">Options during the patching process. For more usage information, see <see cref="PatchOptions"/>.</param>
    /// <param name="progressCallback">A struct containing the specified delegated method to be used to report the progress of the patch process. Use <see cref="ProgressCallback.CreateFromManaged"/> to create the struct and pass the callback.</param>
    /// <param name="token">A cancellation token for the cancellation event while patching operation is happening.</param>
    /// <returns>
    /// Returns a result of the patching process. The returned result can be implicitly cast into a nullable <see cref="Exception"/> or <see cref="bool"/>.<br/>
    /// If cast into a <see cref="bool"/>, the <see langword="true"/> means that the patching process has been successful. Otherwise, failed and <see cref="PatchResult.Exception"/> would be not <see langword="null"/>.<br/>
    /// If cast into a nullable <see cref="Exception"/> and the result is <see langword="null"/>, meaning that the patching process has been successful. Otherwise, failed.
    /// </returns>
    public static async Task<PatchResult> PatchAsync(
        HDiffInfo         info,
        CreateStreamAsync createPatchStreamAsync,
        string            inputPath,
        string            outputPath,
        PatchOptions      options          = default,
        ProgressCallback  progressCallback = default,
        CancellationToken token            = default)
    {
        try
        {
#if NET6_0_OR_GREATER
            await
#endif
            using PatcherBase patcher = await PatcherFactory.CreateFromInfoAsync(info, createPatchStreamAsync, options, progressCallback, token);
            await patcher.StartPatchAsync(inputPath, outputPath, token);
            return true;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// Performs patch routines to the specified Input and Output paths asynchronously
    /// </summary>
    /// <param name="info">The struct containing context and information about the patch file.</param>
    /// <param name="patchPath">The specified Patch file path.</param>
    /// <param name="inputPath">The specified Input path of a file or directory.</param>
    /// <param name="outputPath">The specified Output path of a file or directory to be written to</param>
    /// <param name="options">Options during the patching process. For more usage information, see <see cref="PatchOptions"/>.</param>
    /// <param name="progressCallback">A struct containing the specified delegated method to be used to report the progress of the patch process. Use <see cref="ProgressCallback.CreateFromManaged"/> to create the struct and pass the callback.</param>
    /// <param name="token">A cancellation token for the cancellation event while patching operation is happening.</param>
    /// <returns>
    /// Returns a result of the patching process. The returned result can be implicitly cast into a nullable <see cref="Exception"/> or <see cref="bool"/>.<br/>
    /// If cast into a <see cref="bool"/>, the <see langword="true"/> means that the patching process has been successful. Otherwise, failed and <see cref="PatchResult.Exception"/> would be not <see langword="null"/>.<br/>
    /// If cast into a nullable <see cref="Exception"/> and the result is <see langword="null"/>, meaning that the patching process has been successful. Otherwise, failed.
    /// </returns>
    public static async Task<PatchResult> PatchAsync(
        HDiffInfo         info,
        string            patchPath,
        string            inputPath,
        string            outputPath,
        PatchOptions      options          = default,
        ProgressCallback  progressCallback = default,
        CancellationToken token            = default)
    {
        try
        {
#if NET6_0_OR_GREATER
            await
#endif
            using PatcherBase patcher = await PatcherFactory.CreateFromInfoAsync(info,
                (pos, _) => CreateFileStreamWrapperAsync(patchPath, pos),
                options,
                progressCallback,
                token);
            await patcher.StartPatchAsync(inputPath, outputPath, token);
            return true;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}

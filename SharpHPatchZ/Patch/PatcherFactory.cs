using System.Threading;
using System.Threading.Tasks;
using SharpHPatchZ.Extension;
using SharpHPatchZ.Header;

namespace SharpHPatchZ.Patch;

internal static partial class PatcherFactory
{
    public static PatcherBase CreateFromInfo(ref HDiffInfo    info,
                                             CreateStream     createPatchStream,
                                             PatchOptions     options,
                                             ProgressCallback progressCallback)
    {
        if (info.MagicType is HDiffMagic.HDiff19 or HDiffMagic.HDiff13)
        {
            return HDiff13Derived.Create(ref info, createPatchStream, options, progressCallback);
        }

        throw ExceptionHelper.ThrowHDiffPatchFactoryNotSupported(info.MagicType);
    }

    public static async Task<PatcherBase> CreateFromInfoAsync(HDiffInfo         info,
                                                              CreateStreamAsync createPatchStreamAsync,
                                                              PatchOptions      options,
                                                              ProgressCallback  progressCallback,
                                                              CancellationToken token)
    {
        if (info.MagicType is HDiffMagic.HDiff19 or HDiffMagic.HDiff13)
        {
            return await HDiff13Derived.CreateAsync(info, createPatchStreamAsync, options, progressCallback, token);
        }

        throw ExceptionHelper.ThrowHDiffPatchFactoryNotSupported(info.MagicType);
    }
}

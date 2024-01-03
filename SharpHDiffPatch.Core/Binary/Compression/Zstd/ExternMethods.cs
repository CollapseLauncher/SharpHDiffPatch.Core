using System;
using System.Runtime.InteropServices;

namespace ZstdNet
{
    internal static class ExternMethods
    {
        internal const string DllName = "libzstd";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint ZDICT_isError(UIntPtr code);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ZDICT_getErrorName(UIntPtr code);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ZSTD_createDCtx();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_freeDCtx(IntPtr cctx);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_decompressDCtx(IntPtr ctx, ref byte dst, UIntPtr dstCapacity, ref byte src, UIntPtr srcSize);
        public static UIntPtr ZSTD_decompressDCtx(IntPtr ctx, Span<byte> dst, UIntPtr dstCapacity, ReadOnlySpan<byte> src, UIntPtr srcSize)
            => ZSTD_decompressDCtx(ctx, ref MemoryMarshal.GetReference(dst), dstCapacity, ref MemoryMarshal.GetReference(src), srcSize);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ZSTD_createDDict(byte[] dict, UIntPtr dictSize);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_freeDDict(IntPtr ddict);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_decompress_usingDDict(IntPtr dctx, ref byte dst, UIntPtr dstCapacity, ref byte src, UIntPtr srcSize, IntPtr ddict);
        public static UIntPtr ZSTD_decompress_usingDDict(IntPtr dctx, Span<byte> dst, UIntPtr dstCapacity, ReadOnlySpan<byte> src, UIntPtr srcSize, IntPtr ddict)
            => ZSTD_decompress_usingDDict(dctx, ref MemoryMarshal.GetReference(dst), dstCapacity, ref MemoryMarshal.GetReference(src), srcSize, ddict);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong ZSTD_getFrameContentSize(ref byte src, UIntPtr srcSize);
        public static ulong ZSTD_getFrameContentSize(ReadOnlySpan<byte> src, UIntPtr srcSize)
            => ZSTD_getFrameContentSize(ref MemoryMarshal.GetReference(src), srcSize);

        public const ulong ZSTD_CONTENTSIZE_UNKNOWN = unchecked(0UL - 1);
        public const ulong ZSTD_CONTENTSIZE_ERROR = unchecked(0UL - 2);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint ZSTD_isError(UIntPtr code);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ZSTD_getErrorName(UIntPtr code);

        #region Advanced APIs

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_DCtx_reset(IntPtr dctx, ZSTD_ResetDirective reset);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ZSTD_bounds ZSTD_dParam_getBounds(ZSTD_dParameter dParam);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_DCtx_setParameter(IntPtr dctx, ZSTD_dParameter param, int value);

        #endregion

        #region Streaming APIs

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ZSTD_createDStream();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_freeDStream(IntPtr zds);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_decompressStream(IntPtr zds, ref ZSTD_Buffer output, ref ZSTD_Buffer input);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_DStreamInSize();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_DCtx_refDDict(IntPtr cctx, IntPtr cdict);

        #endregion
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ZSTD_Buffer
    {
        public ZSTD_Buffer(UIntPtr pos, UIntPtr size)
        {
            this.buffer = (void*)IntPtr.Zero;
            this.size = size;
            this.pos = pos;
        }

        public void* buffer;
        public UIntPtr size;
        public UIntPtr pos;

        public bool IsFullyConsumed => (ulong)size <= (ulong)pos;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ZSTD_bounds
    {
        public UIntPtr error;
        public int lowerBound;
        public int upperBound;
    }

    public enum ZSTD_ResetDirective
    {
        ZSTD_reset_session_only = 1,
        ZSTD_reset_parameters = 2,
        ZSTD_reset_session_and_parameters = 3
    }

    public enum ZSTD_dParameter
    {
        ZSTD_d_windowLogMax = 100
    }

    public enum ZSTD_ErrorCode
    {
        ZSTD_error_no_error = 0,
        ZSTD_error_GENERIC = 1,
        ZSTD_error_prefix_unknown = 10,
        ZSTD_error_version_unsupported = 12,
        ZSTD_error_frameParameter_unsupported = 14,
        ZSTD_error_frameParameter_windowTooLarge = 16,
        ZSTD_error_corruption_detected = 20,
        ZSTD_error_checksum_wrong = 22,
        ZSTD_error_dictionary_corrupted = 30,
        ZSTD_error_dictionary_wrong = 32,
        ZSTD_error_dictionaryCreation_failed = 34,
        ZSTD_error_parameter_unsupported = 40,
        ZSTD_error_parameter_outOfBound = 42,
        ZSTD_error_tableLog_tooLarge = 44,
        ZSTD_error_maxSymbolValue_tooLarge = 46,
        ZSTD_error_maxSymbolValue_tooSmall = 48,
        ZSTD_error_stage_wrong = 60,
        ZSTD_error_init_missing = 62,
        ZSTD_error_memory_allocation = 64,
        ZSTD_error_workSpace_tooSmall = 66,
        ZSTD_error_dstSize_tooSmall = 70,
        ZSTD_error_srcSize_wrong = 72,
        ZSTD_error_dstBuffer_null = 74
    }
}

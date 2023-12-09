namespace ManagedLzma.LZMA.Master
{
    partial class LZMA
    {
        internal const int kHash2Size = (1 << 10);
        internal const int kHash3Size = (1 << 16);
        internal const int kHash4Size = (1 << 20);

        internal const int kFix3HashSize = (kHash2Size);
        internal const int kFix4HashSize = (kHash2Size + kHash3Size);
        internal const int kFix5HashSize = (kHash2Size + kHash3Size + kHash4Size);

        /*
        00000000  -  EOS
        00000001 U U  -  Uncompressed Reset Dic
        00000010 U U  -  Uncompressed No Reset
        100uuuuu U U P P  -  LZMA no reset
        101uuuuu U U P P  -  LZMA reset state
        110uuuuu U U P P S  -  LZMA reset state + new prop
        111uuuuu U U P P S  -  LZMA reset state + new prop + reset dic

          u, U - Unpack Size
          P - Pack Size
          S - Props
        */

        internal const int LZMA2_CONTROL_LZMA = (1 << 7);
        internal const int LZMA2_CONTROL_COPY_NO_RESET = 2;
        internal const int LZMA2_CONTROL_COPY_RESET_DIC = 1;
        internal const int LZMA2_CONTROL_EOF = 0;
        internal const int LZMA2_LCLP_MAX = 4;

        internal static SRes SZ_OK { get { return new SRes(0); } }
        internal static SRes SZ_ERROR_DATA { get { return new SRes(1); } }
        internal static SRes SZ_ERROR_MEM { get { return new SRes(2); } }
        internal static SRes SZ_ERROR_CRC { get { return new SRes(3); } }
        internal static SRes SZ_ERROR_UNSUPPORTED { get { return new SRes(4); } }
        internal static SRes SZ_ERROR_PARAM { get { return new SRes(5); } }
        internal static SRes SZ_ERROR_INPUT_EOF { get { return new SRes(6); } }
        internal static SRes SZ_ERROR_OUTPUT_EOF { get { return new SRes(7); } }
        internal static SRes SZ_ERROR_READ { get { return new SRes(8); } }
        internal static SRes SZ_ERROR_WRITE { get { return new SRes(9); } }
        internal static SRes SZ_ERROR_PROGRESS { get { return new SRes(10); } }
        internal static SRes SZ_ERROR_FAIL { get { return new SRes(11); } }
        internal static SRes SZ_ERROR_THREAD { get { return new SRes(12); } }
        internal static SRes SZ_ERROR_ARCHIVE { get { return new SRes(16); } }
        internal static SRes SZ_ERROR_NO_ARCHIVE { get { return new SRes(17); } }
    }
}

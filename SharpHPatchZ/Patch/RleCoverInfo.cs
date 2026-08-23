using SharpHPatchZ.Extension;
using SharpHPatchZ.Header;
using SharpHPatchZ.Header.Metadata;
using SharpHPatchZ.IO.Reader;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace SharpHPatchZ.Patch;

[StructLayout(LayoutKind.Sequential)]
internal struct RleCoverInfo
{
    public long OldStreamPosition;
    public long NewStreamPosition;
    public long RleLength;
    public long CopyLength;

    public RleCoverInfo() { }

    public RleCoverInfo(long oldStreamPosition,
                        long newStreamPosition,
                        long rleLength,
                        long copyLength)
    {
        OldStreamPosition = oldStreamPosition;
        NewStreamPosition = newStreamPosition;
        RleLength         = rleLength;
        CopyLength        = copyLength;
    }

    internal static Span<RleCoverInfo> Read(BittableStreamReader reader,
                                            HDiffInfo            info,
                                            out RleCoverInfo[]   backedBuffer,
                                            CancellationToken    token)
    {
        ref PatchMetadata patchMetadata = ref info.GetPatchMetadata();

        int rleCoverCount = patchMetadata.CoverDataCount;
        backedBuffer = BigArrayPool<RleCoverInfo>.Shared.Rent(rleCoverCount);

        long lastOldPosBack = 0;
        long lastNewPosBack = 0;

        for (int i = 0; i < rleCoverCount; i++)
        {
            token.ThrowIfCancellationRequested();

            long oldPosBack = lastOldPosBack;
            long newPosBack = lastNewPosBack;

            long incOldPos = reader.ReadLong7Bit(BittableStreamReader.KSignTagBit);

            byte incOldPosSign = (byte)(reader.PreviousByte >> (8 - BittableStreamReader.KSignTagBit));
            long oldPos        = incOldPosSign == 0 ? oldPosBack + incOldPos : oldPosBack - incOldPos;

            long copyLength  = reader.ReadLong7Bit();
            long coverLength = reader.ReadLong7Bit();

            oldPosBack =  oldPos;
            newPosBack += copyLength;
            oldPosBack += coverLength;

            backedBuffer[i] =  new RleCoverInfo(oldPos, newPosBack, coverLength, copyLength);
            newPosBack     += coverLength;

            lastOldPosBack = oldPosBack;
            lastNewPosBack = newPosBack;
        }

        return backedBuffer.AsSpan(0, rleCoverCount);
    }

    public override string ToString() => $"RleLength: {RleLength} | CopyLength: {CopyLength} | OldPos: {OldStreamPosition} | NewPos: {NewStreamPosition}";
}

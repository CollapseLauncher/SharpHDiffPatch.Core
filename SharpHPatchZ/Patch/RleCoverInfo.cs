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

    internal static NativeMemoryBuffer<RleCoverInfo> Read(BittableStreamReader reader,
                                                          HDiffInfo            info,
                                                          CancellationToken    token)
    {
        ref PatchMetadata patchMetadata = ref info.GetPatchMetadata();

        int rleCoverCount = patchMetadata.CoverDataCount;
        NativeMemoryBuffer<RleCoverInfo> backedBuffer = new(rleCoverCount);

        try
        {
            Span<RleCoverInfo> covers = backedBuffer.Span;
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

                covers[i]  = new RleCoverInfo(oldPos, newPosBack, coverLength, copyLength);
                newPosBack += coverLength;

                lastOldPosBack = oldPosBack;
                lastNewPosBack = newPosBack;
            }

            return backedBuffer;
        }
        catch
        {
            backedBuffer.Dispose();
            throw;
        }
    }

    public override string ToString() => $"RleLength: {RleLength} | CopyLength: {CopyLength} | OldPos: {OldStreamPosition} | NewPos: {NewStreamPosition}";
}

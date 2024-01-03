using System;

namespace SharpHDiffPatch.Core.Event
{
    public struct LoggerEvent
    {
        public LoggerEvent(string message, Verbosity logLevel)
        {
            Message = message;
            LogLevel = logLevel;
        }

        public Verbosity LogLevel;
        public string Message;
    }

    public sealed class PatchEvent
    {
        public PatchEvent()
        {
            this.Speed = 0;
            this.CurrentSizePatched = 0;
            this.TotalSizeToBePatched = 0;
            this.Read = 0;
        }

        public void UpdateEvent(long CurrentSizePatched, long TotalSizeToBePatched, long Read, double TotalSecond)
        {
            this.Speed = (long)(CurrentSizePatched / TotalSecond);
            this.CurrentSizePatched = CurrentSizePatched;
            this.TotalSizeToBePatched = TotalSizeToBePatched;
            this.Read = Read;
        }

        public long CurrentSizePatched { get; private set; }
        public long TotalSizeToBePatched { get; private set; }
        public double ProgressPercentage => Math.Round((CurrentSizePatched / (double)TotalSizeToBePatched) * 100, 2);
        public long Read { get; private set; }
        public long Speed { get; private set; }
        public TimeSpan TimeLeft => checked(TimeSpan.FromSeconds((TotalSizeToBePatched - CurrentSizePatched) / UnZeroed(Speed)));
        private long UnZeroed(long Input) => Math.Max(Input, 1);
    }
}

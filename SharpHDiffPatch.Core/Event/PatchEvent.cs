using System;

namespace SharpHDiffPatch.Core.Event
{
    public class LoggerEvent(string message, Verbosity logLevel)
    {
        public Verbosity LogLevel = logLevel;
        public string Message = message;
    }

    public sealed class PatchEvent
    {
        public PatchEvent()
        {
            Speed = 0;
            CurrentSizePatched = 0;
            TotalSizeToBePatched = 0;
            Read = 0;
        }

        public void UpdateEvent(long CurrentSizePatched, long TotalSizeToBePatched, long Read, double TotalSecond)
        {
            Speed = (long)(CurrentSizePatched / TotalSecond);
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

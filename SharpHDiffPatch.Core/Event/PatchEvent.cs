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
        public void UpdateEvent(long currentSizePatched, long totalSizeToBePatched, long read, double totalSecond)
        {
            Speed = (long)(currentSizePatched / totalSecond);
            CurrentSizePatched = currentSizePatched;
            TotalSizeToBePatched = totalSizeToBePatched;
            Read = read;
        }

        public         long     CurrentSizePatched   { get; private set; }
        public         long     TotalSizeToBePatched { get; private set; }
        public         double   ProgressPercentage   => Math.Round(CurrentSizePatched / (double)TotalSizeToBePatched * 100, 2);
        public         long     Read                 { get; private set; }
        public         long     Speed                { get; private set; }
        public         TimeSpan TimeLeft             => TimeSpan.FromSeconds((TotalSizeToBePatched - (double)CurrentSizePatched) / UnZeroed(Speed));
        private static long     UnZeroed(long input) => Math.Max(input, 1);
    }
}

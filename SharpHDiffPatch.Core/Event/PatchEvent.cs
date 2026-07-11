using System;
using System.Threading;

namespace SharpHDiffPatch.Core.Event
{
    public class LoggerEvent(string message, Verbosity logLevel)
    {
        public Verbosity LogLevel = logLevel;
        public string Message = message;
    }

    public sealed class PatchEvent
    {
        private const double ScOneSecond = 1000;
        private       long   _scLastTick = Environment.TickCount;
        private       long   _scLastReceivedBytes;
        private       double _scLastSpeed;

        public void UpdateEvent(long currentSizePatched, long totalSizeToBePatched, long read, double totalSecond)
        {
            Speed                = (long)(currentSizePatched / totalSecond);
            CurrentSizePatched   = currentSizePatched;
            TotalSizeToBePatched = totalSizeToBePatched;
            Read                 = read;
        }

        private long _currentSizePatched;
        public long CurrentSizePatched
        {
            get => _currentSizePatched;
            private set
            {
                Speed               = (long)CalculateSpeed(value - _currentSizePatched);
                TimeLeft            = TimeSpan.FromSeconds((TotalSizeToBePatched - value) / UnZeroed(Speed));
                ProgressPercentage  = Math.Round(value / (double)TotalSizeToBePatched * 100, 2);
                _currentSizePatched = value;
            }
        }

        public         long     TotalSizeToBePatched   { get; private set; }
        public         double   ProgressPercentage     { get; private set; }
        public         long     Read                   { get; private set; }
        public         long     Speed                  { get; private set; }
        public         TimeSpan TimeLeft               { get; private set; }
        private static double   UnZeroed(double input) => Math.Max(input, 1);

        private double CalculateSpeed(long receivedBytes) => CalculateSpeed(receivedBytes, ref _scLastSpeed, ref _scLastReceivedBytes, ref _scLastTick);

        private static double CalculateSpeed(long receivedBytes, ref double lastSpeedToUse, ref long lastReceivedBytesToUse, ref long lastTickToUse)
        {
            long   currentTick           = Environment.TickCount - lastTickToUse + 1;
            long   totalReceivedInSecond = Interlocked.Add(ref lastReceivedBytesToUse, receivedBytes);
            double speed                 = totalReceivedInSecond * ScOneSecond / currentTick;

            if (!(currentTick > ScOneSecond))
            {
                return lastSpeedToUse;
            }

            lastSpeedToUse = speed;
            _              = Interlocked.Exchange(ref lastSpeedToUse,         speed);
            _              = Interlocked.Exchange(ref lastReceivedBytesToUse, 0);
            _              = Interlocked.Exchange(ref lastTickToUse,          Environment.TickCount);
            return lastSpeedToUse;
        }
    }
}

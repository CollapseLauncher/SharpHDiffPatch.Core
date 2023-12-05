using System;

namespace ManagedLzma.LZMA.Master
{
    partial class LZMA
    {
        #region CThread

        internal sealed class CThread
        {
        }

        internal static void Thread_Construct(out CThread p)
        {
            p = null;
        }

        internal static bool Thread_WasCreated(CThread p)
        {
            return p != null;
        }

        internal static void Thread_Close(ref CThread p)
        {
            p = null;
        }

        internal static SRes Thread_Wait(CThread p)
        {
            return default;
        }

        internal static SRes Thread_Create(out CThread p, Action func, string threadName)
        {
            p = new CThread();
            return default;
        }

        #endregion

        #region CEvent

        // this is a win32 autoreset event

        internal sealed class CEvent
        {
#if DISABLE_TRACE
            public System.Threading.AutoResetEvent Event;
#endif
        }

        internal static void Event_Construct(out CEvent p)
        {
            p = null;
        }

        internal static bool Event_IsCreated(CEvent p)
        {
            return p != null;
        }

        internal static void Event_Close(ref CEvent p)
        {
            if (p != null)
            {
#if !DISABLE_TRACE
#elif BUILD_PORTABLE
                p.Event.Dispose();
#else
                p.Event.Close();
#endif
            }
            p = null;
        }

        internal static SRes Event_Wait(CEvent p)
        {
#if !DISABLE_TRACE
#else
            p.Event.WaitOne();
#endif
            return default;
        }

        internal static SRes Event_Set(CEvent p)
        {
#if !DISABLE_TRACE
#else
            p.Event.Set();
#endif
            return default;
        }

        internal static SRes Event_Reset(CEvent p)
        {
#if !DISABLE_TRACE
#else
            p.Event.Reset();
#endif
            return default;
        }

        internal static SRes AutoResetEvent_CreateNotSignaled(out CEvent p)
        {
            p = new CEvent();
#if !DISABLE_TRACE
#else
            p.Event = new System.Threading.AutoResetEvent(false);
#endif
            return default;
        }

        #endregion

        #region CSemaphore

        internal sealed class CSemaphore
        {
#if DISABLE_TRACE
            public System.Threading.Semaphore Semaphore;
#endif
        }

        internal static void Semaphore_Construct(out CSemaphore p)
        {
            p = null;
        }

        internal static void Semaphore_Close(ref CSemaphore p)
        {
            if (p != null)
            {
#if !DISABLE_TRACE
#elif BUILD_PORTABLE
                p.Semaphore.Dispose();
#else
                p.Semaphore.Close();
#endif
            }

            p = null;
        }

        internal static SRes Semaphore_Wait(CSemaphore p)
        {
#if !DISABLE_TRACE
#else
            p.Semaphore.WaitOne();
#endif
            return default;
        }

        internal static SRes Semaphore_Create(out CSemaphore p, uint initCount, uint maxCount)
        {
            p = new CSemaphore();
#if !DISABLE_TRACE
#else
            p.Semaphore = new System.Threading.Semaphore(checked((int)initCount), checked((int)maxCount));
#endif
            return default;
        }

        internal static SRes Semaphore_Release1(CSemaphore p)
        {
#if !DISABLE_TRACE
#else
            p.Semaphore.Release();
#endif
            return default;
        }

        #endregion

        #region CCriticalSection

        internal sealed class CCriticalSection { }

        internal static SRes CriticalSection_Init(out CCriticalSection p)
        {
            p = new CCriticalSection();
            return SZ_OK; // never fails in C code either
        }

        internal static void CriticalSection_Delete(CCriticalSection p)
        {
        }

        internal static void CriticalSection_Enter(CCriticalSection p)
        {
        }

        internal static void CriticalSection_Leave(CCriticalSection p)
        {
#if !DISABLE_TRACE
#else
            System.Threading.Monitor.Exit(p);
#endif
        }

        #endregion
    }
}

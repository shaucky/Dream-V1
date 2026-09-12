using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
#if WINDOWS
using Dream.Studio.Engine.Native;
#endif

namespace Dream.Studio.Engine
{
#if WINDOWS
    /// <summary>
    /// Windows Job Object 守卫：把 ADL 进程加入 Job Object，设置 KILL_ON_JOB_CLOSE。
    /// 当 Studio 进程结束（含崩溃、VS 主动停止）时，内核自动关闭 Job Object 句柄，
    /// 触发 Job 内所有进程被终止——无需依赖 Studio 正常走 Closed → Stop() 链路。
    /// 这是内核级保证：只要 Studio 进程死亡，ADL 必然被清理，不会残留占用 .workspace。
    /// </summary>
    internal sealed class JobObjectGuard : IDisposable
    {
        private IntPtr _hJob;

        public JobObjectGuard()
        {
            var sa = new Win32.SECURITY_ATTRIBUTES { nLength = (uint)Marshal.SizeOf<Win32.SECURITY_ATTRIBUTES>() };
            _hJob = Win32.CreateJobObject(ref sa, null);
            if (_hJob == IntPtr.Zero) return;

            // KILL_ON_JOB_CLOSE：最后一个指向 Job 的句柄关闭时，终止 Job 内全部进程。
            var info = new Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new Win32.JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = Win32.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                }
            };
            Win32.SetInformationJobObject(_hJob, Win32.JobObjectExtendedLimitInformation,
                ref info, (uint)Marshal.SizeOf<Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        }

        /// <summary>把进程加入 Job Object。失败时静默——主链路 Stop() 仍是兜底。</summary>
        public void Assign(Process process)
        {
            if (_hJob == IntPtr.Zero || process.HasExited) return;
            try { Win32.AssignProcessToJobObject(_hJob, process.Handle); }
            catch { /* 进程已退出或权限不足 */ }
        }

        public void Dispose()
        {
            if (_hJob != IntPtr.Zero)
            {
                Win32.CloseHandle(_hJob);
                _hJob = IntPtr.Zero;
            }
        }
    }
#else
    /// <summary>非 Windows 平台空实现。</summary>
    internal sealed class JobObjectGuard : IDisposable
    {
        public void Assign(Process process) { }
        public void Dispose() { }
    }
#endif
}

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClaudeVS.Chat
{
    /// <summary>
    /// Job Windows « tuer à la fermeture » : les processus qui y sont affectés (et leurs descendants) meurent
    /// quand le dernier handle du job est fermé, donc aussi si devenv disparaît brutalement.
    /// </summary>
    internal sealed class JobObject : IDisposable
    {
        private readonly SafeFileHandle _handle;

        private JobObject(SafeFileHandle handle) => _handle = handle;

        /// <summary>Crée le job et y affecte le processus. Retourne null (sans lever) si l'OS refuse.</summary>
        public static JobObject TryCreateFor(Process process, Log log)
        {
            SafeFileHandle handle = null;
            try
            {
                handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == null || handle.IsInvalid) throw new Win32Exception();

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
                };
                var size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ptr, (uint)size)) throw new Win32Exception();
                }
                finally { Marshal.FreeHGlobal(ptr); }

                if (!AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception();
                return new JobObject(handle);
            }
            catch (Exception ex)
            {
                // Job parent sans jobs imbriqués (Windows 7), stratégie de sécurité… : on continue sans
                log?.Info("Job Object indisponible pour claude : " + ex.Message);
                handle?.Dispose();
                return null;
            }
        }

        /// <summary>Ferme le job, ce qui tue tous les processus qu'il contient.</summary>
        public void Dispose() => _handle.Dispose();

        // ---- P/Invoke ------------------------------------------------------------------------

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(SafeFileHandle hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, IntPtr hProcess);
    }
}

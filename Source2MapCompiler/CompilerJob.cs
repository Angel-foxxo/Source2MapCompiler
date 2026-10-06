using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.System.JobObjects;

namespace Source2MapCompiler;

// Ties resourcecompiler to the app's lifetime. Processes added here go in a Windows job object that kills everything in it
// once the job's handle closes, and Windows closes that handle when the app exits for any reason, including a crash, being
// ended from Task Manager or the debugger stopping. Processes started by those processes, like vrad3, are put in the same
// job by Windows, so they go too. The handle is never closed by the app itself, it only goes away with the process
internal static class CompilerJob
{
    private static readonly Lazy<SafeFileHandle> Job = new(Create);

    // Called right after the process starts, before resourcecompiler has started anything of its own
    public static void Add(Process process)
    {
        if (!PInvoke.AssignProcessToJobObject(Job.Value, process.SafeHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject");
        }
    }

    private static SafeFileHandle Create()
    {
        var job = PInvoke.CreateJobObject(null, null);

        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject");
        }

        // The same flags Source 2's tier0 sets on the job it puts its own child processes in. Allowing breakaway lets a
        // process that asks to leave the job, like a crash reporter, still start, instead of failing because the job won't
        // let it go
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_BREAKAWAY_OK },
        };

        if (!PInvoke.SetInformationJobObject(job, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, MemoryMarshal.AsBytes(new ReadOnlySpan<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(in limits))))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject");
        }

        return job;
    }
}

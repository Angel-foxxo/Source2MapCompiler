using Windows.Wdk.System.Threading;
using Windows.Win32;
using Windows.Win32.System.Threading;
using Wdk = Windows.Wdk.PInvoke;

namespace Source2MapCompiler.LightmapPreview;

// The app only runs one compile at a time, but Hammer or another copy of the app can be baking too, so this makes sure we
// read the vrad3 our compile started
internal static class ProcessTree
{
    public static bool IsDescendantOf(int processId, int ancestorId)
    {
        // resourcecompiler starts vrad3 either directly or through a helper, so a few levels up is enough
        for (var depth = 0; depth < 8; depth++)
        {
            var parent = ParentOf(processId);

            if (parent == ancestorId)
            {
                return true;
            }

            if (parent <= 0 || parent == processId)
            {
                return false;
            }

            processId = parent;
        }

        return false;
    }

    private static unsafe int ParentOf(int processId)
    {
        var handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);

        if (handle.IsNull)
        {
            return 0;
        }

        try
        {
            PROCESS_BASIC_INFORMATION information;
            var status = Wdk.NtQueryInformationProcess(handle, PROCESSINFOCLASS.ProcessBasicInformation, &information, (uint)sizeof(PROCESS_BASIC_INFORMATION), null);
            return status.SeverityCode == Windows.Win32.Foundation.NTSTATUS.Severity.Success ? (int)information.InheritedFromUniqueProcessId : 0;
        }
        finally
        {
            PInvoke.CloseHandle(handle);
        }
    }
}

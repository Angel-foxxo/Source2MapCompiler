using System.Runtime.InteropServices;
using System.Runtime.Versioning;

// the version is the oldest Windows the generated Win32 imports say they need, which .NET itself is long past
[assembly: SupportedOSPlatform("windows5.1.2600")]
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

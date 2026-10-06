using System.Runtime.Versioning;

// Source 2's tools only run on Windows, and the version is the oldest Windows the generated Win32 imports say they need,
// which .NET itself is long past
[assembly: SupportedOSPlatform("windows5.1.2600")]

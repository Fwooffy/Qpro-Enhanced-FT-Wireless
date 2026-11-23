using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace QproFaceTracking.Hub;

internal sealed class SteamVrOpenVrApplications : ISteamVrApplications
{
    // Valve's public OpenVR ABI. Background clients can connect to an
    // already running SteamVR session and never start it. The application
    // function table below is specific to IVRApplications_008.
    // https://github.com/ValveSoftware/openvr/blob/master/headers/openvr_capi.h
    private const int BackgroundApplication = 3;
    private const int BinaryPathProperty = 13;
    private static readonly object ConnectionLock = new();
    private readonly nint _loader;
    private readonly Shutdown _shutdown;
    private readonly Add _add;
    private readonly Remove _remove;
    private readonly IsInstalled _installed;
    private readonly GetString _getString;
    private readonly SetAuto _setAuto;
    private readonly GetAuto _getAuto;
    private bool _disposed;

    private SteamVrOpenVrApplications(nint loader, Shutdown shutdown, nint table)
    {
        _loader = loader;
        _shutdown = shutdown;
        _add = Method<Add>(table, 0);
        _remove = Method<Remove>(table, 1);
        _installed = Method<IsInstalled>(table, 2);
        _getString = Method<GetString>(table, 14);
        _setAuto = Method<SetAuto>(table, 17);
        _getAuto = Method<GetAuto>(table, 18);
    }

    internal static ISteamVrApplications Open()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new InvalidOperationException("SteamVR startup requires the Windows x64 Companion.");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VR_PATHREG_OVERRIDE")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VR_OVERRIDE")))
            throw new InvalidOperationException("Use the registered SteamVR runtime to manage Companion startup.");
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(local))
            throw new InvalidOperationException("SteamVR's Windows user profile is unavailable.");
        using var paths = OpenRegisteredPaths(Path.Combine(local, "openvr", "openvrpaths.vrpath"));
        var runtime = FindRegisteredRuntime(paths)
            ?? throw new InvalidOperationException("A registered SteamVR installation was not found. Install and open SteamVR once, then retry.");
        string binary = Path.Combine(runtime, "bin", "win64");
        if (!SteamVrRunning(Path.Combine(binary, "vrserver.exe")))
            throw new InvalidOperationException("Open SteamVR to check or change its startup setting.");

        // The OpenVR loader uses the same registered path file. Hold that
        // file plus both validated libraries through initialization.
        string loaderPath = Path.Combine(binary, "openvr_api.dll");
        using var loaderFile = File.OpenRead(loaderPath);
        using var clientFile = File.OpenRead(Path.Combine(runtime, "bin", "vrclient_x64.dll"));
        if (!SteamVrValveTrust.IsSignedByValve(loaderFile) || !SteamVrValveTrust.IsSignedByValve(clientFile))
            throw new InvalidOperationException("SteamVR's OpenVR libraries could not be verified as Valve files. Verify SteamVR in Steam.");

        // VR_Init/VR_Shutdown manage process-global state in the OpenVR
        // loader. A status read and a change must not overlap.
        Monitor.Enter(ConnectionLock);
        nint loader = 0;
        Shutdown? shutdown = null;
        bool initialized = false;
        try
        {
            const uint searchDllDirectoryAndSystem = 0x00000100 | 0x00000800;
            loader = LoadLibraryEx(loaderPath, 0, searchDllDirectoryAndSystem);
            if (loader == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SteamVR's OpenVR library could not be loaded.");
            var init = Export<Init>(loader, "VR_InitInternal");
            shutdown = Export<Shutdown>(loader, "VR_ShutdownInternal");
            var getInterface = Export<GetInterface>(loader, "VR_GetGenericInterface");
            init(out int error, BackgroundApplication);
            if (error != 0)
                throw new InvalidOperationException($"SteamVR is not ready for startup registration (OpenVR initialization error {error}).");
            initialized = true;
            var table = getInterface("FnTable:IVRApplications_008", out error);
            if (table == 0 || error != 0)
                throw new InvalidOperationException($"SteamVR does not provide its OpenVR applications interface (error {error}). Verify or update SteamVR.");
            return new SteamVrOpenVrApplications(loader, shutdown, table);
        }
        catch
        {
            try { if (initialized) shutdown!(); }
            finally
            {
                if (loader != 0) NativeLibrary.Free(loader);
                Monitor.Exit(ConnectionLock);
            }
            throw;
        }
    }

    private static FileStream OpenRegisteredPaths(string path)
    {
        try { return File.OpenRead(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("SteamVR's registered runtime path could not be read. Open SteamVR once, then retry.", error);
        }
    }

    internal static string? FindRegisteredRuntime(Stream file)
    {
        if (file.Length is <= 0 or > 65536) return null;
        try
        {
            using var paths = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 8 });
            if (paths.RootElement.ValueKind != JsonValueKind.Object ||
                !paths.RootElement.TryGetProperty("runtime", out var roots) || roots.ValueKind != JsonValueKind.Array ||
                roots.GetArrayLength() == 0 || roots[0].ValueKind != JsonValueKind.String)
                return null;
            // Valve uses index zero; later entries are history. Decline
            // device paths, UNC shares and arbitrary relative load paths.
            string? first = roots[0].GetString();
            if (first is not { Length: >= 3 } || first[1] != ':' || !Path.IsPathFullyQualified(first))
                return null;
            string runtime = Path.GetFullPath(first);
            string bin = Path.Combine(runtime, "bin");
            string x64 = Path.Combine(bin, "win64");
            return File.Exists(Path.Combine(x64, "vrserver.exe")) &&
                File.Exists(Path.Combine(bin, "vrclient_x64.dll")) &&
                File.Exists(Path.Combine(x64, "openvr_api.dll")) ? runtime : null;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return null; }
    }

    private static bool SteamVrRunning(string server)
    {
        var processes = Process.GetProcessesByName("vrserver");
        try
        {
            using var current = Process.GetCurrentProcess();
            foreach (var process in processes)
            {
                try
                {
                    if (process.SessionId == current.SessionId &&
                        string.Equals(process.MainModule?.FileName, server, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            }
            return false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public SteamVrApplication? Read(string appKey)
    {
        if (!_installed(appKey)) return null;
        var buffer = new byte[32768];
        uint size = _getString(appKey, BinaryPathProperty, buffer, (uint)buffer.Length, out int error);
        Check(error, "read the Companion startup app");
        if (size < 2 || size > buffer.Length || buffer[(int)size - 1] != 0)
            throw new InvalidOperationException("SteamVR returned an invalid Companion startup path.");
        return new(Encoding.UTF8.GetString(buffer, 0, (int)size - 1), _getAuto(appKey));
    }

    public void AddManifest(string path) => Check(_add(path, false), "register the Companion startup app");
    public void RemoveManifest(string path) => Check(_remove(path), "remove the Companion startup app");
    public void SetAutoLaunch(string appKey, bool enabled) => Check(_setAuto(appKey, enabled), "change its auto-launch setting");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _shutdown(); }
        finally
        {
            NativeLibrary.Free(_loader);
            Monitor.Exit(ConnectionLock);
        }
    }

    private static T Export<T>(nint library, string name) where T : Delegate =>
        NativeLibrary.TryGetExport(library, name, out var pointer)
            ? Marshal.GetDelegateForFunctionPointer<T>(pointer)
            : throw new InvalidOperationException("SteamVR's OpenVR library is incomplete. Verify or update SteamVR.");

    private static T Method<T>(nint table, int index) where T : Delegate
    {
        var pointer = Marshal.ReadIntPtr(table, index * IntPtr.Size);
        if (pointer == 0) throw new InvalidOperationException("SteamVR's OpenVR applications interface is incomplete.");
        return Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    private static void Check(int error, string operation)
    {
        if (error != 0) throw new InvalidOperationException($"SteamVR could not {operation} (OpenVR error {error}).");
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string path, nint file, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint Init(out int error, int application);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Shutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetInterface([MarshalAs(UnmanagedType.LPUTF8Str)] string version, out int error);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Add([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.I1)] bool temporary);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Remove([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool IsInstalled([MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetString([MarshalAs(UnmanagedType.LPUTF8Str)] string key, int property,
        [Out] byte[] buffer, uint bufferSize, out int error);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetAuto([MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool GetAuto([MarshalAs(UnmanagedType.LPUTF8Str)] string key);
}

internal static class SteamVrValveTrust
{
    // WinVerifyTrust verifies the selected file and its actual verified
    // signing chain. A publisher string or file metadata alone is insufficient.
    internal static bool IsSignedByValve(FileStream file)
    {
        var policy = new Guid("00aac56b-cd44-11d0-8cc2-00c04fc295ee");
        var path = Marshal.StringToCoTaskMemUni(file.Name);
        var fileInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf<TrustFile>());
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(),
            UIChoice = 2, // WTD_UI_NONE
            ContentChoice = 1, // WTD_CHOICE_FILE
            File = fileInfo,
            StateAction = 1, // WTD_STATEACTION_VERIFY
            ProviderFlags = 0x1000 | 0x10, // WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE
        };
        try
        {
            Marshal.StructureToPtr(new TrustFile((uint)Marshal.SizeOf<TrustFile>(), path,
                file.SafeFileHandle.DangerousGetHandle(), 0), fileInfo, false);
            if (WinVerifyTrust(new nint(-1), ref policy, ref data) != 0) return false;
            nint provider = WTHelperProvDataFromStateData(data.State);
            nint signer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            nint certificate = signer == 0 ? 0 : WTHelperGetProvCertFromChain(signer, 0);
            if (certificate == 0) return false;
            nint context = Marshal.PtrToStructure<ProviderCertificate>(certificate).CertificateContext;
            if (context == 0) return false;
            using var verified = new X509Certificate2(context);
            return verified.GetNameInfo(X509NameType.SimpleName, false) is "Valve Corp." or "Valve Corporation";
        }
        catch (CryptographicException) { return false; }
        finally
        {
            data.StateAction = 2; // WTD_STATEACTION_CLOSE
            WinVerifyTrust(new nint(-1), ref policy, ref data);
            Marshal.FreeCoTaskMem(fileInfo);
            Marshal.FreeCoTaskMem(path);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TrustFile(uint Size, nint Path, nint Handle, nint KnownSubject);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ProviderCertificate(uint Size, nint CertificateContext);

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        internal uint Size;
        internal nint PolicyCallback, SipClient;
        internal uint UIChoice, RevocationChecks, ContentChoice;
        internal nint File;
        internal uint StateAction;
        internal nint State, UrlReference;
        internal uint ProviderFlags, UIContext;
        internal nint SignatureSettings;
    }

    [DllImport("wintrust.dll")]
    private static extern int WinVerifyTrust(nint window, ref Guid policy, ref TrustData data);
    [DllImport("wintrust.dll")]
    private static extern nint WTHelperProvDataFromStateData(nint state);
    [DllImport("wintrust.dll")]
    private static extern nint WTHelperGetProvSignerFromChain(nint provider, uint index, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);
    [DllImport("wintrust.dll")]
    private static extern nint WTHelperGetProvCertFromChain(nint signer, uint index);
}

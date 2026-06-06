#include "framework.h"
#include <string>

static std::wstring g_payloadPath;
static std::wstring g_dllDir;

DWORD WINAPI ClrHostThread(LPVOID /*lpParam*/)
{
    ICLRMetaHost*    pMetaHost    = nullptr;
    ICLRRuntimeInfo* pRuntimeInfo = nullptr;
    ICLRRuntimeHost* pRuntimeHost = nullptr;
    HRESULT hr;
    DWORD   retVal = 0;

    // 1. 获取 CLR MetaHost
    hr = CLRCreateInstance(CLSID_CLRMetaHost, IID_ICLRMetaHost, reinterpret_cast<LPVOID*>(&pMetaHost));
    if (FAILED(hr))
    {
        return 1;
    }

    // 2. 获取 v4.0.30319 运行时
    hr = pMetaHost->GetRuntime(L"v4.0.30319", IID_ICLRRuntimeInfo, reinterpret_cast<LPVOID*>(&pRuntimeInfo));
    if (FAILED(hr))
    {
        pMetaHost->Release();
        return 2;
    }

    // 3. 获取 Runtime Host 接口
    hr = pRuntimeInfo->GetInterface(CLSID_CLRRuntimeHost, IID_ICLRRuntimeHost, reinterpret_cast<LPVOID*>(&pRuntimeHost));
    if (FAILED(hr))
    {
        pRuntimeInfo->Release();
        pMetaHost->Release();
        return 3;
    }

    // 4. 启动运行时（如果已启动则为 no-op）
    hr = pRuntimeHost->Start();
    if (FAILED(hr))
    {
        pRuntimeHost->Release();
        pRuntimeInfo->Release();
        pMetaHost->Release();
        return 4;
    }

    // 5. 在默认 AppDomain 中执行托管方法
    hr = pRuntimeHost->ExecuteInDefaultAppDomain(
        g_payloadPath.c_str(),
        L"Payload.EntryPoint",
        L"LinkStart",
        g_dllDir.c_str(),
        &retVal);

    pRuntimeHost->Release();
    pRuntimeInfo->Release();
    pMetaHost->Release();
    return retVal;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID /*lpReserved*/)
{
    if (ul_reason_for_call == DLL_PROCESS_ATTACH)
    {
        // 获取本 DLL 的完整路径，推导 PayloadDLL.dll 和 DLL 目录
        WCHAR dllPath[MAX_PATH];
        GetModuleFileNameW(hModule, dllPath, MAX_PATH);
        std::wstring fullPath(dllPath);
        size_t pos = fullPath.find_last_of(L"\\/");
        g_dllDir = fullPath.substr(0, pos + 1);
        g_payloadPath = g_dllDir + L"PayloadDLL.dll";

        DisableThreadLibraryCalls(hModule);

        // DllMain 中不能做 CLR Hosting（loader lock），创建独立线程
        HANDLE hThread = CreateThread(nullptr, 0, ClrHostThread, nullptr, 0, nullptr);
        if (hThread)
            CloseHandle(hThread);
    }
    return TRUE;
}

// ================================================================
//  导出函数 — 供注入器在 DLL 已驻留时直接调用
//  绕过 LoadLibraryW→DllMain 路径（LoadLibraryW 对已加载 DLL 不会再次调用 DllMain）
// ================================================================
extern "C" __declspec(dllexport) DWORD WINAPI BootstrapStart(LPVOID /*lpParam*/)
{
    // 与 DllMain(DLL_PROCESS_ATTACH) 完全相同的逻辑：
    // 创建独立线程执行 CLR Hosting，避免阻塞远程线程。
    HANDLE hThread = CreateThread(nullptr, 0, ClrHostThread, nullptr, 0, nullptr);
    if (hThread)
        CloseHandle(hThread);
    return 0;
}

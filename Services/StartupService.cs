using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;

namespace XrayUI.Services
{
    /// <summary>
    /// Manages autostart via a per-user Task Scheduler task, using direct COM
    /// vtable dispatch (no RCW / ComWrappers) so it works cleanly under
    /// NativeAOT without needing BuiltInComInteropSupport.
    /// </summary>
    public unsafe class StartupService
    {
        // Shared with App.xaml.cs so the flag string lives in exactly one place.
        // The boot task always passes this; auto-connect-on-boot is a separate
        // setting (AppSettings.IsAutoConnect) evaluated by MainViewModel.
        public const string StartupMinimizedArgument = "--startup-minimized";

        private const string TaskName = "XrayUI_Autostart";
        private const int TASK_CREATE_OR_UPDATE        = 6;
        private const int TASK_LOGON_INTERACTIVE_TOKEN = 3;
        private const uint CLSCTX_INPROC_SERVER        = 0x1;
        private const int E_FILENOTFOUND               = unchecked((int)0x80070002);

        private static readonly Guid CLSID_TaskScheduler = new("0F87369F-A4E5-4CFC-BD3E-73E6154572DD");
        private static readonly Guid IID_ITaskService    = new("2FABA4C7-4DA9-4013-9697-20CC3FD40F85");

        private static readonly string _exePath =
            Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

        public bool IsStartupEnabled()
        {
            try
            {
                IntPtr service = CreateAndConnectService();
                try
                {
                    IntPtr folder = TaskServiceGetFolder(service, @"\");
                    try
                    {
                        int hr = TaskFolderGetTask(folder, TaskName, out IntPtr pTask);
                        if (hr == E_FILENOTFOUND) return false;
                        Marshal.ThrowExceptionForHR(hr);
                        Release(pTask);
                        return true;
                    }
                    finally { Release(folder); }
                }
                finally { Release(service); }
            }
            catch
            {
                return false;
            }
        }

        public void SetStartupEnabled(bool enabled)
        {
            if (enabled)
            {
                if (string.IsNullOrEmpty(_exePath))
                    throw new InvalidOperationException("Cannot resolve exe path.");
                RegisterTaskXml();
            }
            else
            {
                DeleteTaskIfExists();
            }
        }

        private static void RegisterTaskXml()
        {
            IntPtr service = CreateAndConnectService();
            try
            {
                IntPtr folder = TaskServiceGetFolder(service, @"\");
                try
                {
                    IntPtr registered = TaskFolderRegisterTask(
                        folder, TaskName, BuildTaskXml(),
                        TASK_CREATE_OR_UPDATE, TASK_LOGON_INTERACTIVE_TOKEN);
                    Release(registered);
                }
                finally { Release(folder); }
            }
            finally { Release(service); }
        }

        private static void DeleteTaskIfExists()
        {
            IntPtr service = CreateAndConnectService();
            try
            {
                IntPtr folder = TaskServiceGetFolder(service, @"\");
                try
                {
                    int hr = TaskFolderDeleteTask(folder, TaskName);
                    if (hr == E_FILENOTFOUND) return;
                    Marshal.ThrowExceptionForHR(hr);
                }
                finally { Release(folder); }
            }
            finally { Release(service); }
        }

        // ── COM vtable dispatch ───────────────────────────────────────────────
        //
        // Vtable layout for an IDispatch-derived interface:
        //   [0..2]  IUnknown     (QueryInterface, AddRef, Release)
        //   [3..6]  IDispatch    (GetTypeInfoCount, GetTypeInfo, GetIDsOfNames, Invoke)
        //   [7..]   interface-specific methods in IDL declaration order
        //
        // ITaskService:  GetFolder=7, GetRunningTasks=8, NewTask=9, Connect=10, ...
        // ITaskFolder:   get_Name=7, get_Path=8, GetFolder=9, GetFolders=10,
        //                CreateFolder=11, DeleteFolder=12, GetTask=13, GetTasks=14,
        //                DeleteTask=15, RegisterTask=16, ...

        private static IntPtr CreateAndConnectService()
        {
            Guid clsid = CLSID_TaskScheduler;
            Guid iid   = IID_ITaskService;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out IntPtr pService);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                Variant empty = default;
                void** vtbl = *(void***)pService;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, Variant, Variant, Variant, Variant, int>)vtbl[10];
                int connectHr = fn(pService, empty, empty, empty, empty);
                Marshal.ThrowExceptionForHR(connectHr);
                return pService;
            }
            catch
            {
                Release(pService);
                throw;
            }
        }

        private static IntPtr TaskServiceGetFolder(IntPtr pService, string path)
        {
            IntPtr bstr = Marshal.StringToBSTR(path);
            try
            {
                IntPtr pFolder;
                void** vtbl = *(void***)pService;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)vtbl[7];
                int hr = fn(pService, bstr, &pFolder);
                Marshal.ThrowExceptionForHR(hr);
                return pFolder;
            }
            finally { Marshal.FreeBSTR(bstr); }
        }

        private static int TaskFolderGetTask(IntPtr pFolder, string name, out IntPtr pTask)
        {
            IntPtr bstr = Marshal.StringToBSTR(name);
            try
            {
                IntPtr task;
                void** vtbl = *(void***)pFolder;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)vtbl[13];
                int hr = fn(pFolder, bstr, &task);
                pTask = task;
                return hr;
            }
            finally { Marshal.FreeBSTR(bstr); }
        }

        private static int TaskFolderDeleteTask(IntPtr pFolder, string name)
        {
            IntPtr bstr = Marshal.StringToBSTR(name);
            try
            {
                void** vtbl = *(void***)pFolder;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int>)vtbl[15];
                return fn(pFolder, bstr, 0);
            }
            finally { Marshal.FreeBSTR(bstr); }
        }

        private static IntPtr TaskFolderRegisterTask(
            IntPtr pFolder, string name, string xml, int createFlags, int logonType)
        {
            IntPtr bstrName = Marshal.StringToBSTR(name);
            IntPtr bstrXml  = Marshal.StringToBSTR(xml);
            try
            {
                Variant empty = default;
                IntPtr registered;
                void** vtbl = *(void***)pFolder;
                var fn = (delegate* unmanaged[Stdcall]<
                    IntPtr, IntPtr, IntPtr, int,
                    Variant, Variant, int, Variant,
                    IntPtr*, int>)vtbl[16];
                int hr = fn(pFolder, bstrName, bstrXml, createFlags,
                            empty, empty, logonType, empty, &registered);
                Marshal.ThrowExceptionForHR(hr);
                return registered;
            }
            finally
            {
                Marshal.FreeBSTR(bstrName);
                Marshal.FreeBSTR(bstrXml);
            }
        }

        private static uint Release(IntPtr pUnk)
        {
            if (pUnk == IntPtr.Zero) return 0;
            void** vtbl = *(void***)pUnk;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2];
            return fn(pUnk);
        }

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoCreateInstance(
            ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
            ref Guid riid, out IntPtr ppv);

        // Matches Win32 VARIANT: 16 bytes on x86, 24 on x64/ARM64.
        // Zero-initialized → VT_EMPTY, which is what ITaskService/Register expect
        // for "use default" in their optional VARIANT parameters.
        [StructLayout(LayoutKind.Sequential)]
        private struct Variant
        {
            public ushort vt;
            public ushort r1, r2, r3;
            public IntPtr data1;
            public IntPtr data2;
        }

        // ── Task XML ──────────────────────────────────────────────────────────

        private static string BuildTaskXml()
        {
            var sid        = WindowsIdentity.GetCurrent().User?.Value ?? "";
            var exe        = SecurityElement.Escape(_exePath);
            var workingDir = SecurityElement.Escape(Path.GetDirectoryName(_exePath) ?? "");

            // ExecutionTimeLimit=PT0S avoids Windows' 72h default killing the app
            // on long-running sessions. The other two Settings defaults (battery
            // behavior) would otherwise refuse to start / kill us on unplug.
            return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{sid}</UserId>
      <Delay>PT5S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{sid}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{exe}</Command>
      <Arguments>{StartupMinimizedArgument}</Arguments>
      <WorkingDirectory>{workingDir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
        }
    }
}

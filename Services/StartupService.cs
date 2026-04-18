using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;

namespace XrayUI.Services
{
    public class StartupService
    {
        private const string TaskName = "XrayUI_Autostart";
        private const string RootFolder = "\\";

        private static readonly string _exePath =
            Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

        public bool IsStartupEnabled()
        {
            return TaskExists();
        }

        public void SetStartupEnabled(bool enabled, bool minimizeOnBoot = false)
        {
            try
            {
                if (enabled)
                {
                    if (string.IsNullOrEmpty(_exePath))
                        throw new InvalidOperationException("Cannot resolve exe path.");
                    CreateTask(minimizeOnBoot);
                }
                else
                {
                    DeleteTask();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Startup] SetStartupEnabled({enabled}) failed: {ex.Message}");
            }
        }

        private static bool TaskExists()
        {
            ITaskService? service = null;
            ITaskFolder? folder = null;
            IRegisteredTask? task = null;
            try
            {
                service = CreateTaskService();
                folder = service.GetFolder(RootFolder);
                task = folder.GetTask(TaskName);
                // A task that exists but is disabled in Task Scheduler won't be run
                // by Windows at logon, so we treat that as "startup off" for UI/state.
                return task != null && task.Enabled;
            }
            catch
            {
                return false;
            }
            finally
            {
                ReleaseCom(task);
                ReleaseCom(folder);
                ReleaseCom(service);
            }
        }

        private static void CreateTask(bool minimizeOnBoot)
        {
            var xml = BuildTaskXml(minimizeOnBoot);

            ITaskService? service = null;
            ITaskFolder? folder = null;
            IRegisteredTask? task = null;
            try
            {
                service = CreateTaskService();
                folder = service.GetFolder(RootFolder);
                task = folder.RegisterTask(
                    TaskName,
                    xml,
                    (int)TASK_CREATION.TASK_CREATE_OR_UPDATE,
                    null,
                    null,
                    TASK_LOGON_TYPE.TASK_LOGON_INTERACTIVE_TOKEN,
                    null);
                // CREATE_OR_UPDATE preserves the existing Enabled flag, so a task
                // the user previously disabled would stay disabled. Force it on.
                task.Enabled = true;
            }
            finally
            {
                ReleaseCom(task);
                ReleaseCom(folder);
                ReleaseCom(service);
            }
        }

        private static void DeleteTask()
        {
            ITaskService? service = null;
            ITaskFolder? folder = null;
            try
            {
                service = CreateTaskService();
                folder = service.GetFolder(RootFolder);
                folder.DeleteTask(TaskName, 0);
            }
            catch (Exception ex)
            {
                // Task may not exist — treat as best-effort
                Debug.WriteLine($"[Startup] DeleteTask: {ex.Message}");
            }
            finally
            {
                ReleaseCom(folder);
                ReleaseCom(service);
            }
        }

        // COM activation via CLSID does not go through a managed constructor — it calls
        // CoCreateInstance in native code. Trim analyzer's PublicParameterlessConstructor
        // requirement does not apply; the type is reached through [ComImport] interfaces.
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
            "Trimming", "IL2072:UnrecognizedReflectionPattern",
            Justification = "COM class activated via CLSID; no managed ctor required.")]
        private static ITaskService CreateTaskService()
        {
            var type = Type.GetTypeFromCLSID(new Guid("0F87369F-A4E5-4CFC-BD3E-73E6154572DD"))
                ?? throw new InvalidOperationException("Schedule.Service CLSID not registered.");
            var instance = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Failed to create Schedule.Service instance.");
            var service = (ITaskService)instance;
            service.Connect();
            return service;
        }

        private static void ReleaseCom(object? com)
        {
            if (com != null && Marshal.IsComObject(com))
            {
                try { Marshal.ReleaseComObject(com); } catch { /* best-effort */ }
            }
        }

        private static string BuildTaskXml(bool minimizeOnBoot)
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
            var exe = SecurityElement.Escape(_exePath);
            var workingDir = SecurityElement.Escape(Path.GetDirectoryName(_exePath) ?? "");
            var args = minimizeOnBoot ? "--startup-minimized" : "";

            // Only the 3 Settings below are kept because their Windows defaults would break us:
            //   DisallowStartIfOnBatteries (default true)  -> laptop on battery won't start the app
            //   StopIfGoingOnBatteries    (default true)  -> app gets killed when unplugged
            //   ExecutionTimeLimit        (default PT72H) -> app gets killed after 72h uptime
            return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{sid}</UserId>
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
      <Arguments>{args}</Arguments>
      <WorkingDirectory>{workingDir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
        }

        // ---------- Task Scheduler COM interop (minimal subset) ----------

        private enum TASK_CREATION
        {
            TASK_CREATE_OR_UPDATE = 6,
        }

        private enum TASK_LOGON_TYPE
        {
            TASK_LOGON_INTERACTIVE_TOKEN = 3,
        }

        [ComImport]
        [Guid("2FABA4C7-4DA9-4013-9697-20CC3FD40F85")]
        [InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface ITaskService
        {
            [return: MarshalAs(UnmanagedType.Interface)]
            ITaskFolder GetFolder([MarshalAs(UnmanagedType.BStr)] string path);

            [return: MarshalAs(UnmanagedType.Interface)]
            object GetRunningTasks(int flags);

            [return: MarshalAs(UnmanagedType.Interface)]
            object NewTask(uint flags);

            void Connect(
                [MarshalAs(UnmanagedType.Struct), In, Optional] object? serverName,
                [MarshalAs(UnmanagedType.Struct), In, Optional] object? user,
                [MarshalAs(UnmanagedType.Struct), In, Optional] object? domain,
                [MarshalAs(UnmanagedType.Struct), In, Optional] object? password);
        }

        [ComImport]
        [Guid("8CFAC062-A080-4C15-9A88-AA7C2AF80DFC")]
        [InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface ITaskFolder
        {
            string Name { get; }

            string Path { get; }

            [return: MarshalAs(UnmanagedType.Interface)]
            ITaskFolder GetFolder([MarshalAs(UnmanagedType.BStr)] string path);

            [return: MarshalAs(UnmanagedType.Interface)]
            object GetFolders(int flags);

            [return: MarshalAs(UnmanagedType.Interface)]
            ITaskFolder CreateFolder(
                [MarshalAs(UnmanagedType.BStr)] string subFolderName,
                [MarshalAs(UnmanagedType.Struct), In, Optional] object? sddl);

            void DeleteFolder(
                [MarshalAs(UnmanagedType.BStr)] string subFolderName,
                int flags);

            [return: MarshalAs(UnmanagedType.Interface)]
            IRegisteredTask GetTask([MarshalAs(UnmanagedType.BStr)] string path);

            [return: MarshalAs(UnmanagedType.Interface)]
            object GetTasks(int flags);

            void DeleteTask(
                [MarshalAs(UnmanagedType.BStr)] string name,
                int flags);

            [return: MarshalAs(UnmanagedType.Interface)]
            IRegisteredTask RegisterTask(
                [MarshalAs(UnmanagedType.BStr)] string path,
                [MarshalAs(UnmanagedType.BStr)] string xmlText,
                int flags,
                [MarshalAs(UnmanagedType.Struct), In, Optional] object? userId,
                [MarshalAs(UnmanagedType.Struct), In, Optional] object? password,
                TASK_LOGON_TYPE logonType,
                [MarshalAs(UnmanagedType.Struct), In, Optional] object? sddl);
        }

        [ComImport]
        [Guid("9C86F320-DEE3-4DD1-B972-A303F26B061E")]
        [InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IRegisteredTask
        {
            // Order must match the native IRegisteredTask vtable:
            // Name, Path, State, Enabled, ... (only the members we use are declared,
            // but every earlier slot must be present as a placeholder).
            string Name { get; }

            string Path { get; }

            int State { get; }

            bool Enabled
            {
                [return: MarshalAs(UnmanagedType.VariantBool)] get;
                [param: MarshalAs(UnmanagedType.VariantBool)] set;
            }
        }
    }
}

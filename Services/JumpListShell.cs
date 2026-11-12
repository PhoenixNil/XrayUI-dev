using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace XrayUI.Services;

public sealed record JumpListEntry(string Id, string Name);

/// <summary>
/// Unpackaged Win32 Jump Lists. Direct COM vtable calls also work in the NativeAOT release.
/// Shell objects stay on a single STA and every acquired interface is released there.
/// </summary>
public static unsafe class JumpListShell
{
    private static readonly Guid DestinationListClsid = new("77f10cf0-3db5-4966-b520-b7c54fd35ed6");
    private static readonly Guid DestinationListIid = new("6332debf-87b5-4670-90c0-5e57b408a49e");
    private static readonly Guid CollectionClsid = new("2d3468c1-36a7-43b6-ac24-d3f02fd9607a");
    private static readonly Guid CollectionIid = new("5632b1a4-e38a-400a-928a-d4cd63230295");
    private static readonly Guid ArrayIid = new("92ca9dcd-5622-4bba-a805-5e9f541bd8c9");
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-c000-000000000046");
    private static readonly Guid ShellLinkIid = new("000214f9-0000-0000-c000-000000000046");
    private static readonly Guid PropertyStoreIid = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    // PKEY_Title's format ID: the label Shell shows for a Jump List link.
    private static readonly Guid TitleFormatId = new("f29f85e0-4ff9-1068-ab91-08002b27b3d9");
    private const int AccessDenied = unchecked((int)0x80070005);

    private static readonly string ExePath = Environment.ProcessPath!;
    private static readonly string ExeDir = Path.GetDirectoryName(ExePath)!;
    private static readonly string IconPath = Path.Combine(ExeDir, "Assets", "Icons", "output.ico");

    // Vtable slots used below (IUnknown: QueryInterface=0, AddRef=1, Release=2):
    //   ICustomDestinationList  BeginList=4, AppendCategory=5, CommitList=8, AbortList=11
    //   IObjectArray            GetCount=3, GetAt=4
    //   IObjectCollection       AddObject=5
    //   IShellLinkW             SetWorkingDirectory=9, GetArguments=10, SetArguments=11,
    //                           SetIconLocation=17, SetPath=20
    //   IPropertyStore          SetValue=6, Commit=7

    /// <summary>Publishes <paramref name="items"/> and returns the node IDs Windows reported as
    /// removed by the user, after <paramref name="removeFromHistory"/> has persisted them.</summary>
    public static Task<IReadOnlySet<string>> PublishAsync(IReadOnlyList<JumpListEntry> items, string category,
        Action<IReadOnlySet<string>> removeFromHistory)
    {
        var completion = new TaskCompletionSource<IReadOnlySet<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            var initialized = false;
            try
            {
                Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2));
                initialized = true;
                completion.SetResult(Publish(items, category, removeFromHistory));
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally { if (initialized) CoUninitialize(); }
        }) { IsBackground = true, Name = "XrayUI Jump List" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    private static HashSet<string> Publish(IReadOnlyList<JumpListEntry> items, string category,
        Action<IReadOnlySet<string>> removeFromHistory)
    {
        nint destinations = 0, removed = 0, collection = 0;
        var begun = false;
        try
        {
            destinations = Create(DestinationListClsid, DestinationListIid);
            // Keep the application's original, Shell-inferred identity. An explicit process
            // AppUserModelID changes taskbar icon selection and can mask the live window icon.
            // BeginList infers our identity because this COM object runs in the app process.
            // https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-icustomdestinationlist-setappid
            uint slots = 0;
            var iid = ArrayIid;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, Guid*, nint*, int>)Table(destinations)[4])
                (destinations, &slots, &iid, &removed));
            begun = true;
            var excluded = ReadRemovedIds(removed);
            // CommitList clears Windows' removed-items list. Persist the removal first, or a
            // later refresh/relaunch would silently put those items back. Failure aborts the list.
            if (excluded.Count > 0) removeFromHistory(excluded);
            collection = Create(CollectionClsid, CollectionIid);
            var count = 0;
            foreach (var item in items.Where(i => !excluded.Contains(i.Id)).Take((int)slots))
            {
                var link = CreateLink(item);
                try
                {
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)Table(collection)[5])(collection, link));
                    count++;
                }
                finally { Release(link); }
            }
            if (count > 0)
            {
                fixed (char* label = category)
                {
                    int hr = ((delegate* unmanaged[Stdcall]<nint, char*, nint, int>)Table(destinations)[5])
                        (destinations, label, collection);
                    // Respect Windows' recent-items privacy setting; commit an empty list.
                    if (hr != AccessDenied) Marshal.ThrowExceptionForHR(hr);
                }
            }
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)Table(destinations)[8])(destinations));
            begun = false;
            return excluded;
        }
        finally
        {
            if (begun) _ = ((delegate* unmanaged[Stdcall]<nint, int>)Table(destinations)[11])(destinations);
            Release(collection);
            Release(removed);
            Release(destinations);
        }
    }

    private static HashSet<string> ReadRemovedIds(nint array)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        uint count = 0;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Table(array)[3])(array, &count));
        var iid = ShellLinkIid;
        const int argumentsLength = 4096;
        char* arguments = stackalloc char[argumentsLength];
        for (uint i = 0; i < count; i++)
        {
            nint link = 0;
            int hr = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Table(array)[4])(array, i, &iid, &link);
            if (hr < 0) continue; // An old version may have used IShellItem instead.
            try
            {
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Table(link)[10])
                    (link, arguments, argumentsLength));
                if (JumpListRequest.Parse((string[])[new string(arguments)]) is { } request) ids.Add(request.ServerId);
            }
            finally { Release(link); }
        }
        return ids;
    }

    private static nint CreateLink(JumpListEntry item)
    {
        var link = Create(ShellLinkClsid, ShellLinkIid);
        try
        {
            SetString(link, 20, ExePath);
            SetString(link, 9, ExeDir);
            SetString(link, 11, new JumpListRequest(item.Id).ToArguments());
            fixed (char* path = IconPath)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Table(link)[17])(link, path, 0));
            var store = Query(link, PropertyStoreIid);
            try
            {
                SetStringProperty(store, TitleFormatId, 2, string.IsNullOrWhiteSpace(item.Name) ? "XrayUI" : item.Name);
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)Table(store)[7])(store));
            }
            finally { Release(store); }
            return link;
        }
        catch { Release(link); throw; }
    }

    private static nint Create(Guid clsid, Guid iid)
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, 0, 1, ref iid, out var result));
        return result;
    }

    private static nint Query(nint instance, Guid iid)
    {
        nint result = 0;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Table(instance)[0])(instance, &iid, &result));
        return result;
    }

    private static nint* Table(nint instance) => *(nint**)instance;
    private static void Release(nint instance)
    {
        if (instance != 0) _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Table(instance)[2])(instance);
    }

    private static void SetString(nint instance, int slot, string value)
    {
        fixed (char* text = value)
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int>)Table(instance)[slot])(instance, text));
    }

    private static void SetStringProperty(nint store, Guid formatId, uint propertyId, string value)
    {
        var key = new PropertyKey { FormatId = formatId, PropertyId = propertyId };
        // PROPVARIANT's largest union member is a counted array: 8 bytes on x86, 16 on x64/ARM64.
        var variant = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(value) };
        try
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)Table(store)[6])
                (store, &key, &variant));
        }
        finally { Marshal.FreeCoTaskMem(variant.Pointer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid FormatId; public uint PropertyId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        private ushort _reserved1, _reserved2, _reserved3;
        public nint Pointer;
        private nint _unionPadding;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}

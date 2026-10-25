using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Klippy.Services;

/// <summary>
/// Makes sure a file <c>explorer.exe /select,</c> was asked to show ends up selected, by
/// selecting it again in every Explorer view of its folder that turns up in the next few
/// seconds.
///
/// Explorer selects the file in the window it opens, and on its own that is enough. But a
/// tab utility such as ExplorerTabUtility catches that window as it opens, reads what is
/// selected in it before the selection has arrived, and moves it into a tab in a window you
/// already have — selecting nothing. With that folder already open in a tab, it switches to
/// that tab and selects nothing either. Selecting in each view as it appears is what lands
/// in the tab, whichever one it is.
///
/// Each view is selected in once, so a file you click on afterwards stays clicked. Through
/// the shell's own interfaces, called by hand: the late-bound COM a script would use is not
/// there under NativeAOT.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class ExplorerSelection
{
    /// <summary>How long a tab utility has to finish moving the window into a tab.</summary>
    private static readonly TimeSpan Watch = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    /// <summary>Starts watching for <paramref name="path"/>'s folder, and returns at once.</summary>
    public static void SelectWhenShown(string path)
    {
        // The shell's objects want a single-threaded apartment, and one of our own: the
        // pool thread a run arrives on is not ours to choose an apartment for.
        var thread = new Thread(() => Select(path)) { IsBackground = true, Name = "Explorer selection" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void Select(string path)
    {
        if (CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED) < 0) return;
        IntPtr file = IntPtr.Zero, windows = IntPtr.Zero;
        var selected = new List<IntPtr>(); // views already selected in, held so none is mistaken for a new one
        try
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out file, 0, out _) < 0) return;
            var clsid = CLSID_ShellWindows;
            var iid = IID_IShellWindows;
            if (CoCreateInstance(&clsid, IntPtr.Zero, CLSCTX_ALL, &iid, out windows) < 0) return;

            if (Path.GetDirectoryName(path) is not { } folderPath) return;
            folderPath = Path.TrimEndingDirectorySeparator(folderPath);

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < Watch)
            {
                SelectInNewViews(windows, file, folderPath, selected);
                Thread.Sleep(Poll);
            }
        }
        catch (Exception)
        {
            // A window closing under us is the shell's business, not a failure to report:
            // Explorer was started, and it has selected the file on its own if it could.
        }
        finally
        {
            foreach (var view in selected) Release(view);
            if (windows != IntPtr.Zero) Release(windows);
            if (file != IntPtr.Zero) CoTaskMemFree(file);
            CoUninitialize();
        }
    }

    /// <summary>
    /// Selects the file in each view showing <paramref name="folderPath"/> that is not in
    /// <paramref name="selected"/> yet. Every tab is a window of its own here.
    /// </summary>
    private static void SelectInNewViews(IntPtr windows, IntPtr file, string folderPath, List<IntPtr> selected)
    {
        int count;
        if (Call(windows, IShellWindows_get_Count, &count) < 0) return;

        for (var i = 0; i < count; i++)
        {
            var index = new Variant { Type = VT_I4, Value = i };
            // Item takes the VARIANT by value, which the calling convention hands over as a
            // pointer to a copy; the function pointer's signature is what makes that copy.
            IntPtr window;
            var item = (delegate* unmanaged[Stdcall]<IntPtr, Variant, IntPtr*, int>)Slot(windows, IShellWindows_Item);
            if (item(windows, index, &window) != 0 || window == IntPtr.Zero) continue;

            var view = ViewOf(window);
            Release(window);
            if (view == IntPtr.Zero) continue;

            var identity = Identity(view);
            if (identity == IntPtr.Zero || selected.Contains(identity) || !Shows(view, folderPath))
            {
                if (identity != IntPtr.Zero) Release(identity);
                Release(view);
                continue;
            }

            // Selected in that folder, and nothing else; scrolled to and given the focus,
            // as /select does it.
            var child = ILFindLastID(file);
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)Slot(view, IShellView_SelectItem))(
                view, child, SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_ENSUREVISIBLE | SVSI_FOCUSED);
            selected.Add(identity);
            Release(view);
        }
    }

    /// <summary>The view a shell window is showing, or zero while it has none.</summary>
    private static IntPtr ViewOf(IntPtr window)
    {
        var iid = IID_IServiceProvider;
        if (QueryInterface(window, &iid, out var services) < 0) return IntPtr.Zero;
        try
        {
            var sid = SID_STopLevelBrowser;
            var browserIid = IID_IShellBrowser;
            IntPtr browser;
            if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, IntPtr*, int>)Slot(services, IServiceProvider_QueryService))(
                    services, &sid, &browserIid, &browser) < 0) return IntPtr.Zero;
            try
            {
                IntPtr view;
                return Call(browser, IShellBrowser_QueryActiveShellView, &view) < 0 ? IntPtr.Zero : view;
            }
            finally
            {
                Release(browser);
            }
        }
        finally
        {
            Release(services);
        }
    }

    /// <summary>
    /// Whether a view is showing <paramref name="folderPath"/>. By path rather than by
    /// comparing item IDs: a view's folder and the same folder parsed from its path are
    /// told apart by bytes that do not change which folder it is.
    /// </summary>
    private static bool Shows(IntPtr view, string folderPath)
    {
        var iid = IID_IFolderView;
        if (QueryInterface(view, &iid, out var folderView) < 0) return false;
        try
        {
            var persistIid = IID_IPersistFolder2;
            IntPtr persist;
            if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(folderView, IFolderView_GetFolder))(
                    folderView, &persistIid, &persist) < 0) return false;
            try
            {
                IntPtr folder;
                if (Call(persist, IPersistFolder2_GetCurFolder, &folder) < 0 || folder == IntPtr.Zero) return false;
                try
                {
                    if (SHGetNameFromIDList(folder, SIGDN_FILESYSPATH, out var name) < 0) return false;
                    try
                    {
                        return string.Equals(Path.TrimEndingDirectorySeparator(Marshal.PtrToStringUni(name) ?? ""),
                            folderPath, StringComparison.OrdinalIgnoreCase);
                    }
                    finally
                    {
                        CoTaskMemFree(name);
                    }
                }
                finally
                {
                    CoTaskMemFree(folder);
                }
            }
            finally
            {
                Release(persist);
            }
        }
        finally
        {
            Release(folderView);
        }
    }

    /// <summary>The object's IUnknown, which is the same pointer however it is reached.</summary>
    private static IntPtr Identity(IntPtr instance)
    {
        var iid = IID_IUnknown;
        return QueryInterface(instance, &iid, out var unknown) < 0 ? IntPtr.Zero : unknown;
    }

    // ---- COM by hand: an interface pointer is a pointer to its table of methods ----

    private static IntPtr Slot(IntPtr instance, int slot) => (*(IntPtr**)instance)[slot];

    private static int Call(IntPtr instance, int slot, void* result) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, void*, int>)Slot(instance, slot))(instance, result);

    private static int QueryInterface(IntPtr instance, Guid* iid, out IntPtr result)
    {
        IntPtr found;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(instance, 0))(instance, iid, &found);
        result = hr < 0 ? IntPtr.Zero : found;
        return hr;
    }

    private static void Release(IntPtr instance) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(instance, 2))(instance);

    // Slots counted from IUnknown's three, through each interface's ancestors.
    private const int IServiceProvider_QueryService = 3;
    private const int IShellWindows_get_Count = 7;         // after IDispatch's four
    private const int IShellWindows_Item = 8;
    private const int IShellBrowser_QueryActiveShellView = 15;
    private const int IShellView_SelectItem = 14;
    private const int IFolderView_GetFolder = 5;
    private const int IPersistFolder2_GetCurFolder = 5;

    private static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid IID_IShellWindows = new("85CB6900-4D95-11CF-960C-0080C7F4EE85");
    private static readonly Guid IID_IServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IID_IFolderView = new("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
    private static readonly Guid IID_IPersistFolder2 = new("1AC3D9F0-175C-11D1-95BE-00609797EA4F");
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

    private const uint COINIT_APARTMENTTHREADED = 0x2;
    private const uint CLSCTX_ALL = 0x17;
    private const ushort VT_I4 = 3;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const uint SVSI_SELECT = 0x1, SVSI_DESELECTOTHERS = 0x4, SVSI_ENSUREVISIBLE = 0x8, SVSI_FOCUSED = 0x10;

    /// <summary>
    /// A VARIANT holding a 32-bit integer — the one kind passed here. Its value is a union
    /// two pointers wide, which is 16 bytes in all on 32-bit Windows and 24 on 64-bit.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Variant
    {
        public ushort Type;
        private ushort _reserved1, _reserved2, _reserved3;
        public IntPtr Value;
        private IntPtr _record;
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(Guid* clsid, IntPtr outer, uint context, Guid* iid, out IntPtr instance);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl,
        uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdn, out IntPtr name);
}

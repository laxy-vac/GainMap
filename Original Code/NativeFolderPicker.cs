using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

public static class NativeFolderPicker
{
    public static bool ValidateWithoutShowing(string path)
    {
        IFileOpenDialog dialog = (IFileOpenDialog)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")));
        IShellItem item = null;
        try
        {
            uint options; dialog.GetOptions(out options);
            dialog.SetOptions(options | 0x20u | 0x40u | 0x800u | 0x8u);
            uint configured; dialog.GetOptions(out configured);
            dialog.SetTitle("选择 JPEG Gain Map 输出目录");
            Guid iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
            dialog.SetFolder(item);
            IntPtr display; item.GetDisplayName(0x80058000u, out display);
            try { return (configured & 0x60u) == 0x60u && String.Equals(Path.GetFullPath(path), Marshal.PtrToStringUni(display), StringComparison.OrdinalIgnoreCase); }
            finally { Marshal.FreeCoTaskMem(display); }
        }
        finally { if (item != null) Marshal.ReleaseComObject(item); Marshal.ReleaseComObject(dialog); }
    }
    [DllImport("shell32.dll", CharSet=CharSet.Unicode, PreserveSig=true)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr context,
        ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    public static string Pick(Window owner, string initialPath)
    {
        IFileOpenDialog dialog = (IFileOpenDialog)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")));
        IShellItem initial = null, selected = null;
        try
        {
            uint options; dialog.GetOptions(out options);
            dialog.SetOptions(options | 0x20u | 0x40u | 0x800u | 0x8u);
            dialog.SetTitle("选择 JPEG Gain Map 输出目录");
            dialog.SetOkButtonLabel("选择文件夹");
            if (!String.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath))
            {
                Guid iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                if (SHCreateItemFromParsingName(initialPath, IntPtr.Zero, ref iid, out initial) >= 0)
                    dialog.SetFolder(initial);
            }
            int result = dialog.Show(new WindowInteropHelper(owner).Handle);
            if (result == unchecked((int)0x800704C7)) return null;
            Marshal.ThrowExceptionForHR(result);
            dialog.GetResult(out selected);
            IntPtr name;
            selected.GetDisplayName(0x80058000u, out name);
            try { return Marshal.PtrToStringUni(name); }
            finally { Marshal.FreeCoTaskMem(name); }
        }
        finally
        {
            if (selected != null) Marshal.ReleaseComObject(selected);
            if (initial != null && !Object.ReferenceEquals(initial, selected)) Marshal.ReleaseComObject(initial);
            Marshal.ReleaseComObject(dialog);
        }
    }

    [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, IntPtr specs);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, uint alignment);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
        void GetResults(out IntPtr items);
        void GetSelectedItems(out IntPtr items);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr obj);
        void GetParent(out IShellItem item);
        void GetDisplayName(uint format, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hints, out int order);
    }
}

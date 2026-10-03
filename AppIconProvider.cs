using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace StartupManager;

// Windows owns icon discovery; cache images for the window lifetime. Extraction
// runs off the UI thread so slow shortcuts/packages cannot block board interaction.
sealed class AppIconProvider : IDisposable
{
    readonly Dictionary<string, Task<Bitmap?>> cache = new();
    readonly Lazy<Dictionary<string, string>> shortcuts = new(FindShortcuts);
    bool disposed;

    public Task<Bitmap?> GetAsync(JsonObject app, string name)
    {
        string key = app.ToJsonString();
        if (disposed) return Task.FromResult<Bitmap?>(null);
        if (!cache.TryGetValue(key, out var task)) cache[key] = task = Task.Run(() => Extract(app, name));
        return task;
    }

    Bitmap? Extract(JsonObject app, string name)
    {
        string Value(string key) => app[key]?.GetValue<string>() ?? "";
        try
        {
            if (Value("Kind") == "PackageApp") return ShellImage("shell:AppsFolder\\" + Value("AppId"));
            // A Start-menu shortcut often has a better icon than a launcher executable.
            if (shortcuts.Value.TryGetValue(name, out var shortcut))
            {
                var image = ShellImage(shortcut); if (image != null) return image;
            }
            string file = Environment.ExpandEnvironmentVariables(Value("FileName"));
            if (Value("Kind") == "Task") file = TaskCommand(Value("TaskPath"), Value("TaskName"));
            return string.IsNullOrWhiteSpace(file) ? null : ShellImage(file);
        }
        catch { return null; } // An unavailable app still gets a named fallback tile.
    }

    static Dictionary<string, string> FindShortcuts()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(Environment.GetFolderPath(folder), "*.lnk", SearchOption.AllDirectories)) result.TryAdd(Path.GetFileNameWithoutExtension(path), path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    static string TaskCommand(string path, string name)
    {
        object? service = null, folder = null, task = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!);
            ((dynamic)service!).Connect(); folder = ((dynamic)service).GetFolder(string.IsNullOrWhiteSpace(path) ? "\\" : path);
            task = ((dynamic)folder).GetTask(name);
            var xml = XDocument.Parse((string)((dynamic)task).Xml);
            string command = xml.Descendants().FirstOrDefault(node => node.Name.LocalName == "Command")?.Value ?? "";
            return Environment.ExpandEnvironmentVariables(command.Trim('"'));
        }
        finally { foreach (object? obj in new[] { task, folder, service }) if (obj != null && Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj); }
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, uint flags, out IntPtr bitmap);
    }
    [StructLayout(LayoutKind.Sequential)]
    struct NativeSize { public int Width, Height; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);
    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)]
    struct NativeBitmap { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant; }
    [DllImport("gdi32.dll")]
    static extern int GetObject(IntPtr bitmap, int size, out NativeBitmap info);
    [DllImport("gdi32.dll")]
    static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] pixels, ref BitmapInfo info, uint usage);
    [DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr window, IntPtr dc);

    static Bitmap? ShellImage(string path)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0) return null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            // ICONONLY | BIGGERSIZEOK; never request a thumbnail of user files.
            if (factory.GetImage(new NativeSize { Width = 256, Height = 256 }, 0x5, out bitmap) != 0 || bitmap == IntPtr.Zero) return null;
            return TransparentBitmap(bitmap);
        }
        finally { if (bitmap != IntPtr.Zero) DeleteObject(bitmap); Marshal.FinalReleaseComObject(factory); }
    }

    static Bitmap? TransparentBitmap(IntPtr source)
    {
        if (GetObject(source, Marshal.SizeOf<NativeBitmap>(), out var size) == 0 || size.Width <= 0 || size.Height <= 0) return null;
        var pixels = new byte[checked(size.Width * size.Height * 4)];
        var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32, SizeImage = (uint)pixels.Length };
        IntPtr dc = GetDC(IntPtr.Zero);
        try { if (GetDIBits(dc, source, 0, (uint)size.Height, pixels, ref info, 0) == 0) return null; }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        // Older icons may have no alpha channel. Keep those opaque, while retaining
        // the Shell's premultiplied alpha for modern icons (FromHbitmap discards it).
        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { hasAlpha = true; break; }
        if (!hasAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var result = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = result.LockBits(new Rectangle(0, 0, result.Width, result.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, result.PixelFormat);
        try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
        finally { result.UnlockBits(data); }
        return result;
    }

    public void Dispose()
    {
        disposed = true;
        foreach (var task in cache.Values) _ = task.ContinueWith(completed => { if (completed.Status == TaskStatus.RanToCompletion) completed.Result?.Dispose(); }, TaskScheduler.Default);
        cache.Clear();
    }
}

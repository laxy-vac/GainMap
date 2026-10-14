using System;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Web.Script.Serialization;

public sealed class WorkerResult
{
    public bool Success;
    public string Error;
    public Dictionary<string, object> Info;
}

public sealed class WorkerClient : IDisposable
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll")]
    static extern bool FreeLibrary(IntPtr module);

    static void EnsureSystemRuntime()
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (string name in new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "ucrtbase.dll" })
        {
            string path = Path.Combine(system, name);
            IntPtr module = File.Exists(path) ? LoadLibraryEx(path, IntPtr.Zero, 0x900) : IntPtr.Zero;
            if (module == IntPtr.Zero)
                throw new IOException("缺少或无法加载 Microsoft Visual C++ v14 x64 运行库（" + name + "）。\n" +
                    "请从微软官网下载并安装最新 x64 运行库后重试：\nhttps://aka.ms/vc14/vc_redist.x64.exe");
            FreeLibrary(module);
        }
    }

    readonly object gate = new object();
    readonly object errorGate = new object();
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    readonly StringBuilder errors = new StringBuilder();
    Process process;
    int requestId;
    bool disposed;

    static string ExtractWorker()
    {
        byte[] payload;
        using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("GainMap.Worker"))
        {
            if (resource == null) throw new InvalidOperationException("程序中的转换组件缺失。");
            using (MemoryStream copy = new MemoryStream()) { resource.CopyTo(copy); payload = copy.ToArray(); }
        }
        string digest;
        using (SHA256 hash = SHA256.Create()) digest = BitConverter.ToString(hash.ComputeHash(payload)).Replace("-", "");
        string directory = Path.Combine(Path.GetTempPath(), "GainMap", digest.Substring(0,24));
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "GainMapWorker.exe");
        using (var mutex = new System.Threading.Mutex(false, "Local\\GainMapWorker_"+digest.Substring(0,24)))
        {
            bool acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (System.Threading.AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("转换组件正被其他窗口初始化，请稍后重试。");
                bool valid = false;
                if (File.Exists(destination))
                {
                    using (SHA256 hash = SHA256.Create())
                    using (FileStream existing = File.OpenRead(destination))
                        valid = BitConverter.ToString(hash.ComputeHash(existing)).Replace("-", "") == digest;
                }
                if (!valid)
                {
                    string staging = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        File.WriteAllBytes(staging, payload);
                        if (File.Exists(destination)) File.Delete(destination);
                        File.Move(staging, destination);
                    }
                    finally { if (File.Exists(staging)) File.Delete(staging); }
                }
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        return destination;
    }

    void EnsureStarted()
    {
        if (process != null && !process.HasExited) return;
        if (process != null) { process.Dispose(); process = null; }
        EnsureSystemRuntime();
        string worker = ExtractWorker();
        ProcessStartInfo start = new ProcessStartInfo(worker) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(worker) };
        lock (errorGate) errors.Clear();
        process = new Process { StartInfo = start };
        process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
            if (e.Data == null) return;
            lock (errorGate) { if (errors.Length < 8192) errors.AppendLine(e.Data); }
        };
        if (!process.Start()) throw new IOException("无法启动转换组件。");
        process.BeginErrorReadLine();
    }

    public WorkerResult Convert(string source, string destination, bool overwrite, int quality = 95)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException("WorkerClient");
            if (quality < 1 || quality > 100) throw new ArgumentOutOfRangeException("quality", "输出质量必须在 1 到 100 之间。");
            EnsureStarted();
            int id = ++requestId;
            Dictionary<string, object> request = new Dictionary<string, object> {
                {"op", "convert"}, {"id", id}, {"source", Path.GetFullPath(source)},
                {"destination", Path.GetFullPath(destination)}, {"overwrite", overwrite}, {"quality", quality} };
            // StreamWriter's default encoding can vary by framework. Write the
            // protocol bytes explicitly so Chinese and spaces round-trip.
            byte[] bytes = new UTF8Encoding(false).GetBytes(json.Serialize(request)+"\n");
            process.StandardInput.BaseStream.Write(bytes,0,bytes.Length);
            process.StandardInput.BaseStream.Flush();
            string line = process.StandardOutput.ReadLine();
            if (line == null)
            {
                string detail; lock (errorGate) detail = errors.ToString().Trim();
                throw new IOException(String.IsNullOrEmpty(detail) ? "转换组件意外退出。" : detail);
            }
            Dictionary<string, object> reply = json.Deserialize<Dictionary<string, object>>(line);
            if (System.Convert.ToInt32(reply["id"]) != id) throw new IOException("转换回复不匹配。");
            return new WorkerResult {
                Success = System.Convert.ToBoolean(reply["success"]),
                Error = reply.ContainsKey("error") ? System.Convert.ToString(reply["error"]) : "",
                Info = reply.ContainsKey("info") ? reply["info"] as Dictionary<string, object> : null };
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (process == null) return;
            try
            {
                if (!process.HasExited)
                {
                    byte[] shutdown = Encoding.UTF8.GetBytes("{\"op\":\"shutdown\"}\n");
                    process.StandardInput.BaseStream.Write(shutdown,0,shutdown.Length);
                    process.StandardInput.BaseStream.Flush();
                    process.StandardInput.Close();
                    if (!process.WaitForExit(2000)) process.Kill();
                }
            }
            catch (InvalidOperationException) { }
            catch (IOException) { }
            finally { process.Dispose(); process = null; }
        }
    }
}

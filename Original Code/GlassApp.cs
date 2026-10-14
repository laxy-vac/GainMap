using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

public sealed class GlassApp : IDisposable
{
    const string OriginalDirectoryText = "原图所在文件夹";
    readonly WorkerClient worker;
    readonly ObservableCollection<QueueItem> items = new ObservableCollection<QueueItem>();
    readonly string configPath;
    readonly bool userConfigEnabled;
    Window window;
    TextBlock outputDirectoryLabel;
    TextBlock qualityValueLabel;
    TextBlock queueCount;
    TextBlock progressHint;
    Button chooseOutputButton;
    Button resetOutputButton;
    Button selectImagesButton;
    Button addFilesButton;
    Button removeSelectionButton;
    Button clearQueueButton;
    Button startConvertButton;
    Button failureDetailsButton;
    ProgressBar conversionProgress;
    Slider qualitySlider;
    ListView fileList;
    StackPanel emptyState;
    bool busy;
    bool disposed;
    bool qualityPreferenceDirty;
    int outputQuality = 95;
    string outputDirectory;
    readonly List<Dictionary<string, object>> batchResults = new List<Dictionary<string, object>>();

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public GlassApp(bool loadUserConfig)
    {
        worker = new WorkerClient();
        userConfigEnabled = loadUserConfig;
        configPath = loadUserConfig ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                                    ".hdrjxr_converter_config.json") : null;
        if (loadUserConfig) outputDirectory = LoadOutputDirectory();
    }

    public Window CreateWindow()
    {
        string xaml;
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GainMap.UI"))
        {
            if (stream == null) throw new InvalidOperationException("界面资源缺失，无法启动图片转换器。");
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8)) xaml = reader.ReadToEnd();
        }
        xaml = xaml.Replace("__SELF__", Assembly.GetExecutingAssembly().GetName().Name);
        window = (Window)XamlReader.Parse(xaml);
        window.Title = "GainMap 图片转换器";
        outputDirectoryLabel = (TextBlock)window.FindName("OutputDirectoryLabel");
        qualitySlider = (Slider)window.FindName("QualitySlider");
        qualityValueLabel = (TextBlock)window.FindName("QualityValueLabel");
        queueCount = (TextBlock)window.FindName("QueueCount");
        progressHint = (TextBlock)window.FindName("ProgressHint");
        chooseOutputButton = (Button)window.FindName("ChooseOutputDirectoryButton");
        resetOutputButton = (Button)window.FindName("ResetOutputDirectoryButton");
        selectImagesButton = (Button)window.FindName("SelectImagesButton");
        addFilesButton = (Button)window.FindName("AddFilesButton");
        removeSelectionButton = (Button)window.FindName("RemoveSelectionButton");
        clearQueueButton = (Button)window.FindName("ClearQueueButton");
        startConvertButton = (Button)window.FindName("StartConvertButton");
        failureDetailsButton = (Button)window.FindName("FailureDetailsButton");
        conversionProgress = (ProgressBar)window.FindName("ConversionProgress");
        fileList = (ListView)window.FindName("FileList");
        emptyState = (StackPanel)window.FindName("EmptyState");
        qualitySlider.Value = outputQuality;
        qualityValueLabel.Text = outputQuality.ToString(CultureInfo.InvariantCulture);
        fileList.ItemsSource = items;
        LoadIcon();
        SetOutputDirectory(outputDirectory, false);
        WireEvents();
        UpdateQueueView();
        ConfigureWindowChrome();
        return window;
    }

    void LoadIcon()
    {
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GainMap.Icon"))
        {
            if (stream == null) throw new InvalidOperationException("程序图标资源缺失。");
            BitmapSource icon = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                                                   BitmapCacheOption.OnLoad);
            icon.Freeze();
            window.Icon = icon;
            ((Image)window.FindName("BrandIcon")).Source = icon;
        }
    }

    void WireEvents()
    {
        selectImagesButton.Click += delegate { SelectFiles(); };
        addFilesButton.Click += delegate { SelectFiles(); };
        removeSelectionButton.Click += delegate { RemoveSelected(); };
        clearQueueButton.Click += delegate { ClearQueue(); };
        chooseOutputButton.Click += delegate { ChooseOutputDirectory(); };
        resetOutputButton.Click += delegate { SetOutputDirectory(null, true); };
        startConvertButton.Click += async delegate { await BeginBatchAsync(false); };
        failureDetailsButton.Click += delegate { ShowFailureDetails(); };
        qualitySlider.ValueChanged += delegate(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            outputQuality = (int)Math.Round(e.NewValue);
            qualityValueLabel.Text = outputQuality.ToString(CultureInfo.InvariantCulture);
            qualityPreferenceDirty = true;
        };
        qualitySlider.PreviewMouseLeftButtonUp += delegate { CommitQualityPreference(); };
        qualitySlider.PreviewKeyUp += delegate(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Left || e.Key == Key.Right ||
                e.Key == Key.PageUp || e.Key == Key.PageDown || e.Key == Key.Home || e.Key == Key.End)
                CommitQualityPreference();
        };
        qualitySlider.LostKeyboardFocus += delegate { CommitQualityPreference(); };
        fileList.SelectionChanged += delegate { UpdateQueueView(); };
        fileList.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && !busy)
            {
                RemoveSelected();
                e.Handled = true;
            }
        };
        window.PreviewDragOver += delegate(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        window.Drop += delegate(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths != null) AddFiles(paths);
            }
            e.Handled = true;
        };
        window.Closing += delegate(object sender, CancelEventArgs e)
        {
            if (busy)
            {
                e.Cancel = true;
                progressHint.Text = "转换结束后即可关闭窗口";
            }
            else CommitQualityPreference();
        };
        window.Closed += delegate { Dispose(); };
        WindowChrome.SetWindowChrome(window, new WindowChrome {
            CaptionHeight = 32,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = true
        });
        window.SourceInitialized += delegate { ApplyGlass(); };
    }

    void ConfigureWindowChrome()
    {
        window.ResizeMode = ResizeMode.CanResize;
        window.ShowInTaskbar = true;
    }

    void ApplyGlass()
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        HwndSource source = HwndSource.FromHwnd(handle);
        if (source != null) source.CompositionTarget.BackgroundColor = Colors.Transparent;
        int corner = 2;
        int alpha = 1;
        int light = 0;
        int acrylic = 3;
        DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
        DwmSetWindowAttribute(handle, 39, ref alpha, sizeof(int));
        DwmSetWindowAttribute(handle, 20, ref light, sizeof(int));
        int acrylicResult = DwmSetWindowAttribute(handle, 38, ref acrylic, sizeof(int));
        int noBorder = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(handle, 34, ref noBorder, sizeof(int));
        if (acrylicResult < 0)
        {
            window.Background = new SolidColorBrush(Color.FromRgb(245, 248, 252));
            Grid root = window.Content as Grid;
            if (root != null) root.Background = new SolidColorBrush(Color.FromRgb(245, 248, 252));
        }
    }

    void SelectFiles()
    {
        OpenFileDialog dialog = new OpenFileDialog {
            Title = "选择 JXR 图片",
            Filter = "JXR HDR 图片 (*.jxr)|*.jxr",
            Multiselect = true,
            CheckFileExists = true,
            CheckPathExists = true,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(window) == true) AddFiles(dialog.FileNames);
    }

    int AddFiles(IEnumerable<string> paths)
    {
        if (busy || paths == null) return 0;
        int added = 0;
        foreach (string path in paths)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                if (!String.Equals(Path.GetExtension(fullPath), ".jxr", StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(fullPath)) continue;
                if (items.Any(delegate(QueueItem item) {
                    return String.Equals(item.FilePath, fullPath, StringComparison.OrdinalIgnoreCase);
                })) continue;
                items.Add(new QueueItem(fullPath));
                added++;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
        }
        if (added > 0)
        {
            progressHint.Text = "已添加 " + items.Count + " 张 JXR 图片";
            conversionProgress.Maximum = Math.Max(1, items.Count);
            conversionProgress.Value = 0;
            UpdateQueueView();
        }
        return added;
    }

    void RemoveSelected()
    {
        if (busy) return;
        List<QueueItem> selected = fileList.SelectedItems.Cast<QueueItem>().ToList();
        foreach (QueueItem item in selected) items.Remove(item);
        conversionProgress.Maximum = Math.Max(1, items.Count);
        conversionProgress.Value = 0;
        UpdateQueueView();
    }

    void ClearQueue()
    {
        if (busy) return;
        items.Clear();
        batchResults.Clear();
        conversionProgress.Maximum = 1;
        conversionProgress.Value = 0;
        failureDetailsButton.Visibility = Visibility.Collapsed;
        progressHint.Text = "添加图片，即可开始转换";
        UpdateQueueView();
    }

    void UpdateQueueView()
    {
        bool hasItems = items.Count > 0;
        emptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        fileList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        queueCount.Text = hasItems ? "已添加 " + items.Count + " 张图片" : "尚未添加图片";
        addFilesButton.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        removeSelectionButton.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        clearQueueButton.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        removeSelectionButton.IsEnabled = hasItems && !busy && fileList.SelectedItems.Count > 0;
        clearQueueButton.IsEnabled = hasItems && !busy;
        addFilesButton.IsEnabled = !busy;
        selectImagesButton.IsEnabled = !busy;
        chooseOutputButton.IsEnabled = !busy;
        resetOutputButton.IsEnabled = !busy;
        qualitySlider.IsEnabled = !busy;
        startConvertButton.IsEnabled = hasItems && !busy;
        fileList.IsEnabled = !busy;
        failureDetailsButton.IsEnabled = !busy;
    }

    void ChooseOutputDirectory()
    {
        if (busy) return;
        string initialPath = Directory.Exists(outputDirectory) ? outputDirectory :
                             Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string selected = NativeFolderPicker.Pick(window, initialPath);
        if (!String.IsNullOrWhiteSpace(selected) && Directory.Exists(selected))
            SetOutputDirectory(selected, true);
    }

    void SetOutputDirectory(string path, bool save)
    {
        outputDirectory = String.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        outputDirectoryLabel.Text = outputDirectory == null ? OriginalDirectoryText : outputDirectory;
        outputDirectoryLabel.ToolTip = outputDirectory == null ? OriginalDirectoryText : outputDirectory;
        if (save) SaveOutputDirectory();
    }

    string LoadOutputDirectory()
    {
        outputQuality = 95;
        try
        {
            if (!File.Exists(configPath)) return null;
            string json = File.ReadAllText(configPath, Encoding.UTF8);
            Dictionary<string, object> values = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            object value;
            object qualityValue;
            int loadedQuality;
            if (values != null && values.TryGetValue("quality", out qualityValue) &&
                TryParseQuality(qualityValue, out loadedQuality)) outputQuality = loadedQuality;
            if (values == null || !values.TryGetValue("output_dir", out value)) return null;
            string path = System.Convert.ToString(value);
            return !String.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? Path.GetFullPath(path) : null;
        }
        catch { outputQuality = 95; return null; }
    }

    static bool TryParseQuality(object value, out int quality)
    {
        int parsed = 95;
        bool valid = value != null && Int32.TryParse(System.Convert.ToString(value, CultureInfo.InvariantCulture),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed >= 1 && parsed <= 100;
        quality = valid ? parsed : 95;
        return valid;
    }

    int CurrentQuality()
    {
        return qualitySlider == null ? outputQuality : (int)Math.Round(qualitySlider.Value);
    }

    void CommitQualityPreference()
    {
        if (!qualityPreferenceDirty || busy) return;
        SaveOutputDirectory();
        qualityPreferenceDirty = false;
    }

    void SaveOutputDirectory()
    {
        if (!userConfigEnabled) return;
        try
        {
            string parent = Path.GetDirectoryName(configPath);
            if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
            Dictionary<string, object> values = new Dictionary<string, object>();
            values.Add("output_dir", outputDirectory ?? String.Empty);
            values.Add("quality", outputQuality);
            File.WriteAllText(configPath, new JavaScriptSerializer().Serialize(values), new UTF8Encoding(false));
        }
        catch
        {
            MessageBox.Show(window, "本次可继续使用当前设置，但无法保存偏好。", "GainMap",
                            MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    async Task<bool> BeginBatchAsync(bool silent)
    {
        if (busy || items.Count == 0) return false;
        Dictionary<QueueItem, string> outputPaths;
        List<string> errors;
        if (!TryBuildOutputPaths(items.ToList(), outputDirectory, out outputPaths, out errors))
        {
            foreach (QueueItem item in items)
            {
                string detail = errors.FirstOrDefault(delegate(string error) {
                    return error.IndexOf(item.FilePath, StringComparison.OrdinalIgnoreCase) >= 0;
                });
                if (detail != null)
                {
                    item.Status = "失败";
                    item.StatusDetail = detail;
                }
            }
            UpdateFailureButton();
            UpdateQueueView();
            progressHint.Text = "输出目录或文件名冲突，请检查失败项";
            if (!silent)
                MessageBox.Show(window, String.Join(Environment.NewLine, errors.ToArray()), "无法开始转换",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        HashSet<string> existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in outputPaths.Values) if (File.Exists(path)) existing.Add(path);
        if (existing.Count > 0 && !silent)
        {
            MessageBoxResult answer = MessageBox.Show(window,
                "有 " + existing.Count + " 个 JPEG 文件已经存在。是否覆盖这些文件？",
                "确认覆盖", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return false;
        }
        if (existing.Count > 0 && silent)
        {
            progressHint.Text = "自检输出文件已存在，已停止以避免覆盖";
            return false;
        }

        int batchQuality = CurrentQuality();
        batchResults.Clear();
        busy = true;
        conversionProgress.Maximum = items.Count;
        conversionProgress.Value = 0;
        foreach (QueueItem item in items)
        {
            item.Status = "待转换";
            item.StatusDetail = Path.GetDirectoryName(item.FilePath);
        }
        progressHint.Text = "转换准备中（0 / " + items.Count + "）";
        UpdateFailureButton();
        UpdateQueueView();
        return await ProcessBatchAsync(items.ToList(), outputPaths, existing, batchQuality);
    }

    async Task<bool> ProcessBatchAsync(List<QueueItem> batch, Dictionary<QueueItem, string> outputPaths,
                                       HashSet<string> overwritePaths, int batchQuality)
    {
        int succeeded = 0;
        int failed = 0;
        int completed = 0;
        foreach (QueueItem item in batch)
        {
            Dispatch(delegate
            {
                item.Status = "转换中";
                item.StatusDetail = Path.GetDirectoryName(item.FilePath);
                progressHint.Text = "正在转换 " + (completed + 1) + " / " + batch.Count;
            });
            WorkerResult result = null;
            string exceptionText = null;
            string destination = outputPaths[item];
            try
            {
                result = await Task.Run(delegate {
                    return worker.Convert(item.FilePath, destination, overwritePaths.Contains(destination), batchQuality);
                }).ConfigureAwait(false);
            }
            catch (Exception ex) { exceptionText = ex.Message; }

            bool success = result != null && result.Success;
            if (success) succeeded++; else failed++;
            completed++;
            Dictionary<string, object> record = new Dictionary<string, object>();
            record["source"] = item.FilePath;
            record["destination"] = destination;
            record["success"] = success;
            record["error"] = result == null ? exceptionText : result.Error;
            if (result != null && result.Info != null) record["info"] = result.Info;
            batchResults.Add(record);
            string detail = success ? destination : NormalizeError(result == null ? exceptionText : result.Error);
            Dispatch(delegate
            {
                item.Status = success ? "已完成" : "失败";
                item.StatusDetail = detail;
                conversionProgress.Value = completed;
                progressHint.Text = "转换进度 " + completed + " / " + batch.Count;
                UpdateFailureButton();
            });
        }

        Dispatch(delegate
        {
            busy = false;
            progressHint.Text = "转换完成：" + succeeded + " 成功，" + failed + " 失败";
            UpdateFailureButton();
            UpdateQueueView();
        });
        return failed == 0;
    }

    static void Dispatch(Action action)
    {
        if (Application.Current == null || Application.Current.Dispatcher.CheckAccess()) action();
        else Application.Current.Dispatcher.Invoke(action);
    }

    static string NormalizeError(string error)
    {
        return String.IsNullOrWhiteSpace(error) ? "转换失败，可查看详情或重试" : error.Trim();
    }

    void UpdateFailureButton()
    {
        int failed = items.Count(delegate(QueueItem item) { return item.Status == "失败"; });
        failureDetailsButton.Content = failed > 0 ? "查看失败详情（" + failed + "）" : "查看失败详情";
        failureDetailsButton.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowFailureDetails()
    {
        List<string> details = items.Where(delegate(QueueItem item) { return item.Status == "失败"; })
            .Select(delegate(QueueItem item) { return item.FileName + Environment.NewLine + item.StatusDetail; })
            .ToList();
        if (details.Count == 0) return;
        MessageBox.Show(window, String.Join(Environment.NewLine + Environment.NewLine, details.ToArray()),
                        "转换失败详情", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    static bool TryBuildOutputPaths(IList<QueueItem> sourceItems, string fixedDirectory,
                                    out Dictionary<QueueItem, string> outputs, out List<string> errors)
    {
        outputs = new Dictionary<QueueItem, string>();
        errors = new List<string>();
        if (!String.IsNullOrWhiteSpace(fixedDirectory) && !Directory.Exists(fixedDirectory))
        {
            errors.Add("输出目录不存在：" + fixedDirectory);
            return false;
        }
        Dictionary<string, List<QueueItem>> targets = new Dictionary<string, List<QueueItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (QueueItem item in sourceItems)
        {
            string directory = String.IsNullOrWhiteSpace(fixedDirectory) ? Path.GetDirectoryName(item.FilePath) : fixedDirectory;
            if (String.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                errors.Add("无法访问原图目录：" + item.FilePath);
                continue;
            }
            string target = Path.GetFullPath(Path.Combine(directory, Path.GetFileNameWithoutExtension(item.FilePath) + ".jpg"));
            outputs[item] = target;
            List<QueueItem> grouped;
            if (!targets.TryGetValue(target, out grouped))
            {
                grouped = new List<QueueItem>();
                targets[target] = grouped;
            }
            grouped.Add(item);
        }
        foreach (KeyValuePair<string, List<QueueItem>> target in targets)
        {
            if (target.Value.Count <= 1) continue;
            foreach (QueueItem item in target.Value)
                errors.Add("多个输入文件会生成同名 JPEG：" + item.FilePath + " → " + target.Key);
        }
        return errors.Count == 0;
    }

    void SetTestOutputDirectory(string path)
    {
        SetOutputDirectory(path, false);
    }

    public IList<Dictionary<string, object>> GetBatchResults()
    {
        return batchResults.AsReadOnly();
    }

    public IList<QueueItem> GetItems()
    {
        return items.ToList().AsReadOnly();
    }

    public int AddForTest(IEnumerable<string> paths) { return AddFiles(paths); }

    public void ClearForTest() { ClearQueue(); }

    public void SetTestOutputDirectoryForTest(string path) { SetTestOutputDirectory(path); }

    public void SetQualityForTest(int quality)
    {
        if (quality < 1 || quality > 100) throw new ArgumentOutOfRangeException("quality");
        if (busy) throw new InvalidOperationException("转换期间无法修改输出质量。");
        if (qualitySlider == null) throw new InvalidOperationException("质量滑块尚未创建。");
        qualitySlider.Value = quality;
    }

    public Task<bool> BeginBatchForTest() { return BeginBatchAsync(true); }

    public bool IsBusy { get { return busy; } }

    public static bool CanPlanForTest(IList<string> paths, string outputDirectory, out List<string> errors)
    {
        List<QueueItem> testItems = paths.Select(delegate(string path) { return new QueueItem(Path.GetFullPath(path)); }).ToList();
        Dictionary<QueueItem, string> outputs;
        return TryBuildOutputPaths(testItems, outputDirectory, out outputs, out errors);
    }

    public void LayoutForTest()
    {
        FrameworkElement content = window.Content as FrameworkElement;
        if (content == null) throw new InvalidOperationException("界面内容缺失。");
        content.Measure(new Size(1020, 628));
        content.Arrange(new Rect(0, 0, 1020, 628));
        content.UpdateLayout();
        if (content.ActualWidth <= 0 || content.ActualHeight <= 0)
            throw new InvalidOperationException("界面离屏布局无效。");
        if (selectImagesButton.Template == null || !selectImagesButton.Focusable)
            throw new InvalidOperationException("图片选择按钮无法键盘访问。");
    }

    public static int RunSelfTest(string inputPath, string testOutputDirectory)
    {
        string fullOutputDirectory = Path.GetFullPath(testOutputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);
        string reportPath = Path.Combine(fullOutputDirectory, "frontend-test.json");
        Dictionary<string, object> report = new Dictionary<string, object>();
        Dictionary<string, object> checks = new Dictionary<string, object>();
        report["mode"] = "self-test";
        report["input"] = Path.GetFullPath(inputPath);
        report["checks"] = checks;
        report["success"] = false;
        int exitCode = 1;
        GlassApp app = null;
        try
        {
            if (!File.Exists(inputPath) || !String.Equals(Path.GetExtension(inputPath), ".jxr", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("自检输入必须是已存在的 JXR 文件。");
            string testRoot = Path.Combine(fullOutputDirectory, "_frontend-self-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);
            Application application = Application.Current;
            if (application == null) application = new Application();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app = new GlassApp(false);
            app.CreateWindow();
            app.window.ShowInTaskbar = false;
            app.LayoutForTest();
            checks["xaml_resource_and_offscreen_layout"] = true;
            checks["native_folder_picker_without_showing"] = NativeFolderPicker.ValidateWithoutShowing(testRoot);

            app.SetTestOutputDirectoryForTest(testRoot);
            int added = app.AddForTest(new string[] { inputPath, inputPath });
            IList<QueueItem> current = app.GetItems();
            bool queueAndDedup = added == 1 && current.Count == 1 && current[0].Status == "待转换";
            checks["queue_add_and_duplicate_ignored"] = queueAndDedup;
            Task<bool> conversionTask = app.BeginBatchForTest();
            PumpDispatcher(conversionTask);
            bool converted = conversionTask.Result && app.GetItems().Count == 1 && app.GetItems()[0].Status == "已完成";
            checks["worker_conversion_and_completed_state"] = converted;
            report["conversion_output"] = app.GetItems().Count == 1 ?
                app.GetBatchResults().LastOrDefault() == null ? "" : app.GetBatchResults().LastOrDefault()["destination"] : "";
            if (app.GetBatchResults().Count > 0)
            {
                Dictionary<string, object> latest = app.GetBatchResults().Last();
                if (latest.ContainsKey("info")) report["worker_info"] = latest["info"];
                if (latest.ContainsKey("error")) report["conversion_error"] = latest["error"];
            }

            int reportedQuality;
            Dictionary<string, object> quality95Result = app.GetBatchResults().LastOrDefault();
            bool defaultQuality95 = qualitySliderShows(app, 95) && converted &&
                TryGetReportedQuality(quality95Result, out reportedQuality) && reportedQuality == 95;
            checks["default_quality_95_conversion"] = defaultQuality95;

            app.ClearForTest();
            string quality60OutputDirectory = Path.Combine(testRoot, "quality-60");
            Directory.CreateDirectory(quality60OutputDirectory);
            app.SetTestOutputDirectoryForTest(quality60OutputDirectory);
            app.SetQualityForTest(60);
            bool quality60Control = qualitySliderShows(app, 60);
            checks["quality_60_slider_and_label"] = quality60Control;
            app.AddForTest(new string[] { inputPath });
            Task<bool> quality60Task = app.BeginBatchForTest();
            PumpDispatcher(quality60Task);
            Dictionary<string, object> quality60Result = app.GetBatchResults().LastOrDefault();
            int quality60ReportedValue;
            bool quality60Converted = quality60Task.Result && app.GetItems().Count == 1 &&
                app.GetItems()[0].Status == "已完成" && TryGetReportedQuality(quality60Result, out quality60ReportedValue) &&
                quality60ReportedValue == 60;
            checks["quality_60_end_to_end"] = quality60Control && quality60Converted;
            if (quality60Result != null)
            {
                report["quality_60_conversion_output"] = quality60Result["destination"];
                if (quality60Result.ContainsKey("info")) report["quality_60_worker_info"] = quality60Result["info"];
            }

            app.ClearForTest();
            string badDirectory = Path.Combine(testRoot, "bad-input");
            string badOutput = Path.Combine(testRoot, "bad-output");
            Directory.CreateDirectory(badDirectory);
            Directory.CreateDirectory(badOutput);
            string badInput = Path.Combine(badDirectory, "not-an-image.jxr");
            File.WriteAllBytes(badInput, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });
            app.SetTestOutputDirectoryForTest(badOutput);
            app.AddForTest(new string[] { badInput });
            Task<bool> badTask = app.BeginBatchForTest();
            PumpDispatcher(badTask);
            IList<QueueItem> badItems = app.GetItems();
            bool badRejected = badItems.Count == 1 && badItems[0].Status == "失败";
            checks["invalid_jxr_is_reported_as_failure"] = badRejected;
            if (badItems.Count > 0) report["bad_jxr_error"] = badItems[0].StatusDetail;

            app.ClearForTest();
            string firstDirectory = Path.Combine(testRoot, "collision-input-a");
            string secondDirectory = Path.Combine(testRoot, "collision-input-b");
            string collisionOutput = Path.Combine(testRoot, "collision-output");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            Directory.CreateDirectory(collisionOutput);
            string firstCollision = Path.Combine(firstDirectory, "same-name.jxr");
            string secondCollision = Path.Combine(secondDirectory, "same-name.jxr");
            File.WriteAllBytes(firstCollision, new byte[] { 1 });
            File.WriteAllBytes(secondCollision, new byte[] { 2 });
            app.SetTestOutputDirectoryForTest(collisionOutput);
            int collisionAdded = app.AddForTest(new string[] { firstCollision, secondCollision });
            Task<bool> collisionTask = app.BeginBatchForTest();
            bool collisionRejected = !collisionTask.Result && collisionAdded == 2 && app.GetItems().Count == 2 &&
                app.GetItems().All(delegate(QueueItem item) { return item.Status == "失败"; });
            checks["same_stem_output_collision_rejected"] = collisionRejected;

            bool allPassed = checks.Values.All(delegate(object value) { return Object.Equals(value, true); });
            report["success"] = allPassed;
            report["test_output_directory"] = fullOutputDirectory;
            report["test_files_directory"] = testRoot;
            exitCode = allPassed ? 0 : 1;
        }
        catch (Exception ex)
        {
            report["success"] = false;
            report["error"] = ex.ToString();
            exitCode = 1;
        }
        finally
        {
            if (app != null) app.Dispose();
            try
            {
                File.WriteAllText(reportPath, new JavaScriptSerializer().Serialize(report), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("无法写入自检报告：" + ex.Message);
                exitCode = 1;
            }
        }
        return exitCode;
    }

    static void PumpDispatcher(Task task)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        DispatcherFrame frame = new DispatcherFrame();
        task.ContinueWith(delegate
        {
            dispatcher.BeginInvoke(new Action(delegate { frame.Continue = false; }));
        });
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    static bool qualitySliderShows(GlassApp app, int quality)
    {
        return app.qualitySlider != null && app.qualityValueLabel != null &&
            (int)Math.Round(app.qualitySlider.Value) == quality &&
            app.qualityValueLabel.Text == quality.ToString(CultureInfo.InvariantCulture);
    }

    static bool TryGetReportedQuality(Dictionary<string, object> result, out int quality)
    {
        quality = 0;
        object infoValue;
        System.Collections.IDictionary info = result != null && result.TryGetValue("info", out infoValue)
            ? infoValue as System.Collections.IDictionary : null;
        if (info == null || !info.Contains("quality")) return false;
        try
        {
            quality = System.Convert.ToInt32(info["quality"], CultureInfo.InvariantCulture);
            return true;
        }
        catch { return false; }
    }

    public static int RunConvert(string inputPath, string outputPath)
    {
        try
        {
            string source = Path.GetFullPath(inputPath);
            string destination = Path.GetFullPath(outputPath);
            if (!File.Exists(source)) throw new FileNotFoundException("找不到输入文件。", source);
            if (File.Exists(destination)) throw new IOException("输出文件已存在；命令行模式不会覆盖现有文件。");
            string parent = Path.GetDirectoryName(destination);
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("输出目录不存在：" + parent);
            using (WorkerClient client = new WorkerClient())
            {
                WorkerResult result = client.Convert(source, destination, false);
                if (result == null || !result.Success)
                    throw new InvalidOperationException(result == null ? "转换失败。" : NormalizeError(result.Error));
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("GainMap 转换失败：" + ex.Message);
            return 1;
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args != null && args.Length > 0 && args[0] == "--self-test")
        {
            if (args.Length != 3)
            {
                Console.Error.WriteLine("用法：--self-test INPUT_JXR TEST_OUTPUT_DIR");
                return 2;
            }
            try { return RunSelfTest(args[1], args[2]); }
            catch (Exception ex) { Console.Error.WriteLine("自检失败：" + ex.Message); return 1; }
        }
        if (args != null && args.Length > 0 && args[0] == "--convert")
        {
            if (args.Length != 3)
            {
                Console.Error.WriteLine("用法：--convert INPUT_JXR OUTPUT_JPEG");
                return 2;
            }
            return RunConvert(args[1], args[2]);
        }
        if (args != null && args.Length > 0)
        {
            Console.Error.WriteLine("未知参数。使用 --convert INPUT_JXR OUTPUT_JPEG 或 --self-test INPUT_JXR TEST_OUTPUT_DIR。");
            return 2;
        }
        GlassApp app = null;
        try
        {
            Application application = new Application();
            app = new GlassApp(true);
            Window mainWindow = app.CreateWindow();
            application.Run(mainWindow);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show("GainMap 无法启动。\n\n" + ex.Message, "GainMap",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        finally { if (app != null) app.Dispose(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        worker.Dispose();
    }

}

public sealed class QueueItem : INotifyPropertyChanged
{
    string status;
    string statusDetail;
    public string FilePath { get; private set; }
    public string FileName { get { return Path.GetFileName(FilePath); } }
    public string Status
    {
        get { return status; }
        set { if (status != value) { status = value; OnPropertyChanged("Status"); } }
    }
    public string StatusDetail
    {
        get { return statusDetail; }
        set { if (statusDetail != value) { statusDetail = value; OnPropertyChanged("StatusDetail"); } }
    }
    public QueueItem(string path)
    {
        FilePath = Path.GetFullPath(path);
        status = "待转换";
        statusDetail = Path.GetDirectoryName(FilePath);
    }
    public event PropertyChangedEventHandler PropertyChanged;
    void OnPropertyChanged(string propertyName)
    {
        PropertyChangedEventHandler handler = PropertyChanged;
        if (handler != null) handler(this, new PropertyChangedEventArgs(propertyName));
    }
}

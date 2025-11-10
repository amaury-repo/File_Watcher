using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace File_Watcher
{
    public partial class Form1 : Form
    {
        private NotifyIcon trayIcon;  // 系统托盘图标

        private ContextMenu trayMenu;  // 托盘图标右键菜单

        private FileSystemWatcher fileWatcher;  // 文件监控对象

        private string watchFolderPath;  // 要监控的文件夹的路径

        private string outputFolderPath;

        private List<int> filterList = new List<int>();

        // 图标
        private static readonly Icon appIcon = new Icon(Assembly.GetExecutingAssembly().GetManifestResourceStream("File_Watcher.icon.ico"));

        public Form1()
        {
            InitializeComponent();
            this.Icon = appIcon;  // 设置窗体图标
            LogHelper.InitializeLogging();
            LoadWatchParametersFromIni(); // 从 INI 文件加载需要监控的目录
            InitializeTrayIcon();  // 初始化系统托盘图标
            InitializeFileWatcher();  // 初始化文件监控
            HideForm();  // 隐藏窗体
        }

        // 初始化托盘图标和右键菜单
        private void InitializeTrayIcon()
        {
            trayMenu = new ContextMenu();
            trayMenu.MenuItems.Add("Exit", OnExit);  // 菜单项：退出

            trayIcon = new NotifyIcon
            {
                Text = "File Watcher",  // 鼠标悬停时显示的文本
                Icon = appIcon,  // 设置图标
                ContextMenu = trayMenu,  // 设置右键菜单
                Visible = true  // 显示托盘图标
            };
        }

        // 隐藏窗体，使其最小化到系统托盘
        private void HideForm()
        {
            this.WindowState = FormWindowState.Minimized;  // 设置窗体最小化
            this.ShowInTaskbar = false;  // 不显示在任务栏
        }

        // 退出应用程序
        private void OnExit(object sender, EventArgs e)
        {
            try
            {
                // 释放托盘图标
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                    trayIcon = null;
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                // 确保应用程序退出
                Application.Exit();
            }
        }

        // 窗体加载时自动隐藏
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Hide();  // 隐藏窗体
        }

        // 从 INI 文件中读取需要监控的参数
        private void LoadWatchParametersFromIni()
        {
            string iniFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");

            if (!System.IO.File.Exists(iniFilePath))
            {
                MessageBox.Show("配置文件 config.ini 未找到，请检查！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
            }

            watchFolderPath = IniFileReader.ReadIniValue(iniFilePath, "Settings", "WatchFolder", "").Trim();
            outputFolderPath = IniFileReader.ReadIniValue(iniFilePath, "Settings", "OutputFolder", "").Trim();
            string filterString = IniFileReader.ReadIniValue(iniFilePath, "Settings", "Filter", "").Trim();

            filterList = filterString
    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
    .Select(s => int.TryParse(s.Trim(), out int v) ? v : -1)
    .Where(v => v >= 0)
    .ToList();

            // 如果 JSON 输出文件夹不存在，创建
            if (!string.IsNullOrEmpty(outputFolderPath) && !Directory.Exists(outputFolderPath))
            {
                Directory.CreateDirectory(outputFolderPath);
            }

            if (!string.IsNullOrEmpty(watchFolderPath) && Directory.Exists(watchFolderPath))
            {
                LogHelper.WriteLog($"INFO: 监控文件夹: {watchFolderPath}");
            }
            else
            {
                LogHelper.WriteLog("ERROR: 无有效的路径配置，请检查 INI 文件参数！");
                Application.Exit();
            }
        }

        // 初始化文件监控
        private void InitializeFileWatcher()
        {
            fileWatcher = new FileSystemWatcher
            {
                Path = watchFolderPath,
                Filter = "*.csv",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };

            fileWatcher.Created += OnFileCreated;

            LogHelper.WriteLog("INFO: 文件监控已启动...");
        }

        // 文件新增事件
        private void OnFileCreated(object source, FileSystemEventArgs e)
        {
            try
            {
                LogHelper.WriteLog($"INFO: 检测到新增文件: {e.FullPath}");
                System.Threading.Thread.Sleep(100); // 防止文件未写完就读

                HandleCsvProcessing(e.FullPath);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLog($"ERROR: 处理文件变化时发生错误: {ex.Message}");
            }
        }

        private void HandleCsvProcessing(string filePath)
        {
            LogHelper.WriteLog($"INFO: 开始处理文件: {filePath}");
            (string serialNumber, int programNumber, List<double> xVals, List<double> yVals) = ParseCsvData(filePath);
            if (!filterList.Contains(programNumber))
            {
                LogHelper.WriteLog($"INFO: 程序号 {programNumber} 不在过滤列表中，跳过生成");
                return;
            }
            if (xVals != null && yVals != null && !string.IsNullOrEmpty(serialNumber))
            {
                SaveAsJson(outputFolderPath, serialNumber, xVals, yVals);
            }
            else
            {
                LogHelper.WriteLog("WARN: CSV 文件解析失败或无有效数据。");
            }
        }

        private static (string serialNumber, int programNumber, List<double> xVals, List<double> yVals)
ParseCsvData(string filePath)
        {
            string serialNumber = "";
            int programNumber = 0;

            List<double> xVals = new List<double>();
            List<double> yVals = new List<double>();

            var lines = SafeReadAllLines(filePath);

            bool dataSectionStarted = false;

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // 前置信息
                if (line.StartsWith("Part serial number;", StringComparison.OrdinalIgnoreCase))
                {
                    serialNumber = line.Split(';')[1].Trim();
                    continue;
                }

                if (line.StartsWith("Measuring program number;", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line.Split(';')[1].Trim(), out programNumber);
                    continue;
                }

                if (line.StartsWith("s;mm;KN", StringComparison.OrdinalIgnoreCase))
                {
                    dataSectionStarted = true;
                    continue;
                }

                // 数据区
                if (dataSectionStarted)
                {
                    string[] parts = line.Split(';');
                    if (parts.Length < 4)
                        continue;

                    if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                        double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                    {
                        xVals.Add(x);
                        yVals.Add(y);
                    }
                }
            }

            return (serialNumber, programNumber, xVals, yVals);
        }

        private static string[] SafeReadAllLines(string filePath)
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    return File.ReadAllLines(filePath);
                }
                catch
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
            throw new IOException($"文件正在被占用，无法读取: {filePath}");
        }

        private static void SaveAsJson(string outputFolder, string curveId, List<double> xVals, List<double> yVals)
        {
            try
            {
                var data = new[]
                {
                    new
                    {
                        CurveId = curveId,
                        x_vals = xVals,
                        y_vals = yVals
                    }
                };

                string jsonFileName = $"{curveId}_{DateTime.Now:yyyyMMddHHmmss}.json";
                string jsonFilePath = Path.Combine(outputFolder, jsonFileName);

                System.IO.File.WriteAllText(jsonFilePath, JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.Indented));
                LogHelper.WriteLog($"INFO: 文件已保存到: {jsonFilePath}");
            }
            catch (Exception ex)
            {
                LogHelper.WriteLog($"ERROR: 保存 JSON 文件失败: {ex.Message}");
            }
        }
    }

    public static class LogHelper
    {
        private static readonly string LogFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

        public static void InitializeLogging()
        {
            if (!Directory.Exists(LogFolderPath))
            {
                Directory.CreateDirectory(LogFolderPath);
            }
        }

        public static void WriteLog(string message)
        {
            string logFilePath = Path.Combine(LogFolderPath, $"{DateTime.Now:yyyy-MM-dd}.log");
            string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";

            System.IO.File.AppendAllText(logFilePath, logMessage + Environment.NewLine);
        }
    }

    // INI 文件读取工具类
    public static class IniFileReader
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetPrivateProfileString(
            string lpAppName, string lpKeyName, string lpDefault, StringBuilder lpReturnedString,
            int nSize, string lpFileName);

        public static string ReadIniValue(string filePath, string section, string key, string defaultValue = "")
        {
            const int bufferSize = 1024;
            StringBuilder returnedString = new StringBuilder(bufferSize);
            GetPrivateProfileString(section, key, defaultValue, returnedString, bufferSize, filePath);
            return returnedString.ToString();
        }
    }
}
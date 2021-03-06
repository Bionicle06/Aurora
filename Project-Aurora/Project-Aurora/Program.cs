﻿using Aurora.Devices;
using Aurora.Profiles;
using Aurora.Settings;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;
using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using SharpDX.RawInput;
using System.Reflection;

namespace Aurora
{
    /// <summary>
    /// Globally accessible classes and variables
    /// </summary>
    public static class Global
    {
        public static string ScriptDirectory = "Scripts";
        public static ScriptEngine PythonEngine = Python.CreateEngine();

        /// <summary>
        /// A boolean indicating if Aurora was started with Debug parameter
        /// </summary>
        public static bool isDebug = false;

        private static string _ExecutingDirectory = "";

        /// <summary>
        /// The path to the application executing directory
        /// </summary>
        public static string ExecutingDirectory
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_ExecutingDirectory))
                    _ExecutingDirectory = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);

                return _ExecutingDirectory;
            }
        }

        /// <summary>
        /// Output logger for errors, warnings, and information
        /// </summary>
        public static Logger logger;

        /// <summary>
        /// Input event subscriptions
        /// </summary>
        public static InputEvents InputEvents;

        //public static GameEventHandler geh;
        public static PluginManager PluginManager;
        public static LightingStateManager LightingStateManager;
        public static NetworkListener net_listener;
        public static Configuration Configuration;
        public static DeviceManager dev_manager;
        public static KeyboardLayoutManager kbLayout;
        public static Effects effengine;
        public static KeyRecorder key_recorder;

        /// <summary>
        /// Currently held down modifer key
        /// </summary>
        public static Keys held_modified = Keys.None;

        public static object Clipboard { get; set; }

        public static long StartTime;

        public static void Initialize()
        {
            logger = new Logger();
        }
    }

    static class Program
    {
        private static readonly Mutex mutex = new Mutex(true, "{C88D62B0-DE49-418E-835D-CE213D58444C}");
        public static System.Windows.Application WinApp { get; private set; }
        public static Window MainWindow;
        private static InputInterceptor InputInterceptor;

        public static bool isSilent = false;
        private static bool isDelayed = false;
        private static int delayTime = 5000;
        private static bool ignore_update = false;

        [STAThread]
        static void Main(string[] args)
        {
            if (mutex.WaitOne(TimeSpan.Zero, true))
            {
#if DEBUG
                Global.isDebug = true;
#endif
                Global.Initialize();
                Directory.SetCurrentDirectory(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));

                string arg = "";

                for (int arg_i = 0; arg_i < args.Length; arg_i++)
                {
                    arg = args[arg_i];

                    switch (arg)
                    {
                        case ("-debug"):
                            Global.isDebug = true;
                            Global.logger.LogLine("Program started in debug mode.", Logging_Level.Info);
                            break;
                        case ("-silent"):
                            isSilent = true;
                            Global.logger.LogLine("Program started with '-silent' parameter", Logging_Level.Info);
                            break;
                        case ("-ignore_update"):
                            ignore_update = true;
                            Global.logger.LogLine("Program started with '-ignore_update' parameter", Logging_Level.Info);
                            break;
                        case ("-delay"):
                            isDelayed = true;

                            if (arg_i + 1 < args.Length && int.TryParse(args[arg_i + 1], out delayTime))
                                arg_i++;
                            else
                                delayTime = 5000;

                            Global.logger.LogLine("Program started with '-delay' parameter with delay of " + delayTime + " ms", Logging_Level.Info);

                            break;
                        case ("-install_logitech"):
                            Global.logger.LogLine("Program started with '-install_logitech' parameter", Logging_Level.Info);

                            try
                            {
                                InstallLogitech();
                            }
                            catch (Exception exc)
                            {
                                System.Windows.MessageBox.Show("Could not patch Logitech LED SDK. Error: \r\n\r\n" + exc, "Aurora Error");
                            }

                            Environment.Exit(0);
                            break;
                    }
                }

                AppDomain currentDomain = AppDomain.CurrentDomain;
                if (!Global.isDebug)
                    currentDomain.UnhandledException += CurrentDomain_UnhandledException;

                //Make sure there is only one instance of Aurora
                /*Process[] processes;
                if ((processes = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName)).Length > 1)
                {
                    try
                    {
                        NamedPipeClientStream client = new NamedPipeClientStream(".", "aurora\\interface", PipeDirection.Out);
                        client.Connect(30);
                        if (!client.IsConnected)
                            throw new Exception();
                        byte[] command = System.Text.Encoding.ASCII.GetBytes("restore");
                        client.Write(command, 0, command.Length);
                        client.Close();
                    }
                    catch
                    {
                        Global.logger.LogLine("Aurora is already running.", Logging_Level.Error);
                        System.Windows.MessageBox.Show("Aurora is already running.\r\nExiting.", "Aurora - Error");
                    }
                    Environment.Exit(0);
                }*/


                if (isDelayed)
                    System.Threading.Thread.Sleep((int)delayTime);

                AppDomain.CurrentDomain.ProcessExit += new EventHandler(OnProcessExit);

                Global.StartTime = Utils.Time.GetMillisecondsSinceEpoch();

                Global.dev_manager = new DeviceManager();
                Global.effengine = new Effects();

                //Load config
                Global.logger.LogLine("Loading Configuration", Logging_Level.Info);
                try
                {
                    Global.Configuration = ConfigManager.Load();
                }
                catch (Exception e)
                {
                    Global.logger.LogLine("Exception during ConfigManager.Load(). Error: " + e, Logging_Level.Error);
                    System.Windows.MessageBox.Show("Exception during ConfigManager.Load().Error: " + e.Message + "\r\n\r\n Default configuration loaded.", "Aurora - Error");

                    Global.Configuration = new Configuration();
                }

                if (Global.Configuration.updates_check_on_start_up && !ignore_update)
                {
                    string updater_path = System.IO.Path.Combine(Global.ExecutingDirectory, "Aurora-Updater.exe");

                    if (File.Exists(updater_path))
                    {
                        try
                        {
                            ProcessStartInfo updaterProc = new ProcessStartInfo();
                            updaterProc.FileName = updater_path;
                            updaterProc.Arguments = Global.Configuration.updates_allow_silent_minor ? "-silent_minor -silent" : "-silent";
                            Process.Start(updaterProc);
                        }
                        catch (Exception exc)
                        {
                            Global.logger.LogLine("Could not start Aurora Updater. Error: " + exc, Logging_Level.Error);
                        }
                    }
                }

                Global.logger.LogLine("Loading Plugins", Logging_Level.Info);
                (Global.PluginManager = new PluginManager()).Initialize();

	            Global.logger.LogLine("Loading KB Layouts", Logging_Level.Info);
	            Global.kbLayout = new KeyboardLayoutManager();
	            Global.kbLayout.LoadBrand(Global.Configuration.keyboard_brand, Global.Configuration.mouse_preference, Global.Configuration.mouse_orientation);

				Global.logger.LogLine("Loading Input Hooking", Logging_Level.Info);
                Global.InputEvents = new InputEvents();
                Global.InputEvents.KeyDown += InputEventsOnKeyDown;
                Global.Configuration.PropertyChanged += SetupVolumeAsBrightness;
                SetupVolumeAsBrightness(Global.Configuration,
                    new PropertyChangedEventArgs(nameof(Global.Configuration.UseVolumeAsBrightness)));

                Global.key_recorder = new KeyRecorder(Global.InputEvents);

                Global.logger.LogLine("Loading Applications", Logging_Level.Info);
                (Global.LightingStateManager = new LightingStateManager()).Initialize();

                Global.logger.LogLine("Loading Device Manager", Logging_Level.Info);
                Global.dev_manager.RegisterVariables();
                Global.dev_manager.Initialize();





                /*Global.logger.LogLine("Starting GameEventHandler", Logging_Level.Info);
                Global.geh = new GameEventHandler();
                if (!Global.geh.Init())
                {
                    Global.logger.LogLine("GameEventHander could not initialize", Logging_Level.Error);
                    return;
                }*/

                Global.logger.LogLine("Starting GameStateListener", Logging_Level.Info);
                try
                {
                    Global.net_listener = new NetworkListener(9088);
                    Global.net_listener.NewGameState += new NewGameStateHandler(Global.LightingStateManager.GameStateUpdate);
                    Global.net_listener.WrapperConnectionClosed += new WrapperConnectionClosedHandler(Global.LightingStateManager.ResetGameState);
                }
                catch (Exception exc)
                {
                    Global.logger.LogLine("GameStateListener Exception, " + exc, Logging_Level.Error);
                    System.Windows.MessageBox.Show("GameStateListener Exception.\r\n" + exc);
                    Environment.Exit(0);
                }

                if (!Global.net_listener.Start())
                {
                    Global.logger.LogLine("GameStateListener could not start", Logging_Level.Error);
                    System.Windows.MessageBox.Show("GameStateListener could not start. Try running this program as Administrator.\r\nExiting.");
                    Environment.Exit(0);
                }

                Global.logger.LogLine("Listening for game integration calls...", Logging_Level.None);

                Global.logger.LogLine("Loading WinApp...", Logging_Level.None);
                WinApp = new System.Windows.Application();
                Global.logger.LogLine("Loaded WinApp", Logging_Level.None);

                Global.logger.LogLine("Loading ResourceDictionaries...", Logging_Level.None);

                WinApp.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Themes/MetroDark/MetroDark.MSControls.Core.Implicit.xaml", UriKind.Relative) });
                WinApp.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Themes/MetroDark/MetroDark.MSControls.Toolkit.Implicit.xaml", UriKind.Relative) });

                Global.logger.LogLine("Loaded ResourceDictionaries", Logging_Level.None);

                WinApp.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                Global.logger.LogLine("Loading ConfigUI...", Logging_Level.None);

                MainWindow = new ConfigUI();
                WinApp.MainWindow = MainWindow;
                ((ConfigUI)MainWindow).Display();

                WinApp.Run();

                ConfigManager.Save(Global.Configuration);

                Exit();
            }
            else
            {
                try
                {
                    NamedPipeClientStream client = new NamedPipeClientStream(".", "aurora\\interface", PipeDirection.Out);
                    client.Connect(30);
                    if (!client.IsConnected)
                        throw new Exception();
                    byte[] command = System.Text.Encoding.ASCII.GetBytes("restore");
                    client.Write(command, 0, command.Length);
                    client.Close();
                }
                catch
                {
                    //Global.logger.LogLine("Aurora is already running.", Logging_Level.Error);
                    System.Windows.MessageBox.Show("Aurora is already running.\r\nExiting.", "Aurora - Error");
                }
            }
        }

        private static void InputEventsOnKeyDown(object sender, KeyboardInputEventArgs e)
        {
            if (e.Key == Keys.VolumeUp || e.Key == Keys.VolumeDown)
            {
                Global.LightingStateManager.AddOverlayForDuration(
                    new Profiles.Overlays.Event_VolumeOverlay(), Global.Configuration.volume_overlay_settings.delay * 1000);
            }
        }

        private static void SetupVolumeAsBrightness(object sender, PropertyChangedEventArgs eventArgs)
        {
            if (eventArgs.PropertyName == nameof(Global.Configuration.UseVolumeAsBrightness))
            {
                if (Global.Configuration.UseVolumeAsBrightness)
                {
                    InputInterceptor = new InputInterceptor();
                    InputInterceptor.Input += InterceptVolumeAsBrightness;
                }
                else if (InputInterceptor != null)
                {
                    InputInterceptor.Input -= InterceptVolumeAsBrightness;
                    InputInterceptor.Dispose();
                }
            }
        }

        private static void InterceptVolumeAsBrightness(object sender, InputInterceptor.InputEventData e)
        {
            var keys = (Keys)e.Data.VirtualKeyCode;
            
            if ((keys.HasFlag(Keys.VolumeDown) || keys.HasFlag(Keys.VolumeUp))
                && Global.InputEvents.Alt)
            {
                e.Intercepted = true;
                Task.Factory.StartNew(() =>
                    {
                        if (e.KeyDown)
                        {
                            float brightness = Global.Configuration.GlobalBrightness;
                            brightness += keys == Keys.VolumeUp ? 0.05f : -0.05f;
                            Global.Configuration.GlobalBrightness = Math.Max(0f, Math.Min(1f, brightness));

                            ConfigManager.Save(Global.Configuration);
                        }
                    }
                );
            }
        }

        /// <summary>
        /// Executes exit operations
        /// </summary>
        public static void Exit()
        {
            Global.LightingStateManager.SaveAll();
            Global.PluginManager.SaveSettings();

            if (Global.Configuration != null)
                ConfigManager.Save(Global.Configuration);

            Global.key_recorder?.Dispose();
            Global.InputEvents?.Dispose();
            Global.LightingStateManager?.Dispose();
            Global.net_listener?.Stop();
            Global.dev_manager?.Shutdown();
            Global.dev_manager?.Dispose();

            InputInterceptor?.Dispose();

            try
            {
                foreach (Process proc in Process.GetProcessesByName("Aurora-SkypeIntegration"))
                {
                    proc.Kill();
                }
            }
            catch (Exception exc)
            {
                Global.logger.LogLine("Exception closing \"Aurora-SkypeIntegration\", Exception: " + exc);
            }

            Global.logger.Dispose();

            //Environment.Exit(0);
            Process.GetCurrentProcess().Kill();
            //System.Windows.Application.Current.Shutdown();
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception exc = (Exception)e.ExceptionObject;
            Global.logger.LogLine("Fatal Exception caught : " + exc, Logging_Level.Error);
            Global.logger.LogLine(String.Format("Runtime terminating: {0}", e.IsTerminating), Logging_Level.Error);

            System.Windows.MessageBox.Show("Aurora fatally crashed. Please report the follow to author: \r\n\r\n" + exc, "Aurora has stopped working");

            //Perform exit operations
            Exit();
        }

        public static void InstallLogitech()
        {
            //Check for Admin
            bool isElevated;
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);

            if (!isElevated)
            {
                Global.logger.LogLine("Program does not have admin rights", Logging_Level.Error);
                System.Windows.MessageBox.Show("Program does not have admin rights");
                Environment.Exit(1);
            }

            //Patch 32-bit
            string logitech_path = (string)Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node\CLSID\{a6519e67-7632-4375-afdf-caa889744403}\ServerBinary", "(Default)", null);
            if (logitech_path == null)
            {
                logitech_path = @"C:\Program Files\Logitech Gaming Software\SDK\LED\x86\LogitechLed.dll";

                if (!Directory.Exists(Path.GetDirectoryName(logitech_path)))
                    Directory.CreateDirectory(Path.GetDirectoryName(logitech_path));

                RegistryKey key = Registry.LocalMachine.OpenSubKey("SOFTWARE", true);

                key.CreateSubKey("Classes");
                key = key.OpenSubKey("Classes", true);

                key.CreateSubKey("WOW6432Node");
                key = key.OpenSubKey("WOW6432Node", true);

                key.CreateSubKey("CLSID");
                key = key.OpenSubKey("CLSID", true);

                key.CreateSubKey("{a6519e67-7632-4375-afdf-caa889744403}");
                key = key.OpenSubKey("{a6519e67-7632-4375-afdf-caa889744403}", true);

                key.CreateSubKey("ServerBinary");
                key = key.OpenSubKey("ServerBinary", true);

                key.SetValue("(Default)", logitech_path);
            }

            if (File.Exists(logitech_path) && !File.Exists(logitech_path + ".aurora_backup"))
                File.Move(logitech_path, logitech_path + ".aurora_backup");

            using (BinaryWriter logitech_wrapper_86 = new BinaryWriter(new FileStream(logitech_path, FileMode.Create, FileAccess.Write)))
            {
                logitech_wrapper_86.Write(Properties.Resources.Aurora_LogiLEDWrapper86);
            }

            //Patch 64-bit
            string logitech_path_64 = (string)Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID\{a6519e67-7632-4375-afdf-caa889744403}\ServerBinary", "(Default)", null);
            if (logitech_path_64 == null)
            {
                logitech_path_64 = @"C:\Program Files\Logitech Gaming Software\SDK\LED\x64\LogitechLed.dll";

                if (!Directory.Exists(Path.GetDirectoryName(logitech_path_64)))
                    Directory.CreateDirectory(Path.GetDirectoryName(logitech_path_64));

                RegistryKey key = Registry.LocalMachine.OpenSubKey("SOFTWARE", true);

                key.CreateSubKey("Classes");
                key = key.OpenSubKey("Classes", true);

                key.CreateSubKey("CLSID");
                key = key.OpenSubKey("CLSID", true);

                key.CreateSubKey("{a6519e67-7632-4375-afdf-caa889744403}");
                key = key.OpenSubKey("{a6519e67-7632-4375-afdf-caa889744403}", true);

                key.CreateSubKey("ServerBinary");
                key = key.OpenSubKey("ServerBinary", true);

                key.SetValue("(Default)", logitech_path_64);
            }

            if (File.Exists(logitech_path_64) && !File.Exists(logitech_path_64 + ".aurora_backup"))
                File.Move(logitech_path_64, logitech_path_64 + ".aurora_backup");

            using (BinaryWriter logitech_wrapper_64 = new BinaryWriter(new FileStream(logitech_path_64, FileMode.Create, FileAccess.Write)))
            {
                logitech_wrapper_64.Write(Properties.Resources.Aurora_LogiLEDWrapper64);
            }

            Global.logger.LogLine("Logitech LED SDK patched successfully", Logging_Level.Info);
            System.Windows.MessageBox.Show("Logitech LED SDK patched successfully");

            //Environment.Exit(0);
        }

        static void OnProcessExit(object sender, EventArgs e)
        {
            try
            {
                Global.net_listener?.Stop();

                Global.dev_manager?.Shutdown();
                Global.dev_manager?.Dispose();



                //Kill all Skype Integrations on Exit
                foreach (Process proc in Process.GetProcessesByName("Aurora-SkypeIntegration"))
                {
                    proc.Kill();
                }

            }
            catch (Exception exc)
            {
                Global.logger.LogLine("Exception during OnProcessExit(). Error: " + exc, Logging_Level.Error);
            }
        }
    }
}

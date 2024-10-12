using log4net.Core;
using MahApps.Metro.Controls;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using WoteverCommon.Extensions;
using vJoyInterfaceWrap;
using HidLibrary;

namespace JZCoding.Simhub.FlightPlugin {
	[PluginDescription("Flight Simulator Plugin")]
	[PluginAuthor("Jamie")]
	[PluginName("MSFS Plugin")]
	public class FlightPlugin : IPlugin, IWPFSettingsV2 {
		public PluginSettings Settings;

		/// <summary>
		/// Instance of the current plugin manager
		/// </summary>
		public PluginManager PluginManager { get; set; }

		/// <summary>
		/// Gets the left menu icon. Icon must be 24x24 and compatible with black and white display.
		/// </summary>
		public ImageSource PictureIcon => this.ToIcon(Properties.Resources.menuIcon);

		/// <summary>
		/// Gets a short plugin title to show in left menu. Return null if you want to use the title as defined in PluginName attribute.
		/// </summary>
		public string LeftMenuTitle => "Flight Plugin";

        private UI UI { set; get; }
		private static Thread UdpListeningThread;
		private static Process SimconnectServerProcess;
        private static vJoyInterfaceWrap.vJoy joystick;
		public DateTime LastUpdateTime { private set; get; } = DateTime.MinValue;
		public DateTime ShowLogUntil { set; get; }
		public bool IsSimconnectServerRunning { get => SimconnectServerProcess?.HasExited == false; }
		public bool IsUdpListening { get => UdpListeningThread?.ThreadState == System.Threading.ThreadState.Running; }

		public FlightPlugin() {
			UI = new UI(this);
            joystick = new vJoy();

        }

        private void PluginManager_ApplicationExit(object sender, EventArgs e) {
			UI.ServerProcess?.Kill();
			this.StopUdpServer();
		}

		internal TelemetryStates Telemetry { set; get; }

		/// <summary>
		/// Called at plugin manager stop, close/dispose anything needed here !
		/// Plugins are rebuilt at game change
		/// </summary>
		/// <param name="pluginManager"></param>
		public void End(PluginManager pluginManager) {
			// Save settings
			this.SaveCommonSettings(pluginManager.GameName, Settings);
		}



		/// <summary>
		/// Returns the settings control, return null if no settings control is required
		/// </summary>
		/// <param name="pluginManager"></param>
		/// <returns></returns>
		public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager) {
			return this.UI;
		}

		/// <summary>
		/// Called once after plugins startup
		/// Plugins are rebuilt at game change
		/// </summary>
		/// <param name="pluginManager"></param>
		public void Init(PluginManager pluginManager) {
			SimHub.Logging.Current.Debug("Starting plugin");
			Settings = this.ReadCommonSettings(nameof(FlightPlugin), () => new PluginSettings());
			this.PluginManager.ApplicationExit += this.PluginManager_ApplicationExit;
			this.Telemetry = new TelemetryStates();
			var props = typeof(TelemetryStates).GetProperties();

            if (!joystick.vJoyEnabled())
            {
                AddLog("vJoy driver not enabled: Failed Getting vJoy attributes.\n");
                return;
            }
            else
            {
                Console.WriteLine("Vendor: {0}\nProduct :{1}\nVersion Number:{2}\n",
                joystick.GetvJoyManufacturerString(),
                joystick.GetvJoyProductString(),
                joystick.GetvJoySerialNumberString());

                AddLog(joystick.GetvJoyManufacturerString() +
                joystick.GetvJoyProductString() +
                joystick.GetvJoySerialNumberString());
            }

			uint id = 1;

            //var iReport = new vJoy.JoystickState();

            VjdStat status = joystick.GetVJDStatus(id);
            switch (status)
            {
                case VjdStat.VJD_STAT_OWN:
                    Console.WriteLine("vJoy Device {0} is already owned by this feeder\n", id);
                    break;
                case VjdStat.VJD_STAT_FREE:
                    Console.WriteLine("vJoy Device {0} is free\n", id);
                    break;
                case VjdStat.VJD_STAT_BUSY:
                    Console.WriteLine("vJoy Device {0} is already owned by another feeder\nCannot continue\n", id);
                    return;
                case VjdStat.VJD_STAT_MISS:
                    Console.WriteLine("vJoy Device {0} is not installed or disabled\nCannot continue\n", id);
                    return;
                default:
                    Console.WriteLine("vJoy Device {0} general error\nCannot continue\n", id);
                    return;
            };

            bool AxisX = joystick.GetVJDAxisExist(id, HID_USAGES.HID_USAGE_X);
            bool AxisY = joystick.GetVJDAxisExist(id, HID_USAGES.HID_USAGE_Y);
            bool AxisZ = joystick.GetVJDAxisExist(id, HID_USAGES.HID_USAGE_Z);

            AddLog($"{AxisX} {AxisY} {AxisZ} {status}");

			joystick.AcquireVJD(id);
            //joystick.ResetVJD(id);


            this.PluginManager.AddProperty("MSFS_PLUGIN_VERSION", typeof(FlightPlugin), Assembly.GetExecutingAssembly().GetName().Version.ToString());
			this.AttachDelegate("IS_MSFS_DATA_UPDATING", () => (DateTime.Now - LastUpdateTime).TotalSeconds <= 5);

            foreach (var prop in props) {
				var nameAttr = prop.GetCustomAttribute<DisplayNameAttribute>(false);

				var propName = nameAttr?.DisplayName ?? "FlightData." + prop.Name;
				this.AttachDelegate(propName, () => prop.GetValue(this.Telemetry));
			}
		}

		internal void StartUdpListener() {

            try {
				var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
				socket.Bind(new IPEndPoint(IPAddress.Any, Settings.UdpPort));
				var i = 0;

                long maxval = 0;
                long minval = 0;

                joystick.GetVJDAxisMax(1, HID_USAGES.HID_USAGE_X, ref maxval);
                joystick.GetVJDAxisMin(1, HID_USAGES.HID_USAGE_X, ref minval);

                var stateProps = typeof(TelemetryStates).GetProperties();
				UdpListeningThread = new Thread(() => {
					AddLog("UDP Connection started");
					while(true) {
						if(UdpListeningThread == null) {
							break;
						}
						try {
							var buffer = new byte[1024 * 1024];
							socket.ReceiveTimeout = 500;
							socket.Receive(buffer, SocketFlags.None);
							var content = Encoding.UTF8.GetString(buffer);

							//AddLog(content);

							if(!content.StartsWith("{")) {
								continue;
							}
							i++;
							var obj = JObject.Parse(content); 
							foreach(var jToken in obj.Children()) {
								if(jToken is JProperty jProp) {
									var prop = stateProps.FirstOrDefault(p => p.Name == jProp.Name);
									if(prop != null) {
										prop.SetValue(this.Telemetry, Convert.ChangeType(jProp.Value, prop.PropertyType));

                                        //if(i %10 == 0)
                                        if(true)
										{
											if (jProp.Name == "ACCELERATION_BODY_X")
											{
												var val = (float) Convert.ChangeType(jProp.Value, typeof(float));
												joystick.SetAxis((int)(val * 500) + (int)(maxval/2), 1, HID_USAGES.HID_USAGE_X);
											}
											else if (jProp.Name == "ACCELERATION_BODY_Y")
											{
												var val = (float)Convert.ChangeType(jProp.Value, typeof(float));
												joystick.SetAxis((int)(val * 500) + (int)(maxval / 2), 1, HID_USAGES.HID_USAGE_Y);
											}
											else if (jProp.Name == "ACCELERATION_BODY_Z")
											{
												var val = (float)Convert.ChangeType(jProp.Value, typeof(float));
												joystick.SetAxis((int)(val * 500) + (int)(maxval / 2), 1, HID_USAGES.HID_USAGE_Z);
											}
                                        }

                                        LastUpdateTime = DateTime.Now;
									}
								}
							}
						} catch(SocketException ex) {
							if(ex.SocketErrorCode != SocketError.TimedOut) {
								AddLog("UDP Listener Error: " + ex.Message);
							}
						} catch(Exception ex) {
								AddLog("UDP Listener Error: " + ex.Message);
                        }
                    }
				});
				UdpListeningThread.Start();
			} catch(Exception ex) {
				AddLog($"Failed to start UDP connection. Port: {Settings.UdpPort}. {ex.Message}");
			}
		}

        internal void X()
        {
			var t = new Thread(() =>
			{
				for (int i = 0; i < 5; i++)
				{
                    AddLog($"{i}");
                    joystick.SetAxis(0, 1, HID_USAGES.HID_USAGE_X);
					System.Threading.Thread.Sleep(1000);
					joystick.SetAxis(32767, 1, HID_USAGES.HID_USAGE_X);
                    System.Threading.Thread.Sleep(1000);
                }
                ClearAxis();
            });

            t.Start();
        }

        internal void Y()
        {
            var t = new Thread(() =>
            {
                for (int i = 0; i < 5; i++)
                {
                    AddLog($"{i}");
                    joystick.SetAxis(0, 1, HID_USAGES.HID_USAGE_Y);
                    System.Threading.Thread.Sleep(1000);
                    joystick.SetAxis(32767, 1, HID_USAGES.HID_USAGE_Y);
                    System.Threading.Thread.Sleep(1000);
                }
                ClearAxis();
            });

            t.Start();
        }
        internal void Z()
        {
            var t = new Thread(() =>
            {
                for (int i = 0; i < 5; i++)
                {
                    AddLog($"{i}");
                    joystick.SetAxis(0, 1, HID_USAGES.HID_USAGE_Z);
                    System.Threading.Thread.Sleep(1000);
                    joystick.SetAxis(32767, 1, HID_USAGES.HID_USAGE_Z);
                    System.Threading.Thread.Sleep(1000);
                }
                ClearAxis();
            });

            t.Start();
        }
        internal void ClearAxis()
		{
            joystick.SetAxis(32767/2, 1, HID_USAGES.HID_USAGE_X);
            joystick.SetAxis(32767/2, 1, HID_USAGES.HID_USAGE_Y);
            joystick.SetAxis(32767/2, 1, HID_USAGES.HID_USAGE_Z);
        }

        internal void StopUdpServer() {
			try {
				if(IsUdpListening) {
					UdpListeningThread.Abort();
					UdpListeningThread = null;
					AddLog($"UDP connection stopped.");
				}
			} catch(Exception ex) {
				AddLog($"Failed to stop UDP connection: {ex.Message}");
			}
		}

		internal void StartSimconnectServer() {
			SimconnectServerProcess = new Process() {
				StartInfo = new ProcessStartInfo {
					FileName = "simconnectserver.exe",
					Arguments = $"--port {Settings.UdpPort} --hide"
				}
			};
			SimconnectServerProcess.Start();
			AddLog("SimConnect Server Started.");
		}

		internal void StopSimconnectServer() {
			if(IsSimconnectServerRunning) {
				SimconnectServerProcess.Kill();
				AddLog("SimConnect Server Stopped.");
			}
		}

		private void AddLog(string message) {
			if(this.ShowLogUntil >= DateTime.Now || !message.StartsWith("{")) {
				if(!this.UI.Dispatcher.CheckAccess()) {
					this.UI.Invoke(new Action(() => { AddLog(message); }));
				} else {
					this.UI.Log.Text += (this.UI.Log.Text == "" ? "" : "\n") + message.Replace("\0", "").Trim();
				}
			}
		}
	}
}
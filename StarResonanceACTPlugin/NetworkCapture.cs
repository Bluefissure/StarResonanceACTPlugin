using Advanced_Combat_Tracker;
using PacketDotNet;
using SharpPcap;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace StarResonanceACTPlugin
{
    public class NetworkCapture
    {
        private int targetPid;
        private Dictionary<int, int> portToPidMap = TcpTable.GetTcpPortPidMap();
        private NetworkAnalyzer analyzer;
        private StreamWriter logWriter;
        internal string selectedDeviceName;
        internal IEnumerable<string> deviceDescs;
        private string selectedDevice;

        private void initLog()
        {
            string logDir = Path.Combine(ActGlobals.oFormActMain.AppDataFolder.FullName, "SRLogs");
            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir); // 自动创建目录
            }
            string logFile = Path.Combine(logDir, "Network.log");
            var fileStream = new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            logWriter = new StreamWriter(fileStream, Encoding.UTF8) { AutoFlush = true };
            Log("日志初始化");
        }
        private void Log(string message)
        {
            string timestamp = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            logWriter.WriteLine(timestamp);       // 写入日志文件
            Console.WriteLine(timestamp);         // 可选：同步输出到控制台
        }

        public NetworkCapture(string processName)
        {
            this.initLog();
            this.analyzer = new NetworkAnalyzer(this.logWriter);
            // 获取目标进程 PID
            var process = Process.GetProcessesByName(processName);
            if (process.Length == 0)
                throw new Exception($"未找到进程: {processName}");
            targetPid = process[0].Id;
            Log($"目标进程 {processName} PID={targetPid}");
        }

        public void initDevices()
        {
            var devices = CaptureDeviceList.Instance;
            if (devices.Count < 1)
            {
                Log("没有检测到可用网卡");
                return;
            }
            this.deviceDescs = devices.Select(device => device.Description);
        }

        public void ChangeCapture(string newSelectedDevice)
        {
            var oldSelectedDevice = this.selectedDevice;
            var devices = CaptureDeviceList.Instance;
            if (devices.Count < 1)
            {
                Log("没有检测到可用网卡");
                return;
            }

            var oldDevice = devices.Where(device => device.Description == oldSelectedDevice).FirstOrDefault();
            oldDevice?.StopCapture();
            oldDevice?.Dispose();

            var newDevice = devices.Where(device => device.Description == newSelectedDevice).FirstOrDefault();
            this.selectedDeviceName = newDevice.Description;
            newDevice.OnPacketArrival += new PacketArrivalEventHandler(OnPacketArrival);
            newDevice.Open(DeviceModes.Promiscuous, 1000);
            newDevice.Filter = "tcp or udp";
            Log($"重新开始抓包于 {newDevice.Description}");
            this.analyzer?.Dispose();
            this.analyzer = new NetworkAnalyzer(this.logWriter);
            newDevice.StartCapture();
        }

        public void StartCapture(string selectedDevice)
        {
            this.selectedDevice = selectedDevice;
            var devices = CaptureDeviceList.Instance;
            if (devices.Count < 1 || selectedDevice == "")
            {
                Log("没有检测到可用网卡");
                return;
            }
            var device = devices.Where(device => device.Description == selectedDevice).FirstOrDefault();
            if (device == null)
            {
                Log("未选择可用网卡");
                return;
            }
            for (int i = 0; i < devices.Count; i += 1)
            {
                var d = devices[i];
                if (d.Name == selectedDevice)
                {
                    device = devices[i];
                }
            }
            this.selectedDeviceName = device.Description;
            device.OnPacketArrival += new PacketArrivalEventHandler(OnPacketArrival);
            device.Open(DeviceModes.Promiscuous, 1000);
            device.Filter = "tcp or udp";
            Log($"开始抓包于 {device.Description}");
            device.StartCapture();

            // 定时更新端口->PID映射（避免频繁查询）
            var portUpdateTimer = new System.Timers.Timer(2000);
            portUpdateTimer.Elapsed += (s, e) => { portToPidMap = TcpTable.GetTcpPortPidMap(); };
            portUpdateTimer.Start();
            //device.StopCapture();
            //device.Close();
        }

        private void OnPacketArrival(object sender, PacketCapture e)
        {
            var rawPacket = e.GetPacket();
            var packet = Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);

            var tcp = packet.Extract<TcpPacket>();
            var udp = packet.Extract<UdpPacket>();
            TransportPacket transport = tcp ?? (TransportPacket)udp;

            if (transport != null)
            {
                int srcPort = transport.SourcePort;
                int dstPort = transport.DestinationPort;
                var transportSrcPid = -1;
                var transportDstPid = -1;
                if (portToPidMap.ContainsKey(srcPort))
                {
                    transportSrcPid = portToPidMap[srcPort];
                }
                if (portToPidMap.ContainsKey(dstPort))
                {
                    transportDstPid = portToPidMap[dstPort];
                }

                if (transportSrcPid == targetPid || transportDstPid == targetPid)
                {
                    //Log($"[{DateTime.Now}] PID={targetPid} {transport.GetType().Name} " +
                    //                  $"{srcPort} -> {dstPort}, 长度={transport.PayloadData.Length}字节");
                    this.analyzer.OnPacketArrival(sender, e);
                }
            }
        }

        public void Dispose()
        {
            var devices = CaptureDeviceList.Instance;
            foreach(var device in devices)
            {
                if (this.selectedDevice == device.Description)
                {
                    Log($"Stopping capturing {device.Description}");
                    device.StopCapture();
                    device.Close();
                }
            }
            this.analyzer?.Dispose();
            logWriter?.Close();
            logWriter?.Dispose();
        }
    }

}

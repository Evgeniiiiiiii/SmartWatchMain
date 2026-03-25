using System;
using System.IO.Ports;
using System.Text;

namespace SmartWatchProj.Services.Devices
{
    public sealed class SerialPortConnectionOptions
    {
        public string PortName { get; set; } = string.Empty;
        public int BaudRate { get; set; } = 115200;
        public int DataBits { get; set; } = 8;
        public Parity Parity { get; set; } = Parity.None;
        public StopBits StopBits { get; set; } = StopBits.One;
        public Handshake Handshake { get; set; } = Handshake.None;
        public bool DtrEnable { get; set; }
        public bool RtsEnable { get; set; }
        public string NewLine { get; set; } = "\n";
        public Encoding Encoding { get; set; } = Encoding.UTF8;
        public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(2);
        public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(10);
    }
}

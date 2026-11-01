using System.IO.Ports;

namespace SerialScope.Models;

public sealed class SerialSettings
{
    public string PortName { get; set; } = string.Empty;
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Handshake Handshake { get; set; } = Handshake.None;
    public bool DtrEnable { get; set; }
    public bool RtsEnable { get; set; }

    public string FrameFormat
    {
        get
        {
            string parity = Parity switch
            {
                Parity.None => "N",
                Parity.Odd => "O",
                Parity.Even => "E",
                Parity.Mark => "M",
                Parity.Space => "S",
                _ => "?",
            };
            string stop = StopBits switch
            {
                StopBits.One => "1",
                StopBits.OnePointFive => "1.5",
                StopBits.Two => "2",
                _ => "?",
            };
            return $"{DataBits}{parity}{stop}";
        }
    }

    public string Summary => string.IsNullOrWhiteSpace(PortName)
        ? "未选择串口"
        : $"{PortName} · {BaudRate} · {FrameFormat}";
}

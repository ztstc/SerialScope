namespace SerialScope.Models;

public enum ProtocolKind
{
    FireWater = 0,
    JustFloat = 1,
    RawData = 2,
}

public static class ProtocolInfo
{
    public static string DisplayName(ProtocolKind kind) => kind switch
    {
        ProtocolKind.FireWater => "FireWater",
        ProtocolKind.JustFloat => "JustFloat",
        ProtocolKind.RawData => "RawData",
        _ => "未知",
    };

    public static string Description(ProtocolKind kind) => kind switch
    {
        ProtocolKind.FireWater => "ASCII 文本协议，逗号分隔多通道数值，以 \\n 结束一帧。",
        ProtocolKind.JustFloat => "二进制协议，小端 float32 序列，帧尾为 00 00 80 7F。",
        ProtocolKind.RawData => "原始字节流，仅终端显示，不生成波形。",
        _ => string.Empty,
    };

    public static string FormatHint(ProtocolKind kind) => kind switch
    {
        ProtocolKind.FireWater => "1.25, 3.14, -0.50\\n",
        ProtocolKind.JustFloat => "float32 + 0x7F800000",
        ProtocolKind.RawData => "UTF-8 / HEX",
        _ => string.Empty,
    };
}

using System.Globalization;

namespace MultiStation.Instruments.Protocol.Scpi.Algorithm;

/// <summary>SCPI 回读值解析：仪器可能返回 "+1.234E+01"、"-0.001" 或多段以分号分隔的值。</summary>
public static class ScpiNumber
{
    public static double Parse(string response)
    {
        var token = response.Trim().Split(';')[0].Trim();
        return double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    public static bool ParseBool(string response) => Parse(response) != 0;
}

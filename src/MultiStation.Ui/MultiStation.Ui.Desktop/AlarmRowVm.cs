using System.Globalization;
using MultiStation.Platform.Simulation;

namespace MultiStation.Ui.Desktop;

/// <summary>报警中心表格行。</summary>
public sealed class AlarmRowVm
{
    private AlarmRowVm(string time, string stationId, string message)
    {
        Time = time;
        StationId = stationId;
        Message = message;
    }

    public string Time { get; }

    public string StationId { get; }

    public string Message { get; }

    public static AlarmRowVm From(AlarmItem item) =>
        new(item.Timestamp.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture), item.StationId, item.Message);
}

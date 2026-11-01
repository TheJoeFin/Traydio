using System.ComponentModel;

namespace Trdo.Services.Playback;

public sealed partial class CastDeviceEntry : INotifyPropertyChanged
{
    private CastHistoryRow _row;
    private bool _isChecking;

    internal CastDeviceEntry(CastHistoryRow row) => _row = row;

    public event PropertyChangedEventHandler? PropertyChanged;
    internal CastDevice? LiveDevice { get; private set; }
    internal CastHistoryDevice Device => _row.Device;
    public string Name => Device.Name;
    public string AutomationName => $"{Name}, {Details}";
    public override string ToString() => AutomationName;
    public string Glyph => Device.Protocol == CastProtocol.Sonos ? "\uE7F5" : "\uE7F4";
    public string Details
    {
        get
        {
            string status = _row.IsAvailable
                ? LocalizationService.GetString("CastDeviceDialog_Available", "Available")
                : _isChecking
                    ? LocalizationService.GetString("CastDeviceDialog_Checking", "Checking availability...")
                    : LocalizationService.GetString("CastDeviceDialog_Offline", "Unavailable");
            if (_row.IsRemembered)
                status = string.Format(LocalizationService.GetString(
                    "CastDeviceDialog_PreviouslyUsed", "Previously used - {0}"), status);

            return string.Format(LocalizationService.GetString(
                "CastDeviceDialog_DeviceStatus", "{0} - {1}"), Device.Kind, status);
        }
    }

    internal void Update(CastHistoryRow row, CastDevice? live, bool checking)
    {
        _row = row;
        LiveDevice = live;
        _isChecking = checking;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Glyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Details)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomationName)));
    }
}

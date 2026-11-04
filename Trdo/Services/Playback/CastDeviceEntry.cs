using System.Collections.Generic;
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

    /// <summary>The rooms of a Sonos group, or empty for a single speaker or a device not currently seen.</summary>
    public IReadOnlyList<string> Rooms => Device.Rooms.Count > 1 ? Device.Rooms : [];
    public string AutomationName => $"{Name}, {Details}";
    public override string ToString() => AutomationName;
    public bool IsAvailable => _row.IsAvailable;

    /// <summary>Cast to on this network before.</summary>
    public bool IsRemembered => _row.IsRemembered;

    /// <summary>The initial availability check is still running, so "not available" isn't final yet.</summary>
    public bool IsChecking => _isChecking && !_row.IsAvailable;
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

            // A group's name can be a summary ("Kitchen + 3 rooms"), so spell the rooms out.
            string kind = Rooms.Count > 0 ? $"{Device.Kind} ({string.Join(", ", Rooms)})" : Device.Kind;
            return string.Format(LocalizationService.GetString(
                "CastDeviceDialog_DeviceStatus", "{0} - {1}"), kind, status);
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

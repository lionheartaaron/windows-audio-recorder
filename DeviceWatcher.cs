using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace WindowAudioRecorder;

/// <summary>
/// Watches for endpoints appearing, disappearing, or becoming the system default.
/// Callbacks arrive on a COM thread, so subscribers must marshal to the UI themselves.
/// </summary>
internal sealed class DeviceWatcher : IMMNotificationClient
{
    /// <summary>Raised with the id of the new default render endpoint.</summary>
    public event Action<string>? DefaultChanged;

    /// <summary>Raised when the set of available endpoints may have changed.</summary>
    public event Action? ListChanged;

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => ListChanged?.Invoke();

    public void OnDeviceAdded(string pwstrDeviceId) => ListChanged?.Invoke();

    public void OnDeviceRemoved(string deviceId) => ListChanged?.Invoke();

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) DefaultChanged?.Invoke(defaultDeviceId);
    }

    // Fires constantly (volume, peak meter, ...) and never tells us anything we act on.
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
}

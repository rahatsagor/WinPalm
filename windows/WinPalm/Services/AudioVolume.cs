using System.Runtime.InteropServices;
namespace WinPalm.Services;

internal static class AudioVolume
{
  internal static (float Value, bool Muted)? Read()
  {
    try { return WithEndpoint(endpoint => { Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out var value)); Marshal.ThrowExceptionForHR(endpoint.GetMute(out var muted)); return (value, muted); }); }
    catch (COMException) { return null; }
  }
  internal static void Set(float? value, bool? muted) => WithEndpoint(endpoint =>
  {
    var context = Guid.Empty;
    if (value is { } scalar) Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar(Math.Clamp(scalar, 0, 1), ref context));
    if (muted is { } flag) Marshal.ThrowExceptionForHR(endpoint.SetMute(flag, ref context));
    return true;
  });
  private static T WithEndpoint<T>(Func<IAudioEndpointVolume, T> work)
  {
    var enumerator = (IMMDeviceEnumerator)new DeviceEnumerator();
    IMMDevice? device = null; object? volume = null;
    try
    {
      Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 1, out device));
      var iid = typeof(IAudioEndpointVolume).GUID;
      Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, 0, out volume));
      return work((IAudioEndpointVolume)volume);
    }
    finally
    {
      if (volume is not null) Marshal.FinalReleaseComObject(volume);
      if (device is not null) Marshal.FinalReleaseComObject(device);
      Marshal.FinalReleaseComObject(enumerator);
    }
  }
  [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class DeviceEnumerator { }
  [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IMMDeviceEnumerator
  {
    [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out nint devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(nint client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(nint client);
  }
  [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IMMDevice
  {
    [PreserveSig] int Activate(ref Guid iid, uint context, nint activation, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    [PreserveSig] int OpenPropertyStore(int mode, out nint store);
    [PreserveSig] int GetId(out nint id);
    [PreserveSig] int GetState(out uint state);
  }
  [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IAudioEndpointVolume
  {
    [PreserveSig] int RegisterControlChangeNotify(nint notify);
    [PreserveSig] int UnregisterControlChangeNotify(nint notify);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
    [PreserveSig] int GetMasterVolumeLevel(out float level);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
    [PreserveSig] int VolumeStepUp(ref Guid context);
    [PreserveSig] int VolumeStepDown(ref Guid context);
    [PreserveSig] int QueryHardwareSupport(out uint mask);
    [PreserveSig] int GetVolumeRange(out float min, out float max, out float step);
  }
}

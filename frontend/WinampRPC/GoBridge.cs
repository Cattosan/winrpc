using System;
using System.Runtime.InteropServices;

namespace WinampRPC
{
    public static class GoBridge
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void TrackUpdateCallback(
            IntPtr title, 
            IntPtr artist, 
            IntPtr album, 
            IntPtr year, 
            IntPtr quality,
            IntPtr coverUrl,
            IntPtr fileInfoJson);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void StatusCallback(IntPtr statusMsg);

        [DllImport("winamp_bridge.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void InitCallbacks(TrackUpdateCallback trackCb, StatusCallback statusCb);

        [DllImport("winamp_bridge.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void StartPresence([MarshalAs(UnmanagedType.LPUTF8Str)] string clientID, [MarshalAs(UnmanagedType.LPUTF8Str)] string lastFmKey);

        [DllImport("winamp_bridge.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void StopPresence();
    }
}

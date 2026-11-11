using System;
using System.Runtime.InteropServices;

namespace ArcademiaUpdater.Platform
{
    internal static class X11
    {
        private const string Lib = "libX11.so.6";

        private const long SubstructureNotifyMask = 1L << 19;
        private const long SubstructureRedirectMask = 1L << 20;
        private const int ClientMessage = 33;

        [StructLayout(LayoutKind.Sequential, Size = 192)]
        private struct XClientMessageEvent
        {
            public int type;
            public nint serial;
            public int send_event;
            public nint display;
            public nint window;
            public nint message_type;
            public int format;
            public nint l0;
            public nint l1;
            public nint l2;
            public nint l3;
            public nint l4;
        }

        [DllImport(Lib)]
        private static extern nint XOpenDisplay(nint name);

        [DllImport(Lib)]
        private static extern int XCloseDisplay(nint display);

        [DllImport(Lib)]
        private static extern nint XDefaultRootWindow(nint display);

        [DllImport(Lib)]
        private static extern nint XInternAtom(nint display, string name, bool onlyIfExists);

        [DllImport(Lib)]
        private static extern int XGetWindowProperty(
            nint display,
            nint window,
            nint property,
            nint offset,
            nint length,
            bool delete,
            nint reqType,
            out nint actualType,
            out int actualFormat,
            out nint itemCount,
            out nint bytesAfter,
            out nint data
        );

        [DllImport(Lib)]
        private static extern int XFree(nint data);

        [DllImport(Lib)]
        private static extern int XSendEvent(nint display, nint window, bool propagate, nint eventMask, ref XClientMessageEvent ev);

        [DllImport(Lib)]
        private static extern int XMapRaised(nint display, nint window);

        [DllImport(Lib)]
        private static extern int XFlush(nint display);

        public static bool IsAvailable =>
            OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

        public static nint FindWindowForProcess(int pid)
        {
            if (!IsAvailable)
                return 0;

            try
            {
                var display = XOpenDisplay(0);
                if (display == 0)
                    return 0;

                try
                {
                    return FindWindowForProcess(display, pid);
                }
                finally
                {
                    XCloseDisplay(display);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return 0;
            }
        }

        public static bool Activate(int pid)
        {
            if (!IsAvailable)
                return false;

            try
            {
                var display = XOpenDisplay(0);
                if (display == 0)
                    return false;

                try
                {
                    var window = FindWindowForProcess(display, pid);
                    if (window == 0)
                        return false;

                    var root = XDefaultRootWindow(display);
                    var ev = new XClientMessageEvent
                    {
                        type = ClientMessage,
                        send_event = 1,
                        display = display,
                        window = window,
                        message_type = XInternAtom(display, "_NET_ACTIVE_WINDOW", false),
                        format = 32,
                        l0 = 2,
                    };

                    XSendEvent(display, root, false, (nint)(SubstructureRedirectMask | SubstructureNotifyMask), ref ev);
                    XMapRaised(display, window);
                    XFlush(display);
                    return true;
                }
                finally
                {
                    XCloseDisplay(display);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }

        private static nint FindWindowForProcess(nint display, int pid)
        {
            var root = XDefaultRootWindow(display);
            var clientList = XInternAtom(display, "_NET_CLIENT_LIST", false);
            var wmPid = XInternAtom(display, "_NET_WM_PID", false);

            foreach (var window in ReadLongs(display, root, clientList))
            {
                var pids = ReadLongs(display, window, wmPid);
                if (pids.Length > 0 && pids[0] == pid)
                    return window;
            }

            return 0;
        }

        private static nint[] ReadLongs(nint display, nint window, nint property)
        {
            if (XGetWindowProperty(display, window, property, 0, 4096, false, 0, out _, out var format, out var count, out _, out var data) != 0)
                return [];

            try
            {
                if (data == 0 || format != 32 || count <= 0)
                    return [];

                var values = new nint[count];
                for (int i = 0; i < values.Length; i++)
                    values[i] = Marshal.ReadIntPtr(data, i * IntPtr.Size);
                return values;
            }
            finally
            {
                if (data != 0)
                    XFree(data);
            }
        }
    }
}

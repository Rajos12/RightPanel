using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace RightPanel
{
    internal static partial class Ccd
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            public ushort Spec, Driver, Size, Extra;
            public uint Fields;
            public int X, Y;
            public uint Orientation, FixedOutput;
            public short Color, Duplex, YRes, TT, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Form;
            public ushort LogPixels;
            public uint Bits, Width, Height, Flags, Frequency, IcmMethod, IcmIntent, Media, Dither, Reserved1, Reserved2, PanWidth, PanHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern bool EnumDisplaySettingsExW(string device, int index, ref DevMode mode, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int ChangeDisplaySettingsExW(string device, ref DevMode mode, IntPtr window, uint flags, IntPtr parameters);

        private static IEnumerable<DevMode> NativeModes(string device, int width, int height)
        {
            for (int i = 0; ; ++i)
            {
                DevMode mode = new DevMode();
                mode.Size = checked((ushort)Marshal.SizeOf(typeof(DevMode)));
                if (!EnumDisplaySettingsExW(device, i, ref mode, 0)) yield break;
                if (mode.Width == width && mode.Height == height && mode.Frequency > 1 &&
                    mode.Bits == 32 && (mode.Flags & 2) == 0 && mode.Orientation == 0)
                    yield return mode;
            }
        }

        public static int[] Rates(string device, int physicalWidth, int physicalHeight)
        {
            var rates = new SortedSet<int>();
            foreach (DevMode mode in NativeModes(device, physicalWidth, physicalHeight))
                rates.Add(checked((int)mode.Frequency));
            var result = new int[rates.Count];
            rates.CopyTo(result);
            return result;
        }

        private static void RequestTiming(string device, int width, int height, double hz)
        {
            foreach (DevMode available in NativeModes(device, width, height))
            {
                if (Math.Abs(available.Frequency - hz) > 0.001) continue;
                DevMode candidate = available;
                // Set only the fields of the video mode, preserving the desktop arrangement.
                candidate.Fields = 0x40000 | 0x80000 | 0x100000 | 0x200000 | 0x400000;
                int test = ChangeDisplaySettingsExW(device, ref candidate, IntPtr.Zero, 2, IntPtr.Zero);
                if (test != 0) throw new InvalidOperationException("ChangeDisplaySettingsExW TEST: DISP_CHANGE " + test);
                int rc = ChangeDisplaySettingsExW(device, ref candidate, IntPtr.Zero, 0, IntPtr.Zero);
                if (rc != 0) throw new InvalidOperationException("ChangeDisplaySettingsExW: DISP_CHANGE " + rc);
                return;
            }
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                "{0}×{1} @ {2} Гц отсутствует в списке режимов драйвера ({3} Гц).",
                width, height, hz, string.Join(", ", Rates(device, width, height))));
        }
    }
}

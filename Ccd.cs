using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RightPanel
{
    internal sealed class Display
    {
        public string Device, Name, TargetPath;
        public int Width, Height, SignalWidth, SignalHeight, SourceWidth, SourceHeight;
        public int RegionX, RegionY, RegionWidth, RegionHeight, ClipX, ClipY, ClipWidth, ClipHeight;
        public int DesktopX, DesktopY;
        public double Hz, PathHz;
        public uint Rotation, Scaling, Technology, ScanLine;
        public bool Virtual, Cloned;
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0} {1}: desktop={2}×{3}, signal={4}×{5} @ {6:F3} Hz, surface={7}×{8}, region=({9},{10}) {11}×{12}, clip=({13},{14}) {15}×{16}, scaling={17}, rotation={18}, link={19}, pathHz={20:F3}, virtual={21}, clone={22}",
                Device, Name, Width, Height, SignalWidth, SignalHeight, Hz, SourceWidth, SourceHeight,
                RegionX, RegionY, RegionWidth, RegionHeight, ClipX, ClipY, ClipWidth, ClipHeight,
                Scaling, Rotation, Technology, PathHz, Virtual, Cloned);
        }
    }

    internal static partial class Ccd
    {
        private const uint QueryFlags = 0x2 | 0x10 | 0x40; // active, virtual mode, virtual refresh
        private const uint SetFlags = 0x20 | 0x8000 | 0x20000; // supplied, virtual mode/refresh aware
        private const uint Validate = 0x40, Apply = 0x80, Save = 0x200;
        private const int SnapshotMagic = 0x31444352;
        private const int MaxPaths = 512, MaxModes = 4096;
        [StructLayout(LayoutKind.Sequential)] private struct Adapter { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] private struct Rational
        {
            public uint Num, Den;
            public double Value { get { return Den == 0 ? 0 : (double)Num / Den; } }
        }
        [StructLayout(LayoutKind.Sequential)] private struct Point
        {
            public int X, Y;
            public Point(int x,int y) { X=x; Y=y; }
        }
        [StructLayout(LayoutKind.Sequential)] private struct Rect
        {
            public int Left, Top, Right, Bottom;
            public Rect(int x,int y,int width,int height)
            { Left=x; Top=y; Right=checked(x+width); Bottom=checked(y+height); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct SourcePath
        { public Adapter Adapter; public uint Id, Index, Status; }
        [StructLayout(LayoutKind.Sequential)] private struct Target
        {
            public Adapter Adapter; public uint Id, Index, OutputTechnology, Rotation, Scaling;
            public Rational Refresh;
            public uint Scanline, Available, Status;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Link
        {
            public SourcePath Source; public Target Target; public uint Flags;
            public bool Virtual { get { return (Flags & 8) != 0; } }
            public int SourceIndex { get { return Virtual ? (int)(Source.Index >> 16) : unchecked((int)Source.Index); } }
            public int TargetIndex { get { return Virtual ? (int)(Target.Index >> 16) : unchecked((int)Target.Index); } }
            public int ImageIndex { get { return Virtual ? (int)(Target.Index & 0xFFFF) : -1; } }
        }
        [StructLayout(LayoutKind.Sequential)] private struct Source
        { public uint Width, Height, Format; public Point Origin; }
        [StructLayout(LayoutKind.Sequential)] private struct Signal
        {
            public ulong PixelClock;
            public Rational HSync, VSync;
            public uint Width, Height, TotalWidth, TotalHeight, Additional, Scanline;
        }
        [StructLayout(LayoutKind.Sequential)] private struct DesktopImage
        { public Point Surface; public Rect Region, Clip; }
        [StructLayout(LayoutKind.Explicit, Size=64)] private struct Mode
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(4)] public uint Id;
            [FieldOffset(8)] public Adapter Adapter;
            [FieldOffset(16)] public Source Source;
            [FieldOffset(16)] public Signal Signal;
            [FieldOffset(16)] public DesktopImage Image;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Header
        {
            public uint Type, Size; public Adapter Adapter; public uint Id;
            public Header(uint type, Type packet, Adapter adapter, uint id)
            { Type=type; Size=(uint)Marshal.SizeOf(packet); Adapter=adapter; Id=id; }
        }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct SourceName
        {
            public Header Header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string Name;
        }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct TargetName
        {
            public Header Header; public uint Flags, Technology; public ushort Manufacturer, Product;
            public uint ConnectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=64)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string Path;
        }
        [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] Link[] paths, ref uint numModes, [Out] Mode[] modes, IntPtr topology);
        [DllImport("user32.dll")] private static extern int SetDisplayConfig(uint pathCount, [In] Link[] paths, uint modeCount, [In] Mode[] modes, uint flags);
        [DllImport("user32.dll", EntryPoint="DisplayConfigGetDeviceInfo")] private static extern int GetName(ref SourceName source);
        [DllImport("user32.dll", EntryPoint="DisplayConfigGetDeviceInfo")] private static extern int GetName(ref TargetName target);

        static Ccd()
        {
            if (Marshal.SizeOf(typeof(Link)) != 72 || Marshal.SizeOf(typeof(Mode)) != 64 ||
                Marshal.SizeOf(typeof(TargetName)) != 420 || Marshal.SizeOf(typeof(SourceName)) != 84)
                throw new TypeLoadException("Неверный размер структуры Win32 CCD.");
        }

        private sealed class State
        {
            public Link[] Links;
            public Mode[] Modes;
            public string[] Targets;
            public int Execute(uint action)
            {
                return SetDisplayConfig((uint)Links.Length, Links, (uint)Modes.Length, Modes, SetFlags | action);
            }
            public void Set(uint action)
            {
                Check(Execute(Validate), "SetDisplayConfig VALIDATE");
                Check(Execute(action), "SetDisplayConfig APPLY");
            }
        }

        private static void Check(int rc, string api)
        {
            if (rc != 0) throw new Win32Exception(rc, string.Format("{0}: {1} ({2})", api, new Win32Exception(rc).Message, rc));
        }
        private static State Query()
        {
            for (int attempt=0; attempt<5; ++attempt)
            {
                uint np, nm;
                Check(GetDisplayConfigBufferSizes(QueryFlags, out np, out nm), "GetDisplayConfigBufferSizes");
                if (np > MaxPaths || nm > MaxModes) throw new InvalidDataException("Слишком много путей CCD.");
                Link[] links = new Link[np]; Mode[] modes = new Mode[nm];
                int rc = QueryDisplayConfig(QueryFlags, ref np, links, ref nm, modes, IntPtr.Zero);
                if (rc == 122) continue;
                Check(rc, "QueryDisplayConfig");
                Array.Resize(ref links, (int)np); Array.Resize(ref modes, (int)nm);
                return new State { Links=links, Modes=modes };
            }
            throw new InvalidOperationException("Список дисплеев изменяется. Повторите после завершения подключения.");
        }
        private static bool ValidMode(State s, int index, uint type)
        { return index >= 0 && index < s.Modes.Length && s.Modes[index].Type == type; }
        private static TargetName TargetInfo(Link path)
        {
            TargetName target = new TargetName();
            target.Header = new Header(2, typeof(TargetName), path.Target.Adapter, path.Target.Id);
            Check(GetName(ref target), "DisplayConfigGetDeviceInfo TARGET_NAME");
            return target;
        }
        private static Display Describe(State s, int at)
        {
            Link p = s.Links[at];
            SourceName name = new SourceName();
            name.Header = new Header(1, typeof(SourceName), p.Source.Adapter, p.Source.Id);
            Check(GetName(ref name),"DisplayConfigGetDeviceInfo SOURCE_NAME");
            TargetName target = TargetInfo(p);
            Display d = new Display { Device=name.Name, Name=target.Name, TargetPath=target.Path,
                Rotation=p.Target.Rotation, Scaling=p.Target.Scaling, Technology=p.Target.OutputTechnology,
                Virtual=p.Virtual, PathHz=p.Target.Refresh.Value };
            d.Cloned = s.Links.Count(t => t.Source.Adapter.Equals(p.Source.Adapter) && t.Source.Id == p.Source.Id) > 1;
            if (ValidMode(s, p.SourceIndex, 1))
            {
                Source m = s.Modes[p.SourceIndex].Source;
                d.Width=(int)m.Width; d.Height=(int)m.Height; d.DesktopX=m.Origin.X; d.DesktopY=m.Origin.Y;
            }
            if (ValidMode(s, p.TargetIndex, 2))
            {
                Signal m = s.Modes[p.TargetIndex].Signal;
                d.SignalWidth=(int)m.Width; d.SignalHeight=(int)m.Height; d.Hz=m.VSync.Value; d.ScanLine=m.Scanline;
            }
            if (ValidMode(s, p.ImageIndex, 3))
            {
                DesktopImage m = s.Modes[p.ImageIndex].Image;
                d.SourceWidth=m.Surface.X; d.SourceHeight=m.Surface.Y;
                d.RegionX=m.Region.Left; d.RegionY=m.Region.Top;
                d.RegionWidth=m.Region.Right-m.Region.Left; d.RegionHeight=m.Region.Bottom-m.Region.Top;
                d.ClipX=m.Clip.Left; d.ClipY=m.Clip.Top;
                d.ClipWidth=m.Clip.Right-m.Clip.Left; d.ClipHeight=m.Clip.Bottom-m.Clip.Top;
            }
            return d;
        }
        private static Display[] Displays(State s)
        {
            Display[] result = new Display[s.Links.Length];
            for(int i=0;i<result.Length;++i) result[i]=Describe(s,i);
            return result;
        }
        public static Display[] Read() { return Displays(Query()); }
        private static int UniqueDisplay(Display[] displays, string device)
        {
            int index=Array.FindIndex(displays,d=>string.Equals(d.Device,device,StringComparison.OrdinalIgnoreCase));
            if(index < 0) throw new InvalidOperationException("Дисплей недоступен: " + device);
            if(displays[index].Cloned) throw new InvalidOperationException("Этот источник Windows сейчас используется группой дублирования.");
            if(displays[index].Rotation != 1) throw new InvalidOperationException("Для этого профиля нужна обычная ориентация экрана.");
            return index;
        }
        private static bool TimingMatches(Display d, int width, int height, double hz)
        {
            return d.SignalWidth == width && d.SignalHeight == height && Math.Abs(d.Hz-hz)<0.25 &&
                d.ScanLine == 1 && (d.PathHz <= 0 || Math.Abs(d.PathHz-hz)<0.25);
        }
        public static void SelectTiming(string device,int physicalWidth,int physicalHeight,double hz)
        {
            Display[] active=Read();
            int index=UniqueDisplay(active,device);
            if(TimingMatches(active[index],physicalWidth,physicalHeight,hz)) return;
            RequestTiming(device,physicalWidth,physicalHeight,hz);
            for (int i=0;i<8;++i)
            {
                active=Read(); index=UniqueDisplay(active,device);
                if(TimingMatches(active[index],physicalWidth,physicalHeight,hz)) return;
                Thread.Sleep(70);
            }
            throw new InvalidOperationException("Драйвер выбрал другой физический режим: " + active[index]);
        }
        public static bool Matches(Display d,int x,int y,int width,int height,int physicalWidth,int physicalHeight,double hz)
        {
            return d.Virtual && !d.Cloned && d.Rotation==1 && TimingMatches(d,physicalWidth,physicalHeight,hz) &&
                d.Width==width && d.Height==height && d.SourceWidth==physicalWidth && d.SourceHeight==physicalHeight &&
                d.RegionX==x && d.RegionY==y && d.RegionWidth==width && d.RegionHeight==height &&
                d.ClipX==0 && d.ClipY==0 && d.ClipWidth==width && d.ClipHeight==height &&
                (d.Scaling==1 || d.Scaling==2 || d.Scaling==3 || d.Scaling==4);
        }
        private static State PlanRegion(string device,int x,int y,int width,int height,int physicalWidth,int physicalHeight,double hz)
        {
            if(x<0 || y<0 || width<=0 || height<=0 || (long)x+width>physicalWidth || (long)y+height>physicalHeight)
                throw new ArgumentOutOfRangeException("region","Рабочая область выходит за края физического сигнала.");
            State state=Query(); Display[] displays=Displays(state); int i=UniqueDisplay(displays,device);
            Link path=state.Links[i];
            if(!TimingMatches(displays[i],physicalWidth,physicalHeight,hz)) throw new InvalidOperationException("Запрошенный физический режим сейчас не активен.");
            if(!path.Virtual || !ValidMode(state,path.ImageIndex,3) || !ValidMode(state,path.SourceIndex,1))
                throw new NotSupportedException("Драйвер не предоставил этому дисплею виртуальный режим CCD.");
            // DWM renders the source at the logical size and places it on an equally sized rectangle.
            // The complete VidPn surface has the signal's active dimensions, including the damaged strip.
            state.Modes[path.SourceIndex].Source.Width=(uint)width;
            state.Modes[path.SourceIndex].Source.Height=(uint)height;
            state.Modes[path.ImageIndex].Image=new DesktopImage { Surface=new Point(physicalWidth,physicalHeight),
                Region=new Rect(x,y,width,height), Clip=new Rect(0,0,width,height) };
            state.Links[i].Target.Scaling=1; // DISPLAYCONFIG_SCALING_IDENTITY
            return state;
        }
        public static void ValidateRegion(string device,int x,int y,int width,int height,int physicalWidth,int physicalHeight,double hz)
        {
            State candidate=PlanRegion(device,x,y,width,height,physicalWidth,physicalHeight,hz);
            Check(candidate.Execute(Validate),"SetDisplayConfig VALIDATE");
        }
        public static void ApplyRegion(string device,int x,int y,int width,int height,int physicalWidth,int physicalHeight,double hz)
        {
            SelectTiming(device,physicalWidth,physicalHeight,hz);
            State candidate=PlanRegion(device,x,y,width,height,physicalWidth,physicalHeight,hz);
            candidate.Set(Apply);
            Display[] read=Read(); Display actual=read[UniqueDisplay(read,device)];
            if(!Matches(actual,x,y,width,height,physicalWidth,physicalHeight,hz))
                throw new InvalidOperationException("Фактические параметры после SetDisplayConfig отличаются: "+actual);
        }
        public static void PersistCurrent() { Query().Set(Apply | Save); }

        private static byte[] RawArray<T>(T[] data) where T:struct
        {
            byte[] raw=new byte[checked(Marshal.SizeOf(typeof(T))*data.Length)];
            if (raw.Length == 0) return raw;
            GCHandle pin=GCHandle.Alloc(data,GCHandleType.Pinned);
            try { Marshal.Copy(pin.AddrOfPinnedObject(),raw,0,raw.Length); }
            finally { pin.Free(); }
            return raw;
        }
        private static T[] ReadArray<T>(BinaryReader input,int limit) where T:struct
        {
            int count=input.ReadInt32();
            if(count<1 || count>limit) throw new InvalidDataException("Неверное количество записей CCD.");
            int size=checked(count*Marshal.SizeOf(typeof(T)));
            byte[] raw=input.ReadBytes(size);
            if(raw.Length!=size) throw new EndOfStreamException();
            T[] result=new T[count];
            GCHandle pin=GCHandle.Alloc(result,GCHandleType.Pinned);
            try { Marshal.Copy(raw,0,pin.AddrOfPinnedObject(),size); }
            finally { pin.Free(); }
            return result;
        }
        public static byte[] Capture()
        {
            State s=Query();
            using(var output=new MemoryStream())
            using(var w=new BinaryWriter(output,Encoding.UTF8,true))
            {
                w.Write(SnapshotMagic);
                w.Write(s.Links.Length); w.Write(RawArray(s.Links));
                w.Write(s.Modes.Length); w.Write(RawArray(s.Modes));
                foreach(Link path in s.Links)
                {
                    string id=TargetInfo(path).Path;
                    if(string.IsNullOrEmpty(id)) throw new InvalidOperationException("Монитор не сообщил постоянный путь устройства.");
                    w.Write(id);
                }
                w.Flush();
                using(var hash=System.Security.Cryptography.SHA256.Create())
                    w.Write(hash.ComputeHash(output.ToArray()));
                return output.ToArray();
            }
        }
        private static State Decode(byte[] snapshot)
        {
            if(snapshot==null || snapshot.Length<48 || snapshot.Length>1024*1024)
                throw new InvalidDataException("Повреждён снимок конфигурации дисплеев.");
            int end=snapshot.Length-32;
            using(var hash=System.Security.Cryptography.SHA256.Create())
                if(!hash.ComputeHash(snapshot,0,end).SequenceEqual(snapshot.Skip(end)))
                    throw new InvalidDataException("Контрольная сумма снимка конфигурации неверна.");
            using(var input=new BinaryReader(new MemoryStream(snapshot,0,end,false)))
            {
                if(input.ReadInt32()!=SnapshotMagic) throw new InvalidDataException("Неизвестный формат снимка CCD.");
                State s=new State {Links=ReadArray<Link>(input,MaxPaths), Modes=ReadArray<Mode>(input,MaxModes)};
                s.Targets=new string[s.Links.Length];
                for(int i=0;i<s.Targets.Length;++i) s.Targets[i]=input.ReadString();
                if(input.BaseStream.Position!=end || s.Targets.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=s.Links.Length)
                    throw new InvalidDataException("Некорректный список мониторов в снимке.");
                return s;
            }
        }

        private static void Rebind(State saved, State live)
        {
            string[] devices=live.Links.Select(p=>TargetInfo(p).Path).ToArray();
            if(live.Links.Length!=saved.Links.Length || devices.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=devices.Length)
                throw new InvalidOperationException("Набор подключённых дисплеев изменился. Возвратите исходное подключение.");
            for(int i=0;i<saved.Links.Length;++i)
            {
                int liveIndex=Array.FindIndex(devices,id=>StringComparer.OrdinalIgnoreCase.Equals(id,saved.Targets[i]));
                if(liveIndex<0) throw new InvalidOperationException("Исходный монитор сейчас не подключён.");
                Link current=live.Links[liveIndex], old=saved.Links[i];
                // LUIDs are valid only until reboot. Keep the saved path graph and modes, but
                // remap owners using the persistent PnP target and current source/target IDs.
                if(old.Virtual!=current.Virtual)
                    throw new InvalidOperationException("Изменились возможности драйвера на пути дисплея.");
                for(int j=0;j<i;++j)
                {
                    int other=Array.FindIndex(devices,id=>StringComparer.OrdinalIgnoreCase.Equals(id,saved.Targets[j]));
                    bool priorShared=saved.Links[j].SourceIndex==old.SourceIndex;
                    bool nowShared=live.Links[other].Source.Adapter.Equals(current.Source.Adapter) && live.Links[other].Source.Id==current.Source.Id;
                    if(priorShared!=nowShared) throw new InvalidOperationException("Изменилась группа дублирования дисплеев.");
                }
                int si=old.SourceIndex, ti=old.TargetIndex, di=old.ImageIndex;
                if(!ValidMode(saved,si,1) || !ValidMode(saved,ti,2))
                    throw new InvalidDataException("Неверные индексы режимов в снимке.");
                saved.Modes[si].Adapter=current.Source.Adapter; saved.Modes[si].Id=current.Source.Id;
                saved.Modes[ti].Adapter=current.Target.Adapter; saved.Modes[ti].Id=current.Target.Id;
                if(ValidMode(saved,di,3))
                {
                    saved.Modes[di].Adapter=current.Target.Adapter;
                    saved.Modes[di].Id=current.Target.Id;
                }
                old.Source.Adapter=current.Source.Adapter; old.Source.Id=current.Source.Id;
                old.Target.Adapter=current.Target.Adapter; old.Target.Id=current.Target.Id;
                saved.Links[i]=old;
            }
        }
        private static bool SameGeometry(Display a,Display b)
        {
            return a.Width==b.Width && a.Height==b.Height && a.DesktopX==b.DesktopX && a.DesktopY==b.DesktopY &&
                a.SourceWidth==b.SourceWidth && a.SourceHeight==b.SourceHeight && a.SignalWidth==b.SignalWidth &&
                a.SignalHeight==b.SignalHeight && Math.Abs(a.Hz-b.Hz)<0.02 && Math.Abs(a.PathHz-b.PathHz)<0.02 &&
                a.RegionX==b.RegionX && a.RegionY==b.RegionY && a.RegionWidth==b.RegionWidth &&
                a.RegionHeight==b.RegionHeight && a.ClipX==b.ClipX && a.ClipY==b.ClipY &&
                a.ClipWidth==b.ClipWidth && a.ClipHeight==b.ClipHeight && a.Rotation==b.Rotation &&
                a.Scaling==b.Scaling && a.ScanLine==b.ScanLine && a.Cloned==b.Cloned;
        }
        public static void Restore(byte[] snapshot) { Restore(snapshot,false); }
        public static void Restore(byte[] snapshot,bool persist)
        {
            State saved=Decode(snapshot);
            Rebind(saved,Query());
            Display[] expected=Displays(saved);
            saved.Set(Apply | (persist?Save:0));
            Display[] actual=Read();
            foreach(Display wanted in expected)
            {
                Display match=Array.Find(actual,d=>StringComparer.OrdinalIgnoreCase.Equals(d.TargetPath,wanted.TargetPath));
                if(match==null || !SameGeometry(wanted,match))
                    throw new InvalidOperationException("Драйвер не подтвердил восстановление. Ожидалось: "+wanted+"; получено: "+match);
            }
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RightPanel {
 internal static class Nv {
  [DllImport("nvapi64.dll", EntryPoint="nvapi_QueryInterface",CallingConvention=CallingConvention.Cdecl)] static extern IntPtr Query(uint id);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Init();
  [UnmanagedFunctionPointer(CallingConvention.Cdecl,CharSet=CharSet.Ansi)] delegate int Find(string name,out uint id);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Data(uint id,IntPtr input,IntPtr output);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Enum(uint id,uint index,IntPtr data);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Multi(ref uint id,uint count,IntPtr data);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Save(ref uint id,uint count,uint output,uint monitor);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Revert(ref uint id,uint count);
  static T Fn<T>(uint id) { var p=Query(id); if(p==IntPtr.Zero) throw new Exception("NVAPI interface unavailable: "+id.ToString("X")); return (T)(object)Marshal.GetDelegateForFunctionPointer(p,typeof(T)); }
  static void Check(int rc,string name) { if(rc!=0) throw new Exception(name+" NVAPI "+rc); }
  static void Put(IntPtr p,int off,int v) { Marshal.WriteInt32(p,off,v); }
  static void Float(IntPtr p,int off,float v) { Put(p,off,BitConverter.ToInt32(BitConverter.GetBytes(v),0)); }
  public static uint Id(string name) { Check(Fn<Init>(0x0150e828)(),"NvAPI Initialize"); uint id; Check(Fn<Find>(0xae457190)(name,out id),"NvAPI display"); return id; }
  public static bool Exists(uint id,int width,int height) {
   var d=Marshal.AllocHGlobal(144);
   try { for(uint i=0;i<256;i++){ for(int k=0;k<144;k+=4) Put(d,k,0); Put(d,0,65536+144); int rc=Fn<Enum>(0xa2072d59)(id,i,d); if(rc==-7)return false; Check(rc,"EnumCustomDisplay"); if(Marshal.ReadInt32(d,4)==width && Marshal.ReadInt32(d,8)==height)return true; } throw new Exception("Too many custom modes"); }
   finally { Marshal.FreeHGlobal(d); }
  }
  public static void Create(uint id,int w,int h,string journal) {
   IntPtr input=Marshal.AllocHGlobal(32),d=Marshal.AllocHGlobal(144);
   try {
    for(int i=0;i<32;i+=4)Put(input,i,0); for(int i=0;i<144;i+=4)Put(d,i,0);
    Put(input,0,65536+32); Put(d,0,65536+144);Put(d,4,w);Put(d,8,h);Put(d,12,32);Put(d,16,21);
    Float(d,28,1);Float(d,32,1);Float(d,36,1);Float(d,40,1);
    Check(Fn<Data>(0x175167e9)(id,input,IntPtr.Add(d,44)),"GetTiming");
    byte[] b=new byte[144];Marshal.Copy(d,b,0,144);File.WriteAllBytes(journal,b);
    Check(Fn<Multi>(0x1f7db630)(ref id,1,d),"TryCustomDisplay");
    Check(Fn<Save>(0x49882876)(ref id,1,1,1),"SaveCustomDisplay");
   } finally { Marshal.FreeHGlobal(input);Marshal.FreeHGlobal(d); }
  }
  public static void Cleanup(uint id,string journal) {
   Fn<Revert>(0xcbbd40f0)(ref id,1);
   if(!File.Exists(journal)) return;
   byte[] b=File.ReadAllBytes(journal);if(b.Length!=144)throw new Exception("Invalid NV mode journal");
   int w=BitConverter.ToInt32(b,4),h=BitConverter.ToInt32(b,8);
   if(Exists(id,w,h)) {
    IntPtr p=Marshal.AllocHGlobal(144);try {Marshal.Copy(b,0,p,144);Check(Fn<Multi>(0x552e5b9b)(ref id,1,p),"DeleteCustomDisplay");}finally{Marshal.FreeHGlobal(p);}
   }
   File.Delete(journal);
  }
 }
 internal static class Engine {
  static readonly string Dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RightPanel");
  static string F(string n){return Path.Combine(Dir,n);}
  static Display Display(){var d=Ccd.Read();if(d.Length!=1)throw new Exception("Нужен один активный монитор.");return d[0];}
  public static string Status(){var d=Display();return d.Name+" · "+d.Width+"×"+d.Height+" логический\nСигнал "+d.SignalWidth+"×"+d.SignalHeight+" · "+d.Hz.ToString("0.##")+" Гц";}
  public static string Diagnose(){var d=Display();return (File.Exists(F("error")) ? File.ReadAllText(F("error"))+"\n" : "")+d+"\nДоступные частоты: "+string.Join(", ",Ccd.Rates(d.Device,2560,1080))+" Гц\nЭксклюзивный Fullscreen может изменить положение изображения.";}
  public static string TrialStatus(){try {if(!File.Exists(F("active")))return "";return File.ReadAllText(F("active"));}catch{return "Идёт проверка";}}
  public static bool CanConfirm(){return TrialStatus().StartsWith("Подтвердите");}
  static void Spawn(string args){Process.Start(new ProcessStartInfo(Application.ExecutablePath,args){UseShellExecute=false,CreateNoWindow=true});}
  public static void StartTrial(int x,int y,int w,int h,int hz){
   Directory.CreateDirectory(Dir);if(File.Exists(F("error")))File.Delete(F("error"));if(File.Exists(F("active"))||File.Exists(F("snapshot")))throw new Exception("Уже есть пробная или сохранённая конфигурация. Сначала восстановите исходный режим.");
   var d=Display();if(!Ccd.Rates(d.Device,2560,1080).Contains(hz))throw new Exception("Частота отсутствует в списке драйвера: "+hz);
   uint id=Nv.Id(d.Device);if(Nv.Exists(id,w,h))throw new Exception("Пользовательский режим уже существует. Для защиты существующих настроек выберите другое разрешение.");
   File.WriteAllBytes(F("snapshot"),Ccd.Capture());File.WriteAllText(F("active"),"Подготовка пробного режима");
   Spawn("--worker "+x+" "+y+" "+w+" "+h+" "+hz);
  }
  public static void Confirm(){if(!CanConfirm())throw new Exception("Проверка ещё не готова.");File.WriteAllText(F("confirm"),"yes");}
  public static void Restore(){Directory.CreateDirectory(Dir);if(File.Exists(F("active"))){File.WriteAllText(F("cancel"),"yes");return;} if(!File.Exists(F("snapshot")))throw new Exception("Исходная конфигурация не сохранена.");Spawn("--restore");}
  static void RestoreNow(){var d=Display();uint id=Nv.Id(d.Device);Ccd.Restore(File.ReadAllBytes(F("snapshot")),true);Nv.Cleanup(id,F("nv-mode"));Ccd.Restore(File.ReadAllBytes(F("snapshot")),true);File.Delete(F("snapshot"));File.Delete(F("active"));File.Delete(F("confirm"));File.Delete(F("cancel"));}
  public static void Worker(string[] args) {
   try {
    int x=int.Parse(args[1]),y=int.Parse(args[2]),w=int.Parse(args[3]),h=int.Parse(args[4]),hz=int.Parse(args[5]);var d=Display();
    Ccd.SelectTiming(d.Device,2560,1080,hz);uint id=Nv.Id(d.Device);
    Nv.Create(id,w,h,F("nv-mode"));Ccd.ValidateRegion(d.Device,x,y,w,h,2560,1080,hz);Ccd.ApplyRegion(d.Device,x,y,w,h,2560,1080,hz);
    var end=DateTime.UtcNow.AddSeconds(15);
    while(DateTime.UtcNow<end && !File.Exists(F("cancel"))){
     if(File.Exists(F("confirm"))){Ccd.PersistCurrent();File.Delete(F("confirm"));File.Delete(F("active"));return;}
     File.WriteAllText(F("active"),"Подтвердите за "+Math.Max(0,(int)Math.Ceiling((end-DateTime.UtcNow).TotalSeconds))+" с");Thread.Sleep(200);
    }
    RestoreNow();
   } catch(Exception ex){try{RestoreNow();}catch(Exception e){File.WriteAllText(F("error"),ex+"\r\nОткат: "+e);return;}File.WriteAllText(F("error"),ex.ToString());}
  }
  public static void RestoreWorker(){try{RestoreNow();}catch(Exception ex){File.WriteAllText(F("error"),ex.ToString());}}
 }
 static class Program {
  [STAThread] static void Main(string[] args){
   if(args.Length>0 && args[0]=="--worker"){Engine.Worker(args);return;}
   if(args.Length>0 && args[0]=="--restore"){Engine.RestoreWorker();return;}
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new MainWindow(args.Length>0 && args[0]=="--tray"));
  }
 }
}


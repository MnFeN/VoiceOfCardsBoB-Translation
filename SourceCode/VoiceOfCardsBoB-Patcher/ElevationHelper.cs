using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace VoiceOfCardsPatch
{
    internal static class ElevationHelper
    {
        public static bool IsAdmin()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static bool RestartElevated(string actionArgument, string workingDirectory)
        {
            string? executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                Console.Error.WriteLine("无法确定当前补丁程序的 EXE 路径，不能自动请求管理员权限。");
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = actionArgument,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = true,
                    Verb = "runas"
                });
                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Console.Error.WriteLine("已取消管理员权限请求。未修改任何游戏文件。");
                return false;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("请求管理员权限失败：" + ex.Message);
                return false;
            }
        }
    }
}

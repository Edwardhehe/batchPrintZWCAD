using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ZwcadBatchPlot;

/// <summary>签名库等跨进程写锁：拿不到锁时仍执行，避免挡住出图。</summary>
internal static class CrossProcessMutex
{
    public static string NameForPath(string prefix, string path)
    {
        var bytes = Encoding.UTF8.GetBytes(PathSafe(path));
#if NET8_0_OR_GREATER
        var hash = SHA256.HashData(bytes);
#else
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes);
#endif
        var builder = new StringBuilder(prefix.Length + 1 + 16);
        builder.Append(prefix);
        builder.Append('_');
        for (var i = 0; i < 8; i++)
        {
            builder.Append(hash[i].ToString("x2"));
        }

        return builder.ToString();
    }

    public static void RunOrFallback(string name, Action action, string what)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        var mutexName = @"Local\LABatchPlot_" + Sanitize(name);
        using var mutex = new Mutex(false, mutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(20));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                PluginLog.Error("签章", "未能获取" + what + "的跨进程锁，改为无锁执行。");
            }

            action();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static string PathSafe(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch
        {
            return path ?? "";
        }
    }

    private static string Sanitize(string name)
    {
        var chars = (name ?? "").ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var ch = chars[i];
            if (!char.IsLetterOrDigit(ch) && ch != '_')
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }
}

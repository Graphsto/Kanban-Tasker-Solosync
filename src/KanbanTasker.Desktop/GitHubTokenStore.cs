using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using KanbanTasker.Core.GitHub;

namespace KanbanTasker.Desktop;

internal sealed class GitHubTokenStore(string path) : IGitHubTokenStore
{
    public async Task<GitHubTokens?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Invalid GitHub credential file.");
        var clear = Transform(await File.ReadAllBytesAsync(path,ct),false);
        try { return JsonSerializer.Deserialize<GitHubTokens>(clear); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid GitHub credential file.",ex); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public async Task SaveAsync(GitHubTokens tokens, CancellationToken ct = default)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(tokens);
        byte[] protectedBytes;
        try { protectedBytes = Transform(clear,true); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await new KanbanTasker.Core.AtomicFileWriter().WriteAsync(path,protectedBytes,File.Exists(path),ct);
    }
    public Task DeleteAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input,string? description,
        IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input,IntPtr description,
        IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    private static byte[] Transform(byte[] bytes,bool protect)
    {
        var input = new Blob { Length=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes,0,input.Data,bytes.Length);
            var success = protect ? CryptProtectData(ref input,null,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output)
                : CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);
            if (!success) throw new CryptographicException("Windows could not protect or read the GitHub credentials. Sign in again.");
            var result = new byte[output.Length]; Marshal.Copy(output.Data,result,0,result.Length); return result;
        }
        finally
        {
            Marshal.Copy(new byte[input.Length],0,input.Data,input.Length); Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) { Marshal.Copy(new byte[output.Length],0,output.Data,output.Length); LocalFree(output.Data); }
        }
    }
}

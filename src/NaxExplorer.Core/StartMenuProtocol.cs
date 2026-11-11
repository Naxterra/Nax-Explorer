using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Principal;
using System.Text.Json;

namespace NaxStartMenu.Integration;

// Wire format v1, shared by Nax Explorer and Nax-StartMenu. Keep the copies in both repositories in sync.
public sealed record PinGroup(string Id, string Name);
public sealed record PinRequest(string Operation, string? AppId = null, string? GroupId = null, int Version = 1);
public sealed record PinReply(bool Success, string? Error = null, List<PinGroup>? Groups = null);

public static class StartMenuProtocol
{
    public const int MaximumMessageBytes = 16384;
    public static string PipeName => $"Naxterra.NaxStartMenu.Pin.v1.{WindowsIdentity.GetCurrent().User!.Value}.{Process.GetCurrentProcess().SessionId}";

    public static async Task<PinReply> SendAsync(PinRequest request, string? pipeName = null)
    {
        // Identification prevents a process that pre-created the pipe from impersonating Explorer's user.
        using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await WriteAsync(pipe, request, timeout.Token).ConfigureAwait(false);
        return await ReadAsync<PinReply>(pipe, timeout.Token).ConfigureAwait(false);
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("Pin request is too large.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumMessageBytes) throw new InvalidDataException("Invalid pin request size.");
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty pin request.");
    }
}

/// <summary>Accepts only this Windows user's requests, including requests from the normal-rights file manager.</summary>
public sealed class StartMenuPinServer : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly NamedPipeServerStream _pipe;
    public Task Completion { get; }

    public StartMenuPinServer(Func<PinRequest, CancellationToken, Task<PinReply>> handle, Action<Exception> onError, string? pipeName = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        // The medium label is deliberate: the menu runs elevated, Explorer does not. No access for other accounts.
        string security = $"D:(A;;FA;;;SY)(A;;0x12019B;;;{identity.User!.Value})S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(security, 1, out IntPtr descriptor, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            // OVERLAPPED | FIRST_PIPE_INSTANCE | DUPLEX; remote clients are rejected before any request is read.
            SafePipeHandle nativeHandle = CreateNamedPipe(@"\\.\pipe\" + (pipeName ?? StartMenuProtocol.PipeName),
                0x40080003, 0x8, 1, 4096, 4096, 0, ref attributes);
            if (nativeHandle.IsInvalid) { int error = Marshal.GetLastWin32Error(); nativeHandle.Dispose(); throw new Win32Exception(error); }
            try { _pipe = new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, nativeHandle); }
            catch { nativeHandle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
        Completion = RunAsync(handle, onError);
    }

    private async Task RunAsync(Func<PinRequest, CancellationToken, Task<PinReply>> handle, Action<Exception> onError)
    {
        while (!_stop.IsCancellationRequested)
        {
            bool accepted = false;
            try
            {
                await _pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                accepted = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                PinRequest request = await StartMenuProtocol.ReadAsync<PinRequest>(_pipe, timeout.Token).ConfigureAwait(false);
                PinReply reply;
                if (request.Version != 1 || request.Operation is not ("groups" or "pin")) reply = new(false, "Unsupported pin request.");
                else
                {
                    // Whatever the handler throws ends this one request with an error reply, never the listener.
                    try { reply = await handle(request, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        onError(ex);
                        reply = new(false, "The pin request failed: " + ex.Message);
                    }
                }
                await StartMenuProtocol.WriteAsync(_pipe, reply, timeout.Token).ConfigureAwait(false);
                // DisconnectNamedPipe discards unread output. Wait for the client to consume the reply and close.
                await _pipe.ReadAsync(new byte[1], timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or JsonException or ObjectDisposedException
                or InvalidOperationException or UnauthorizedAccessException)
            {
                if (!_stop.IsCancellationRequested && ex is not OperationCanceledException) onError(ex);
                // An unrecoverable listener failure must not turn into a busy retry loop in the background; it is
                // reported through onError above instead of ending silently.
                if (!accepted) return;
            }
            finally
            {
                // A peer that closed during a read puts the stream into Broken (IsConnected is then false), but it
                // still needs Disconnect to reset the server for the next connection.
                if (!_stop.IsCancellationRequested && accepted)
                {
                    try { _pipe.Disconnect(); }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { onError(ex); }
                }
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _pipe.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int InheritHandle; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, IntPtr size);
    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint instances,
        uint outputBytes, uint inputBytes, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace PawprintTagger.Core;

/// <summary>A private, local pipe routes extension imports to the open desktop library.</summary>
public sealed class BrowserImportServer : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Func<BrowserRequest, Task<BrowserReply>> _handle;
    private readonly Task _loop;
    public static string PipeName(string settingsPath) => "pawprint-tagger-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(settingsPath))))[..24].ToLowerInvariant();

    public BrowserImportServer(string pipeName, Func<BrowserRequest, Task<BrowserReply>> handle)
    {
        _handle = handle;
        _pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _loop = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        while (!_cancel.IsCancellationRequested)
        {
            try
            {
                await _pipe.WaitForConnectionAsync(_cancel.Token).ConfigureAwait(false);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                BrowserReply reply;
                try
                {
                    var request = await BrowserMessageCodec.ReadAsync<BrowserRequest>(_pipe, requestTimeout.Token).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The browser disconnected before sending an import.");
                    reply = await _handle(request).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or
                    UnauthorizedAccessException or System.Text.Json.JsonException or OperationCanceledException)
                { reply = BrowserReply.Failure(ex.Message); }
                await BrowserMessageCodec.WriteAsync(_pipe, reply, _cancel.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            { if (_cancel.IsCancellationRequested) break; }
            finally { if (!_cancel.IsCancellationRequested && _pipe.IsConnected) _pipe.Disconnect(); }
        }
    }

    public static async Task<BrowserReply?> TrySendAsync(string pipeName, BrowserRequest request,
        CancellationToken cancellationToken = default)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(500, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
        // Once connected, never fall back to a second writer if the first writer loses its reply.
        using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        responseTimeout.CancelAfter(TimeSpan.FromMinutes(2));
        await BrowserMessageCodec.WriteAsync(pipe, request, responseTimeout.Token).ConfigureAwait(false);
        return await BrowserMessageCodec.ReadAsync<BrowserReply>(pipe, responseTimeout.Token).ConfigureAwait(false)
            ?? throw new IOException("Pawprint Tagger disconnected before confirming the import.");
    }

    public void Dispose()
    {
        _cancel.Cancel();
        _pipe.Dispose();
        // Do not wait here: a pending handler may still need the GTK main thread to finish.
    }
}
